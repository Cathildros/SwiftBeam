using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace SwiftBeam.Core.Server;

public sealed record InstallerArtifacts(
    string? WindowsGuiPath,
    string? WindowsCliPath,
    string? LinuxGuiPath,
    string? LinuxCliPath,
    string? LinuxIconPath = null,
    string? WindowsInstallerPath = null)
{
    public bool HasWindows => !string.IsNullOrEmpty(WindowsGuiPath) && File.Exists(WindowsGuiPath);
    public bool HasWindowsInstaller => !string.IsNullOrEmpty(WindowsInstallerPath) && File.Exists(WindowsInstallerPath);
    public bool HasLinux => !string.IsNullOrEmpty(LinuxGuiPath) && File.Exists(LinuxGuiPath);

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (HasWindows || HasWindowsInstaller) parts.Add("Windows");
            if (HasLinux) parts.Add("Linux");
            return parts.Count > 0 ? $"Ready: {string.Join(" + ", parts)}" : "No binaries found";
        }
    }
}

/// <summary>
/// Zero-dependency HTTP/1.1 local download server using raw TcpListener (requires no admin/URLACL on Windows or Linux).
/// Serves a sleek browser download page + one-line curl/PowerShell installers + SwiftBeam binaries over Wi-Fi or direct cable.
/// </summary>
public sealed class InstallerWebServer : IAsyncDisposable
{
    public const int DefaultHttpPort = 45892;

    private readonly Func<string> _deviceNameProvider;
    private readonly Func<int> _transferPortProvider;
    private readonly int _requestedPort;
    private readonly bool _hasExplicitArtifacts;
    private string? _extraSearchDir;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _downloadsServed;
    private InstallerArtifacts _artifacts;

    public bool IsRunning => _listener is not null && _cts is { IsCancellationRequested: false };
    public int ListeningPort { get; private set; }
    public int DownloadsServed => Volatile.Read(ref _downloadsServed);
    public InstallerArtifacts Artifacts => _artifacts;

    public event Action<string>? Log;
    public event Action<int>? DownloadCountChanged;

    public InstallerWebServer(
        Func<string> deviceNameProvider,
        Func<int> transferPortProvider,
        int port = DefaultHttpPort,
        InstallerArtifacts? explicitArtifacts = null)
    {
        _deviceNameProvider = deviceNameProvider;
        _transferPortProvider = transferPortProvider;
        _requestedPort = port;
        _hasExplicitArtifacts = explicitArtifacts is not null;
        _artifacts = explicitArtifacts ?? DiscoverArtifacts();
    }

    public void RefreshArtifacts(string? extraSearchDir = null)
    {
        if (!string.IsNullOrWhiteSpace(extraSearchDir))
            _extraSearchDir = extraSearchDir;
        _artifacts = DiscoverArtifacts(_extraSearchDir);
    }

    public static InstallerArtifacts DiscoverArtifacts(string? extraSearchDir = null)
    {
        var searchRoots = new List<string>();
        if (!string.IsNullOrWhiteSpace(extraSearchDir) && Directory.Exists(extraSearchDir))
            searchRoots.Add(extraSearchDir);

        string baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir))
        {
            // Prefer published `dist` folder first (prevents picking up 160 KB dev apphost stubs from bin/Debug)
            var parent = Directory.GetParent(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent is not null)
            {
                var cur = parent;
                for (int i = 0; i < 6 && cur is not null; i++)
                {
                    string distCandidate = Path.Combine(cur.FullName, "dist");
                    if (Directory.Exists(distCandidate))
                        searchRoots.Add(distCandidate);
                    cur = cur.Parent;
                }
                // Sibling directory (e.g., running from dist/win-x64 -> finds dist/linux-x64)
                searchRoots.Add(parent.FullName);
            }

            searchRoots.Add(Path.Combine(baseDir, "bundles"));
            searchRoots.Add(baseDir);
        }

        string? winGui = FindStandaloneBinary(searchRoots, ["win-x64/SwiftBeam.exe", "SwiftBeam.exe"], expectWindowsPe: true);
        string? winCli = FindStandaloneBinary(searchRoots, ["win-x64/swiftbeam-cli.exe", "swiftbeam-cli.exe"], expectWindowsPe: true);
        string? winInstaller = FindStandaloneBinary(searchRoots, ["win-x64/SwiftBeam-Setup.exe", "SwiftBeam-Setup.exe"], expectWindowsPe: true);
        string? linuxGui = FindStandaloneBinary(searchRoots, ["linux-x64/SwiftBeam", "SwiftBeam"], expectWindowsPe: false);
        string? linuxCli = FindStandaloneBinary(searchRoots, ["linux-x64/swiftbeam-cli", "swiftbeam-cli"], expectWindowsPe: false);
        string? linuxIcon = null;
        if (!string.IsNullOrEmpty(linuxGui))
        {
            string candidate = Path.Combine(Path.GetDirectoryName(linuxGui) ?? string.Empty, "swiftbeam.png");
            if (File.Exists(candidate))
                linuxIcon = candidate;
        }

        // Fallback: serve our own running single-file executable if it is truly self-contained (>= 5 MB)
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) && File.Exists(processPath))
        {
            var selfInfo = new FileInfo(processPath);
            string exeName = Path.GetFileName(processPath);
            bool isDotnetHost = exeName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
                                exeName.Equals("dotnet", StringComparison.OrdinalIgnoreCase);

            if (!isDotnetHost && selfInfo.Length >= 5 * 1024 * 1024)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && string.IsNullOrEmpty(winGui) &&
                    exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    winGui = processPath;
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && string.IsNullOrEmpty(linuxGui) &&
                         LooksLikeElfBinary(processPath))
                {
                    linuxGui = processPath;
                }
            }
        }

        return new InstallerArtifacts(winGui, winCli, linuxGui, linuxCli, linuxIcon, winInstaller);
    }

    private static string? FindStandaloneBinary(List<string> roots, string[] relativeCandidates, bool expectWindowsPe)
    {
        const long MinStandaloneBytes = 5 * 1024 * 1024; // Reject ~160 KB non-standalone apphost stubs

        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (string rel in relativeCandidates)
            {
                string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) continue;

                try
                {
                    if (new FileInfo(full).Length < MinStandaloneBytes)
                        continue;

                    if (expectWindowsPe && !LooksLikeWindowsPe(full))
                        continue;

                    if (!expectWindowsPe && !LooksLikeElfBinary(full))
                        continue;

                    return full;
                }
                catch
                {
                    continue;
                }
            }
        }
        return null;
    }

    private static bool LooksLikeWindowsPe(string path)
    {
        try
        {
            byte[] magic = new byte[2];
            using var fs = File.OpenRead(path);
            return fs.Read(magic, 0, 2) == 2 && magic[0] == (byte)'M' && magic[1] == (byte)'Z';
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeElfBinary(string path)
    {
        try
        {
            byte[] magic = new byte[4];
            using var fs = File.OpenRead(path);
            if (fs.Read(magic, 0, 4) < 4) return false;
            return magic[0] == 0x7F && magic[1] == (byte)'E' && magic[2] == (byte)'L' && magic[3] == (byte)'F';
        }
        catch
        {
            return false;
        }
    }

    public void Start()
    {
        if (IsRunning) return;
        if (!_hasExplicitArtifacts)
            RefreshArtifacts();
        _cts = new CancellationTokenSource();

        try
        {
            _listener = new TcpListener(IPAddress.IPv6Any, _requestedPort);
            _listener.Server.DualMode = true;
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start(backlog: 32);
        }
        catch (SocketException)
        {
            _listener = new TcpListener(IPAddress.Any, _requestedPort);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start(backlog: 32);
        }

        ListeningPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Log?.Invoke($"Local installer page active on port {ListeningPort} ({_artifacts.Summary}).");
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        try { _listener?.Stop(); } catch { }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }
        _listener = null;
        _cts.Dispose();
        _cts = null;
        Log?.Invoke("Local installer page stopped.");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; }

            _ = Task.Run(() => HandleHttpClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleHttpClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                client.SendBufferSize = 256 * 1024;
                client.ReceiveTimeout = 10_000;

                using var stream = client.GetStream();
                var req = await ReadHttpRequestAsync(stream, ct).ConfigureAwait(false);
                if (req is null) return;

                string method = req.Value.Method;
                string path = req.Value.Path;
                string hostHeader = req.Value.HostHeader;

                if (method is not ("GET" or "HEAD"))
                {
                    await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "text/plain", "Method Not Allowed", headOnly: false, ct).ConfigureAwait(false);
                    return;
                }

                bool headOnly = method == "HEAD";
                string baseUrl = $"http://{hostHeader}";

                switch (path)
                {
                    case "/":
                    case "/index.html":
                    {
                        string html = BuildLandingPageHtml(baseUrl);
                        await WriteTextResponseAsync(stream, 200, "OK", "text/html; charset=utf-8", html, headOnly, ct).ConfigureAwait(false);
                        break;
                    }

                    case "/install.sh":
                    {
                        string script = BuildLinuxInstallScript(baseUrl);
                        await WriteTextResponseAsync(stream, 200, "OK", "text/plain; charset=utf-8", script, headOnly, ct).ConfigureAwait(false);
                        break;
                    }

                    case "/install.ps1":
                    {
                        string script = BuildWindowsInstallScript(baseUrl);
                        await WriteTextResponseAsync(stream, 200, "OK", "text/plain; charset=utf-8", script, headOnly, ct).ConfigureAwait(false);
                        break;
                    }

                    case "/download/windows/SwiftBeam.exe":
                        await StreamArtifactAsync(stream, _artifacts.WindowsGuiPath, "SwiftBeam.exe", headOnly, ct).ConfigureAwait(false);
                        break;

                    case "/download/windows/SwiftBeam-Setup.exe":
                        await StreamArtifactAsync(stream, _artifacts.WindowsInstallerPath, "SwiftBeam-Setup.exe", headOnly, ct).ConfigureAwait(false);
                        break;

                    case "/download/windows/swiftbeam-cli.exe":
                        await StreamArtifactAsync(stream, _artifacts.WindowsCliPath, "swiftbeam-cli.exe", headOnly, ct).ConfigureAwait(false);
                        break;

                    case "/download/linux/SwiftBeam":
                        await StreamArtifactAsync(stream, _artifacts.LinuxGuiPath, "SwiftBeam", headOnly, ct).ConfigureAwait(false);
                        break;

                    case "/download/linux/swiftbeam-cli":
                        await StreamArtifactAsync(stream, _artifacts.LinuxCliPath, "swiftbeam-cli", headOnly, ct).ConfigureAwait(false);
                        break;

                    case "/download/linux/swiftbeam.png":
                        await StreamArtifactAsync(stream, _artifacts.LinuxIconPath, "swiftbeam.png", headOnly, ct).ConfigureAwait(false);
                        break;

                    default:
                        await WriteTextResponseAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", "Not Found", headOnly, ct).ConfigureAwait(false);
                        break;
                }
            }
            catch
            {
                // Ignore transient client disconnects
            }
        }
    }

    private async Task<(string Method, string Path, string HostHeader)?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] buf = new byte[8192];
        int total = 0;
        int headerEnd = -1;

        while (total < buf.Length)
        {
            int n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct).ConfigureAwait(false);
            if (n == 0) return null;
            total += n;

            headerEnd = FindHeaderEnd(buf.AsSpan(0, total));
            if (headerEnd >= 0) break;
        }

        if (headerEnd < 0) return null;

        string headerText = Encoding.ASCII.GetString(buf, 0, headerEnd);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;

        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        string method = requestLine[0].ToUpperInvariant();
        string rawTarget = requestLine[1];
        if (rawTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(rawTarget, UriKind.Absolute, out var absUri))
        {
            rawTarget = absUri.AbsolutePath;
        }
        int qIdx = rawTarget.IndexOf('?');
        string path = qIdx >= 0 ? rawTarget[..qIdx] : rawTarget;

        string host = $"localhost:{ListeningPort}";
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                string val = lines[i][5..].Trim();
                if (!string.IsNullOrEmpty(val))
                    host = val;
                break;
            }
        }

        return (method, path, host);
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> span)
    {
        for (int i = 0; i <= span.Length - 4; i++)
        {
            if (span[i] == '\r' && span[i + 1] == '\n' && span[i + 2] == '\r' && span[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private static async Task WriteTextResponseAsync(
        Stream stream,
        int statusCode,
        string reason,
        string contentType,
        string body,
        bool headOnly,
        CancellationToken ct)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string headers =
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            $"Cache-Control: no-store\r\n" +
            $"Connection: close\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        if (!headOnly && bodyBytes.Length > 0)
            await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task StreamArtifactAsync(Stream stream, string? filePath, string downloadFileName, bool headOnly, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "text/plain; charset=utf-8",
                $"Artifact '{downloadFileName}' is not available on this host.", headOnly, ct).ConfigureAwait(false);
            return;
        }

        var fi = new FileInfo(filePath);
        string headers =
            $"HTTP/1.1 200 OK\r\n" +
            $"Content-Type: application/octet-stream\r\n" +
            $"Content-Disposition: attachment; filename=\"{downloadFileName}\"\r\n" +
            $"Content-Length: {fi.Length}\r\n" +
            $"Cache-Control: no-store\r\n" +
            $"Connection: close\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);

        if (headOnly)
        {
            await stream.FlushAsync(ct).ConfigureAwait(false);
            return;
        }

        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            int read;
            while ((read = await fs.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        int count = Interlocked.Increment(ref _downloadsServed);
        DownloadCountChanged?.Invoke(count);
        Log?.Invoke($"Served installer binary: {downloadFileName} ({TransferManagerSize(fi.Length)})");
    }

    private static string TransferManagerSize(long bytes)
    {
        return $"{bytes / (1024.0 * 1024.0):0.0} MB";
    }

    private string BuildLinuxInstallScript(string baseUrl)
    {
        bool hasCli = !string.IsNullOrEmpty(_artifacts.LinuxCliPath) && File.Exists(_artifacts.LinuxCliPath);
        bool hasIcon = !string.IsNullOrEmpty(_artifacts.LinuxIconPath) && File.Exists(_artifacts.LinuxIconPath);
        string cliBlock = hasCli
            ? $$"""
              echo "Downloading swiftbeam-cli..."
              download "{{baseUrl}}/download/linux/swiftbeam-cli" "${BIN_DIR}/swiftbeam-cli"
              chmod +x "${BIN_DIR}/swiftbeam-cli"
              """
            : "";
        string iconBlock = hasIcon
            ? $$"""
              ICON_DIR="${HOME}/.local/share/icons/hicolor/256x256/apps"
              mkdir -p "${ICON_DIR}"
              download "{{baseUrl}}/download/linux/swiftbeam.png" "${ICON_DIR}/swiftbeam.png" || true
              """
            : "";

        return $$"""
            #!/usr/bin/env bash
            set -euo pipefail

            BIN_DIR="${HOME}/.local/bin"
            APP_DIR="${HOME}/.local/share/applications"
            mkdir -p "${BIN_DIR}" "${APP_DIR}"

            download() {
              local url="$1"
              local out="$2"
              if command -v curl >/dev/null 2>&1; then
                curl -fL --progress-bar "$url" -o "$out"
              elif command -v wget >/dev/null 2>&1; then
                wget -q --show-progress "$url" -O "$out"
              else
                echo "Error: curl or wget is required." >&2
                exit 1
              fi
            }

            echo "Downloading SwiftBeam from {{baseUrl}}..."
            download "{{baseUrl}}/download/linux/SwiftBeam" "${BIN_DIR}/SwiftBeam"
            chmod +x "${BIN_DIR}/SwiftBeam"
            {{cliBlock}}
            {{iconBlock}}

            cat > "${APP_DIR}/swiftbeam.desktop" <<EOF
            [Desktop Entry]
            Name=SwiftBeam
            Comment=Fast cross-platform parallel file transfer
            Exec=${BIN_DIR}/SwiftBeam
            Icon=swiftbeam
            StartupWMClass=SwiftBeam
            Terminal=false
            Type=Application
            Categories=Network;FileTransfer;Utility;
            EOF

            if command -v update-desktop-database >/dev/null 2>&1; then
              update-desktop-database "${APP_DIR}" >/dev/null 2>&1 || true
            fi
            if command -v gtk-update-icon-cache >/dev/null 2>&1; then
              gtk-update-icon-cache -f -t "${HOME}/.local/share/icons/hicolor" >/dev/null 2>&1 || true
            fi
            if [[ -d "${HOME}/Desktop" ]]; then
              cp "${APP_DIR}/swiftbeam.desktop" "${HOME}/Desktop/SwiftBeam.desktop"
              chmod 755 "${HOME}/Desktop/SwiftBeam.desktop"
              if command -v gio >/dev/null 2>&1; then
                gio set "${HOME}/Desktop/SwiftBeam.desktop" metadata::trusted true >/dev/null 2>&1 || true
              fi
            fi

            echo ""
            echo "SwiftBeam installed to ${BIN_DIR}/SwiftBeam"
            echo "Launch it from your app menu or run:  ${BIN_DIR}/SwiftBeam &"
            """.Replace("\r\n", "\n");
    }

    private string BuildWindowsInstallScript(string baseUrl)
    {
        bool hasCli = !string.IsNullOrEmpty(_artifacts.WindowsCliPath) && File.Exists(_artifacts.WindowsCliPath);
        string cliBlock = hasCli
            ? $"""
              Write-Host "Downloading swiftbeam-cli.exe..." -ForegroundColor Cyan
              Invoke-WebRequest -Uri "{baseUrl}/download/windows/swiftbeam-cli.exe" -OutFile (Join-Path $installDir "swiftbeam-cli.exe")
              """
            : "";

        return $$"""
            $ErrorActionPreference = "Stop"
            $installDir = Join-Path $env:LOCALAPPDATA "SwiftBeam"
            New-Item -ItemType Directory -Force -Path $installDir | Out-Null

            $exePath = Join-Path $installDir "SwiftBeam.exe"
            Write-Host "Downloading SwiftBeam.exe from {{baseUrl}}..." -ForegroundColor Cyan
            Invoke-WebRequest -Uri "{{baseUrl}}/download/windows/SwiftBeam.exe" -OutFile $exePath
            {{cliBlock}}

            $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
            if (-not ($userPath -split ';' | Where-Object { $_ -eq $installDir })) {
                [Environment]::SetEnvironmentVariable("Path", ($userPath.TrimEnd(';') + ";" + $installDir), "User")
            }

            $startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
            $shell = New-Object -ComObject WScript.Shell
            $shortcut = $shell.CreateShortcut((Join-Path $startMenu "SwiftBeam.lnk"))
            $shortcut.TargetPath = $exePath
            $shortcut.WorkingDirectory = $installDir
            $shortcut.Description = "SwiftBeam — Fast Cross-Platform File Transfer"
            $shortcut.Save()

            Write-Host "Installed SwiftBeam to $installDir and added Start Menu shortcut!" -ForegroundColor Green
            Write-Host "Launching SwiftBeam..." -ForegroundColor Green
            Start-Process $exePath
            """;
    }

    private string BuildLandingPageHtml(string baseUrl)
    {
        string deviceName = WebUtility.HtmlEncode(_deviceNameProvider());
        int transferPort = _transferPortProvider();
        string winSize = _artifacts.HasWindowsInstaller
            ? TransferManagerSize(new FileInfo(_artifacts.WindowsInstallerPath!).Length)
            : (_artifacts.HasWindows ? TransferManagerSize(new FileInfo(_artifacts.WindowsGuiPath!).Length) : "");
        string linuxSize = _artifacts.HasLinux ? TransferManagerSize(new FileInfo(_artifacts.LinuxGuiPath!).Length) : "";

        string winButtons = _artifacts.HasWindowsInstaller
            ? (_artifacts.HasWindows
                ? """
                  <a class="btn primary" href="/download/windows/SwiftBeam-Setup.exe">Download Windows Installer (SwiftBeam-Setup.exe)</a>
                  <a style="color:#a78bfa;font-size:12px;text-align:center;" href="/download/windows/SwiftBeam.exe">Or download portable standalone SwiftBeam.exe</a>
                  """
                : """
                  <a class="btn primary" href="/download/windows/SwiftBeam-Setup.exe">Download Windows Installer (SwiftBeam-Setup.exe)</a>
                  """)
            : """
              <a class="btn primary" href="/download/windows/SwiftBeam.exe">Download SwiftBeam.exe</a>
              """;

        string winCard = (_artifacts.HasWindows || _artifacts.HasWindowsInstaller)
            ? $$"""
              <div class="card" id="card-win">
                <div class="card-head">
                  <h2>Windows (64-bit)</h2>
                  <span class="badge" id="badge-win">{{winSize}}</span>
                </div>
                <p>Zero-UAC installer: puts SwiftBeam in your Apps folder with Start Menu, Desktop shortcut &amp; auto-updates.</p>
                {{winButtons}}
                <div class="cmd-label">Or install &amp; launch in 1 step (PowerShell):</div>
                <div class="cmd-row">
                  <code id="cmd-win">irm {{baseUrl}}/install.ps1 | iex</code>
                  <button class="btn copy" onclick="copyText('cmd-win', this)">Copy</button>
                </div>
              </div>
              """
            : """
              <div class="card muted">
                <h2>Windows (64-bit)</h2>
                <p>Windows binary not found on host. Run <code>scripts/publish.ps1</code> to include both platforms.</p>
              </div>
              """;

        string linuxCard = _artifacts.HasLinux
            ? $$"""
              <div class="card" id="card-linux">
                <div class="card-head">
                  <h2>Linux (64-bit)</h2>
                  <span class="badge" id="badge-linux">{{linuxSize}}</span>
                </div>
                <p>Standalone native GUI binary. Works over Wi-Fi and direct Ethernet cable.</p>
                <a class="btn primary" href="/download/linux/SwiftBeam">Download SwiftBeam</a>
                <div class="cmd-label">Or install to ~/.local/bin &amp; app menu (Terminal):</div>
                <div class="cmd-row">
                  <code id="cmd-linux">curl -fsSL {{baseUrl}}/install.sh | bash</code>
                  <button class="btn copy" onclick="copyText('cmd-linux', this)">Copy</button>
                </div>
              </div>
              """
            : """
              <div class="card muted">
                <h2>Linux (64-bit)</h2>
                <p>Linux binary not found on host. Run <code>scripts/publish.ps1</code> to include both platforms.</p>
              </div>
              """;

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>Get SwiftBeam — Local Install from {{deviceName}}</title>
              <style>
                :root { color-scheme: dark; }
                * { box-sizing: border-box; }
                body {
                  margin: 0; font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif;
                  background: #0b1120; color: #f1f5f9; min-height: 100vh;
                  display: flex; align-items: center; justify-content: center; padding: 24px;
                }
                .wrap { max-width: 860px; width: 100%; }
                header { display: flex; align-items: center; gap: 14px; margin-bottom: 24px; }
                .logo {
                  width: 44px; height: 44px; border-radius: 10px; background: #8b5cf6;
                  display: grid; place-items: center; font-weight: 800; font-size: 18px; color: #fff;
                }
                h1 { margin: 0; font-size: 24px; }
                .sub { color: #94a3b8; font-size: 14px; margin-top: 2px; }
                .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); gap: 16px; }
                .card {
                  background: #111827; border: 1px solid #1f2937; border-radius: 12px;
                  padding: 20px; display: flex; flex-direction: column; gap: 12px;
                }
                .card.recommended { border-color: #8b5cf6; box-shadow: 0 0 0 1px #8b5cf655; }
                .card.muted { opacity: 0.65; }
                .card-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
                .card h2 { margin: 0; font-size: 18px; }
                .card p { margin: 0; color: #94a3b8; font-size: 13px; line-height: 1.5; }
                .badge {
                  background: #2e1065; color: #a78bfa; border-radius: 6px;
                  padding: 4px 8px; font-size: 12px; font-weight: 600;
                }
                .badge.rec { background: #064e3b; color: #34d399; }
                .btn {
                  display: inline-block; text-align: center; text-decoration: none;
                  border: none; border-radius: 8px; padding: 11px 16px; font-weight: 600;
                  font-size: 14px; cursor: pointer;
                }
                .btn.primary { background: #8b5cf6; color: #fff; }
                .btn.primary:hover { background: #a78bfa; }
                .btn.copy { background: #1e293b; color: #e2e8f0; padding: 8px 12px; font-size: 12px; white-space: nowrap; }
                .btn.copy:hover { background: #334155; }
                .cmd-label { font-size: 12px; color: #94a3b8; margin-top: 4px; }
                .cmd-row {
                  display: flex; align-items: center; gap: 8px;
                  background: #0b1120; border: 1px solid #1e293b; border-radius: 8px; padding: 8px 10px;
                }
                code {
                  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
                  font-size: 12px; color: #a78bfa; overflow-x: auto; flex: 1; white-space: nowrap;
                }
                footer {
                  margin-top: 20px; background: #0f172a; border: 1px solid #1e293b;
                  border-radius: 10px; padding: 14px 16px; font-size: 13px; color: #94a3b8; line-height: 1.6;
                }
                footer strong { color: #f8fafc; }
              </style>
            </head>
            <body>
              <div class="wrap">
                <header>
                  <div class="logo">SB</div>
                  <div>
                    <h1>Install SwiftBeam</h1>
                    <div class="sub">Served directly over your local network from <strong>{{deviceName}}</strong> &bull; No internet or USB stick needed</div>
                  </div>
                </header>
                <div class="grid">
                  {{winCard}}
                  {{linuxCard}}
                </div>
                <footer>
                  <strong>Next step after opening SwiftBeam:</strong>
                  Look for <strong>{{deviceName}}</strong> in <em>Nearby Computers</em> (or enter this computer's IP on port <code>{{transferPort}}</code>),
                  type the 6-digit Pairing PIN displayed on <strong>{{deviceName}}</strong>'s screen, and start pulling files in parallel!
                </footer>
              </div>
              <script>
                (function() {
                  var ua = (navigator.userAgent || '').toLowerCase();
                  if (ua.indexOf('windows') !== -1) {
                    var c = document.getElementById('card-win');
                    var b = document.getElementById('badge-win');
                    if (c) c.classList.add('recommended');
                    if (b) { b.classList.add('rec'); b.textContent = 'This PC • ' + b.textContent; }
                  } else if (ua.indexOf('linux') !== -1) {
                    var c = document.getElementById('card-linux');
                    var b = document.getElementById('badge-linux');
                    if (c) c.classList.add('recommended');
                    if (b) { b.classList.add('rec'); b.textContent = 'This PC • ' + b.textContent; }
                  }
                })();
                function copyText(id, btn) {
                  var text = document.getElementById(id).textContent;
                  if (navigator.clipboard && navigator.clipboard.writeText) {
                    navigator.clipboard.writeText(text).then(function() {
                      btn.textContent = 'Copied!';
                      setTimeout(function() { btn.textContent = 'Copy'; }, 1500);
                    });
                  } else {
                    var ta = document.createElement('textarea');
                    ta.value = text; document.body.appendChild(ta); ta.select();
                    document.execCommand('copy'); document.body.removeChild(ta);
                    btn.textContent = 'Copied!';
                    setTimeout(function() { btn.textContent = 'Copy'; }, 1500);
                  }
                }
              </script>
            </body>
            </html>
            """;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
