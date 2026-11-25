using System.Security.Cryptography;
using SwiftBeam.Core.Client;
using SwiftBeam.Core.Discovery;
using SwiftBeam.Core.Protocol;
using SwiftBeam.Core.Security;
using SwiftBeam.Core.Server;
using SwiftBeam.Core.Settings;
using SwiftBeam.Core.Sync;
using SwiftBeam.Core.Updates;
using Xunit;

namespace SwiftBeam.Core.Tests;

public sealed class TransferEngineTests : IDisposable
{
    private readonly string _rootTemp;
    private readonly string _shareDir;
    private readonly string _downloadDir;

    public TransferEngineTests()
    {
        _rootTemp = Path.Combine(Path.GetTempPath(), "SwiftBeamTests_" + Guid.NewGuid().ToString("N")[..8]);
        _shareDir = Path.Combine(_rootTemp, "share");
        _downloadDir = Path.Combine(_rootTemp, "downloads");
        Directory.CreateDirectory(_shareDir);
        Directory.CreateDirectory(_downloadDir);
    }

    private FileShareServer CreateServer(string pin = "123456", bool requirePin = true, bool allowUnencrypted = true, int port = 0)
    {
        var opts = new FileShareServerOptions
        {
            DeviceName = "TestHost",
            Port = port,
            SharedDirectory = _shareDir,
            RequirePin = requirePin,
            Pin = pin,
            AllowUnencrypted = allowUnencrypted
        };
        return new FileShareServer(opts);
    }

    private static string CreateRandomFile(string path, int sizeBytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] data = new byte[sizeBytes];
        RandomNumberGenerator.Fill(data);
        File.WriteAllBytes(path, data);
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    private static string ComputeSha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    [Fact]
    public async Task TlsPinAuth_RejectsWrongPin_AndAcceptsRightPin()
    {
        await using var server = CreateServer(pin: "482915", requirePin: true, allowUnencrypted: true);
        server.Start();

        await using var badClient = new TransferClient();
        var badTarget = new ConnectionTarget("127.0.0.1", server.ListeningPort, "000000", UseTls: true, "TestClient");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => badClient.ConnectAsync(badTarget));

        await using var goodClient = new TransferClient();
        // Formatted PIN with hyphen should normalize automatically
        var goodTarget = new ConnectionTarget("127.0.0.1", server.ListeningPort, "482-915", UseTls: true, "TestClient");
        var meta = await goodClient.ConnectAsync(goodTarget);

        Assert.Equal("TestHost", meta.DeviceName);
        Assert.True(meta.TlsActive);
        Assert.False(string.IsNullOrWhiteSpace(meta.CertFingerprint));

        // Verify path traversal is blocked
        var traversal = await goodClient.ListDirectoryAsync("../");
        Assert.False(string.IsNullOrEmpty(traversal.Error));
    }

    [Fact]
    public async Task ToggleableEncryption_PlaintextRespectsServerPolicy()
    {
        await using var strictServer = CreateServer(pin: "112233", allowUnencrypted: false);
        strictServer.Start();

        await using var client = new TransferClient();
        var plainTarget = new ConnectionTarget("127.0.0.1", strictServer.ListeningPort, "112233", UseTls: false, "TestClient");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(plainTarget));

        // Now allow unencrypted direct-cable mode
        strictServer.UpdateAllowUnencrypted(true);
        var meta = await client.ConnectAsync(plainTarget);
        Assert.False(meta.TlsActive);
        Assert.Equal("TestHost", meta.DeviceName);
    }

    [Fact]
    public async Task ParallelMultiFileAndRecursiveFolderPull_MatchesSha256()
    {
        var expectedHashes = new Dictionary<string, string>();
        expectedHashes["root.bin"] = CreateRandomFile(Path.Combine(_shareDir, "root.bin"), 350_000);
        expectedHashes["empty.txt"] = CreateRandomFile(Path.Combine(_shareDir, "empty.txt"), 0);

        for (int i = 0; i < 12; i++)
        {
            string rel = $"album/sub{i % 3}/file_{i}.dat";
            expectedHashes[rel] = CreateRandomFile(Path.Combine(_shareDir, rel.Replace('/', Path.DirectorySeparatorChar)), 120_000 + i * 25_000);
        }

        await using var server = CreateServer(pin: "654321", allowUnencrypted: true);
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "654321", UseTls: true, "TestClient");
        await using var browse = new TransferClient();
        await browse.ConnectAsync(target);

        var rootListing = await browse.ListDirectoryAsync(string.Empty, recursive: false);
        Assert.Null(rootListing.Error);
        Assert.True(rootListing.Entries.Count >= 3);

        await using var manager = new TransferManager
        {
            MaxConcurrency = 4,
            ChunkSizeBytes = 256 * 1024,
            VerifySha256 = true
        };
        manager.ConfigureTarget(target);

        var queued = await manager.EnqueueEntriesAsync(browse, rootListing.Entries, _downloadDir, string.Empty);
        Assert.Equal(expectedHashes.Count, queued.Count);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await manager.WaitForAllAsync(cts.Token);

        foreach (var (rel, expectedHash) in expectedHashes)
        {
            string localPath = Path.Combine(_downloadDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(localPath), $"Missing downloaded file: {localPath}");
            Assert.Equal(expectedHash, ComputeSha256(localPath));
            Assert.False(File.Exists(localPath + ".sbpart"));
            Assert.False(File.Exists(localPath + ".sbstate.json"));
        }
    }

    [Fact]
    public async Task LargeFileMultiStreamChunkedPull_WorksInParallel()
    {
        // 8 MB file split into 512 KB chunks across 4 parallel workers
        string expectedHash = CreateRandomFile(Path.Combine(_shareDir, "big_video.bin"), 8 * 1024 * 1024);

        await using var server = CreateServer(pin: "998877", allowUnencrypted: true);
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "998877", UseTls: false, "DirectCableClient");
        await using var browse = new TransferClient();
        await browse.ConnectAsync(target);

        var listing = await browse.ListDirectoryAsync(string.Empty);
        var entry = Assert.Single(listing.Entries);

        await using var manager = new TransferManager
        {
            MaxConcurrency = 4,
            ChunkSizeBytes = 512 * 1024,
            VerifySha256 = true
        };
        manager.ConfigureTarget(target);

        var item = manager.EnqueueFile(entry, Path.Combine(_downloadDir, entry.Name));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await manager.WaitForAllAsync(cts.Token);

        Assert.True(item.Status == TransferStatus.Completed, $"Status={item.Status}, Detail={item.StatusDetail}");
        Assert.Equal(expectedHash, ComputeSha256(Path.Combine(_downloadDir, "big_video.bin")));
    }

    [Fact]
    public async Task ResumeFromPartialState_SkipsCompletedChunksAndCompletes()
    {
        int fileSize = 4 * 1024 * 1024; // 4 MB
        int chunkSize = 512 * 1024;     // 8 chunks of 512 KB
        string srcPath = Path.Combine(_shareDir, "resumable.iso");
        string expectedHash = CreateRandomFile(srcPath, fileSize);
        var srcInfo = new FileInfo(srcPath);

        string destPath = Path.Combine(_downloadDir, "resumable.iso");
        string partPath = destPath + ".sbpart";
        string statePath = destPath + ".sbstate.json";

        // Simulate an interrupted transfer that already completed the first 4 chunks (50%)
        byte[] fullBytes = await File.ReadAllBytesAsync(srcPath);
        byte[] partialBytes = new byte[fileSize];
        Buffer.BlockCopy(fullBytes, 0, partialBytes, 0, 4 * chunkSize);
        await File.WriteAllBytesAsync(partPath, partialBytes);

        string manifestJson = $$"""
            {
              "remotePath": "resumable.iso",
              "fileSize": {{fileSize}},
              "lastModifiedUtcTicks": {{srcInfo.LastWriteTimeUtc.Ticks}},
              "chunkSize": {{chunkSize}},
              "totalChunks": 8,
              "completedChunks": [true, true, true, true, false, false, false, false]
            }
            """;
        await File.WriteAllTextAsync(statePath, manifestJson);

        await using var server = CreateServer(pin: "554433", allowUnencrypted: true);
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "554433", UseTls: true, "ResumeClient");
        var entry = new RemoteEntry("resumable.iso", "resumable.iso", false, srcInfo.Length, srcInfo.LastWriteTimeUtc);

        await using var manager = new TransferManager
        {
            MaxConcurrency = 2,
            ChunkSizeBytes = chunkSize,
            VerifySha256 = true
        };
        manager.ConfigureTarget(target);

        var item = manager.EnqueueFile(entry, destPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await manager.WaitForAllAsync(cts.Token);

        Assert.True(item.Status == TransferStatus.Completed, $"Status={item.Status}, Detail={item.StatusDetail}");
        Assert.True(item.ResumedFromPartial);
        // Server should only have served the remaining 4 chunks (2 MB), not the full 4 MB!
        Assert.Equal(2 * 1024 * 1024, server.TotalBytesServed);
        Assert.Equal(expectedHash, ComputeSha256(destPath));
    }

    [Fact]
    public async Task LiveConnectionDrop_AutoReconnectsAndFinishesTransfer()
    {
        int fileSize = 6 * 1024 * 1024;
        int chunkSize = 256 * 1024;
        string srcPath = Path.Combine(_shareDir, "drop_test.bin");
        string expectedHash = CreateRandomFile(srcPath, fileSize);
        var srcInfo = new FileInfo(srcPath);

        // Start server on an ephemeral port, then remember the port to restart on the same port
        var server1 = CreateServer(pin: "777888", allowUnencrypted: true, port: 0);
        server1.Start();
        int port = server1.ListeningPort;

        var target = new ConnectionTarget("127.0.0.1", port, "777888", UseTls: false, "DropClient");
        var entry = new RemoteEntry("drop_test.bin", "drop_test.bin", false, srcInfo.Length, srcInfo.LastWriteTimeUtc);
        string destPath = Path.Combine(_downloadDir, "drop_test.bin");

        await using var manager = new TransferManager
        {
            MaxConcurrency = 2,
            ChunkSizeBytes = chunkSize,
            VerifySha256 = true,
            MaxReconnectAttempts = 15
        };
        manager.ConfigureTarget(target);

        // Stop server immediately to simulate connection down before/during transfer, then bring server2 online on same port
        await server1.DisposeAsync();

        var item = manager.EnqueueFile(entry, destPath);
        await Task.Delay(700); // Let workers observe the connection drop and enter Reconnecting state

        await using var server2 = CreateServer(pin: "777888", allowUnencrypted: true, port: port);
        server2.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await manager.WaitForAllAsync(cts.Token);

        Assert.True(item.Status == TransferStatus.Completed, $"Status={item.Status}, Detail={item.StatusDetail}");
        Assert.Equal(expectedHash, ComputeSha256(destPath));
    }

    [Fact]
    public void PinAuth_ChannelBindingPreventsCertTampering()
    {
        byte[] sNonce = PinAuth.GenerateNonce();
        byte[] cNonce = PinAuth.GenerateNonce();
        byte[] certA = SHA256.HashData("certA"u8);
        byte[] certB = SHA256.HashData("certB"u8);

        byte[] proof = PinAuth.ComputeProof("482-915", sNonce, cNonce, certA);

        Assert.True(PinAuth.VerifyProof("482915", sNonce, cNonce, proof, certA));
        Assert.False(PinAuth.VerifyProof("482915", sNonce, cNonce, proof, certB));
        Assert.False(PinAuth.VerifyProof("482916", sNonce, cNonce, proof, certA));
    }

    [Fact]
    public void NetworkInfo_ClassifiesLinkLocalDirectCable()
    {
        Assert.True(NetworkInfo.IsIPv4LinkLocal(System.Net.IPAddress.Parse("169.254.42.19")));
        Assert.False(NetworkInfo.IsIPv4LinkLocal(System.Net.IPAddress.Parse("192.168.1.42")));
    }

    [Fact]
    public async Task InstallerWebServer_ServesHtmlScriptsAndBinariesOverHttp()
    {
        string fakeWin = Path.Combine(_rootTemp, "fake-win", "SwiftBeam.exe");
        string fakeLinux = Path.Combine(_rootTemp, "fake-linux", "SwiftBeam");
        string winHash = CreateRandomFile(fakeWin, 256 * 1024);
        string linuxHash = CreateRandomFile(fakeLinux, 192 * 1024);

        var artifacts = new InstallerArtifacts(fakeWin, null, fakeLinux, null);
        await using var web = new InstallerWebServer(() => "MyDesktop", () => 45890, port: 0, explicitArtifacts: artifacts);
        web.Start();

        using var handler = new HttpClientHandler { UseProxy = false };
        using var http = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{web.ListeningPort}") };

        // 1. Landing HTML page
        string html = await http.GetStringAsync("/");
        Assert.Contains("Install SwiftBeam", html);
        Assert.Contains("MyDesktop", html);
        Assert.Contains("/download/windows/SwiftBeam.exe", html);
        Assert.Contains("/download/linux/SwiftBeam", html);

        // 2. Dynamic install scripts point back to the right host
        string sh = await http.GetStringAsync("/install.sh");
        Assert.Contains($"http://127.0.0.1:{web.ListeningPort}/download/linux/SwiftBeam", sh);

        string ps1 = await http.GetStringAsync("/install.ps1");
        Assert.Contains($"http://127.0.0.1:{web.ListeningPort}/download/windows/SwiftBeam.exe", ps1);

        // 3. Binary downloads match byte-for-byte
        byte[] downloadedWin = await http.GetByteArrayAsync("/download/windows/SwiftBeam.exe");
        Assert.Equal(winHash, Convert.ToHexString(SHA256.HashData(downloadedWin)).ToLowerInvariant());

        byte[] downloadedLinux = await http.GetByteArrayAsync("/download/linux/SwiftBeam");
        Assert.Equal(linuxHash, Convert.ToHexString(SHA256.HashData(downloadedLinux)).ToLowerInvariant());

        Assert.Equal(2, web.DownloadsServed);

        // 4. Unknown route returns 404
        using var notFound = await http.GetAsync("/does-not-exist");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, notFound.StatusCode);
    }

    [Fact]
    public async Task FullFilesystemBrowsing_ListsDrivesAndSubfolders()
    {
        string testFile = Path.Combine(_shareDir, "hello_full_fs.txt");
        string expectedHash = CreateRandomFile(testFile, 64 * 1024);

        var opts = new FileShareServerOptions
        {
            DeviceName = "FullFsHost",
            Port = 0,
            SharedDirectory = _shareDir,
            AllowFullFilesystem = true,
            RequirePin = true,
            Pin = "246810",
            AllowUnencrypted = true
        };

        await using var server = new FileShareServer(opts);
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "246810", UseTls: true, "BrowserClient");
        await using var client = new TransferClient();
        var meta = await client.ConnectAsync(target);

        Assert.True(meta.FullFilesystemAccess);
        Assert.False(string.IsNullOrWhiteSpace(meta.HomePath));

        // 1. Virtual Drives root lists real drives/roots
        var drives = await client.ListDirectoryAsync(FileShareServer.DrivesVirtualPath);
        Assert.Null(drives.Error);
        Assert.NotEmpty(drives.Entries);

        // 2. Listing an actual folder path shows our file
        string shareWirePath = _shareDir.Replace('\\', '/');
        var listing = await client.ListDirectoryAsync(shareWirePath);
        Assert.Null(listing.Error);
        var entry = Assert.Single(listing.Entries);
        Assert.Equal("hello_full_fs.txt", entry.Name);

        // 3. Pull directly from full-FS wire path into local folder
        await using var manager = new TransferManager { MaxConcurrency = 2, VerifySha256 = true };
        manager.ConfigureTarget(target);
        await manager.EnqueueEntriesAsync(client, [entry], _downloadDir, shareWirePath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await manager.WaitForAllAsync(cts.Token);

        string localOut = Path.Combine(_downloadDir, "hello_full_fs.txt");
        Assert.True(File.Exists(localOut));
        Assert.Equal(expectedHash, ComputeSha256(localOut));
    }

    [Fact]
    public async Task ParallelPush_SendsMultiChunkFileAndFolderToRemoteWithSha256()
    {
        string localSrcDir = Path.Combine(_rootTemp, "local_push_src");
        string bigLocalFile = Path.Combine(localSrcDir, "big_upload.bin");
        string subLocalFile = Path.Combine(localSrcDir, "nested", "note.dat");
        string bigHash = CreateRandomFile(bigLocalFile, 4 * 1024 * 1024);
        string subHash = CreateRandomFile(subLocalFile, 90_000);

        var opts = new FileShareServerOptions
        {
            DeviceName = "PushTarget",
            Port = 0,
            SharedDirectory = _shareDir,
            AllowFullFilesystem = true,
            RequirePin = true,
            Pin = "135790",
            AllowUnencrypted = true
        };

        await using var server = new FileShareServer(opts);
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "135790", UseTls: false, "PushClient");
        await using var browse = new TransferClient();
        await browse.ConnectAsync(target);

        await using var manager = new TransferManager
        {
            MaxConcurrency = 4,
            ChunkSizeBytes = 512 * 1024,
            VerifySha256 = true
        };
        manager.ConfigureTarget(target);

        string remoteDestDir = _shareDir.Replace('\\', '/');
        var pushed = await manager.EnqueuePushPathsAsync(browse, [localSrcDir], remoteDestDir);
        Assert.Equal(2, pushed.Count);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await manager.WaitForAllAsync(cts.Token);

        Assert.All(pushed, item => Assert.True(item.Status == TransferStatus.Completed, item.StatusDetail));

        string remoteBig = Path.Combine(_shareDir, "local_push_src", "big_upload.bin");
        string remoteSub = Path.Combine(_shareDir, "local_push_src", "nested", "note.dat");
        Assert.True(File.Exists(remoteBig));
        Assert.True(File.Exists(remoteSub));
        Assert.Equal(bigHash, ComputeSha256(remoteBig));
        Assert.Equal(subHash, ComputeSha256(remoteSub));
    }

    [Fact]
    public async Task ThroughputBenchmark_64MbParallelPull_Exceeds50MBps()
    {
        string srcPath = Path.Combine(_shareDir, "bench_64mb.bin");
        // Fast deterministic fill (avoid slow 64 MB CSPRNG overhead in test setup)
        byte[] pattern = new byte[1024 * 1024];
        RandomNumberGenerator.Fill(pattern);
        using (var fs = File.Create(srcPath))
        {
            for (int i = 0; i < 64; i++) fs.Write(pattern);
        }

        await using var server = CreateServer(pin: "123123", allowUnencrypted: true);
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "123123", UseTls: true, "BenchClient");
        var fi = new FileInfo(srcPath);
        var entry = new RemoteEntry("bench_64mb.bin", "bench_64mb.bin", false, fi.Length, fi.LastWriteTimeUtc);
        string destPath = Path.Combine(_downloadDir, "bench_64mb.bin");

        await using var manager = new TransferManager
        {
            MaxConcurrency = 6,
            ChunkSizeBytes = 16 * 1024 * 1024,
            VerifySha256 = true
        };
        manager.ConfigureTarget(target);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var item = manager.EnqueueFile(entry, destPath);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await manager.WaitForAllAsync(cts.Token);
        sw.Stop();

        double mbPerSec = 64.0 / sw.Elapsed.TotalSeconds;
        Assert.True(item.Status == TransferStatus.Completed, item.StatusDetail);
        Assert.True(mbPerSec > 50.0, $"Expected > 50 MB/s over TLS loopback, got {mbPerSec:0.1} MB/s ({sw.ElapsedMilliseconds} ms)");
    }

    [Fact]
    public void MapRemoteToLocalPath_PreservesCleanRelativeStructureForLinuxAndWindowsPaths()
    {
        string localRoot = Path.Combine(_downloadDir, "mapped");
        string linuxMapped = TransferManager.MapRemoteToLocalPath(
            "/home/alice/Downloads/Photos/trip.jpg",
            "/home/alice/Downloads",
            localRoot);
        Assert.Equal(Path.Combine(localRoot, "Photos", "trip.jpg"), linuxMapped);

        string winMapped = TransferManager.MapRemoteToLocalPath(
            "C:/Users/Alice/Desktop/Game/save.dat",
            "C:/Users/Alice/Desktop",
            localRoot);
        Assert.Equal(Path.Combine(localRoot, "Game", "save.dat"), winMapped);
    }

    [Fact]
    public async Task PauseResumeAndCancel_DoNotCorruptStreamAndCleanUpPartialFiles()
    {
        string srcA = Path.Combine(_shareDir, "pause_test_a.bin");
        string srcB = Path.Combine(_shareDir, "cancel_test_b.bin");
        string hashA = CreateRandomFile(srcA, 16 * 1024 * 1024);
        CreateRandomFile(srcB, 16 * 1024 * 1024);

        await using var server = CreateServer(pin: "314159", allowUnencrypted: true);
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "314159", UseTls: true, "PauseClient");
        var fiA = new FileInfo(srcA);
        var fiB = new FileInfo(srcB);
        var entryA = new RemoteEntry("pause_test_a.bin", "pause_test_a.bin", false, fiA.Length, fiA.LastWriteTimeUtc);
        var entryB = new RemoteEntry("cancel_test_b.bin", "cancel_test_b.bin", false, fiB.Length, fiB.LastWriteTimeUtc);

        string destA = Path.Combine(_downloadDir, "pause_test_a.bin");
        string destB = Path.Combine(_downloadDir, "cancel_test_b.bin");

        await using var manager = new TransferManager
        {
            MaxConcurrency = 2,
            ChunkSizeBytes = 256 * 1024,
            VerifySha256 = true
        };
        manager.ConfigureTarget(target);

        TransferItem? itemA = null;
        TransferItem? itemB = null;
        int pausedOnce = 0;
        int cancelledOnce = 0;
        var removedItems = new List<TransferItem>();

        manager.ItemRemoved += removed => { lock (removedItems) removedItems.Add(removed); };
        manager.ItemUpdated += updated =>
        {
            if (updated == itemA && updated.BytesTransferred > 0 && Interlocked.Exchange(ref pausedOnce, 1) == 0)
            {
                manager.PauseItem(updated);
            }
            if (updated == itemB && updated.BytesTransferred > 0 && Interlocked.Exchange(ref cancelledOnce, 1) == 0)
            {
                manager.CancelItem(updated, deletePartialFiles: true);
            }
        };

        itemA = manager.EnqueueFile(entryA, destA);
        itemB = manager.EnqueueFile(entryB, destB);

        // Wait until itemA is paused and itemB is cancelled deterministically
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((Volatile.Read(ref pausedOnce) == 0 || Volatile.Read(ref cancelledOnce) == 0) && DateTime.UtcNow < deadline)
            await Task.Delay(15);

        await Task.Delay(150);

        // Resume A — workers must cleanly recover without frame corruption
        manager.ResumeItem(itemA);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await manager.WaitForAllAsync(cts.Token);

        Assert.True(itemA.Status == TransferStatus.Completed, itemA.StatusDetail);
        Assert.Equal(hashA, ComputeSha256(destA));
        Assert.Equal(TransferStatus.Cancelled, itemB.Status);
        Assert.Contains(itemB, removedItems);
        Assert.DoesNotContain(itemB, manager.GetItemsSnapshot());
        Assert.False(File.Exists(destB + ".sbpart"), "Cancelled partial file should be cleaned up.");
        Assert.False(File.Exists(destB + ".sbstate.json"), "Cancelled state file should be cleaned up.");
    }

    [Fact]
    public async Task RemoteFileManagement_CreateRenameAndDelete_AndConflictRenameBoth()
    {
        await using var server = CreateServer(pin: "556677", allowUnencrypted: true);
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "556677", UseTls: true, "MgmtClient");
        await using var client = new TransferClient();
        await client.ConnectAsync(target);

        // 1. Create folder -> Rename folder -> Create file -> Delete folder
        var created = await client.CreateDirectoryAsync("temp_folder");
        Assert.True(created.Success);
        Assert.True(Directory.Exists(Path.Combine(_shareDir, "temp_folder")));

        var renamed = await client.RenamePathAsync("temp_folder", "renamed_folder");
        Assert.True(renamed.Success);
        Assert.True(Directory.Exists(Path.Combine(_shareDir, "renamed_folder")));

        var deleted = await client.DeletePathAsync("renamed_folder");
        Assert.True(deleted.Success);
        Assert.False(Directory.Exists(Path.Combine(_shareDir, "renamed_folder")));

        // 2. Conflict policy RenameBoth keeps existing file and writes "sample (1).bin"
        string src = Path.Combine(_shareDir, "sample.bin");
        string expectedHash = CreateRandomFile(src, 128 * 1024);
        var fi = new FileInfo(src);
        string dest = Path.Combine(_downloadDir, "sample.bin");
        await File.WriteAllTextAsync(dest, "existing content");

        await using var manager = new TransferManager
        {
            MaxConcurrency = 2,
            ConflictPolicy = SwiftBeam.Core.Settings.FileConflictPolicy.RenameBoth,
            MaxSpeedMBps = 200,
            VerifySha256 = true
        };
        manager.ConfigureTarget(target);

        var entry = new RemoteEntry("sample.bin", "sample.bin", false, fi.Length, fi.LastWriteTimeUtc);
        var item = manager.EnqueueFile(entry, dest);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await manager.WaitForAllAsync(cts.Token);

        Assert.True(item.Status == TransferStatus.Completed, item.StatusDetail);
        Assert.Equal("existing content", await File.ReadAllTextAsync(dest));
        string renamedDest = Path.Combine(_downloadDir, "sample (1).bin");
        Assert.True(File.Exists(renamedDest));
        Assert.Equal(expectedHash, ComputeSha256(renamedDest));
    }

    [Fact]
    public async Task HostIncomingActivity_TracksConnectedPeersAndSupportsKick()
    {
        string srcPath = Path.Combine(_shareDir, "activity_test.bin");
        CreateRandomFile(srcPath, 2 * 1024 * 1024);

        await using var server = CreateServer(pin: "112244", allowUnencrypted: true);
        var activityEvents = new List<IReadOnlyList<IncomingPeerActivity>>();
        server.IncomingActivityChanged += snap =>
        {
            lock (activityEvents) activityEvents.Add(snap);
        };
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "112244", UseTls: true, "ObserverPC");
        await using var client = new TransferClient();
        await client.ConnectAsync(target);

        // Wait briefly for server snapshot to register authenticated session
        var deadline = DateTime.UtcNow.AddSeconds(5);
        IReadOnlyList<IncomingPeerActivity> snap = [];
        while (DateTime.UtcNow < deadline)
        {
            snap = server.GetIncomingActivitySnapshot();
            if (snap.Count > 0) break;
            await Task.Delay(20);
        }

        var peer = Assert.Single(snap);
        Assert.Equal("ObserverPC", peer.ClientName);
        Assert.True(peer.TlsActive);
        Assert.Equal(1, peer.ActiveStreams);

        // Kick incoming client from host
        await server.DisconnectIncomingClientsAsync();

        // Wait until session terminates cleanly on server
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            snap = server.GetIncomingActivitySnapshot();
            if (snap.Count == 0) break;
            await Task.Delay(20);
        }

        Assert.Empty(snap);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ListDirectoryAsync(string.Empty));
    }

    [Fact]
    public async Task SmoothTelemetryEta_RemainsStableDuringTransfer()
    {
        string srcPath = Path.Combine(_shareDir, "eta_smooth.bin");
        byte[] pattern = new byte[1024 * 1024];
        RandomNumberGenerator.Fill(pattern);
        using (var fs = File.Create(srcPath))
        {
            for (int i = 0; i < 32; i++) fs.Write(pattern);
        }

        await using var server = CreateServer(pin: "223344", allowUnencrypted: true);
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "223344", UseTls: false, "EtaClient");
        var fi = new FileInfo(srcPath);
        var entry = new RemoteEntry("eta_smooth.bin", "eta_smooth.bin", false, fi.Length, fi.LastWriteTimeUtc);
        string destPath = Path.Combine(_downloadDir, "eta_smooth.bin");

        var snapshots = new List<TransferTelemetrySnapshot>();
        await using var manager = new TransferManager
        {
            MaxConcurrency = 2,
            ChunkSizeBytes = 2 * 1024 * 1024,
            MaxSpeedMBps = 6, // Pace at 6 MB/s (~4-5s) so multiple telemetry ticks fire past the 1.5s warmup
            VerifySha256 = true
        };
        manager.TelemetryUpdated += s =>
        {
            lock (snapshots) snapshots.Add(s);
        };
        manager.ConfigureTarget(target);

        var item = manager.EnqueueFile(entry, destPath);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await manager.WaitForAllAsync(cts.Token);

        Assert.True(item.Status == TransferStatus.Completed, item.StatusDetail);
        Assert.NotEmpty(snapshots);
        Assert.Contains(snapshots, s => s.AggregateBytesPerSec > 0 && s.EstimatedTimeRemaining is { TotalSeconds: > 0 and < 360000 });
        Assert.All(snapshots, s =>
        {
            Assert.False(double.IsNaN(s.AggregateBytesPerSec) || double.IsInfinity(s.AggregateBytesPerSec));
            Assert.InRange(s.OverallProgressPercent, 0.0, 100.0);
        });
    }

    [Fact]
    public async Task ClearAll_CancelsActiveAndClearsEntireQueue()
    {
        string doneFile = Path.Combine(_shareDir, "clear_done.bin");
        string activeFile = Path.Combine(_shareDir, "clear_active.bin");
        CreateRandomFile(doneFile, 256 * 1024);
        CreateRandomFile(activeFile, 16 * 1024 * 1024);

        await using var server = CreateServer(pin: "334455", allowUnencrypted: true);
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "334455", UseTls: false, "ClearAllClient");
        await using var manager = new TransferManager
        {
            MaxConcurrency = 2,
            ChunkSizeBytes = 1024 * 1024,
            VerifySha256 = true
        };
        manager.ConfigureTarget(target);

        var fiDone = new FileInfo(doneFile);
        var doneDest = Path.Combine(_downloadDir, "clear_done.bin");
        var doneItem = manager.EnqueueFile(new RemoteEntry("clear_done.bin", "clear_done.bin", false, fiDone.Length, fiDone.LastWriteTimeUtc), doneDest);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            await manager.WaitForAllAsync(cts.Token);
        Assert.Equal(TransferStatus.Completed, doneItem.Status);

        manager.MaxSpeedMBps = 2; // Pace active item so workers cannot finish the 16 MB transfer before ClearAll
        var fiActive = new FileInfo(activeFile);
        var activeDest = Path.Combine(_downloadDir, "clear_active.bin");
        var activeItem = manager.EnqueueFile(new RemoteEntry("clear_active.bin", "clear_active.bin", false, fiActive.Length, fiActive.LastWriteTimeUtc), activeDest);

        var startDeadline = DateTime.UtcNow.AddSeconds(5);
        while (activeItem.BytesTransferred == 0 && DateTime.UtcNow < startDeadline)
            await Task.Delay(15);

        int removedCount = 0;
        manager.ItemRemoved += _ => Interlocked.Increment(ref removedCount);

        manager.ClearAll(deletePartialFiles: true);

        Assert.Equal(2, removedCount);
        Assert.Empty(manager.GetItemsSnapshot());
        Assert.Equal(TransferStatus.Completed, doneItem.Status);
        Assert.Equal(TransferStatus.Cancelled, activeItem.Status);

        // In-flight chunk stream finishes cancelling within milliseconds and cleans up .sbpart/.sbstate.json
        var cleanupDeadline = DateTime.UtcNow.AddSeconds(5);
        while ((File.Exists(activeItem.PartFilePath) || File.Exists(activeItem.StateFilePath)) && DateTime.UtcNow < cleanupDeadline)
            await Task.Delay(20);

        Assert.False(File.Exists(activeItem.PartFilePath));
        Assert.False(File.Exists(activeItem.StateFilePath));
    }

    [Fact]
    public async Task CancelAll_CancelsUnfinishedAndKeepsCompleted()
    {
        string doneFile = Path.Combine(_shareDir, "cancel_all_done.bin");
        string activeFile = Path.Combine(_shareDir, "cancel_all_active.bin");
        CreateRandomFile(doneFile, 256 * 1024);
        CreateRandomFile(activeFile, 16 * 1024 * 1024);

        await using var server = CreateServer(pin: "445566", allowUnencrypted: true);
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "445566", UseTls: false, "CancelAllClient");
        await using var manager = new TransferManager
        {
            MaxConcurrency = 2,
            ChunkSizeBytes = 1024 * 1024,
            VerifySha256 = true
        };
        manager.ConfigureTarget(target);

        var fiDone = new FileInfo(doneFile);
        var doneDest = Path.Combine(_downloadDir, "cancel_all_done.bin");
        var doneItem = manager.EnqueueFile(new RemoteEntry("cancel_all_done.bin", "cancel_all_done.bin", false, fiDone.Length, fiDone.LastWriteTimeUtc), doneDest);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            await manager.WaitForAllAsync(cts.Token);
        Assert.Equal(TransferStatus.Completed, doneItem.Status);

        manager.MaxSpeedMBps = 2;
        var fiActive = new FileInfo(activeFile);
        var activeDest = Path.Combine(_downloadDir, "cancel_all_active.bin");
        var activeItem = manager.EnqueueFile(new RemoteEntry("cancel_all_active.bin", "cancel_all_active.bin", false, fiActive.Length, fiActive.LastWriteTimeUtc), activeDest);

        var startDeadline = DateTime.UtcNow.AddSeconds(5);
        while (activeItem.BytesTransferred == 0 && DateTime.UtcNow < startDeadline)
            await Task.Delay(15);

        var removed = new List<TransferItem>();
        manager.ItemRemoved += item => { lock (removed) removed.Add(item); };

        manager.CancelAll(deletePartialFiles: true);

        Assert.Single(removed);
        Assert.Same(activeItem, removed[0]);
        var remaining = manager.GetItemsSnapshot();
        var kept = Assert.Single(remaining);
        Assert.Same(doneItem, kept);
        Assert.Equal(TransferStatus.Completed, kept.Status);
        Assert.Equal(TransferStatus.Cancelled, activeItem.Status);

        var cleanupDeadline = DateTime.UtcNow.AddSeconds(5);
        while ((File.Exists(activeItem.PartFilePath) || File.Exists(activeItem.StateFilePath)) && DateTime.UtcNow < cleanupDeadline)
            await Task.Delay(20);

        Assert.False(File.Exists(activeItem.PartFilePath));
        Assert.False(File.Exists(activeItem.StateFilePath));
    }

    [Fact]
    public void GitHubUpdateService_ParsesVersionsChecksumsAndPlatformAssets()
    {
        Assert.Equal(new Version(1, 2, 3), GitHubUpdateService.ParseVersion("v1.2.3"));
        Assert.Equal(new Version(1, 2, 0), GitHubUpdateService.ParseVersion("1.2"));
        Assert.Equal(new Version(2, 0, 1), GitHubUpdateService.ParseVersion("V2.0.1-beta.1+sha99"));
        Assert.Null(GitHubUpdateService.ParseVersion("not-a-version"));
        Assert.Equal("Cathildros/SwiftBeam", GitHubUpdateService.NormalizeRepoSlug("https://github.com/Cathildros/SwiftBeam.git"));

        string checksums = """
            # SHA256SUMS
            aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  SwiftBeam-Setup.exe
            bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb *SwiftBeam-Linux-x64
            """;
        Assert.Equal(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            GitHubUpdateService.ExtractSha256ForAsset(checksums, "SwiftBeam-Setup.exe"));
        Assert.Equal(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            GitHubUpdateService.ExtractSha256ForAsset(checksums, "SwiftBeam-Linux-x64"));
        Assert.Null(GitHubUpdateService.ExtractSha256ForAsset(checksums, "missing.exe"));

        string releaseJson = """
            {
              "tag_name": "v1.2.0",
              "html_url": "https://github.com/Cathildros/SwiftBeam/releases/tag/v1.2.0",
              "body": "Faster transfers",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "name": "SwiftBeam.exe",
                  "browser_download_url": "https://github.com/Cathildros/SwiftBeam/releases/download/v1.2.0/SwiftBeam.exe",
                  "size": 48000000
                },
                {
                  "name": "SwiftBeam-Setup.exe",
                  "browser_download_url": "https://github.com/Cathildros/SwiftBeam/releases/download/v1.2.0/SwiftBeam-Setup.exe",
                  "size": 84000000
                },
                {
                  "name": "SwiftBeam-Linux-x64",
                  "browser_download_url": "https://github.com/Cathildros/SwiftBeam/releases/download/v1.2.0/SwiftBeam-Linux-x64",
                  "size": 48200000
                },
                {
                  "name": "SwiftBeam",
                  "browser_download_url": "https://github.com/Cathildros/SwiftBeam/releases/download/v1.2.0/SwiftBeam",
                  "size": 48100000
                },
                {
                  "name": "sha256sums.txt",
                  "browser_download_url": "https://github.com/Cathildros/SwiftBeam/releases/download/v1.2.0/sha256sums.txt",
                  "size": 512
                }
              ]
            }
            """;

        var winUpdate = GitHubUpdateService.ParseReleaseJson(releaseJson, new Version(1, 0, 0), isWindows: true, isLinux: false, out string? shaUrl);
        Assert.NotNull(winUpdate);
        Assert.Equal("SwiftBeam-Setup.exe", winUpdate!.AssetName);
        Assert.Equal(new Version(1, 2, 0), winUpdate.Version);
        Assert.Equal("https://github.com/Cathildros/SwiftBeam/releases/download/v1.2.0/sha256sums.txt", shaUrl);

        var linuxUpdate = GitHubUpdateService.ParseReleaseJson(releaseJson, new Version(1, 0, 0), isWindows: false, isLinux: true, out _);
        Assert.NotNull(linuxUpdate);
        Assert.Equal("SwiftBeam-Linux-x64", linuxUpdate!.AssetName);

        var upToDate = GitHubUpdateService.ParseReleaseJson(releaseJson, new Version(1, 2, 0), isWindows: true, isLinux: false, out _);
        Assert.Null(upToDate);
    }

    [Fact]
    public async Task SmartCompression_PullAndPushCompressibleTextAndSkipIncompressible()
    {
        Assert.True(CompressionHelper.ShouldCompressFile("notes.txt"));
        Assert.True(CompressionHelper.ShouldCompressFile("app.log"));
        Assert.False(CompressionHelper.ShouldCompressFile("video.mp4"));
        Assert.False(CompressionHelper.ShouldCompressFile("archive.zip"));

        // Create highly compressible 2 MB text file
        string pullSrc = Path.Combine(_shareDir, "compressible_pull.log");
        string repeatedLine = "2026-11-24 INFO SwiftBeam parallel chunk telemetry latency=0.4ms ok\n";
        string textContent = string.Concat(Enumerable.Repeat(repeatedLine, 32 * 1024));
        await File.WriteAllTextAsync(pullSrc, textContent);
        string expectedHash = ComputeSha256(pullSrc);

        await using var server = new FileShareServer(new FileShareServerOptions
        {
            DeviceName = "CompHost",
            Port = 0,
            SharedDirectory = _shareDir,
            AllowFullFilesystem = true,
            RequirePin = true,
            Pin = "654321",
            AllowUnencrypted = true
        });
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "654321", UseTls: true, "CompClient");
        await using var browse = new TransferClient();
        await browse.ConnectAsync(target);

        await using var manager = new TransferManager
        {
            MaxConcurrency = 2,
            ChunkSizeBytes = 512 * 1024,
            VerifySha256 = true,
            UseSmartCompression = true
        };
        manager.ConfigureTarget(target);

        var fi = new FileInfo(pullSrc);
        string pullDest = Path.Combine(_downloadDir, "compressible_pull.log");
        var pullItem = manager.EnqueueFile(new RemoteEntry("compressible_pull.log", pullSrc.Replace('\\', '/'), false, fi.Length, fi.LastWriteTimeUtc), pullDest);

        string pushSrc = Path.Combine(_downloadDir, "compressible_push.txt");
        await File.WriteAllTextAsync(pushSrc, textContent);
        string pushDestWire = Path.Combine(_shareDir, "compressible_push_out.txt").Replace('\\', '/');
        var pushItem = manager.EnqueuePushFile(pushSrc, pushDestWire);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await manager.WaitForAllAsync(cts.Token);

        Assert.True(pullItem.Status == TransferStatus.Completed, pullItem.StatusDetail);
        Assert.True(pushItem.Status == TransferStatus.Completed, pushItem.StatusDetail);
        Assert.Equal(expectedHash, ComputeSha256(pullDest));
        Assert.Equal(expectedHash, ComputeSha256(Path.Combine(_shareDir, "compressible_push_out.txt")));
    }

    [Fact]
    public async Task ReadOnlyMode_AllowsPullAndBlocksWriteDeleteRename()
    {
        string srcPath = Path.Combine(_shareDir, "readonly_sample.txt");
        await File.WriteAllTextAsync(srcPath, "read only download ok");
        string expectedHash = ComputeSha256(srcPath);

        await using var server = new FileShareServer(new FileShareServerOptions
        {
            DeviceName = "ReadOnlyHost",
            Port = 0,
            SharedDirectory = _shareDir,
            AllowFullFilesystem = false,
            RequirePin = true,
            Pin = "112233",
            AllowUnencrypted = true,
            ReadOnly = true
        });
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "112233", UseTls: true, "RoClient");
        await using var client = new TransferClient();
        var meta = await client.ConnectAsync(target);
        Assert.True(meta.ReadOnly);

        // Write/edit control commands must be rejected
        var mk = await client.CreateDirectoryAsync("blocked_dir");
        Assert.False(mk.Success);
        Assert.Contains("Read-Only", mk.Error);

        var rn = await client.RenamePathAsync("readonly_sample.txt", "renamed.txt");
        Assert.False(rn.Success);

        var del = await client.DeletePathAsync("readonly_sample.txt");
        Assert.False(del.Success);
        Assert.True(File.Exists(srcPath));

        // Push must also fail safely without corrupting connection
        string localUpload = Path.Combine(_downloadDir, "upload.txt");
        await File.WriteAllTextAsync(localUpload, "blocked upload");
        var pushAck = await client.PushRangeAsync("upload.txt", localUpload, new FileInfo(localUpload).Length, DateTime.UtcNow, 0, new FileInfo(localUpload).Length, _ => { });
        Assert.False(pushAck.Success);
        Assert.Contains("Read-Only", pushAck.Error);

        // Pull still succeeds
        var fi = new FileInfo(srcPath);
        await using var manager = new TransferManager { MaxConcurrency = 1, VerifySha256 = true };
        manager.ConfigureTarget(target);
        string dest = Path.Combine(_downloadDir, "readonly_pulled.txt");
        var item = manager.EnqueueFile(new RemoteEntry("readonly_sample.txt", "readonly_sample.txt", false, fi.Length, fi.LastWriteTimeUtc), dest);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await manager.WaitForAllAsync(cts.Token);
        Assert.Equal(TransferStatus.Completed, item.Status);
        Assert.Equal(expectedHash, ComputeSha256(dest));
    }

    [Fact]
    public async Task HostApproval_DeclinesWhenRejected_AndApprovesOnceAcrossParallelStreams()
    {
        string srcPath = Path.Combine(_shareDir, "approved_file.bin");
        string expectedHash = CreateRandomFile(srcPath, 4 * 1024 * 1024);

        bool allowNext = false;
        int approvalCalls = 0;

        await using var server = new FileShareServer(new FileShareServerOptions
        {
            DeviceName = "ApprovalHost",
            Port = 0,
            SharedDirectory = _shareDir,
            AllowFullFilesystem = false,
            RequirePin = true,
            Pin = "778899",
            AllowUnencrypted = true,
            RequireHostApproval = true
        });
        server.ConnectionApprovalHandler = (_, _, _) =>
        {
            Interlocked.Increment(ref approvalCalls);
            return Task.FromResult(allowNext);
        };
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "778899", UseTls: true, "GuestPC");
        await using var rejectedClient = new TransferClient();
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rejectedClient.ConnectAsync(target));
        Assert.Contains("declined", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Now approve and run multi-stream transfer (should prompt only once per session)
        allowNext = true;
        Volatile.Write(ref approvalCalls, 0);

        await using var approvedClient = new TransferClient();
        await approvedClient.ConnectAsync(target);

        await using var manager = new TransferManager
        {
            MaxConcurrency = 3,
            ChunkSizeBytes = 512 * 1024,
            VerifySha256 = true
        };
        manager.ConfigureTarget(target);
        var fi = new FileInfo(srcPath);
        string dest = Path.Combine(_downloadDir, "approved_out.bin");
        var item = manager.EnqueueFile(new RemoteEntry("approved_file.bin", "approved_file.bin", false, fi.Length, fi.LastWriteTimeUtc), dest);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await manager.WaitForAllAsync(cts.Token);

        Assert.Equal(TransferStatus.Completed, item.Status);
        Assert.Equal(expectedHash, ComputeSha256(dest));
        Assert.Equal(1, Volatile.Read(ref approvalCalls));
    }

    [Fact]
    public async Task QuickTextShare_DeliversTextToHostEvent()
    {
        await using var server = CreateServer(pin: "990011", allowUnencrypted: true);
        string? receivedSender = null;
        string? receivedText = null;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.TextReceived += (sender, text) =>
        {
            receivedSender = sender;
            receivedText = text;
            tcs.TrySetResult();
        };
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "990011", UseTls: true, "SenderLaptop");
        await using var client = new TransferClient();
        await client.ConnectAsync(target);

        var ack = await client.SendTextAsync("https://example.com/share?id=42", "SenderLaptop");
        Assert.True(ack.Success);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("SenderLaptop", receivedSender);
        Assert.Equal("https://example.com/share?id=42", receivedText);
    }

    [Fact]
    public async Task QueuePersistence_SavesAndRestoresUnfinishedTransfersPaused()
    {
        string queueFile = Path.Combine(_rootTemp, "pending-queue.json");
        string doneSrc = Path.Combine(_shareDir, "persisted_done.bin");
        CreateRandomFile(doneSrc, 64 * 1024);

        await using var server = CreateServer(pin: "123789", allowUnencrypted: true);
        server.Start();
        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "123789", UseTls: false, "PersistClient");

        string localPushSrc = Path.Combine(_downloadDir, "local_push_queued.bin");
        CreateRandomFile(localPushSrc, 512 * 1024);

        await using (var manager1 = new TransferManager { MaxConcurrency = 1, VerifySha256 = false })
        {
            manager1.ConfigureTarget(target);
            var fiDone = new FileInfo(doneSrc);
            var doneItem = manager1.EnqueueFile(new RemoteEntry("persisted_done.bin", "persisted_done.bin", false, fiDone.Length, fiDone.LastWriteTimeUtc),
                Path.Combine(_downloadDir, "persisted_done.bin"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await manager1.WaitForAllAsync(cts.Token);
            Assert.Equal(TransferStatus.Completed, doneItem.Status);

            var pendingPull = manager1.EnqueueFile(new RemoteEntry("missing_yet.bin", "missing_yet.bin", false, 2_000_000, DateTime.UtcNow),
                Path.Combine(_downloadDir, "missing_yet.bin"));
            manager1.PauseItem(pendingPull);

            var pendingPush = manager1.EnqueuePushFile(localPushSrc, "uploaded_later.bin");
            manager1.PauseItem(pendingPush);

            manager1.SavePendingQueue(queueFile);
        }

        Assert.True(File.Exists(queueFile));

        await using var manager2 = new TransferManager();
        int loaded = manager2.LoadPendingQueue(queueFile);
        Assert.Equal(2, loaded);
        var snap = manager2.GetItemsSnapshot();
        Assert.Equal(2, snap.Count);
        Assert.All(snap, item => Assert.Equal(TransferStatus.Paused, item.Status));
        Assert.Contains(snap, x => x.Direction == TransferDirection.Pull && x.RemoteRelativePath == "missing_yet.bin");
        Assert.Contains(snap, x => x.Direction == TransferDirection.Push && x.LocalDestinationPath == localPushSrc);
    }

    [Fact]
    public async Task ListDirectory_ReturnsDiskSpaceAndRespectsIncludeHiddenFlag()
    {
        string visibleFile = Path.Combine(_shareDir, "visible.txt");
        string hiddenDotFile = Path.Combine(_shareDir, ".env");
        await File.WriteAllTextAsync(visibleFile, "ok");
        await File.WriteAllTextAsync(hiddenDotFile, "SECRET=1");

        await using var server = CreateServer(pin: "334455", allowUnencrypted: true);
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "334455", UseTls: false, "HiddenClient");
        await using var client = new TransferClient();
        await client.ConnectAsync(target);

        var defList = await client.ListDirectoryAsync(string.Empty, recursive: false, includeHidden: false);
        Assert.Null(defList.Error);
        Assert.True(defList.FreeDiskBytes is > 0);
        Assert.Contains(defList.Entries, x => x.Name == "visible.txt" && !x.IsHidden);
        Assert.DoesNotContain(defList.Entries, x => x.Name == ".env");

        var fullList = await client.ListDirectoryAsync(string.Empty, recursive: false, includeHidden: true);
        Assert.Contains(fullList.Entries, x => x.Name == ".env" && x.IsHidden);
    }

    [Fact]
    public async Task ReadFilePreviewAsync_ReturnsBoundedPrefixAndTruncatedFlag()
    {
        string bigText = Path.Combine(_shareDir, "preview_big.txt");
        string smallText = Path.Combine(_shareDir, "preview_small.txt");
        await File.WriteAllTextAsync(bigText, new string('A', 600) + new string('B', 600));
        await File.WriteAllTextAsync(smallText, "Hello Preview");

        await using var server = CreateServer(pin: "445566", allowUnencrypted: true);
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "445566", UseTls: false, "PreviewClient");
        await using var client = new TransferClient();
        await client.ConnectAsync(target);

        var (bigData, bigSize, bigTrunc) = await client.ReadFilePreviewAsync("preview_big.txt", maxBytes: 512);
        Assert.True(bigTrunc);
        Assert.Equal(1200, bigSize);
        Assert.Equal(512, bigData.Length);
        Assert.All(bigData, b => Assert.Equal((byte)'A', b));

        var (smallData, smallSize, smallTrunc) = await client.ReadFilePreviewAsync("preview_small.txt", maxBytes: 512);
        Assert.False(smallTrunc);
        Assert.Equal(13, smallSize);
        Assert.Equal("Hello Preview", System.Text.Encoding.UTF8.GetString(smallData));
    }

    [Fact]
    public async Task TransferManager_MoveToTopAndRetryAllFailed_ReordersAndResumes()
    {
        // Part 1: Deterministic queue reordering on paused items (zero worker race)
        string queueFile = Path.Combine(_rootTemp, "reorder-queue.json");
        await File.WriteAllTextAsync(queueFile, System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new PersistedTransferItem(TransferDirection.Pull, "a.bin", Path.Combine(_downloadDir, "a.bin"), 1024, DateTime.UtcNow, 262144),
            new PersistedTransferItem(TransferDirection.Pull, "b.bin", Path.Combine(_downloadDir, "b.bin"), 1024, DateTime.UtcNow, 262144),
            new PersistedTransferItem(TransferDirection.Pull, "c.bin", Path.Combine(_downloadDir, "c.bin"), 1024, DateTime.UtcNow, 262144)
        }));

        await using (var reorderManager = new TransferManager())
        {
            Assert.Equal(3, reorderManager.LoadPendingQueue(queueFile));
            var initial = reorderManager.GetItemsSnapshot();
            var lastItem = initial[2];
            Assert.True(reorderManager.MoveToTop(lastItem));
            var reordered = reorderManager.GetItemsSnapshot();
            Assert.Equal(lastItem.Id, reordered[0].Id);
        }

        // Part 2: Deterministic RetryAllFailed recovery after fixing PIN
        string retrySrc = Path.Combine(_shareDir, "retry_ok.bin");
        string expectedHash = CreateRandomFile(retrySrc, 64 * 1024);

        await using var server = CreateServer(pin: "998877", allowUnencrypted: true);
        server.Start();

        await using var manager = new TransferManager { MaxConcurrency = 1 };
        manager.ConfigureTarget(new ConnectionTarget("127.0.0.1", server.ListeningPort, "000000", UseTls: false, "RetryClient"));

        var fi = new FileInfo(retrySrc);
        string retryDest = Path.Combine(_downloadDir, "retry_ok.bin");
        var itemFail = manager.EnqueueFile(new RemoteEntry("retry_ok.bin", "retry_ok.bin", false, fi.Length, fi.LastWriteTimeUtc), retryDest);
        using (var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await manager.WaitForAllAsync(cts1.Token);
        Assert.Equal(TransferStatus.Failed, itemFail.Status);

        manager.ConfigureTarget(new ConnectionTarget("127.0.0.1", server.ListeningPort, "998877", UseTls: false, "RetryClient"));
        int retried = manager.RetryAllFailed();
        Assert.Equal(1, retried);

        using (var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await manager.WaitForAllAsync(cts2.Token);
        Assert.Equal(TransferStatus.Completed, itemFail.Status);
        Assert.Equal(expectedHash, ComputeSha256(retryDest));
    }

    [Fact]
    public void FolderSyncPlanner_ComputesLocalToRemoteRemoteToLocalAndTwoWayDiffs()
    {
        string localSync = Path.Combine(_rootTemp, "sync_local");
        Directory.CreateDirectory(localSync);

        DateTime baseUtc = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(Path.Combine(localSync, "same.txt"), "same");
        File.SetLastWriteTimeUtc(Path.Combine(localSync, "same.txt"), baseUtc);

        File.WriteAllText(Path.Combine(localSync, "only_local.txt"), "local-only");
        File.WriteAllText(Path.Combine(localSync, "newer_local.txt"), "newer-local-content");
        File.SetLastWriteTimeUtc(Path.Combine(localSync, "newer_local.txt"), baseUtc.AddMinutes(5));

        File.WriteAllText(Path.Combine(localSync, "newer_remote.txt"), "old");
        File.SetLastWriteTimeUtc(Path.Combine(localSync, "newer_remote.txt"), baseUtc);

        var remoteEntries = new List<RemoteEntry>
        {
            new("same.txt", "/remote/same.txt", false, 4, baseUtc),
            new("only_remote.txt", "/remote/only_remote.txt", false, 99, baseUtc),
            new("newer_local.txt", "/remote/newer_local.txt", false, 5, baseUtc),
            new("newer_remote.txt", "/remote/newer_remote.txt", false, 42, baseUtc.AddMinutes(10))
        };

        var twoWay = FolderSyncPlanner.PlanSync(localSync, remoteEntries, "/remote", SyncDirection.TwoWayNewest);
        Assert.Equal(1, twoWay.IdenticalCount);
        Assert.Equal(2, twoWay.ToSendCount);
        Assert.Equal(2, twoWay.ToPullCount);
        Assert.Contains(twoWay.Items, x => x.RelativePath == "only_local.txt" && x.Action == SyncActionKind.CopyToRemote);
        Assert.Contains(twoWay.Items, x => x.RelativePath == "newer_local.txt" && x.Action == SyncActionKind.CopyToRemote);
        Assert.Contains(twoWay.Items, x => x.RelativePath == "only_remote.txt" && x.Action == SyncActionKind.CopyToLocal);
        Assert.Contains(twoWay.Items, x => x.RelativePath == "newer_remote.txt" && x.Action == SyncActionKind.CopyToLocal);

        var pushOnly = FolderSyncPlanner.PlanSync(localSync, remoteEntries, "/remote", SyncDirection.LocalToRemote);
        Assert.Equal(3, pushOnly.ToSendCount);
        Assert.Equal(0, pushOnly.ToPullCount);
    }

    [Fact]
    public void TransferHistoryStore_PersistsAndCapsEntries()
    {
        string historyPath = Path.Combine(_rootTemp, "history.json");
        var list = new List<TransferHistoryEntry>();
        for (int i = 0; i < TransferHistoryStore.MaxEntries + 15; i++)
        {
            list.Add(new TransferHistoryEntry(
                $"id-{i}",
                DateTime.UtcNow.AddSeconds(i),
                i % 2 == 0 ? TransferDirection.Pull : TransferDirection.Push,
                $"file_{i}.bin",
                $"C:\\dl\\file_{i}.bin",
                $"remote/file_{i}.bin",
                "PeerPC",
                1024 + i,
                5_000_000,
                TransferStatus.Completed));
        }

        TransferHistoryStore.Save(historyPath, list);
        var loaded = TransferHistoryStore.Load(historyPath);
        Assert.Equal(TransferHistoryStore.MaxEntries, loaded.Count);
        Assert.Equal($"id-{TransferHistoryStore.MaxEntries + 14}", loaded[0].Id);
    }

    [Fact]
    public async Task DeleteFolder_ClearsReadOnlyAttributesAndEvictsHandles()
    {
        string remoteFolder = Path.Combine(_shareDir, "ro_tree");
        string remoteNested = Path.Combine(remoteFolder, "nested");
        Directory.CreateDirectory(remoteNested);
        string remoteRoFile = Path.Combine(remoteNested, "locked.txt");
        await File.WriteAllTextAsync(remoteRoFile, "readonly");
        File.SetAttributes(remoteRoFile, FileAttributes.ReadOnly);

        await using var server = CreateServer(pin: "667788", allowUnencrypted: true);
        server.Start();

        var target = new ConnectionTarget("127.0.0.1", server.ListeningPort, "667788", UseTls: false, "DeleteClient");
        await using var client = new TransferClient();
        await client.ConnectAsync(target);

        var resp = await client.DeletePathAsync("ro_tree");
        Assert.True(resp.Success, resp.Error);
        Assert.False(Directory.Exists(remoteFolder));

        string localFolder = Path.Combine(_rootTemp, "local_ro_tree", "sub");
        Directory.CreateDirectory(localFolder);
        string localRoFile = Path.Combine(localFolder, "attr.txt");
        await File.WriteAllTextAsync(localRoFile, "ro");
        File.SetAttributes(localRoFile, FileAttributes.ReadOnly);

        FileShareServer.DeleteFileOrDirectoryRobust(Path.Combine(_rootTemp, "local_ro_tree"));
        Assert.False(Directory.Exists(Path.Combine(_rootTemp, "local_ro_tree")));
    }

    [Fact]
    public void QrCodeEncoder_ProducesValidSquareMatrixWithFinderPatterns()
    {
        bool[,] matrix = QrCodeEncoder.Encode("http://192.168.1.50:45893/?qr=0123456789abcdef0123456789abcdef");
        int n = matrix.GetLength(0);
        Assert.Equal(n, matrix.GetLength(1));
        Assert.True(n >= 21 && (n - 21) % 4 == 0);

        // Verify top-left 7x7 finder outer ring & 3x3 solid center
        for (int i = 0; i < 7; i++)
        {
            Assert.True(matrix[0, i]);
            Assert.True(matrix[6, i]);
            Assert.True(matrix[i, 0]);
            Assert.True(matrix[i, 6]);
        }
        Assert.True(matrix[3, 3]);
        Assert.False(matrix[1, 1]);
        // Top-right & bottom-left finder centers
        Assert.True(matrix[3, n - 4]);
        Assert.True(matrix[n - 4, 3]);
    }

    [Fact]
    public async Task WebPortalServer_AuthenticatesListsUploadsDownloadsZipsDeletesAndReceivesText()
    {
        string existingFile = Path.Combine(_shareDir, "portal_sample.txt");
        await File.WriteAllTextAsync(existingFile, "Hello from PC");

        string subFolder = Path.Combine(_shareDir, "portal_folder");
        Directory.CreateDirectory(subFolder);
        await File.WriteAllTextAsync(Path.Combine(subFolder, "nested.txt"), "Zip payload");

        await using var fileServer = CreateServer(pin: "135790", requirePin: true, allowUnencrypted: true);
        fileServer.Start();

        await using var webPortal = new WebPortalServer(fileServer, port: 0);
        string? receivedText = null;
        webPortal.TextReceived += (_, txt) => receivedText = txt;
        webPortal.Start();

        var handler = new HttpClientHandler { UseCookies = true, UseProxy = false };
        using var http = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{webPortal.ListeningPort}") };

        // Unauthenticated /api/list rejected with 401
        var unauth = await http.GetAsync("/api/list");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauth.StatusCode);

        // Authenticate with wrong PIN -> 401, then right PIN -> 200
        var badAuth = await http.PostAsync("/api/auth", new StringContent("{\"pin\":\"000000\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, badAuth.StatusCode);

        var okAuth = await http.PostAsync("/api/auth", new StringContent("{\"pin\":\"135790\"}", System.Text.Encoding.UTF8, "application/json"));
        okAuth.EnsureSuccessStatusCode();

        // List directory
        string listJson = await http.GetStringAsync("/api/list");
        Assert.Contains("portal_sample.txt", listJson);

        // Upload file via raw POST stream
        byte[] uploadBytes = "Uploaded from phone browser!"u8.ToArray();
        using var uploadContent = new ByteArrayContent(uploadBytes);
        var upResp = await http.PostAsync("/api/upload?dir=&name=phone_upload.txt", uploadContent);
        upResp.EnsureSuccessStatusCode();
        string uploadedDest = Path.Combine(_shareDir, "phone_upload.txt");
        Assert.Equal(uploadBytes, await File.ReadAllBytesAsync(uploadedDest));

        // Download file back
        byte[] downloaded = await http.GetByteArrayAsync("/api/download?path=phone_upload.txt");
        Assert.Equal(uploadBytes, downloaded);

        // Download folder as zip
        byte[] zipBytes = await http.GetByteArrayAsync("/api/download-zip?path=portal_folder");
        using (var ms = new MemoryStream(zipBytes))
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read))
        {
            Assert.Contains(zip.Entries, e => e.FullName.EndsWith("nested.txt"));
        }

        // Send text snippet
        var txtResp = await http.PostAsync("/api/text", new StringContent("{\"text\":\"Check this link\",\"senderName\":\"iPhone\"}", System.Text.Encoding.UTF8, "application/json"));
        txtResp.EnsureSuccessStatusCode();
        Assert.Equal("Check this link", receivedText);

        // Delete uploaded file
        var delResp = await http.PostAsync("/api/delete", new StringContent("{\"path\":\"phone_upload.txt\"}", System.Text.Encoding.UTF8, "application/json"));
        delResp.EnsureSuccessStatusCode();
        Assert.False(File.Exists(uploadedDest));
    }

    [Fact]
    public async Task WebPortalServer_SupportsKeepAliveHeadFaviconAndFastRejectsTlsClientHello()
    {
        await using var fileServer = CreateServer(pin: "135790", requirePin: false, allowUnencrypted: true);
        fileServer.Start();

        await using var webPortal = new WebPortalServer(fileServer, port: 0);
        webPortal.Start();

        var handler = new HttpClientHandler { UseCookies = true, UseProxy = false };
        using var http = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{webPortal.ListeningPort}") };

        // HEAD / and GET /favicon.ico
        using var headResp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"));
        Assert.Equal(System.Net.HttpStatusCode.OK, headResp.StatusCode);

        using var favResp = await http.GetAsync("/favicon.ico");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, favResp.StatusCode);

        // Raw TLS ClientHello must be closed fast (< 2s) rather than waiting 60s
        using (var tlsProbe = new System.Net.Sockets.TcpClient())
        {
            await tlsProbe.ConnectAsync("127.0.0.1", webPortal.ListeningPort);
            var tlsStream = tlsProbe.GetStream();
            await tlsStream.WriteAsync(new byte[] { 0x16, 0x03, 0x01, 0x00, 0x05, 0x01, 0x00, 0x00, 0x01, 0x00 });
            byte[] buf = new byte[16];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            int read = await tlsStream.ReadAsync(buf, cts.Token);
            Assert.Equal(0, read);
        }

        // Two sequential HTTP/1.1 requests over one kept-alive TCP connection
        using (var tcp = new System.Net.Sockets.TcpClient())
        {
            await tcp.ConnectAsync("127.0.0.1", webPortal.ListeningPort);
            var stream = tcp.GetStream();
            byte[] req1 = System.Text.Encoding.ASCII.GetBytes($"GET /api/status HTTP/1.1\r\nHost: 127.0.0.1:{webPortal.ListeningPort}\r\nConnection: keep-alive\r\n\r\n");
            await stream.WriteAsync(req1);

            string resp1 = await ReadSingleHttpResponseAsync(stream);
            Assert.StartsWith("HTTP/1.1 200 OK", resp1);

            byte[] req2 = System.Text.Encoding.ASCII.GetBytes($"GET /api/status HTTP/1.1\r\nHost: 127.0.0.1:{webPortal.ListeningPort}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(req2);

            string resp2 = await ReadSingleHttpResponseAsync(stream);
            Assert.StartsWith("HTTP/1.1 200 OK", resp2);
        }
    }

    private static async Task<string> ReadSingleHttpResponseAsync(System.Net.Sockets.NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var ms = new MemoryStream();
        byte[] buf = new byte[4096];
        int headerEnd = -1;
        int contentLen = 0;

        while (true)
        {
            int n = await stream.ReadAsync(buf, cts.Token);
            Assert.True(n > 0, "Unexpected EOF before HTTP response finished.");
            ms.Write(buf, 0, n);
            byte[] data = ms.GetBuffer();
            int len = (int)ms.Length;

            if (headerEnd < 0)
            {
                string s = System.Text.Encoding.ASCII.GetString(data, 0, len);
                int idx = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    headerEnd = idx + 4;
                    foreach (string line in s[..idx].Split("\r\n"))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(line["Content-Length:".Length..].Trim(), out contentLen);
                    }
                }
            }

            if (headerEnd >= 0 && len >= headerEnd + contentLen)
                return System.Text.Encoding.ASCII.GetString(data, 0, headerEnd + contentLen);
        }
    }

    [Fact]
    public void AutoSyncEngine_ThreeWayDiff_HandlesAddEditDeleteEditVsDeleteAndConflicts()
    {
        var t0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddMinutes(5);
        var t2 = t0.AddMinutes(6);

        var baseline = new AutoSyncSnapshot
        {
            Files = new Dictionary<string, SyncFileRecord>(StringComparer.OrdinalIgnoreCase)
            {
                ["deleted_remote.txt"] = new(100, t0),
                ["deleted_local.txt"] = new(200, t0),
                ["conflict.txt"] = new(300, t0),
                ["edited_vs_deleted.txt"] = new(400, t0),
                ["unchanged.txt"] = new(50, t0)
            }
        };

        var localFiles = new Dictionary<string, SyncFileRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["added_local.txt"] = new(11, t1),
            ["deleted_remote.txt"] = new(100, t0),       // unchanged local, missing remote => delete local
            ["conflict.txt"] = new(310, t1),             // changed both => ConflictKeepBothDownloadRemote
            ["edited_vs_deleted.txt"] = new(450, t1),    // edited locally after remote deleted => upload
            ["unchanged.txt"] = new(50, t0)
        };

        var remoteFiles = new Dictionary<string, (string RemoteFullPath, SyncFileRecord Record)>(StringComparer.OrdinalIgnoreCase)
        {
            ["added_remote.txt"] = ("remote/added_remote.txt", new(22, t1)),
            ["deleted_local.txt"] = ("remote/deleted_local.txt", new(200, t0)), // missing local, unchanged remote => delete remote
            ["conflict.txt"] = ("remote/conflict.txt", new(320, t2)),
            ["unchanged.txt"] = ("remote/unchanged.txt", new(50, t0))
        };

        var cfg = new AutoSyncPairConfig { Mode = AutoSyncMode.TwoWay, SyncDeletes = true };
        var plan = AutoSyncEngine.ComputePlan(
            @"C:\local", "remote",
            localFiles, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            remoteFiles, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            baseline, cfg);

        Assert.Equal(1, plan.IdenticalCount);
        Assert.Contains(plan.Actions, a => a.RelativePath == "added_local.txt" && a.Kind == AutoSyncActionKind.UploadFile);
        Assert.Contains(plan.Actions, a => a.RelativePath == "added_remote.txt" && a.Kind == AutoSyncActionKind.DownloadFile);
        Assert.Contains(plan.Actions, a => a.RelativePath == "deleted_remote.txt" && a.Kind == AutoSyncActionKind.DeleteLocalFile);
        Assert.Contains(plan.Actions, a => a.RelativePath == "deleted_local.txt" && a.Kind == AutoSyncActionKind.DeleteRemoteFile);
        Assert.Contains(plan.Actions, a => a.RelativePath == "edited_vs_deleted.txt" && a.Kind == AutoSyncActionKind.UploadFile);
        Assert.Contains(plan.Actions, a => a.RelativePath == "conflict.txt" && a.Kind == AutoSyncActionKind.ConflictKeepBothDownloadRemote);
    }

    [Fact]
    public async Task AutoSyncManager_LiveTwoWaySync_MirrorsAdditionsEditsDeletionsAndSoftTrash()
    {
        string localSyncDir = Path.Combine(_rootTemp, "sync_local");
        string remoteSyncDir = Path.Combine(_shareDir, "sync_remote");
        string stateDir = Path.Combine(_rootTemp, "sync_state");
        Directory.CreateDirectory(localSyncDir);
        Directory.CreateDirectory(remoteSyncDir);

        await File.WriteAllTextAsync(Path.Combine(localSyncDir, "from_local.txt"), "hello from local");
        await File.WriteAllTextAsync(Path.Combine(localSyncDir, "scratch.tmp"), "ignored temp file");
        await File.WriteAllTextAsync(Path.Combine(remoteSyncDir, "from_remote.txt"), "hello from remote");

        await using var server = CreateServer(pin: "246810", requirePin: true, allowUnencrypted: true);
        server.Start();

        var pair = new AutoSyncPairConfig
        {
            Id = "pair1",
            Name = "Test Pair",
            LocalPath = localSyncDir,
            RemotePath = "sync_remote",
            PeerHost = "127.0.0.1",
            PeerPort = server.ListeningPort,
            PeerUseTls = true,
            PeerPin = "246810",
            Mode = AutoSyncMode.TwoWay,
            SyncDeletes = true,
            UseSoftTrash = true,
            PollIntervalSeconds = 30
        };

        await using var manager = new AutoSyncManager(
            cfg => new ConnectionTarget(cfg.PeerHost, cfg.PeerPort, cfg.PeerPin, cfg.PeerUseTls, "TestSyncClient"),
            stateDir,
            useCompression: false,
            verifySha256: true);

        manager.LoadPairs([pair]);
        await manager.TriggerSyncNowAsync("pair1");

        // 1. Initial sync mirrored both additions and skipped *.tmp
        Assert.True(File.Exists(Path.Combine(remoteSyncDir, "from_local.txt")));
        Assert.False(File.Exists(Path.Combine(remoteSyncDir, "scratch.tmp")));
        Assert.True(File.Exists(Path.Combine(localSyncDir, "from_remote.txt")));
        Assert.Equal("hello from remote", await File.ReadAllTextAsync(Path.Combine(localSyncDir, "from_remote.txt")));

        // 2. Edit remote file & delete local file -> updates local & deletes remote
        await Task.Delay(50);
        string remFromRemote = Path.Combine(remoteSyncDir, "from_remote.txt");
        await File.WriteAllTextAsync(remFromRemote, "updated remote content!");
        File.SetLastWriteTimeUtc(remFromRemote, DateTime.UtcNow.AddSeconds(10));
        File.Delete(Path.Combine(localSyncDir, "from_local.txt"));

        await manager.TriggerSyncNowAsync("pair1");
        Assert.Equal("updated remote content!", await File.ReadAllTextAsync(Path.Combine(localSyncDir, "from_remote.txt")));
        Assert.False(File.Exists(Path.Combine(remoteSyncDir, "from_local.txt")));

        // 3. Delete remote file -> soft-trashes local copy into .swiftbeam-trash
        File.Delete(remFromRemote);
        await manager.TriggerSyncNowAsync("pair1");
        Assert.False(File.Exists(Path.Combine(localSyncDir, "from_remote.txt")));
        string trashDir = Path.Combine(localSyncDir, ".swiftbeam-trash");
        Assert.True(Directory.Exists(trashDir));
        Assert.NotEmpty(Directory.GetFiles(trashDir, "from_remote.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MutualReverseConnect_HostAutoConnectsBackAndRecordsHostTransferHistory()
    {
        string dirA = Path.Combine(_rootTemp, "peerA");
        string dirB = Path.Combine(_rootTemp, "peerB");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        await File.WriteAllTextAsync(Path.Combine(dirA, "fromA.txt"), "Payload from PC A");
        string localToPush = Path.Combine(_rootTemp, "pushToB.txt");
        await File.WriteAllTextAsync(localToPush, "Uploaded from PC A to PC B");

        await using var serverA = new FileShareServer(new FileShareServerOptions
        {
            DeviceName = "PC-A",
            Port = 0,
            SharedDirectory = dirA,
            Pin = "111111",
            RequirePin = true,
            AllowUnencrypted = true
        });
        await using var serverB = new FileShareServer(new FileShareServerOptions
        {
            DeviceName = "PC-B",
            Port = 0,
            SharedDirectory = dirB,
            Pin = "222222",
            RequirePin = true,
            AllowUnencrypted = true
        });
        serverA.Start();
        serverB.Start();

        var offerTcs = new TaskCompletionSource<MutualPeerOffer>(TaskCreationOptions.RunContinuationsAsynchronously);
        serverB.MutualPeerOfferReceived += offer => offerTcs.TrySetResult(offer);

        var hostPullHistory = new List<HostTransferRecord>();
        var hostPushHistory = new List<HostTransferRecord>();
        serverA.HostTransferCompleted += r => hostPullHistory.Add(r);
        serverB.HostTransferCompleted += r => hostPushHistory.Add(r);

        // PC A connects to PC B using PIN 222222 and offers a reverse mutual-auth token on Server A
        string revToken = serverA.RegisterReverseConnectToken("127.0.0.1");
        await using var clientA = new TransferClient();
        await clientA.ConnectAsync(new ConnectionTarget(
            "127.0.0.1", serverB.ListeningPort, "222222", true, "PC-A",
            ReverseCallbackPort: serverA.ListeningPort,
            ReverseCallbackToken: revToken));

        var receivedOffer = await offerTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("PC-A", receivedOffer.ClientName);
        Assert.Equal(serverA.ListeningPort, receivedOffer.CallbackPort);
        Assert.Equal(revToken, receivedOffer.MutualAuthToken);

        // PC B connects back to PC A without needing PC A's PIN (111111) using MutualAuthToken
        await using var clientB = new TransferClient();
        var metaA = await clientB.ConnectAsync(new ConnectionTarget(
            receivedOffer.RemoteAddress,
            receivedOffer.CallbackPort,
            Pin: string.Empty,
            UseTls: receivedOffer.TlsActive,
            ClientName: "PC-B",
            MutualAuthToken: receivedOffer.MutualAuthToken));

        Assert.Equal("PC-A", metaA.DeviceName);
        var listA = await clientB.ListDirectoryAsync("");
        Assert.Contains(listA.Entries, e => e.Name == "fromA.txt");

        // PC B pulls fromA.txt from PC A -> Server A records Push history
        var pullHeader = await clientB.PullRangeAsync("fromA.txt", 0, -1, (_, _, _) => ValueTask.CompletedTask);
        Assert.True(pullHeader.Success);
        Assert.Contains(hostPullHistory, h => h.FileName == "fromA.txt" && h.Direction == TransferDirection.Push && h.PeerName == "PC-B");

        // PC A pushes pushToB.txt to PC B -> Server B records Pull (received) history
        byte[] pushBytes = await File.ReadAllBytesAsync(localToPush);
        var pushAck = await clientA.PushRangeAsync("pushToB.txt", localToPush, pushBytes.Length, DateTime.UtcNow, 0, pushBytes.Length, _ => { });
        Assert.True(pushAck.Success);
        var finAck = await clientA.FinalizePushAsync("pushToB.txt", pushBytes.Length, DateTime.UtcNow, null);
        Assert.True(finAck.Success);
        Assert.Contains(hostPushHistory, h => h.FileName == "pushToB.txt" && h.Direction == TransferDirection.Pull && h.PeerName == "PC-A");

        // Revoking token rejects new reverse-token logins
        serverA.RevokeReverseConnectToken(revToken);
        await using var rejectedClient = new TransferClient();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await rejectedClient.ConnectAsync(new ConnectionTarget(
                receivedOffer.RemoteAddress,
                receivedOffer.CallbackPort,
                Pin: string.Empty,
                UseTls: receivedOffer.TlsActive,
                ClientName: "PC-B",
                MutualAuthToken: revToken)));
    }

    [Fact]
    public async Task PushChunk_WhenServerHitsAccessDenied_DrainsDataFramesAndKeepsConnectionInSync()
    {
        // Creating a directory at 'blocked.txt.sbpart' forces File.OpenHandle to throw UnauthorizedAccessException on both Windows & Linux
        string blockingPartDir = Path.Combine(_shareDir, "blocked.txt.sbpart");
        Directory.CreateDirectory(blockingPartDir);

        string srcFile = Path.Combine(_rootTemp, "payload.bin");
        byte[] payload = new byte[128 * 1024];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(srcFile, payload);

        await using var server = CreateServer(pin: "555555", requirePin: true, allowUnencrypted: true);
        server.Start();

        await using var client = new TransferClient();
        await client.ConnectAsync(new ConnectionTarget("127.0.0.1", server.ListeningPort, "555555", true, "SyncTester"));

        // 1. Failing push must return PushChunkAck(Success = false) and drain all DataChunk/DataEnd frames
        var failAck = await client.PushRangeAsync("blocked.txt", srcFile, payload.Length, DateTime.UtcNow, 0, payload.Length, _ => { });
        Assert.False(failAck.Success);
        Assert.Contains("Access denied", failAck.Error);

        // 2. Same connection must remain 100% synchronized (no "Unsupported frame type DataChunk")
        Assert.True(await client.PingAsync());
        var listing = await client.ListDirectoryAsync("");
        Assert.Null(listing.Error);

        // 3. Subsequent upload on the exact same TCP connection must succeed cleanly
        var okAck = await client.PushRangeAsync("allowed.bin", srcFile, payload.Length, DateTime.UtcNow, 0, payload.Length, _ => { });
        Assert.True(okAck.Success, okAck.Error);
        var finAck = await client.FinalizePushAsync("allowed.bin", payload.Length, DateTime.UtcNow, null);
        Assert.True(finAck.Success, finAck.Error);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(_shareDir, "allowed.bin")));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_rootTemp)) Directory.Delete(_rootTemp, recursive: true); } catch { }
    }
}
