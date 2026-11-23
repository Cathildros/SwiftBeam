using SwiftBeam.Core.Client;
using SwiftBeam.Core.Discovery;
using SwiftBeam.Core.Protocol;
using SwiftBeam.Core.Security;
using SwiftBeam.Core.Server;

namespace SwiftBeam.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        string cmd = args[0].ToLowerInvariant();
        return cmd switch
        {
            "serve" => await RunServeAsync(args[1..]),
            "discover" => await RunDiscoverAsync(args[1..]),
            "pull" => await RunPullAsync(args[1..]),
            "share-app" => await RunShareAppAsync(args[1..]),
            _ => UnknownCommand(cmd)
        };
    }

    private static int UnknownCommand(string cmd)
    {
        Console.Error.WriteLine($"Unknown command: {cmd}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            SwiftBeam CLI — lightweight cross-platform parallel file transfer

            Usage:
              swiftbeam-cli serve [--dir <path>] [--port <45890>] [--pin <6-digit|none>] [--require-tls]
              swiftbeam-cli discover [--seconds <5>]
              swiftbeam-cli pull --host <ip> [--port <45890>] [--pin <pin>] [--remote <path>] [--out <dir>] [--parallel <4>] [--no-tls]
              swiftbeam-cli share-app [--port <45892>] [--binaries <dir>]
            """);
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static async Task<int> RunServeAsync(string[] args)
    {
        string dir = GetArg(args, "--dir") ?? Directory.GetCurrentDirectory();
        int port = int.TryParse(GetArg(args, "--port"), out int p) ? p : ProtocolConstants.DefaultTransferPort;
        string? pinArg = GetArg(args, "--pin");
        bool requirePin = !string.Equals(pinArg, "none", StringComparison.OrdinalIgnoreCase);
        string pin = requirePin ? (string.IsNullOrWhiteSpace(pinArg) ? PinAuth.GeneratePin() : PinAuth.NormalizePin(pinArg)) : string.Empty;
        bool requireTls = HasFlag(args, "--require-tls");

        var opts = new FileShareServerOptions
        {
            DeviceName = Environment.MachineName,
            Port = port,
            SharedDirectory = Path.GetFullPath(dir),
            RequirePin = requirePin,
            Pin = pin,
            AllowUnencrypted = !requireTls
        };

        await using var server = new FileShareServer(opts);
        server.Log += msg => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
        server.Start();

        await using var discovery = new PeerDiscoveryService(
            opts.ServerId,
            ProtocolConstants.DefaultDiscoveryPort,
            () => (opts.DeviceName, server.ListeningPort, opts.RequirePin, opts.AllowUnencrypted, server.IsRunning));
        discovery.Start();

        Console.WriteLine($"Sharing: {opts.SharedDirectory}");
        Console.WriteLine($"Port:    {server.ListeningPort}");
        Console.WriteLine($"PIN:     {(requirePin ? PinAuth.FormatPin(pin) : "(disabled)")}");
        Console.WriteLine($"TLS:     {(requireTls ? "Required" : "Optional (enabled by default)")}");
        Console.WriteLine("Addresses:");
        foreach (var addr in NetworkInfo.GetActiveAddresses())
        {
            Console.WriteLine($"  [{addr.LinkBadge}] {addr.DisplayAddress}:{server.ListeningPort} ({addr.InterfaceName} {addr.SpeedLabel})");
        }
        Console.WriteLine("Press Ctrl+C to stop.");

        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.TrySetResult(); };
        await tcs.Task;
        return 0;
    }

    private static async Task<int> RunDiscoverAsync(string[] args)
    {
        int seconds = int.TryParse(GetArg(args, "--seconds"), out int s) ? Math.Clamp(s, 1, 30) : 5;
        await using var discovery = new PeerDiscoveryService(
            Guid.NewGuid().ToString("N")[..12],
            ProtocolConstants.DefaultDiscoveryPort,
            () => (Environment.MachineName, 0, true, true, false));

        discovery.PeersChanged += peers =>
        {
            foreach (var peer in peers)
            {
                var best = peer.BestEndpoint;
                if (best is not null)
                    Console.WriteLine($"Found: {peer.DeviceName} ({peer.OsPlatform}) -> {best.DisplayLabel} | PIN: {(peer.RequiresPin ? "Required" : "None")}");
            }
        };

        discovery.Start();
        Console.WriteLine($"Scanning Wi-Fi, Wired, and Direct Cable links for {seconds}s...");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        var finalPeers = discovery.GetPeersSnapshot();
        if (finalPeers.Count == 0)
            Console.WriteLine("No peers discovered.");
        return 0;
    }

    private static async Task<int> RunPullAsync(string[] args)
    {
        string? host = GetArg(args, "--host");
        if (string.IsNullOrWhiteSpace(host))
        {
            Console.Error.WriteLine("--host is required.");
            return 1;
        }

        int port = int.TryParse(GetArg(args, "--port"), out int p) ? p : ProtocolConstants.DefaultTransferPort;
        string pin = GetArg(args, "--pin") ?? string.Empty;
        string remote = GetArg(args, "--remote") ?? string.Empty;
        string outDir = Path.GetFullPath(GetArg(args, "--out") ?? Directory.GetCurrentDirectory());
        int parallel = int.TryParse(GetArg(args, "--parallel"), out int w) ? Math.Clamp(w, 1, 16) : 4;
        bool useTls = !HasFlag(args, "--no-tls");

        var target = new ConnectionTarget(host, port, pin, useTls, Environment.MachineName);

        await using var browseClient = new TransferClient();
        var meta = await browseClient.ConnectAsync(target);
        Console.WriteLine($"Connected to {meta.DeviceName} ({meta.OsPlatform}) | TLS: {(meta.TlsActive ? "ON" : "OFF")}");

        await using var manager = new TransferManager { MaxConcurrency = parallel };
        manager.ConfigureTarget(target);
        manager.Log += msg => Console.WriteLine($"\n{msg}");
        manager.TelemetryUpdated += snap =>
        {
            string speed = $"{TransferManager.FormatBytes((long)snap.AggregateBytesPerSec)}/s";
            string eta = snap.EstimatedTimeRemaining is { } t ? t.ToString(@"hh\:mm\:ss") : "--:--:--";
            Console.Write($"\rProgress: {snap.OverallProgressPercent:0.0}% ({TransferManager.FormatBytes(snap.TotalBytesTransferred)} / {TransferManager.FormatBytes(snap.TotalBytesQueued)}) | {speed} | ETA {eta} | Workers: {snap.ActiveWorkers}   ");
        };

        // Check if remote path is a single file or directory
        var info = await browseClient.GetFileInfoAsync(remote);
        if (info.Exists)
        {
            var entry = new RemoteEntry(Path.GetFileName(remote), remote, false, info.Size, info.LastModifiedUtc);
            await manager.EnqueueEntriesAsync(browseClient, [entry], outDir, Path.GetDirectoryName(remote)?.Replace('\\', '/') ?? string.Empty);
        }
        else
        {
            var listing = await browseClient.ListDirectoryAsync(remote, recursive: true);
            if (!string.IsNullOrEmpty(listing.Error))
            {
                Console.Error.WriteLine($"Remote error: {listing.Error}");
                return 1;
            }
            var files = listing.Entries.Where(e => !e.IsDirectory).ToList();
            Console.WriteLine($"Queued {files.Count} file(s) with {parallel} parallel workers...");
            await manager.EnqueueEntriesAsync(browseClient, files, outDir, remote);
        }

        await manager.WaitForAllAsync();
        Console.WriteLine("\nAll transfers finished.");
        return 0;
    }

    private static async Task<int> RunShareAppAsync(string[] args)
    {
        int port = int.TryParse(GetArg(args, "--port"), out int p) ? p : InstallerWebServer.DefaultHttpPort;
        string? binDir = GetArg(args, "--binaries");

        await using var web = new InstallerWebServer(
            () => Environment.MachineName,
            () => ProtocolConstants.DefaultTransferPort,
            port);
        if (!string.IsNullOrWhiteSpace(binDir))
            web.RefreshArtifacts(binDir);

        web.Log += msg => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
        web.Start();

        Console.WriteLine($"Local installer download page active ({web.Artifacts.Summary}):");
        foreach (var addr in NetworkInfo.GetActiveAddresses().Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
        {
            Console.WriteLine($"  [{addr.LinkBadge}] http://{addr.Address}:{web.ListeningPort}");
        }
        Console.WriteLine("Press Ctrl+C to stop.");

        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.TrySetResult(); };
        await tcs.Task;
        return 0;
    }
}
