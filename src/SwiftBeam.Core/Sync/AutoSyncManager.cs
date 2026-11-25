using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using SwiftBeam.Core.Client;
using SwiftBeam.Core.Protocol;
using SwiftBeam.Core.Server;
using SwiftBeam.Core.Settings;

namespace SwiftBeam.Core.Sync;

public enum AutoSyncRunState
{
    Idle,
    Watching,
    Syncing,
    WaitingForPeer,
    Paused,
    Error
}

public sealed record AutoSyncPairStatus(
    string PairId,
    AutoSyncRunState State,
    string StatusText,
    int TrackedFileCount,
    int SentCount,
    int ReceivedCount,
    int DeletedCount,
    DateTime? LastSyncedUtc,
    string? LastError);

/// <summary>
/// Manages real-time two-way / one-way background folder synchronization across connected peers.
/// Combines local FileSystemWatcher events with periodic remote polling and a persistent 3-way snapshot.
/// </summary>
public sealed class AutoSyncManager : IAsyncDisposable
{
    private const int ChunkBytes = 4 * 1024 * 1024;

    private readonly Func<AutoSyncPairConfig, ConnectionTarget> _targetResolver;
    private readonly string _stateDirectory;
    private readonly ConcurrentDictionary<string, SyncPairWorker> _workers = new(StringComparer.OrdinalIgnoreCase);

    public bool UseCompression { get; set; }
    public bool VerifySha256 { get; set; }

    public event Action<AutoSyncPairStatus>? StatusChanged;
    public event Action<string>? Log;
    public event Action<string, string>? FolderSynced;

    public AutoSyncManager(
        Func<AutoSyncPairConfig, ConnectionTarget> targetResolver,
        string stateDirectory,
        bool useCompression = false,
        bool verifySha256 = true)
    {
        _targetResolver = targetResolver;
        _stateDirectory = stateDirectory;
        UseCompression = useCompression;
        VerifySha256 = verifySha256;
        Directory.CreateDirectory(_stateDirectory);
    }

    public void LoadPairs(IEnumerable<AutoSyncPairConfig> pairs)
    {
        foreach (var pair in pairs)
        {
            var worker = new SyncPairWorker(this, CloneConfig(pair));
            if (_workers.TryAdd(pair.Id, worker))
                worker.Start();
            else
                _ = worker.DisposeAsync();
        }
    }

    public async Task UpsertPairAsync(AutoSyncPairConfig pair)
    {
        var copy = CloneConfig(pair);
        if (_workers.TryRemove(copy.Id, out var existing))
            await existing.DisposeAsync().ConfigureAwait(false);

        var worker = new SyncPairWorker(this, copy);
        _workers[copy.Id] = worker;
        worker.Start();
    }

    public async Task RemovePairAsync(string pairId, bool deleteSnapshot = true)
    {
        if (_workers.TryRemove(pairId, out var existing))
            await existing.DisposeAsync().ConfigureAwait(false);

        if (deleteSnapshot)
        {
            string snapPath = GetSnapshotPath(pairId);
            try { if (File.Exists(snapPath)) File.Delete(snapPath); } catch { }
        }
    }

    public void TriggerSyncNow(string pairId)
    {
        if (_workers.TryGetValue(pairId, out var worker))
            worker.RequestImmediateSync();
    }

    public Task TriggerSyncNowAsync(string pairId)
    {
        return _workers.TryGetValue(pairId, out var worker)
            ? worker.RunSyncPassAsync()
            : Task.CompletedTask;
    }

    public void TriggerSyncAll()
    {
        foreach (var worker in _workers.Values)
            worker.RequestImmediateSync();
    }

    public Task TriggerSyncAllAsync()
    {
        var tasks = _workers.Values.Select(w => w.RunSyncPassAsync());
        return Task.WhenAll(tasks);
    }

    public AutoSyncPairStatus? GetStatus(string pairId)
        => _workers.TryGetValue(pairId, out var w) ? w.CurrentStatus : null;

    private string GetSnapshotPath(string pairId) => Path.Combine(_stateDirectory, $"{pairId}.json");

    private static AutoSyncPairConfig CloneConfig(AutoSyncPairConfig c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        LocalPath = c.LocalPath,
        RemotePath = c.RemotePath,
        PeerDeviceName = c.PeerDeviceName,
        PeerHost = c.PeerHost,
        PeerPort = c.PeerPort,
        PeerUseTls = c.PeerUseTls,
        PeerPin = c.PeerPin,
        Mode = c.Mode,
        Enabled = c.Enabled,
        SyncDeletes = c.SyncDeletes,
        UseSoftTrash = c.UseSoftTrash,
        IncludeHidden = c.IncludeHidden,
        PollIntervalSeconds = Math.Clamp(c.PollIntervalSeconds, 2, 300),
        IgnorePatterns = c.IgnorePatterns
    };

    public async ValueTask DisposeAsync()
    {
        var list = _workers.Values.ToList();
        _workers.Clear();
        foreach (var w in list)
            await w.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class SyncPairWorker : IAsyncDisposable
    {
        private readonly AutoSyncManager _owner;
        private readonly AutoSyncPairConfig _config;
        private readonly string _snapshotPath;
        private readonly SemaphoreSlim _syncLock = new(1, 1);
        private readonly SemaphoreSlim _wakeSignal = new(0, 1);
        private readonly ConcurrentDictionary<string, DateTime> _selfModifiedPaths = new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private FileSystemWatcher? _watcher;
        private Timer? _debounceTimer;
        private TransferClient? _client;
        private int _sentCount;
        private int _receivedCount;
        private int _deletedCount;
        private bool _loggedUnreachable;

        public AutoSyncPairStatus CurrentStatus { get; private set; }

        public SyncPairWorker(AutoSyncManager owner, AutoSyncPairConfig config)
        {
            _owner = owner;
            _config = config;
            _snapshotPath = owner.GetSnapshotPath(config.Id);
            var existingSnap = AutoSyncSnapshot.Load(_snapshotPath);
            CurrentStatus = new AutoSyncPairStatus(
                config.Id,
                config.Enabled ? AutoSyncRunState.Idle : AutoSyncRunState.Paused,
                config.Enabled ? "Starting watcher..." : "Paused",
                existingSnap.Files.Count,
                0, 0, 0,
                existingSnap.LastSyncedUtc == default ? null : existingSnap.LastSyncedUtc,
                null);
        }

        public void Start()
        {
            if (!_config.Enabled)
            {
                PublishStatus(AutoSyncRunState.Paused, "Paused", CurrentStatus.TrackedFileCount, CurrentStatus.LastSyncedUtc, null);
                return;
            }

            _cts = new CancellationTokenSource();
            StartWatcher();
            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        }

        public void RequestImmediateSync()
        {
            if (_cts is { IsCancellationRequested: false } && _wakeSignal.CurrentCount == 0)
            {
                try { _wakeSignal.Release(); } catch { }
            }
        }

        private void StartWatcher()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_config.LocalPath) || !Directory.Exists(_config.LocalPath))
                    return;

                var patterns = AutoSyncEngine.ParseIgnorePatterns(_config.IgnorePatterns);
                _watcher = new FileSystemWatcher(_config.LocalPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 32 * 1024
                };

                void HandleEvent(string fullPath)
                {
                    try
                    {
                        string rel = Path.GetRelativePath(_config.LocalPath, fullPath).Replace('\\', '/').Trim('/');
                        if (AutoSyncEngine.IsIgnored(rel, patterns))
                            return;

                        if (_selfModifiedPaths.TryGetValue(rel, out var expiry))
                        {
                            if (DateTime.UtcNow <= expiry)
                                return;
                            _selfModifiedPaths.TryRemove(rel, out _);
                        }

                        _debounceTimer?.Change(650, Timeout.Infinite);
                    }
                    catch { }
                }

                _debounceTimer = new Timer(_ => RequestImmediateSync(), null, Timeout.Infinite, Timeout.Infinite);
                _watcher.Created += (_, e) => HandleEvent(e.FullPath);
                _watcher.Changed += (_, e) => HandleEvent(e.FullPath);
                _watcher.Deleted += (_, e) => HandleEvent(e.FullPath);
                _watcher.Renamed += (_, e) => { HandleEvent(e.OldFullPath); HandleEvent(e.FullPath); };
                _watcher.Error += (_, _) => RequestImmediateSync();
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                _owner.Log?.Invoke($"Auto-Sync '{DisplayName()}': watcher setup warning ({ex.Message}); falling back to polling.");
            }
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            int pollMs = Math.Clamp(_config.PollIntervalSeconds, 2, 300) * 1000;
            while (!ct.IsCancellationRequested)
            {
                await RunSyncPassCoreAsync(ct).ConfigureAwait(false);

                try
                {
                    await _wakeSignal.WaitAsync(pollMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        public Task RunSyncPassAsync() =>
            _cts is null || _cts.IsCancellationRequested
                ? Task.CompletedTask
                : RunSyncPassCoreAsync(_cts.Token);

        private async Task RunSyncPassCoreAsync(CancellationToken ct)
        {
            if (!_config.Enabled) return;

            await _syncLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (string.IsNullOrWhiteSpace(_config.LocalPath) || !Directory.Exists(_config.LocalPath))
                {
                    PublishStatus(AutoSyncRunState.Error, $"Local folder missing: {_config.LocalPath}", CurrentStatus.TrackedFileCount, CurrentStatus.LastSyncedUtc, "Local folder missing");
                    return;
                }

                if (_watcher is null)
                    StartWatcher();

                var client = await EnsureConnectedClientAsync(ct).ConfigureAwait(false);
                if (client is null)
                    return;

                var patterns = AutoSyncEngine.ParseIgnorePatterns(_config.IgnorePatterns);
                var snapshot = AutoSyncSnapshot.Load(_snapshotPath);

                // Fetch remote recursive tree (reconnect once if idle connection dropped)
                ListDirResponseMessage remoteResp;
                try
                {
                    remoteResp = await client.ListDirectoryAsync(_config.RemotePath, recursive: true, includeHidden: _config.IncludeHidden, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or EndOfStreamException or SocketException)
                {
                    await DisconnectClientAsync().ConfigureAwait(false);
                    client = await EnsureConnectedClientAsync(ct).ConfigureAwait(false);
                    if (client is null) return;
                    remoteResp = await client.ListDirectoryAsync(_config.RemotePath, recursive: true, includeHidden: _config.IncludeHidden, ct).ConfigureAwait(false);
                }

                if (!string.IsNullOrEmpty(remoteResp.Error))
                {
                    PublishStatus(AutoSyncRunState.Error, $"Remote error: {remoteResp.Error}", snapshot.Files.Count, CurrentStatus.LastSyncedUtc, remoteResp.Error);
                    return;
                }

                var (localFiles, localDirs) = AutoSyncEngine.ScanLocalDirectory(_config.LocalPath, _config.IncludeHidden, patterns);
                var (remoteFiles, remoteDirs) = AutoSyncEngine.BuildRemoteMaps(remoteResp.Entries, _config.RemotePath, _config.IncludeHidden, patterns);
                var plan = AutoSyncEngine.ComputePlan(_config.LocalPath, _config.RemotePath, localFiles, localDirs, remoteFiles, remoteDirs, snapshot, _config);

                int passSent = 0;
                int passRecv = 0;
                int passDel = 0;
                var failedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (plan.Actions.Count > 0)
                {
                    PublishStatus(AutoSyncRunState.Syncing, $"Syncing {plan.Actions.Count} change(s)...", localFiles.Count, CurrentStatus.LastSyncedUtc, null);

                    var createdRemoteDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var action in plan.Actions)
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            switch (action.Kind)
                            {
                                case AutoSyncActionKind.CreateLocalDir:
                                    MarkSelfModified(action.RelativePath);
                                    Directory.CreateDirectory(action.LocalFullPath);
                                    break;

                                case AutoSyncActionKind.CreateRemoteDir:
                                    if (createdRemoteDirs.Add(action.RemoteFullPath))
                                    {
                                        var mkdirResp = await client.CreateDirectoryAsync(action.RemoteFullPath, ct).ConfigureAwait(false);
                                        if (!mkdirResp.Success)
                                            throw new IOException(mkdirResp.Error ?? "Failed to create remote directory.");
                                    }
                                    break;

                                case AutoSyncActionKind.UploadFile:
                                    await UploadFileAsync(client, action, createdRemoteDirs, ct).ConfigureAwait(false);
                                    passSent++;
                                    _sentCount++;
                                    break;

                                case AutoSyncActionKind.DownloadFile:
                                    await DownloadFileAsync(client, action, isConflictCopy: false, ct).ConfigureAwait(false);
                                    passRecv++;
                                    _receivedCount++;
                                    break;

                                case AutoSyncActionKind.ConflictKeepBothDownloadRemote:
                                    await DownloadFileAsync(client, action, isConflictCopy: true, ct).ConfigureAwait(false);
                                    passRecv++;
                                    _receivedCount++;
                                    break;

                                case AutoSyncActionKind.DeleteRemoteFile:
                                case AutoSyncActionKind.DeleteRemoteDir:
                                {
                                    var delResp = await client.DeletePathAsync(action.RemoteFullPath, ct).ConfigureAwait(false);
                                    if (!delResp.Success && (delResp.Error is null || !delResp.Error.Contains("not found", StringComparison.OrdinalIgnoreCase)))
                                        throw new IOException(delResp.Error ?? "Remote delete failed.");
                                    if (action.Kind == AutoSyncActionKind.DeleteRemoteFile) { passDel++; _deletedCount++; }
                                    break;
                                }

                                case AutoSyncActionKind.DeleteLocalFile:
                                    MarkSelfModified(action.RelativePath);
                                    DeleteLocalWithSoftTrash(action.LocalFullPath, action.RelativePath);
                                    passDel++;
                                    _deletedCount++;
                                    break;

                                case AutoSyncActionKind.DeleteLocalDir:
                                    MarkSelfModified(action.RelativePath);
                                    if (Directory.Exists(action.LocalFullPath))
                                        FileShareServer.DeleteFileOrDirectoryRobust(action.LocalFullPath);
                                    break;
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            failedKeys.Add(action.RelativePath);
                            _owner.Log?.Invoke($"Auto-Sync '{DisplayName()}' failed on '{action.RelativePath}': {ex.Message}");
                        }
                    }

                    // Re-scan local + remote post-sync state for snapshot update (excluding failed keys so they retry cleanly)
                    (localFiles, localDirs) = AutoSyncEngine.ScanLocalDirectory(_config.LocalPath, _config.IncludeHidden, patterns);
                }

                // Update baseline snapshot for all non-failed paths
                var nextFiles = new Dictionary<string, SyncFileRecord>(StringComparer.OrdinalIgnoreCase);
                foreach (var (rel, rec) in localFiles)
                {
                    if (!failedKeys.Contains(rel))
                        nextFiles[rel] = rec;
                    else if (snapshot.Files.TryGetValue(rel, out var oldRec))
                        nextFiles[rel] = oldRec;
                }
                foreach (string fk in failedKeys)
                {
                    if (!nextFiles.ContainsKey(fk) && snapshot.Files.TryGetValue(fk, out var oldRec))
                        nextFiles[fk] = oldRec;
                }

                var nextDirs = new HashSet<string>(localDirs, StringComparer.OrdinalIgnoreCase);
                var nowUtc = DateTime.UtcNow;
                snapshot.Files = nextFiles;
                snapshot.Directories = nextDirs;
                snapshot.LastSyncedUtc = nowUtc;
                snapshot.Save(_snapshotPath);

                if (passSent > 0 || passRecv > 0 || passDel > 0)
                {
                    _owner.Log?.Invoke($"Auto-Sync '{DisplayName()}': {passSent} sent, {passRecv} received, {passDel} deleted.");
                    _owner.FolderSynced?.Invoke(_config.LocalPath, _config.RemotePath);
                }

                if (failedKeys.Count > 0)
                {
                    PublishStatus(AutoSyncRunState.Error, $"Retrying {failedKeys.Count} item(s) — check Activity Log", nextFiles.Count, nowUtc, $"{failedKeys.Count} failed");
                }
                else
                {
                    string peerLabel = string.IsNullOrWhiteSpace(_config.PeerDeviceName) ? _config.PeerHost : _config.PeerDeviceName;
                    PublishStatus(AutoSyncRunState.Watching, $"Up to date • {nextFiles.Count} files • {peerLabel}", nextFiles.Count, nowUtc, null);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down
            }
            catch (Exception ex)
            {
                await DisconnectClientAsync().ConfigureAwait(false);
                PublishStatus(AutoSyncRunState.WaitingForPeer, $"Waiting for {PeerDisplay()} ({ex.Message})", CurrentStatus.TrackedFileCount, CurrentStatus.LastSyncedUtc, ex.Message);
            }
            finally
            {
                _syncLock.Release();
            }
        }

        private async Task<TransferClient?> EnsureConnectedClientAsync(CancellationToken ct)
        {
            if (_client is { IsConnected: true })
            {
                if (await _client.PingAsync(ct).ConfigureAwait(false))
                    return _client;
                await DisconnectClientAsync().ConfigureAwait(false);
            }

            var target = _owner._targetResolver(_config);
            if (string.IsNullOrWhiteSpace(target.Host))
            {
                PublishStatus(AutoSyncRunState.WaitingForPeer, $"Waiting for {PeerDisplay()} address...", CurrentStatus.TrackedFileCount, CurrentStatus.LastSyncedUtc, null);
                return null;
            }

            var candidate = new TransferClient();
            try
            {
                await candidate.ConnectAsync(target, ct).ConfigureAwait(false);
                _client = candidate;
                if (_loggedUnreachable)
                {
                    _owner.Log?.Invoke($"Auto-Sync '{DisplayName()}': reconnected to {target.Host}.");
                    _loggedUnreachable = false;
                }
                return _client;
            }
            catch (Exception ex)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
                if (!_loggedUnreachable)
                {
                    _owner.Log?.Invoke($"Auto-Sync '{DisplayName()}': waiting for {PeerDisplay()} ({ex.Message}).");
                    _loggedUnreachable = true;
                }
                PublishStatus(AutoSyncRunState.WaitingForPeer, $"Offline — waiting for {PeerDisplay()}", CurrentStatus.TrackedFileCount, CurrentStatus.LastSyncedUtc, null);
                return null;
            }
        }

        private async Task UploadFileAsync(TransferClient client, AutoSyncAction action, HashSet<string> createdRemoteDirs, CancellationToken ct)
        {
            var fi = new FileInfo(action.LocalFullPath);
            if (!fi.Exists) return;

            string remoteNorm = action.RemoteFullPath.Replace('\\', '/');
            int slash = remoteNorm.LastIndexOf('/');
            if (slash > 0)
            {
                string parent = remoteNorm[..slash];
                if (createdRemoteDirs.Add(parent))
                {
                    try { await client.CreateDirectoryAsync(parent, ct).ConfigureAwait(false); } catch { }
                }
            }

            long totalSize = fi.Length;
            DateTime mtime = fi.LastWriteTimeUtc;
            string? sha256Hex = null;
            if (_owner.VerifySha256 && totalSize > 0)
            {
                await using var hashStream = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
                byte[] hash = await SHA256.HashDataAsync(hashStream, ct).ConfigureAwait(false);
                sha256Hex = Convert.ToHexString(hash).ToLowerInvariant();
            }

            if (totalSize == 0)
            {
                var ack = await client.PushRangeAsync(remoteNorm, fi.FullName, 0, mtime, 0, 0, _ => { }, ct, _owner.UseCompression).ConfigureAwait(false);
                if (!ack.Success)
                    throw new IOException(ack.Error ?? "Remote upload failed.");
            }
            else
            {
                for (long offset = 0; offset < totalSize; offset += ChunkBytes)
                {
                    long len = Math.Min(ChunkBytes, totalSize - offset);
                    var ack = await client.PushRangeAsync(remoteNorm, fi.FullName, totalSize, mtime, offset, len, _ => { }, ct, _owner.UseCompression).ConfigureAwait(false);
                    if (!ack.Success)
                        throw new IOException(ack.Error ?? "Remote upload chunk failed.");
                }
            }

            var finAck = await client.FinalizePushAsync(remoteNorm, totalSize, mtime, sha256Hex, ct).ConfigureAwait(false);
            if (!finAck.Success)
                throw new IOException(finAck.Error ?? "Remote finalize failed.");
        }

        private async Task DownloadFileAsync(TransferClient client, AutoSyncAction action, bool isConflictCopy, CancellationToken ct)
        {
            MarkSelfModified(action.RelativePath);
            string? localDir = Path.GetDirectoryName(action.LocalFullPath);
            if (!string.IsNullOrEmpty(localDir))
                Directory.CreateDirectory(localDir);

            if (isConflictCopy && File.Exists(action.LocalFullPath))
            {
                string conflictPath = AutoSyncEngine.BuildConflictCopyPath(action.LocalFullPath);
                string conflictRel = Path.GetRelativePath(_config.LocalPath, conflictPath).Replace('\\', '/').Trim('/');
                MarkSelfModified(conflictRel);
                File.Copy(action.LocalFullPath, conflictPath, overwrite: false);
                _owner.Log?.Invoke($"Auto-Sync '{DisplayName()}': conflict on '{action.RelativePath}', kept copy '{Path.GetFileName(conflictPath)}'.");
            }

            var info = await client.GetFileInfoAsync(action.RemoteFullPath, includeSha256: _owner.VerifySha256, ct).ConfigureAwait(false);
            if (!info.Exists)
                throw new FileNotFoundException("Remote file disappeared during sync.", action.RemoteFullPath);

            string partPath = action.LocalFullPath + ".sbsyncpart";
            try
            {
                if (info.Size == 0)
                {
                    await File.WriteAllBytesAsync(partPath, [], ct).ConfigureAwait(false);
                }
                else
                {
                    await using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 256 * 1024, FileOptions.Asynchronous))
                    {
                        fs.SetLength(info.Size);
                        for (long offset = 0; offset < info.Size; offset += ChunkBytes)
                        {
                            long len = Math.Min(ChunkBytes, info.Size - offset);
                            var header = await client.PullRangeAsync(
                                action.RemoteFullPath,
                                offset,
                                len,
                                async (chunk, writePos, token) =>
                                {
                                    await RandomAccess.WriteAsync(fs.SafeFileHandle, chunk, writePos, token).ConfigureAwait(false);
                                },
                                ct,
                                allowCompression: _owner.UseCompression).ConfigureAwait(false);

                            if (!header.Success)
                                throw new IOException(header.Error ?? "Remote download chunk failed.");
                        }

                        if (_owner.VerifySha256 && !string.IsNullOrWhiteSpace(info.Sha256Hex))
                        {
                            fs.Position = 0;
                            byte[] hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
                            string actualHex = Convert.ToHexString(hash).ToLowerInvariant();
                            if (!actualHex.Equals(info.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                                throw new IOException("SHA-256 verification failed on downloaded sync file.");
                        }
                    }
                }

                try { File.SetLastWriteTimeUtc(partPath, info.LastModifiedUtc); } catch { }
                MarkSelfModified(action.RelativePath);
                File.Move(partPath, action.LocalFullPath, overwrite: true);
                try { File.SetLastWriteTimeUtc(action.LocalFullPath, info.LastModifiedUtc); } catch { }
            }
            catch
            {
                try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
                throw;
            }
        }

        private void DeleteLocalWithSoftTrash(string fullPath, string relPath)
        {
            if (!File.Exists(fullPath)) return;

            if (_config.UseSoftTrash)
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string trashDest = Path.Combine(_config.LocalPath, ".swiftbeam-trash", stamp, relPath.Replace('/', Path.DirectorySeparatorChar));
                string? trashParent = Path.GetDirectoryName(trashDest);
                if (!string.IsNullOrEmpty(trashParent))
                    Directory.CreateDirectory(trashParent);
                string uniqueDest = TransferManager.ResolveUniqueLocalPath(trashDest);
                File.Move(fullPath, uniqueDest, overwrite: false);
                return;
            }

            FileShareServer.DeleteFileOrDirectoryRobust(fullPath);
        }

        private void MarkSelfModified(string relPath)
        {
            _selfModifiedPaths[relPath.Replace('\\', '/').Trim('/')] = DateTime.UtcNow.AddSeconds(2);
        }

        private async Task DisconnectClientAsync()
        {
            if (_client is not null)
            {
                try { await _client.DisposeAsync().ConfigureAwait(false); } catch { }
                _client = null;
            }
        }

        private void PublishStatus(AutoSyncRunState state, string text, int trackedCount, DateTime? lastSyncedUtc, string? lastError)
        {
            CurrentStatus = new AutoSyncPairStatus(
                _config.Id,
                state,
                text,
                trackedCount,
                Volatile.Read(ref _sentCount),
                Volatile.Read(ref _receivedCount),
                Volatile.Read(ref _deletedCount),
                lastSyncedUtc,
                lastError);
            _owner.StatusChanged?.Invoke(CurrentStatus);
        }

        private string DisplayName() => string.IsNullOrWhiteSpace(_config.Name) ? Path.GetFileName(_config.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : _config.Name;
        private string PeerDisplay() => string.IsNullOrWhiteSpace(_config.PeerDeviceName) ? _config.PeerHost : _config.PeerDeviceName;

        public async ValueTask DisposeAsync()
        {
            if (_debounceTimer is not null)
            {
                await _debounceTimer.DisposeAsync().ConfigureAwait(false);
                _debounceTimer = null;
            }
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
            if (_cts is not null)
            {
                await _cts.CancelAsync().ConfigureAwait(false);
                if (_loopTask is not null)
                {
                    try { await _loopTask.ConfigureAwait(false); } catch { }
                }
                _cts.Dispose();
                _cts = null;
            }
            await DisconnectClientAsync().ConfigureAwait(false);
            _syncLock.Dispose();
            _wakeSignal.Dispose();
        }
    }
}
