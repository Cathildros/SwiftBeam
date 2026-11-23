using System.Security.Cryptography;
using SwiftBeam.Core.Client;
using SwiftBeam.Core.Discovery;
using SwiftBeam.Core.Protocol;
using SwiftBeam.Core.Security;
using SwiftBeam.Core.Server;
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
            bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb *SwiftBeam
            """;
        Assert.Equal(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            GitHubUpdateService.ExtractSha256ForAsset(checksums, "SwiftBeam-Setup.exe"));
        Assert.Equal(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            GitHubUpdateService.ExtractSha256ForAsset(checksums, "SwiftBeam"));
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
                  "name": "SwiftBeam",
                  "browser_download_url": "https://github.com/Cathildros/SwiftBeam/releases/download/v1.2.0/SwiftBeam",
                  "size": 48200000
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
        Assert.Equal("SwiftBeam", linuxUpdate!.AssetName);

        var upToDate = GitHubUpdateService.ParseReleaseJson(releaseJson, new Version(1, 2, 0), isWindows: true, isLinux: false, out _);
        Assert.Null(upToDate);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_rootTemp)) Directory.Delete(_rootTemp, recursive: true); } catch { }
    }
}
