using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace SwiftBeam.Core.Client;

public enum TransferDirection
{
    Pull,
    Push
}

public enum TransferStatus
{
    Queued,
    Transferring,
    Reconnecting,
    Verifying,
    Paused,
    Completed,
    Skipped,
    Failed,
    Cancelled
}

internal enum ChunkState : byte
{
    Pending = 0,
    InProgress = 1,
    Completed = 2
}

internal sealed class TransferStateManifest
{
    public string RemotePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public long LastModifiedUtcTicks { get; set; }
    public int ChunkSize { get; set; }
    public int TotalChunks { get; set; }
    public bool[] CompletedChunks { get; set; } = [];
}

public sealed class TransferItem : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _sync = new();
    private ChunkState[] _chunks = [];
    private SafeFileHandle? _partHandle;
    private int _inFlightChunks;
    private long _completedChunkBytes;
    private long _inFlightBytes;
    private bool _finalizing;
    private bool _initialized;
    private bool _deletePartialsOnClose;
    private long _lastManifestSaveTicks;
    private static readonly long ManifestSaveIntervalTicks = TimeSpan.FromSeconds(1.5).Ticks;

    public string Id { get; } = Guid.NewGuid().ToString("N")[..10];
    public TransferDirection Direction { get; }
    public string RemoteRelativePath { get; }
    public string FileName { get; }
    public string LocalDestinationPath { get; }
    public string PartFilePath => LocalDestinationPath + ".sbpart";
    public string StateFilePath => LocalDestinationPath + ".sbstate.json";

    public long TotalBytes { get; private set; }
    public DateTime RemoteLastModifiedUtc { get; private set; }
    public int ChunkSize { get; }
    public int TotalChunks { get; private set; }
    public bool ResumedFromPartial { get; private set; }

    public TransferStatus Status { get; internal set; } = TransferStatus.Queued;
    public string? StatusDetail { get; internal set; }
    public double CurrentSpeedBytesPerSec { get; internal set; }
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    public DateTime? StartedUtc { get; internal set; }
    public DateTime? CompletedUtc { get; internal set; }

    public double AverageBytesPerSec =>
        TotalBytes / Math.Max(0.1, ((CompletedUtc ?? DateTime.UtcNow) - (StartedUtc ?? CreatedUtc)).TotalSeconds);

    public long BytesTransferred
    {
        get
        {
            lock (_sync)
            {
                return Math.Min(TotalBytes, _completedChunkBytes + _inFlightBytes);
            }
        }
    }

    public int ActiveStreams
    {
        get
        {
            lock (_sync) return _inFlightChunks;
        }
    }

    public double ProgressPercent =>
        TotalBytes <= 0
            ? (Status is TransferStatus.Completed or TransferStatus.Skipped ? 100.0 : 0.0)
            : Math.Clamp((double)BytesTransferred / TotalBytes * 100.0, 0.0, 100.0);

    public TransferItem(
        string remoteRelativePath,
        string localDestinationPath,
        long totalBytes,
        DateTime remoteLastModifiedUtc,
        int chunkSizeBytes,
        TransferDirection direction = TransferDirection.Pull)
    {
        Direction = direction;
        RemoteRelativePath = remoteRelativePath;
        FileName = Path.GetFileName(
            (direction == TransferDirection.Push ? localDestinationPath : remoteRelativePath)
            .Replace('/', Path.DirectorySeparatorChar));
        LocalDestinationPath = localDestinationPath;
        TotalBytes = Math.Max(0, totalBytes);
        RemoteLastModifiedUtc = remoteLastModifiedUtc;
        ChunkSize = Math.Max(64 * 1024, chunkSizeBytes);
        TotalChunks = TotalBytes == 0 ? 0 : (int)((TotalBytes + ChunkSize - 1) / ChunkSize);
        _chunks = new ChunkState[TotalChunks];
    }

    internal bool TryInitializeAndCheckSkip(bool skipExistingIdentical)
    {
        lock (_sync)
        {
            if (_initialized) return false;
            _initialized = true;

            if (Direction == TransferDirection.Push)
                return false;

            string? dir = Path.GetDirectoryName(LocalDestinationPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Skip if destination already exists with matching size and timestamp
            if (skipExistingIdentical && File.Exists(LocalDestinationPath) && !File.Exists(StateFilePath))
            {
                var existing = new FileInfo(LocalDestinationPath);
                if (existing.Length == TotalBytes &&
                    Math.Abs((existing.LastWriteTimeUtc - RemoteLastModifiedUtc).TotalSeconds) <= 2.5)
                {
                    _completedChunkBytes = TotalBytes;
                    for (int i = 0; i < _chunks.Length; i++) _chunks[i] = ChunkState.Completed;
                    Status = TransferStatus.Skipped;
                    StatusDetail = "Already up to date";
                    CompletedUtc = DateTime.UtcNow;
                    return true;
                }
            }

            // Try to resume existing partial transfer
            if (TotalChunks > 0 && File.Exists(PartFilePath) && File.Exists(StateFilePath))
            {
                try
                {
                    var manifest = JsonSerializer.Deserialize<TransferStateManifest>(File.ReadAllText(StateFilePath), JsonOptions);
                    if (manifest is not null &&
                        manifest.FileSize == TotalBytes &&
                        manifest.ChunkSize == ChunkSize &&
                        manifest.TotalChunks == TotalChunks &&
                        manifest.CompletedChunks.Length == TotalChunks &&
                        Math.Abs(manifest.LastModifiedUtcTicks - RemoteLastModifiedUtc.Ticks) <= TimeSpan.FromSeconds(2).Ticks)
                    {
                        long restoredBytes = 0;
                        for (int i = 0; i < TotalChunks; i++)
                        {
                            if (manifest.CompletedChunks[i])
                            {
                                _chunks[i] = ChunkState.Completed;
                                restoredBytes += GetChunkLength(i);
                            }
                        }
                        _completedChunkBytes = restoredBytes;
                        ResumedFromPartial = restoredBytes > 0;
                        if (ResumedFromPartial)
                            StatusDetail = $"Resumed at {ProgressPercent:F0}%";
                    }
                }
                catch
                {
                    // Corrupt manifest: start fresh
                }
            }

            return false;
        }
    }

    internal long GetChunkOffset(int chunkIndex) => (long)chunkIndex * ChunkSize;

    internal long GetChunkLength(int chunkIndex)
    {
        long offset = GetChunkOffset(chunkIndex);
        return Math.Min(ChunkSize, TotalBytes - offset);
    }

    internal bool TryClaimNextChunk(out int chunkIndex, out long offset, out long length)
    {
        lock (_sync)
        {
            chunkIndex = -1;
            offset = 0;
            length = 0;

            if (Status is TransferStatus.Paused or TransferStatus.Cancelled or TransferStatus.Completed or TransferStatus.Skipped or TransferStatus.Failed)
                return false;

            if (_finalizing)
                return false;

            for (int i = 0; i < _chunks.Length; i++)
            {
                if (_chunks[i] == ChunkState.Pending)
                {
                    _chunks[i] = ChunkState.InProgress;
                    _inFlightChunks++;
                    chunkIndex = i;
                    offset = GetChunkOffset(i);
                    length = GetChunkLength(i);
                    if (Status == TransferStatus.Queued || Status == TransferStatus.Reconnecting)
                    {
                        StartedUtc ??= DateTime.UtcNow;
                        Status = TransferStatus.Transferring;
                        StatusDetail = ResumedFromPartial ? "Resuming..." : null;
                    }
                    return true;
                }
            }

            return false;
        }
    }

    internal bool TryBeginFinalize()
    {
        lock (_sync)
        {
            if (_finalizing || _inFlightChunks > 0)
                return false;

            if (Status is TransferStatus.Paused or TransferStatus.Cancelled or TransferStatus.Completed or TransferStatus.Skipped or TransferStatus.Failed)
                return false;

            for (int i = 0; i < _chunks.Length; i++)
            {
                if (_chunks[i] != ChunkState.Completed)
                    return false;
            }

            _finalizing = true;
            CloseHandleLocked();
            return true;
        }
    }

    internal SafeFileHandle GetOrOpenPartHandle()
    {
        lock (_sync)
        {
            if (_partHandle is not null && !_partHandle.IsClosed && !_partHandle.IsInvalid)
                return _partHandle;

            string? dir = Path.GetDirectoryName(PartFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            _partHandle = File.OpenHandle(
                PartFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                FileOptions.Asynchronous);

            SparseFileHelper.TryMarkSparse(_partHandle);

            if (RandomAccess.GetLength(_partHandle) < TotalBytes)
            {
                RandomAccess.SetLength(_partHandle, TotalBytes);
            }

            return _partHandle;
        }
    }

    internal void ReportChunkBytesProgress(long deltaBytes)
    {
        lock (_sync)
        {
            _inFlightBytes += deltaBytes;
        }
    }

    internal void MarkChunkCompleted(int chunkIndex, long bytesReportedDuringChunk)
    {
        TransferStateManifest? snapshotToSave = null;
        bool shouldDeletePartials = false;
        lock (_sync)
        {
            _inFlightBytes = Math.Max(0, _inFlightBytes - bytesReportedDuringChunk);
            _chunks[chunkIndex] = ChunkState.Completed;
            _completedChunkBytes += GetChunkLength(chunkIndex);
            _inFlightChunks = Math.Max(0, _inFlightChunks - 1);

            if (_inFlightChunks == 0 && Status is TransferStatus.Paused or TransferStatus.Cancelled)
            {
                CloseHandleLocked();
                if (Status == TransferStatus.Cancelled && _deletePartialsOnClose && Direction == TransferDirection.Pull)
                    shouldDeletePartials = true;
            }

            if (Direction == TransferDirection.Pull && Status != TransferStatus.Cancelled)
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                if (nowTicks - _lastManifestSaveTicks >= ManifestSaveIntervalTicks || _completedChunkBytes >= TotalBytes)
                {
                    _lastManifestSaveTicks = nowTicks;
                    snapshotToSave = BuildManifestLocked();
                }
            }
        }

        if (shouldDeletePartials)
            TryDeletePartialFiles();
        else if (snapshotToSave is not null)
            SaveManifest(snapshotToSave);
    }

    internal void ReleaseChunkOnFailure(int chunkIndex, long bytesReportedDuringChunk, bool markReconnecting, string? detail)
    {
        bool shouldDeletePartials = false;
        lock (_sync)
        {
            _inFlightBytes = Math.Max(0, _inFlightBytes - bytesReportedDuringChunk);
            if (chunkIndex >= 0 && chunkIndex < _chunks.Length && _chunks[chunkIndex] == ChunkState.InProgress)
                _chunks[chunkIndex] = ChunkState.Pending;
            _inFlightChunks = Math.Max(0, _inFlightChunks - 1);

            if (markReconnecting && Status == TransferStatus.Transferring)
            {
                Status = TransferStatus.Reconnecting;
                StatusDetail = detail;
            }

            if (_inFlightChunks == 0 && Status is TransferStatus.Paused or TransferStatus.Cancelled or TransferStatus.Failed)
            {
                CloseHandleLocked();
                if (Status == TransferStatus.Cancelled && _deletePartialsOnClose && Direction == TransferDirection.Pull)
                    shouldDeletePartials = true;
            }
        }

        if (shouldDeletePartials)
            TryDeletePartialFiles();
    }

    internal void Pause()
    {
        TransferStateManifest? snapshotToSave = null;
        lock (_sync)
        {
            if (Status is TransferStatus.Queued or TransferStatus.Transferring or TransferStatus.Reconnecting)
            {
                Status = TransferStatus.Paused;
                StatusDetail = "Paused";
                CurrentSpeedBytesPerSec = 0;
                if (Direction == TransferDirection.Pull && _completedChunkBytes > 0)
                    snapshotToSave = BuildManifestLocked();
                if (_inFlightChunks == 0)
                    CloseHandleLocked();
            }
        }
        if (snapshotToSave is not null)
            SaveManifest(snapshotToSave);
    }

    internal void Resume()
    {
        lock (_sync)
        {
            if (Status is TransferStatus.Paused or TransferStatus.Failed)
            {
                _finalizing = false;
                Status = TransferStatus.Queued;
                StatusDetail = null;
            }
        }
    }

    internal void AbortFinalizeForRetry(string? detail)
    {
        lock (_sync)
        {
            _finalizing = false;
            if (Status is not (TransferStatus.Paused or TransferStatus.Cancelled))
            {
                Status = TransferStatus.Reconnecting;
                StatusDetail = detail;
            }
        }
    }

    internal void Cancel(bool deletePartialFiles)
    {
        bool deleteNow = false;
        lock (_sync)
        {
            if (Status is TransferStatus.Completed or TransferStatus.Skipped)
                return;
            Status = TransferStatus.Cancelled;
            StatusDetail = "Cancelled";
            CurrentSpeedBytesPerSec = 0;
            _deletePartialsOnClose = deletePartialFiles;
            if (_inFlightChunks == 0)
            {
                CloseHandleLocked();
                deleteNow = deletePartialFiles && Direction == TransferDirection.Pull;
            }
        }

        if (deleteNow)
        {
            TryDeletePartialFiles();
        }
    }

    internal void MarkCompleted()
    {
        lock (_sync)
        {
            CloseHandleLocked();
            _completedChunkBytes = TotalBytes;
            _inFlightBytes = 0;
            Status = TransferStatus.Completed;
            StatusDetail = null;
            CurrentSpeedBytesPerSec = 0;
            CompletedUtc = DateTime.UtcNow;
            _finalizing = false;
        }
        TryDeleteStateFile();
    }

    internal void MarkFailed(string error)
    {
        lock (_sync)
        {
            CloseHandleLocked();
            Status = TransferStatus.Failed;
            StatusDetail = error;
            CurrentSpeedBytesPerSec = 0;
            _finalizing = false;
        }
    }

    internal void ResetAllChunks()
    {
        lock (_sync)
        {
            CloseHandleLocked();
            for (int i = 0; i < _chunks.Length; i++) _chunks[i] = ChunkState.Pending;
            _completedChunkBytes = 0;
            _inFlightBytes = 0;
            _finalizing = false;
            ResumedFromPartial = false;
        }
        TryDeletePartialFiles();
    }

    private TransferStateManifest BuildManifestLocked()
    {
        var completed = new bool[_chunks.Length];
        for (int i = 0; i < _chunks.Length; i++)
            completed[i] = _chunks[i] == ChunkState.Completed;

        return new TransferStateManifest
        {
            RemotePath = RemoteRelativePath,
            FileSize = TotalBytes,
            LastModifiedUtcTicks = RemoteLastModifiedUtc.Ticks,
            ChunkSize = ChunkSize,
            TotalChunks = TotalChunks,
            CompletedChunks = completed
        };
    }

    private void SaveManifest(TransferStateManifest manifest)
    {
        try
        {
            string tmp = StateFilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, JsonOptions));
            File.Move(tmp, StateFilePath, overwrite: true);
        }
        catch
        {
            // Non-fatal; next chunk completion will rewrite manifest
        }
    }

    internal void TryDeleteStateFile()
    {
        try { if (File.Exists(StateFilePath)) File.Delete(StateFilePath); } catch { }
        try { if (File.Exists(StateFilePath + ".tmp")) File.Delete(StateFilePath + ".tmp"); } catch { }
    }

    internal void TryDeletePartialFiles()
    {
        TryDeleteStateFile();
        try { if (File.Exists(PartFilePath)) File.Delete(PartFilePath); } catch { }
    }

    private void CloseHandleLocked()
    {
        if (_partHandle is not null)
        {
            try { _partHandle.Dispose(); } catch { }
            _partHandle = null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            CloseHandleLocked();
        }
    }
}
