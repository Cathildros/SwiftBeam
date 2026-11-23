using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace SwiftBeam.Desktop;

/// <summary>
/// Cross-platform (Windows &amp; Linux) per-user single-instance guard backed by an OS-locked
/// sentinel file plus a named pipe / Unix domain socket listener to wake an existing tray window.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly FileStream? _lockStream;
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private int _disposed;

    public bool IsPrimaryInstance { get; }

    private SingleInstanceGuard(bool isPrimary, FileStream? lockStream, string pipeName)
    {
        IsPrimaryInstance = isPrimary;
        _lockStream = lockStream;
        _pipeName = pipeName;
    }

    public static SingleInstanceGuard TryAcquire()
    {
        string userScope = $"{Environment.UserName}|{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}";
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(userScope));
        string hash = Convert.ToHexString(hashBytes[..8]).ToLowerInvariant();
        string pipeName = $"SwiftBeam_Instance_{hash}";

        string lockDir = ResolveLockDirectory();
        string lockPath = Path.Combine(lockDir, $"instance-{hash}.lock");

        try
        {
            var fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            // On Linux, .NET NamedPipeServerStream maps to /tmp/CoreFxPipe_<pipeName>.
            // Now that we exclusively hold the OS lock, any leftover socket file is stale from a crash.
            if (!OperatingSystem.IsWindows())
            {
                string staleSocket = Path.Combine(Path.GetTempPath(), $"CoreFxPipe_{pipeName}");
                try
                {
                    if (File.Exists(staleSocket))
                        File.Delete(staleSocket);
                }
                catch { }
            }

            return new SingleInstanceGuard(true, fs, pipeName);
        }
        catch (IOException)
        {
            return new SingleInstanceGuard(false, null, pipeName);
        }
        catch
        {
            // Never block launch if lock folder permissions are restricted
            return new SingleInstanceGuard(true, null, pipeName);
        }
    }

    public void TrySignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.None);
            client.Connect(1500);
            using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: false) { AutoFlush = true };
            writer.WriteLine("SHOW");
        }
        catch
        {
            // Existing instance may still be shutting down or starting up
        }
    }

    public void StartListening(Action onActivateRequested)
    {
        if (!IsPrimaryInstance || Volatile.Read(ref _disposed) != 0 || _cts is not null)
            return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _listenTask = Task.Run(() => ListenLoopAsync(onActivateRequested, token), CancellationToken.None);
    }

    private async Task ListenLoopAsync(Action onActivateRequested, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                string? msg = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (msg is null || msg.Equals("SHOW", StringComparison.OrdinalIgnoreCase))
                    onActivateRequested();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private static string ResolveLockDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            try
            {
                string dir = Path.Combine(localAppData, "SwiftBeam");
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch { }
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "SwiftBeam");
        try { Directory.CreateDirectory(tempDir); } catch { }
        return tempDir;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;

        try { _lockStream?.Dispose(); } catch { }
    }
}
