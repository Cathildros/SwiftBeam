using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SwiftBeam.Core.Protocol;
using SwiftBeam.Core.Settings;

namespace SwiftBeam.Core.Client;

public sealed record PersistedTransferItem(
    TransferDirection Direction,
    string RemoteRelativePath,
    string LocalDestinationPath,
    long TotalBytes,
    DateTime RemoteLastModifiedUtc,
    int ChunkSize);

public sealed record TransferTelemetrySnapshot(
    long TotalBytesQueued,
    long TotalBytesTransferred,
    double OverallProgressPercent,
    double AggregateBytesPerSec,
    TimeSpan? EstimatedTimeRemaining,
    int ActiveWorkers,
    int QueuedOrActiveFiles,
    int CompletedFiles,
    int FailedFiles);

public sealed class TransferManager : IAsyncDisposable
{
    private readonly List<TransferItem> _items = [];
    private readonly object _queueLock = new();
    private readonly SemaphoreSlim _workSignal = new(0, int.MaxValue);
    private readonly ConcurrentQueue<(long TimestampTicks, long Bytes)> _speedSamples = new();

    private ConnectionTarget? _target;
    private CancellationTokenSource? _cts;
    private readonly List<Task> _workerTasks = [];
    private Task? _telemetryTask;

    private int _maxConcurrency = 6;
    private int _chunkSizeBytes = 16 * 1024 * 1024;
    private bool _verifySha256 = true;
    private bool _useSmartCompression;
    private FileConflictPolicy _conflictPolicy = FileConflictPolicy.SkipIdentical;
    private long _maxBytesPerSec; // 0 = Unlimited
    private readonly object _throttleLock = new();
    private double _availableTokens;
    private long _lastTokenTicks = Stopwatch.GetTimestamp();
    private int _maxReconnectAttempts = 60;
    private int _activeWorkers;

    public int MaxConcurrency
    {
        get => Volatile.Read(ref _maxConcurrency);
        set
        {
            int clamped = Math.Clamp(value, 1, 16);
            int prev = Interlocked.Exchange(ref _maxConcurrency, clamped);
            if (clamped > prev && _cts is { IsCancellationRequested: false })
            {
                EnsureWorkerCount();
            }
            SignalWork(clamped);
        }
    }

    public int ChunkSizeBytes
    {
        get => Volatile.Read(ref _chunkSizeBytes);
        set => Interlocked.Exchange(ref _chunkSizeBytes, Math.Clamp(value, 256 * 1024, 64 * 1024 * 1024));
    }

    public bool VerifySha256
    {
        get => _verifySha256;
        set => _verifySha256 = value;
    }

    public bool UseSmartCompression
    {
        get => _useSmartCompression;
        set => _useSmartCompression = value;
    }

    public FileConflictPolicy ConflictPolicy
    {
        get => _conflictPolicy;
        set => _conflictPolicy = value;
    }

    public bool SkipExistingIdentical
    {
        get => _conflictPolicy == FileConflictPolicy.SkipIdentical;
        set => _conflictPolicy = value ? FileConflictPolicy.SkipIdentical : FileConflictPolicy.Overwrite;
    }

    public int MaxSpeedMBps
    {
        get => (int)(Volatile.Read(ref _maxBytesPerSec) / (1024 * 1024));
        set
        {
            long bytesPerSec = value <= 0 ? 0 : (long)Math.Clamp(value, 1, 10_000) * 1024 * 1024;
            Volatile.Write(ref _maxBytesPerSec, bytesPerSec);
            lock (_throttleLock)
            {
                _availableTokens = bytesPerSec * 0.25;
                _lastTokenTicks = Stopwatch.GetTimestamp();
            }
        }
    }

    public int MaxReconnectAttempts
    {
        get => _maxReconnectAttempts;
        set => _maxReconnectAttempts = Math.Max(1, value);
    }

    public event Action<TransferItem>? ItemAdded;
    public event Action<TransferItem>? ItemUpdated;
    public event Action<TransferItem>? ItemRemoved;
    public event Action<TransferItem>? QueueReordered;
    public event Action<TransferTelemetrySnapshot>? TelemetryUpdated;
    public event Action<string>? Log;

    public IReadOnlyList<TransferItem> GetItemsSnapshot()
    {
        lock (_queueLock) return _items.ToList();
    }

    public void ConfigureTarget(ConnectionTarget target)
    {
        _target = target;
        StartEngineIfNeeded();
    }

    public TransferItem EnqueueFile(RemoteEntry entry, string localDestinationPath)
    {
        string targetPath = _conflictPolicy == FileConflictPolicy.RenameBoth
            ? ResolveUniqueLocalPath(localDestinationPath)
            : localDestinationPath;

        var item = new TransferItem(
            entry.RelativePath,
            targetPath,
            entry.Size,
            entry.LastModifiedUtc,
            ChunkSizeBytes);

        lock (_queueLock)
        {
            // Avoid duplicating an already active transfer to the exact same destination
            var existing = _items.FirstOrDefault(x =>
                x.LocalDestinationPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase) &&
                x.Status is TransferStatus.Queued or TransferStatus.Transferring or TransferStatus.Reconnecting or TransferStatus.Paused or TransferStatus.Verifying);
            if (existing is not null)
            {
                if (existing.Status == TransferStatus.Paused)
                {
                    existing.Resume();
                    SignalWork(MaxConcurrency);
                }
                return existing;
            }

            _items.Add(item);
        }

        ItemAdded?.Invoke(item);
        StartEngineIfNeeded();
        SignalWork(MaxConcurrency);
        return item;
    }

    public static string ResolveUniqueLocalPath(string desiredPath)
    {
        if (!File.Exists(desiredPath))
            return desiredPath;

        string dir = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(desiredPath);
        string ext = Path.GetExtension(desiredPath);

        for (int i = 1; i < 10_000; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
        return Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{ext}");
    }

    public TransferItem EnqueuePushFile(string localSourcePath, string remoteDestinationPath)
    {
        var fi = new FileInfo(localSourcePath);
        var item = new TransferItem(
            remoteDestinationPath,
            localSourcePath,
            fi.Length,
            fi.LastWriteTimeUtc,
            ChunkSizeBytes,
            TransferDirection.Push);

        lock (_queueLock)
        {
            _items.Add(item);
        }

        ItemAdded?.Invoke(item);
        StartEngineIfNeeded();
        SignalWork(MaxConcurrency);
        return item;
    }

    public async Task<IReadOnlyList<TransferItem>> EnqueuePushPathsAsync(
        TransferClient browseClient,
        IEnumerable<string> localPaths,
        string remoteTargetFolder,
        CancellationToken ct = default)
    {
        var added = new List<TransferItem>();
        string remoteBase = (remoteTargetFolder ?? string.Empty).Replace('\\', '/');
        if (remoteBase.Length > 1 && !(remoteBase.Length == 3 && remoteBase[1] == ':'))
            remoteBase = remoteBase.TrimEnd('/');

        string CombineRemote(string basePath, string relChild)
        {
            string child = relChild.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrEmpty(basePath)) return child;
            return basePath.EndsWith('/') ? basePath + child : basePath + "/" + child;
        }

        foreach (string localPath in localPaths)
        {
            if (File.Exists(localPath))
            {
                string remoteDest = CombineRemote(remoteBase, Path.GetFileName(localPath));
                added.Add(EnqueuePushFile(localPath, remoteDest));
            }
            else if (Directory.Exists(localPath))
            {
                string folderName = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                string remoteDirRoot = CombineRemote(remoteBase, folderName);
                var mkRootResp = await browseClient.CreateDirectoryAsync(remoteDirRoot, ct).ConfigureAwait(false);
                if (!mkRootResp.Success)
                    throw new IOException(mkRootResp.Error ?? $"Could not create remote folder '{remoteDirRoot}'.");

                var stack = new Stack<DirectoryInfo>();
                stack.Push(new DirectoryInfo(localPath));
                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    IEnumerable<FileSystemInfo> items;
                    try { items = current.EnumerateFileSystemInfos(); }
                    catch { continue; }

                    foreach (var entry in items)
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        string rel = Path.GetRelativePath(localPath, entry.FullName).Replace('\\', '/');
                        string remoteEntryPath = CombineRemote(remoteDirRoot, rel);
                        if (entry is DirectoryInfo subDir)
                        {
                            var mkSubResp = await browseClient.CreateDirectoryAsync(remoteEntryPath, ct).ConfigureAwait(false);
                            if (!mkSubResp.Success)
                                throw new IOException(mkSubResp.Error ?? $"Could not create remote subfolder '{remoteEntryPath}'.");
                            stack.Push(subDir);
                        }
                        else if (entry is FileInfo fi)
                        {
                            added.Add(EnqueuePushFile(fi.FullName, remoteEntryPath));
                        }
                    }
                }
            }
        }

        return added;
    }

    public async Task<IReadOnlyList<TransferItem>> EnqueueEntriesAsync(
        TransferClient browseClient,
        IEnumerable<RemoteEntry> selectedEntries,
        string localRootFolder,
        string remoteBaseFolder,
        CancellationToken ct = default,
        bool includeHidden = false)
    {
        var added = new List<TransferItem>();
        // Preserve leading '/' for Linux full-filesystem paths (e.g. "/home/user/Downloads")
        string normalizedBase = (remoteBaseFolder ?? string.Empty).Replace('\\', '/');
        if (normalizedBase.Length > 1 && !(normalizedBase.Length == 3 && normalizedBase[1] == ':'))
            normalizedBase = normalizedBase.TrimEnd('/');

        foreach (var entry in selectedEntries)
        {
            if (!entry.IsDirectory)
            {
                string localPath = MapRemoteToLocalPath(entry.RelativePath, normalizedBase, localRootFolder);
                added.Add(EnqueueFile(entry, localPath));
            }
            else
            {
                // Recursively list folder contents from remote peer
                var listing = await browseClient.ListDirectoryAsync(entry.RelativePath, recursive: true, includeHidden: includeHidden, ct: ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(listing.Error))
                {
                    Log?.Invoke($"Error listing '{entry.RelativePath}': {listing.Error}");
                    continue;
                }

                // Ensure the folder itself exists even if empty
                string folderLocal = MapRemoteToLocalPath(entry.RelativePath, normalizedBase, localRootFolder);
                Directory.CreateDirectory(folderLocal);

                foreach (var child in listing.Entries)
                {
                    string childLocal = MapRemoteToLocalPath(child.RelativePath, normalizedBase, localRootFolder);
                    if (child.IsDirectory)
                    {
                        Directory.CreateDirectory(childLocal);
                    }
                    else
                    {
                        added.Add(EnqueueFile(child, childLocal));
                    }
                }
            }
        }

        return added;
    }

    public static string MapRemoteToLocalPath(string remoteRelativePath, string remoteBaseFolder, string localRootFolder)
    {
        string rel = (remoteRelativePath ?? string.Empty).Replace('\\', '/');
        string baseNorm = (remoteBaseFolder ?? string.Empty).Replace('\\', '/');

        // Trim trailing slash unless root "/" or "C:/"
        if (baseNorm.Length > 1 && !(baseNorm.Length == 3 && baseNorm[1] == ':'))
            baseNorm = baseNorm.TrimEnd('/');

        if (!string.IsNullOrEmpty(baseNorm))
        {
            string prefix = baseNorm.EndsWith('/') ? baseNorm : baseNorm + "/";
            if (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                rel = rel[prefix.Length..];
            else if (rel.Equals(baseNorm, StringComparison.OrdinalIgnoreCase))
                rel = Path.GetFileName(rel.TrimEnd('/'));
        }

        rel = rel.Trim('/');
        // Strip Windows drive prefix like "C:/..." if still present (e.g., pulling from Drives root)
        if (rel.Length >= 2 && rel[1] == ':')
            rel = rel[2..].TrimStart('/');

        char[] invalidChars = Path.GetInvalidFileNameChars();
        string[] segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            string seg = segments[i];
            if (seg is "." or "..") seg = "_";
            foreach (char bad in invalidChars)
                seg = seg.Replace(bad, '_');
            segments[i] = seg;
        }

        string rootFull = Path.GetFullPath(localRootFolder);
        string rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        string combined = segments.Length == 0 ? rootFull : Path.Combine(rootFull, Path.Combine(segments));
        string resolved = Path.GetFullPath(combined);

        if (!resolved.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
            !resolved.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsafe target path: {remoteRelativePath}");

        return resolved;
    }

    public bool MoveToTop(TransferItem item)
    {
        lock (_queueLock)
        {
            int idx = _items.IndexOf(item);
            if (idx < 0 || item.Status is not (TransferStatus.Queued or TransferStatus.Paused))
                return false;

            if (idx > 0)
            {
                _items.RemoveAt(idx);
                _items.Insert(0, item);
            }
        }

        QueueReordered?.Invoke(item);
        SignalWork(MaxConcurrency);
        return true;
    }

    public int RetryAllFailed()
    {
        List<TransferItem> retried = [];
        lock (_queueLock)
        {
            foreach (var item in _items)
            {
                if (item.Status == TransferStatus.Failed)
                {
                    item.Resume();
                    retried.Add(item);
                }
            }
        }

        foreach (var item in retried)
            ItemUpdated?.Invoke(item);

        if (retried.Count > 0)
        {
            StartEngineIfNeeded();
            SignalWork(MaxConcurrency);
        }

        return retried.Count;
    }

    public void PauseItem(TransferItem item)
    {
        item.Pause();
        ItemUpdated?.Invoke(item);
    }

    public void ResumeItem(TransferItem item)
    {
        item.Resume();
        ItemUpdated?.Invoke(item);
        SignalWork(MaxConcurrency);
    }

    public void CancelItem(TransferItem item, bool deletePartialFiles = true)
    {
        item.Cancel(deletePartialFiles);
        bool canDisposeNow;
        lock (_queueLock)
        {
            _items.Remove(item);
            canDisposeNow = item.ActiveStreams == 0;
        }
        if (canDisposeNow)
            item.Dispose();

        ItemRemoved?.Invoke(item);
    }

    public void PauseAll()
    {
        lock (_queueLock)
        {
            foreach (var item in _items)
                item.Pause();
        }
    }

    public void ResumeAll()
    {
        lock (_queueLock)
        {
            foreach (var item in _items)
                item.Resume();
        }
        SignalWork(MaxConcurrency);
    }

    public void ClearCompleted()
    {
        lock (_queueLock)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].Status is TransferStatus.Completed or TransferStatus.Skipped or TransferStatus.Cancelled)
                {
                    _items[i].Dispose();
                    _items.RemoveAt(i);
                }
            }
        }
    }

    public void ClearAll(bool deletePartialFiles = true)
    {
        List<TransferItem> snapshot;
        lock (_queueLock)
        {
            snapshot = [.. _items];
            _items.Clear();
        }

        foreach (var item in snapshot)
        {
            if (item.Status is not (TransferStatus.Completed or TransferStatus.Skipped or TransferStatus.Cancelled))
                item.Cancel(deletePartialFiles);

            if (item.ActiveStreams == 0)
                item.Dispose();

            ItemRemoved?.Invoke(item);
        }
    }

    public void CancelAll(bool deletePartialFiles = true)
    {
        var toCancel = new List<TransferItem>();
        lock (_queueLock)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var item = _items[i];
                if (item.Status is not (TransferStatus.Completed or TransferStatus.Skipped))
                {
                    toCancel.Add(item);
                    _items.RemoveAt(i);
                }
            }
        }

        foreach (var item in toCancel)
        {
            if (item.Status != TransferStatus.Cancelled)
                item.Cancel(deletePartialFiles);

            if (item.ActiveStreams == 0)
                item.Dispose();

            ItemRemoved?.Invoke(item);
        }
    }

    public async Task WaitForAllAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            bool anyActive;
            lock (_queueLock)
            {
                anyActive = _items.Any(x => x.Status is TransferStatus.Queued or TransferStatus.Transferring or TransferStatus.Reconnecting or TransferStatus.Verifying);
            }
            if (!anyActive) return;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    private void StartEngineIfNeeded()
    {
        lock (_queueLock)
        {
            if (_cts is null || _cts.IsCancellationRequested)
            {
                _cts = new CancellationTokenSource();
                _telemetryTask = Task.Run(() => TelemetryLoopAsync(_cts.Token));
            }
        }
        EnsureWorkerCount();
    }

    private void EnsureWorkerCount()
    {
        lock (_queueLock)
        {
            if (_cts is null || _cts.IsCancellationRequested) return;
            _workerTasks.RemoveAll(t => t.IsCompleted);
            int targetCount = MaxConcurrency;
            while (_workerTasks.Count < targetCount)
            {
                int workerId = _workerTasks.Count + 1;
                _workerTasks.Add(Task.Run(() => WorkerLoopAsync(workerId, _cts.Token)));
            }
        }
    }

    private void SignalWork(int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            try { _workSignal.Release(); } catch (SemaphoreFullException) { break; }
        }
    }

    private async Task WorkerLoopAsync(int workerId, CancellationToken ct)
    {
        await using var client = new TransferClient();
        ConnectionTarget? connectedTarget = null;
        int reconnectAttempt = 0;

        while (!ct.IsCancellationRequested)
        {
            // Scale down gracefully if user lowered MaxConcurrency
            if (workerId > MaxConcurrency)
                break;

            var work = TryDequeueWork();
            if (work is null)
            {
                // Idle: disconnect warm socket if nothing left for a bit
                try
                {
                    bool signaled = await _workSignal.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    if (!signaled && client.IsConnected)
                    {
                        await client.DisconnectAsync().ConfigureAwait(false);
                        connectedTarget = null;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            var (item, chunkIndex, offset, length, isFinalizeOnly) = work.Value;
            Interlocked.Increment(ref _activeWorkers);

            try
            {
                // Ensure connected to current target
                var target = _target;
                if (target is null)
                {
                    item.MarkFailed("No peer target configured.");
                    continue;
                }

                if (!client.IsConnected || connectedTarget != target)
                {
                    await ConnectWithRetryAsync(client, target, item, () => reconnectAttempt, v => reconnectAttempt = v, ct).ConfigureAwait(false);
                    connectedTarget = target;
                    reconnectAttempt = 0;
                }

                if (isFinalizeOnly)
                {
                    await FinalizeItemAsync(client, item, ct).ConfigureAwait(false);
                }
                else
                {
                    await ExecuteChunkAsync(client, item, chunkIndex, offset, length, ct).ConfigureAwait(false);
                    // Trigger another pass so a worker can immediately finalize if that was the last chunk
                    SignalWork(1);
                }
            }
            catch (OperationCanceledException)
            {
                if (!isFinalizeOnly)
                    item.ReleaseChunkOnFailure(chunkIndex, 0, markReconnecting: false, null);
                else
                    item.AbortFinalizeForRetry(null);
                break;
            }
            catch (UnauthorizedAccessException authEx)
            {
                if (!isFinalizeOnly)
                    item.ReleaseChunkOnFailure(chunkIndex, 0, markReconnecting: false, null);
                else
                    item.AbortFinalizeForRetry(null);
                item.MarkFailed(authEx.Message);
                Log?.Invoke($"Auth failed: {authEx.Message}");
                await client.DisconnectAsync().ConfigureAwait(false);
                connectedTarget = null;
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or System.Net.Sockets.SocketException)
            {
                // Network drop! Release chunk back to Pending and auto-reconnect cleanly.
                await client.DisconnectAsync().ConfigureAwait(false);
                connectedTarget = null;

                reconnectAttempt++;
                if (reconnectAttempt > MaxReconnectAttempts)
                {
                    if (!isFinalizeOnly)
                        item.ReleaseChunkOnFailure(chunkIndex, 0, markReconnecting: false, null);
                    else
                        item.AbortFinalizeForRetry(null);
                    item.MarkFailed($"Connection lost after {MaxReconnectAttempts} retries: {ex.Message}");
                    Log?.Invoke($"Transfer failed ({item.FileName}): {ex.Message}");
                }
                else
                {
                    int backoffMs = Math.Min(5000, 500 * (1 << Math.Min(reconnectAttempt, 4)));
                    string detail = $"Connection dropped — reconnecting ({reconnectAttempt}/{MaxReconnectAttempts})...";
                    if (!isFinalizeOnly)
                        item.ReleaseChunkOnFailure(chunkIndex, 0, markReconnecting: true, detail);
                    else
                        item.AbortFinalizeForRetry(detail);

                    Log?.Invoke($"Worker #{workerId}: {detail} ({ex.Message})");
                    ItemUpdated?.Invoke(item);
                    try { await Task.Delay(backoffMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                    SignalWork(1);
                }
            }
            catch (Exception ex)
            {
                if (ex is InvalidDataException)
                    await client.DisconnectAsync().ConfigureAwait(false);
                if (!isFinalizeOnly)
                    item.ReleaseChunkOnFailure(chunkIndex, 0, markReconnecting: false, null);
                else
                    item.AbortFinalizeForRetry(null);
                item.MarkFailed(ex.Message);
                Log?.Invoke($"Error transferring {item.FileName}: {ex.Message}");
                ItemUpdated?.Invoke(item);
            }
            finally
            {
                Interlocked.Decrement(ref _activeWorkers);
            }
        }
    }

    private async Task ConnectWithRetryAsync(
        TransferClient client,
        ConnectionTarget target,
        TransferItem item,
        Func<int> getAttempt,
        Action<int> setAttempt,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await client.ConnectAsync(target, ct).ConfigureAwait(false);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                int attempt = getAttempt() + 1;
                setAttempt(attempt);
                if (attempt > MaxReconnectAttempts)
                    throw new IOException($"Could not connect to {target.Host}:{target.Port}: {ex.Message}", ex);

                item.Status = TransferStatus.Reconnecting;
                item.StatusDetail = $"Reconnecting ({attempt}/{MaxReconnectAttempts})...";
                ItemUpdated?.Invoke(item);

                int backoffMs = Math.Min(5000, 500 * (1 << Math.Min(attempt, 4)));
                await Task.Delay(backoffMs, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteChunkAsync(
        TransferClient client,
        TransferItem item,
        int chunkIndex,
        long offset,
        long length,
        CancellationToken ct)
    {
        long bytesThisChunk = 0;
        var sw = Stopwatch.StartNew();

        try
        {
            if (item.Direction == TransferDirection.Push)
            {
                var ack = await client.PushRangeAsync(
                    item.RemoteRelativePath,
                    item.LocalDestinationPath,
                    item.TotalBytes,
                    item.RemoteLastModifiedUtc,
                    offset,
                    length,
                    sent =>
                    {
                        if (item.Status is TransferStatus.Paused or TransferStatus.Cancelled)
                            throw new OperationCanceledException("Transfer paused or cancelled.");
                        ThrottleSyncIfNeeded(sent, ct);
                        long totalSent = Interlocked.Add(ref bytesThisChunk, sent);
                        item.ReportChunkBytesProgress(sent);
                        _speedSamples.Enqueue((Stopwatch.GetTimestamp(), sent));
                        if (sw.ElapsedMilliseconds >= 250)
                            item.CurrentSpeedBytesPerSec = totalSent / sw.Elapsed.TotalSeconds;
                    },
                    ct,
                    allowCompression: _useSmartCompression).ConfigureAwait(false);

                if (!ack.Success)
                    throw new InvalidOperationException(ack.Error ?? "Remote peer rejected uploaded chunk.");
            }
            else
            {
                var handle = item.GetOrOpenPartHandle();

                var header = await client.PullRangeAsync(
                    item.RemoteRelativePath,
                    offset,
                    length,
                    async (data, writeOffset, token) =>
                    {
                        await RandomAccess.WriteAsync(handle, data, writeOffset, token).ConfigureAwait(false);
                        await ThrottleAsyncIfNeeded(data.Length, token).ConfigureAwait(false);
                    },
                    ct,
                    onNetworkBytesProgress: n =>
                    {
                        if (item.Status is TransferStatus.Paused or TransferStatus.Cancelled)
                            throw new OperationCanceledException("Transfer paused or cancelled.");

                        long totalRead = Interlocked.Add(ref bytesThisChunk, n);
                        item.ReportChunkBytesProgress(n);
                        _speedSamples.Enqueue((Stopwatch.GetTimestamp(), n));
                        if (sw.ElapsedMilliseconds >= 250)
                            item.CurrentSpeedBytesPerSec = totalRead / sw.Elapsed.TotalSeconds;
                    },
                    allowCompression: _useSmartCompression).ConfigureAwait(false);

                if (!header.Success)
                    throw new InvalidOperationException(header.Error ?? "Remote peer refused file pull.");
            }

            long finalChunkBytes = Interlocked.Read(ref bytesThisChunk);
            double elapsedSec = Math.Max(0.05, sw.Elapsed.TotalSeconds);
            item.CurrentSpeedBytesPerSec = finalChunkBytes / elapsedSec;
            item.MarkChunkCompleted(chunkIndex, finalChunkBytes);
            ItemUpdated?.Invoke(item);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Paused or cancelled mid-chunk: disconnect stream so unread chunk bytes don't corrupt future frames!
            await client.DisconnectAsync().ConfigureAwait(false);
            long reported = Interlocked.Read(ref bytesThisChunk);
            item.ReleaseChunkOnFailure(chunkIndex, reported, markReconnecting: false, null);
            if (item.Status != TransferStatus.Cancelled)
                ItemUpdated?.Invoke(item);
        }
        catch
        {
            // Roll back reported bytes for this incomplete chunk so caller's catch block handles retry cleanly
            item.ReportChunkBytesProgress(-Interlocked.Read(ref bytesThisChunk));
            throw;
        }
    }

    private async Task FinalizeItemAsync(TransferClient client, TransferItem item, CancellationToken ct)
    {
        try
        {
            if (item.Direction == TransferDirection.Push)
            {
                string? expectedHash = null;
                if (VerifySha256 && item.TotalBytes > 0)
                {
                    item.Status = TransferStatus.Verifying;
                    item.StatusDetail = "Verifying SHA-256 on remote...";
                    ItemUpdated?.Invoke(item);

                    await using var fs = new FileStream(item.LocalDestinationPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
                    byte[] hashBytes = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
                    expectedHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
                }

                var ack = await client.FinalizePushAsync(
                    item.RemoteRelativePath,
                    item.TotalBytes,
                    item.RemoteLastModifiedUtc,
                    expectedHash,
                    ct).ConfigureAwait(false);

                if (!ack.Success)
                    throw new InvalidOperationException(ack.Error ?? "Remote finalize failed.");

                item.MarkCompleted();
                Log?.Invoke($"Sent: {item.FileName} ({FormatBytes(item.TotalBytes)})");
                ItemUpdated?.Invoke(item);
                return;
            }

            if (item.TotalBytes == 0)
            {
                string? dir = Path.GetDirectoryName(item.LocalDestinationPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(item.LocalDestinationPath, Array.Empty<byte>(), ct).ConfigureAwait(false);
                try { File.SetLastWriteTimeUtc(item.LocalDestinationPath, item.RemoteLastModifiedUtc); } catch { }
                item.MarkCompleted();
                ItemUpdated?.Invoke(item);
                return;
            }

            if (VerifySha256)
            {
                item.Status = TransferStatus.Verifying;
                item.StatusDetail = "Verifying SHA-256...";
                ItemUpdated?.Invoke(item);

                var remoteInfoTask = client.GetFileInfoAsync(item.RemoteRelativePath, includeSha256: true, ct);
                string localHash;
                await using (var fs = new FileStream(item.PartFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous))
                {
                    byte[] hashBytes = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
                    localHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
                }

                var remoteInfo = await remoteInfoTask.ConfigureAwait(false);
                if (!remoteInfo.Exists || string.IsNullOrEmpty(remoteInfo.Sha256Hex))
                    throw new InvalidOperationException(remoteInfo.Error ?? "Remote file disappeared before verification.");

                if (!localHash.Equals(remoteInfo.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                {
                    item.ResetAllChunks();
                    throw new InvalidDataException("SHA-256 checksum mismatch (source file may have changed during transfer).");
                }
            }

            File.Move(item.PartFilePath, item.LocalDestinationPath, overwrite: true);
            try { File.SetLastWriteTimeUtc(item.LocalDestinationPath, item.RemoteLastModifiedUtc); } catch { }

            item.MarkCompleted();
            Log?.Invoke($"Completed: {item.FileName} ({FormatBytes(item.TotalBytes)})");
            ItemUpdated?.Invoke(item);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or System.Net.Sockets.SocketException)
        {
            // Network drop during verification; reset finalizing flag so next worker re-verifies cleanly
            item.AbortFinalizeForRetry("Connection dropped during verification — retrying...");
            throw;
        }
    }

    private (TransferItem Item, int ChunkIndex, long Offset, long Length, bool IsFinalizeOnly)? TryDequeueWork()
    {
        lock (_queueLock)
        {
            foreach (var item in _items)
            {
                if (item.Status is TransferStatus.Paused or TransferStatus.Cancelled or TransferStatus.Completed or TransferStatus.Skipped or TransferStatus.Failed)
                    continue;

                if (item.TryInitializeAndCheckSkip(SkipExistingIdentical))
                {
                    ItemUpdated?.Invoke(item);
                    continue;
                }

                if (item.TryClaimNextChunk(out int chunkIdx, out long offset, out long len))
                {
                    return (item, chunkIdx, offset, len, false);
                }

                if (item.TryBeginFinalize())
                {
                    return (item, -1, 0, 0, true);
                }
            }
        }
        return null;
    }

    private async Task TelemetryLoopAsync(CancellationToken ct)
    {
        long windowTicks = Stopwatch.Frequency * 10; // 10-second rolling window for rock-steady speed & ETA
        long burstStartTicks = 0;
        double smoothedSpeed = 0;
        double? smoothedEtaSec = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            long now = Stopwatch.GetTimestamp();
            long cutoff = now - windowTicks;
            while (_speedSamples.TryPeek(out var sample) && sample.TimestampTicks < cutoff)
                _speedSamples.TryDequeue(out _);

            long windowBytes = 0;
            foreach (var sample in _speedSamples)
                windowBytes += sample.Bytes;

            long totalQueued = 0;
            long totalTransferred = 0;
            int activeFiles = 0;
            int completedFiles = 0;
            int failedFiles = 0;

            lock (_queueLock)
            {
                foreach (var item in _items)
                {
                    if (item.Status == TransferStatus.Cancelled) continue;
                    totalQueued += item.TotalBytes;
                    totalTransferred += item.BytesTransferred;

                    if (item.Status is TransferStatus.Queued or TransferStatus.Transferring or TransferStatus.Reconnecting or TransferStatus.Verifying)
                        activeFiles++;
                    else if (item.Status is TransferStatus.Completed or TransferStatus.Skipped)
                        completedFiles++;
                    else if (item.Status == TransferStatus.Failed)
                        failedFiles++;
                }
            }

            double pct = totalQueued <= 0 ? (activeFiles == 0 && completedFiles > 0 ? 100.0 : 0.0) : Math.Clamp((double)totalTransferred / totalQueued * 100.0, 0.0, 100.0);
            long remainingBytes = Math.Max(0, totalQueued - totalTransferred);
            TimeSpan? eta = null;

            if (activeFiles == 0)
            {
                burstStartTicks = 0;
                smoothedSpeed = 0;
                smoothedEtaSec = null;
                while (_speedSamples.TryDequeue(out _)) { }
            }
            else
            {
                if (burstStartTicks == 0)
                    burstStartTicks = now;

                double elapsedBurstSec = (double)(now - burstStartTicks) / Stopwatch.Frequency;
                double effectiveWindowSec = Math.Clamp(elapsedBurstSec, 1.0, 10.0);
                double instantWindowSpeed = windowBytes / effectiveWindowSec;

                if (instantWindowSpeed > 0)
                {
                    smoothedSpeed = smoothedSpeed <= 0
                        ? instantWindowSpeed
                        : (smoothedSpeed * 0.88 + instantWindowSpeed * 0.12);
                }
                else
                {
                    smoothedSpeed *= 0.94;
                    if (smoothedSpeed < 1) smoothedSpeed = 0;
                }

                if (elapsedBurstSec >= 1.5 && smoothedSpeed > 1024 && remainingBytes > 0)
                {
                    double rawEtaSec = remainingBytes / smoothedSpeed;
                    smoothedEtaSec = smoothedEtaSec is null
                        ? rawEtaSec
                        : (smoothedEtaSec.Value * 0.85 + rawEtaSec * 0.15);

                    double displayEtaSec = smoothedEtaSec.Value >= 600
                        ? Math.Round(smoothedEtaSec.Value / 10.0) * 10.0
                        : Math.Round(smoothedEtaSec.Value);

                    eta = TimeSpan.FromSeconds(Math.Clamp(displayEtaSec, 1.0, 359999.0));
                }
            }

            TelemetryUpdated?.Invoke(new TransferTelemetrySnapshot(
                totalQueued,
                totalTransferred,
                pct,
                smoothedSpeed,
                eta,
                Volatile.Read(ref _activeWorkers),
                activeFiles,
                completedFiles,
                failedFiles));
        }
    }

    private void ThrottleSyncIfNeeded(int bytes, CancellationToken ct)
    {
        int delayMs = ComputeThrottleDelayMs(bytes);
        if (delayMs > 0)
            Task.Delay(delayMs, ct).GetAwaiter().GetResult();
    }

    private async ValueTask ThrottleAsyncIfNeeded(int bytes, CancellationToken ct)
    {
        int delayMs = ComputeThrottleDelayMs(bytes);
        if (delayMs > 0)
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
    }

    private int ComputeThrottleDelayMs(int bytes)
    {
        long limit = Volatile.Read(ref _maxBytesPerSec);
        if (limit <= 0) return 0;

        lock (_throttleLock)
        {
            long now = Stopwatch.GetTimestamp();
            double elapsedSec = (double)(now - _lastTokenTicks) / Stopwatch.Frequency;
            _lastTokenTicks = now;

            // Refill token bucket capped at 0.5s burst
            _availableTokens = Math.Min(limit * 0.5, _availableTokens + elapsedSec * limit);
            _availableTokens -= bytes;

            if (_availableTokens >= 0)
                return 0;

            double waitSec = -_availableTokens / limit;
            return (int)Math.Clamp(waitSec * 1000.0, 1, 2000);
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double val = bytes;
        int idx = 0;
        while (val >= 1024 && idx < units.Length - 1)
        {
            val /= 1024;
            idx++;
        }
        return idx == 0 ? $"{val:0} {units[idx]}" : $"{val:0.##} {units[idx]}";
    }

    public void SavePendingQueue(string filePath)
    {
        List<PersistedTransferItem> pending;
        lock (_queueLock)
        {
            pending = _items
                .Where(i => i.Status is TransferStatus.Queued or TransferStatus.Transferring or TransferStatus.Reconnecting
                    or TransferStatus.Verifying or TransferStatus.Paused or TransferStatus.Failed)
                .Select(i => new PersistedTransferItem(
                    i.Direction,
                    i.RemoteRelativePath,
                    i.LocalDestinationPath,
                    i.TotalBytes,
                    i.RemoteLastModifiedUtc,
                    i.ChunkSize))
                .ToList();
        }

        try
        {
            if (pending.Count == 0)
            {
                if (File.Exists(filePath)) File.Delete(filePath);
                return;
            }

            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string tmp = filePath + ".tmp";
            string json = JsonSerializer.Serialize(pending);
            File.WriteAllText(tmp, json);
            File.Move(tmp, filePath, overwrite: true);
        }
        catch { }
    }

    public int LoadPendingQueue(string filePath)
    {
        if (!File.Exists(filePath))
            return 0;

        List<PersistedTransferItem>? saved;
        try
        {
            saved = JsonSerializer.Deserialize<List<PersistedTransferItem>>(File.ReadAllText(filePath));
        }
        catch
        {
            return 0;
        }

        if (saved is null || saved.Count == 0)
            return 0;

        var loaded = new List<TransferItem>();
        lock (_queueLock)
        {
            foreach (var entry in saved)
            {
                if (entry.Direction == TransferDirection.Push && !File.Exists(entry.LocalDestinationPath))
                    continue;

                bool duplicate = _items.Any(x =>
                    x.Direction == entry.Direction &&
                    x.LocalDestinationPath.Equals(entry.LocalDestinationPath, StringComparison.OrdinalIgnoreCase));
                if (duplicate) continue;

                long size = entry.TotalBytes;
                DateTime modUtc = entry.RemoteLastModifiedUtc;
                if (entry.Direction == TransferDirection.Push)
                {
                    var fi = new FileInfo(entry.LocalDestinationPath);
                    size = fi.Length;
                    modUtc = fi.LastWriteTimeUtc;
                }

                int chunkSize = entry.ChunkSize >= 256 * 1024 ? entry.ChunkSize : ChunkSizeBytes;
                var item = new TransferItem(
                    entry.RemoteRelativePath,
                    entry.LocalDestinationPath,
                    size,
                    modUtc,
                    chunkSize,
                    entry.Direction);
                item.Pause();
                item.StatusDetail = "Saved from previous session — connect & click Resume";

                _items.Add(item);
                loaded.Add(item);
            }
        }

        foreach (var item in loaded)
            ItemAdded?.Invoke(item);

        return loaded.Count;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            SignalWork(32);
            try { await Task.WhenAll(_workerTasks).ConfigureAwait(false); } catch { }
            if (_telemetryTask is not null)
            {
                try { await _telemetryTask.ConfigureAwait(false); } catch { }
            }
            _cts.Dispose();
            _cts = null;
        }

        lock (_queueLock)
        {
            foreach (var item in _items)
                item.Dispose();
        }
        _workSignal.Dispose();
    }
}
