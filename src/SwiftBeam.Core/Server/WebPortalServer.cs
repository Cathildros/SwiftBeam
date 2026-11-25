using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwiftBeam.Core.Client;
using SwiftBeam.Core.Protocol;

namespace SwiftBeam.Core.Server;

/// <summary>
/// Built-in Mobile &amp; Browser Companion HTTP portal for SwiftBeam.
/// Lets phones (iOS / Android) and browsers upload, download, zip-download, manage folders, and share text over Wi-Fi/LAN.
/// </summary>
public sealed class WebPortalServer : IAsyncDisposable
{
    public const int DefaultHttpPort = 45893;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly byte[] PortalHtmlBytes = Encoding.UTF8.GetBytes(PortalHtml);

    private readonly FileShareServer _fileServer;
    private readonly int _requestedPort;
    private readonly ConcurrentDictionary<string, DateTime> _sessions = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _uploadedCount;
    private int _downloadedCount;
    private string _latestHostText = string.Empty;

    public bool IsRunning => _listener is not null && _cts is { IsCancellationRequested: false };
    public int ListeningPort { get; private set; }
    public string QrLoginToken { get; private set; } = GenerateToken(16);
    public int UploadedCount => Volatile.Read(ref _uploadedCount);
    public int DownloadedCount => Volatile.Read(ref _downloadedCount);
    public string LatestHostText => _latestHostText;

    public event Action<string>? Log;
    public event Action<string, string>? TextReceived;
    public event Action? StatsChanged;

    public WebPortalServer(FileShareServer fileServer, int port = DefaultHttpPort)
    {
        _fileServer = fileServer;
        _requestedPort = port;
    }

    public void SetHostSharedText(string text)
    {
        _latestHostText = text ?? string.Empty;
    }

    public void RegenerateQrToken()
    {
        QrLoginToken = GenerateToken(16);
        _sessions.Clear();
    }

    public void Start()
    {
        if (IsRunning) return;

        QrLoginToken = GenerateToken(16);
        _cts = new CancellationTokenSource();
        _listener = TcpListener.Create(_requestedPort);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start(backlog: 64);

        ListeningPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Log?.Invoke($"Mobile & Web Portal listening on http://0.0.0.0:{ListeningPort}");
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            try { _listener?.Stop(); } catch { }
            if (_acceptLoop is not null)
            {
                try { await _acceptLoop.ConfigureAwait(false); } catch { }
            }
            _cts.Dispose();
            _cts = null;
            _acceptLoop = null;
            _listener = null;
        }
        _sessions.Clear();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken ct)
    {
        string remoteIp = (tcpClient.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        using (tcpClient)
        {
            try
            {
                tcpClient.NoDelay = true;
                tcpClient.SendTimeout = 60_000;
                tcpClient.ReceiveTimeout = 60_000;
                await using var stream = tcpClient.GetStream();

                byte[] pendingBytes = [];
                for (int reqIndex = 0; reqIndex < 100 && !ct.IsCancellationRequested; reqIndex++)
                {
                    int idleTimeoutMs = reqIndex == 0 ? 30_000 : 15_000;
                    var parsed = await ReadHttpRequestHeadersAsync(stream, pendingBytes, idleTimeoutMs, ct).ConfigureAwait(false);
                    if (parsed is null) break;

                    var (method, target, httpVersion, headers, leftover) = parsed.Value;
                    pendingBytes = [];

                    int qIdx = target.IndexOf('?');
                    string path = qIdx >= 0 ? target[..qIdx] : target;
                    string query = qIdx >= 0 ? target[(qIdx + 1)..] : string.Empty;
                    var queryParams = ParseQuery(query);

                    bool hasContentLength = headers.TryGetValue("content-length", out string? clHeader);
                    long.TryParse(clHeader, out long contentLength);
                    bool isChunked = headers.TryGetValue("transfer-encoding", out string? teHdr) &&
                                     teHdr.Contains("chunked", StringComparison.OrdinalIgnoreCase);

                    headers.TryGetValue("connection", out string? connHdr);
                    bool keepAlive = !isChunked && reqIndex < 99 && (httpVersion == "HTTP/1.0"
                        ? connHdr?.Contains("keep-alive", StringComparison.OrdinalIgnoreCase) == true
                        : connHdr?.Contains("close", StringComparison.OrdinalIgnoreCase) != true);

                    if ((contentLength <= 0 && (method is "GET" or "HEAD" or "OPTIONS" || hasContentLength)) && leftover.Length > 0)
                    {
                        pendingBytes = leftover;
                        leftover = [];
                    }

                    if (method == "OPTIONS")
                    {
                        await SendEmptyAsync(stream, 204, "No Content", keepAlive, ct, extraHeaders: "Allow: GET, HEAD, POST, OPTIONS\r\n").ConfigureAwait(false);
                        if (!keepAlive) break;
                        continue;
                    }

                    if ((method is "GET" or "HEAD") && path == "/favicon.ico")
                    {
                        await SendEmptyAsync(stream, 204, "No Content", keepAlive, ct).ConfigureAwait(false);
                        if (!keepAlive) break;
                        continue;
                    }

                    string? cookieToken = ExtractSessionCookie(headers);
                    bool hasCookieAuth = !_fileServer.Options.RequirePin || IsValidSession(cookieToken);

                    // Special QR 1-tap login query token on GET /
                    string? setCookieHeader = null;
                    if (method == "GET" && (path == "/" || path == "/index.html") &&
                        queryParams.TryGetValue("qr", out string? qrVal) &&
                        !string.IsNullOrEmpty(qrVal) && string.Equals(qrVal, QrLoginToken, StringComparison.Ordinal))
                    {
                        string newSession = CreateSession();
                        hasCookieAuth = true;
                        setCookieHeader = $"sb_session={newSession}; Path=/; HttpOnly; SameSite=Lax";
                    }

                    if ((method is "GET" or "HEAD") && (path == "/" || path == "/index.html"))
                    {
                        if (method == "GET")
                            Log?.Invoke($"Web/Phone connected: {remoteIp} -> {method} {path}");

                        if (method == "HEAD")
                            await SendHeadAsync(stream, 200, "OK", "text/html; charset=utf-8", PortalHtmlBytes.Length, ct, setCookieHeader, keepAlive).ConfigureAwait(false);
                        else
                            await SendBytesAsync(stream, 200, "OK", "text/html; charset=utf-8", PortalHtmlBytes, ct, setCookieHeader, keepAlive).ConfigureAwait(false);

                        if (!keepAlive) break;
                        continue;
                    }

                    if (method == "GET" && path == "/api/status")
                    {
                        var (home, desktop, downloads, documents) = _fileServer.Options.AllowFullFilesystem
                            ? FileShareServer.GetStandardLocations()
                            : (null, null, null, null);

                        await SendJsonAsync(stream, 200, new
                        {
                            authenticated = hasCookieAuth,
                            deviceName = _fileServer.Options.DeviceName,
                            readOnly = _fileServer.Options.ReadOnly,
                            requirePin = _fileServer.Options.RequirePin,
                            fullFilesystem = _fileServer.Options.AllowFullFilesystem,
                            osPlatform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                            homePath = home,
                            desktopPath = desktop,
                            downloadsPath = downloads,
                            documentsPath = documents
                        }, ct, setCookieHeader, keepAlive).ConfigureAwait(false);
                        if (!keepAlive) break;
                        continue;
                    }

                    if (method == "POST" && path == "/api/auth")
                    {
                        byte[] body = await ReadBoundedBodyAsync(stream, leftover, contentLength, 64 * 1024, ct).ConfigureAwait(false);
                        string? pin = null;
                        string? qr = null;
                        try
                        {
                            using var doc = JsonDocument.Parse(body);
                            if (doc.RootElement.TryGetProperty("pin", out var pEl)) pin = pEl.GetString();
                            if (doc.RootElement.TryGetProperty("qr", out var qEl)) qr = qEl.GetString();
                        }
                        catch { }

                        bool authorized = !_fileServer.Options.RequirePin;
                        if (!authorized && !string.IsNullOrEmpty(qr) && string.Equals(qr, QrLoginToken, StringComparison.Ordinal))
                            authorized = true;
                        if (!authorized && !string.IsNullOrEmpty(pin))
                        {
                            string cleanPin = new(pin.Where(char.IsDigit).ToArray());
                            authorized = string.Equals(cleanPin, _fileServer.Options.Pin, StringComparison.Ordinal);
                        }

                        if (!authorized)
                        {
                            await SendJsonAsync(stream, 401, new { ok = false, error = "Incorrect PIN." }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                            if (!keepAlive) break;
                            continue;
                        }

                        string tok = CreateSession();
                        await SendJsonAsync(stream, 200, new { ok = true }, ct, $"sb_session={tok}; Path=/; HttpOnly; SameSite=Lax", keepAlive).ConfigureAwait(false);
                        if (!keepAlive) break;
                        continue;
                    }

                    // All remaining routes require session auth (when PIN is required)
                    if (!hasCookieAuth)
                    {
                        await DrainFixedBodyAsync(stream, leftover, contentLength, ct).ConfigureAwait(false);
                        await SendJsonAsync(stream, 401, new { error = "Authentication required." }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                        if (!keepAlive) break;
                        continue;
                    }

                    if (method == "GET" && path == "/api/list")
                    {
                        queryParams.TryGetValue("path", out string? rawPath);
                        bool includeHidden = queryParams.TryGetValue("hidden", out string? hid) && (hid == "1" || hid.Equals("true", StringComparison.OrdinalIgnoreCase));
                        var resp = _fileServer.HandleListDir(new ListDirRequestMessage(rawPath ?? string.Empty, Recursive: false, IncludeHidden: includeHidden));
                        await SendJsonAsync(stream, string.IsNullOrEmpty(resp.Error) ? 200 : 400, new
                        {
                            currentPath = resp.CurrentPath,
                            entries = resp.Entries,
                            error = resp.Error,
                            freeDiskBytes = resp.FreeDiskBytes,
                            totalDiskBytes = resp.TotalDiskBytes,
                            readOnly = _fileServer.Options.ReadOnly
                        }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                        if (!keepAlive) break;
                        continue;
                    }

                    if (method == "GET" && path == "/api/download")
                    {
                        queryParams.TryGetValue("path", out string? rawPath);
                        if (!_fileServer.TryResolveSafePath(rawPath ?? string.Empty, out string fullPath, out _) || !File.Exists(fullPath))
                        {
                            await SendJsonAsync(stream, 404, new { error = "File not found." }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                            if (!keepAlive) break;
                            continue;
                        }

                        string fileName = Path.GetFileName(fullPath);
                        await StreamFileDownloadAsync(stream, fullPath, fileName, "application/octet-stream", ct).ConfigureAwait(false);
                        Interlocked.Increment(ref _downloadedCount);
                        StatsChanged?.Invoke();
                        Log?.Invoke($"Web/Phone downloaded: {fileName}");
                        return;
                    }

                    if (method == "GET" && path == "/api/download-zip")
                    {
                        queryParams.TryGetValue("path", out string? rawPath);
                        if (!_fileServer.TryResolveSafePath(rawPath ?? string.Empty, out string fullDir, out _) ||
                            fullDir == FileShareServer.DrivesVirtualPath || !Directory.Exists(fullDir))
                        {
                            await SendJsonAsync(stream, 404, new { error = "Folder not found." }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                            if (!keepAlive) break;
                            continue;
                        }

                        string folderName = Path.GetFileName(fullDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        if (string.IsNullOrWhiteSpace(folderName)) folderName = "SwiftBeamFolder";
                        string tempZip = Path.Combine(Path.GetTempPath(), $"sb_zip_{Guid.NewGuid():N}.zip");
                        try
                        {
                            CreateSafeZipFromDirectory(fullDir, tempZip);
                            await StreamFileDownloadAsync(stream, tempZip, $"{folderName}.zip", "application/zip", ct).ConfigureAwait(false);
                            Interlocked.Increment(ref _downloadedCount);
                            StatsChanged?.Invoke();
                            Log?.Invoke($"Web/Phone downloaded folder zip: {folderName}.zip");
                        }
                        finally
                        {
                            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                        }
                        return;
                    }

                    if (method == "POST" && path == "/api/upload")
                    {
                        if (_fileServer.Options.ReadOnly)
                        {
                            await DrainFixedBodyAsync(stream, leftover, contentLength, ct).ConfigureAwait(false);
                            await SendJsonAsync(stream, 403, new { error = "Host is in Read-Only mode." }, ct, keepAlive: false).ConfigureAwait(false);
                            return;
                        }

                        queryParams.TryGetValue("dir", out string? dirRel);
                        queryParams.TryGetValue("name", out string? rawName);
                        if (!_fileServer.TryResolveSafePath(dirRel ?? string.Empty, out string fullDir, out _) || fullDir == FileShareServer.DrivesVirtualPath)
                        {
                            await DrainFixedBodyAsync(stream, leftover, contentLength, ct).ConfigureAwait(false);
                            await SendJsonAsync(stream, 400, new { error = "Open a folder before uploading." }, ct, keepAlive: false).ConfigureAwait(false);
                            return;
                        }

                        string safeName = Path.GetFileName((rawName ?? string.Empty).Trim());
                        if (string.IsNullOrEmpty(safeName) || safeName is "." or "..")
                            safeName = $"upload_{DateTime.Now:yyyyMMdd_HHmmss}.bin";

                        Directory.CreateDirectory(fullDir);
                        string desiredTarget = TransferManager.ResolveUniqueLocalPath(Path.Combine(fullDir, safeName));
                        string partPath = desiredTarget + ".sbwebpart";

                        try
                        {
                            long written = await StreamFixedBodyToFileAsync(stream, leftover, contentLength, partPath, ct).ConfigureAwait(false);
                            File.Move(partPath, desiredTarget, overwrite: false);
                            Interlocked.Increment(ref _uploadedCount);
                            StatsChanged?.Invoke();
                            string finalName = Path.GetFileName(desiredTarget);
                            Log?.Invoke($"Web/Phone uploaded: {finalName} ({TransferManager.FormatBytes(written)})");
                            await SendJsonAsync(stream, 200, new { ok = true, fileName = finalName, size = written }, ct, keepAlive: false).ConfigureAwait(false);
                        }
                        catch
                        {
                            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
                            throw;
                        }
                        return;
                    }

                    if (method == "POST" && path == "/api/mkdir")
                    {
                        if (_fileServer.Options.ReadOnly)
                        {
                            await DrainFixedBodyAsync(stream, leftover, contentLength, ct).ConfigureAwait(false);
                            await SendJsonAsync(stream, 403, new { error = "Host is in Read-Only mode." }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                            if (!keepAlive) break;
                            continue;
                        }

                        byte[] body = await ReadBoundedBodyAsync(stream, leftover, contentLength, 32 * 1024, ct).ConfigureAwait(false);
                        string reqPath = JsonDocument.Parse(body).RootElement.GetProperty("path").GetString() ?? string.Empty;
                        if (!_fileServer.TryResolveSafePath(reqPath, out string fullPath, out _) || fullPath == FileShareServer.DrivesVirtualPath)
                        {
                            await SendJsonAsync(stream, 400, new { error = "Invalid folder path." }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                            if (!keepAlive) break;
                            continue;
                        }

                        Directory.CreateDirectory(fullPath);
                        await SendJsonAsync(stream, 200, new { ok = true }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                        if (!keepAlive) break;
                        continue;
                    }

                    if (method == "POST" && path == "/api/rename")
                    {
                        if (_fileServer.Options.ReadOnly)
                        {
                            await DrainFixedBodyAsync(stream, leftover, contentLength, ct).ConfigureAwait(false);
                            await SendJsonAsync(stream, 403, new { error = "Host is in Read-Only mode." }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                            if (!keepAlive) break;
                            continue;
                        }

                        byte[] body = await ReadBoundedBodyAsync(stream, leftover, contentLength, 32 * 1024, ct).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(body);
                        string reqPath = doc.RootElement.GetProperty("path").GetString() ?? string.Empty;
                        string newName = (doc.RootElement.GetProperty("newName").GetString() ?? string.Empty).Trim();

                        if (!_fileServer.TryResolveSafePath(reqPath, out string fullPath, out _) || fullPath == FileShareServer.DrivesVirtualPath ||
                            string.IsNullOrEmpty(newName) || newName.Contains('/') || newName.Contains('\\') || newName is "." or "..")
                        {
                            await SendJsonAsync(stream, 400, new { error = "Invalid path or name." }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                            if (!keepAlive) break;
                            continue;
                        }

                        string? parent = Path.GetDirectoryName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        if (string.IsNullOrEmpty(parent))
                        {
                            await SendJsonAsync(stream, 400, new { error = "Cannot rename root drive." }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                            if (!keepAlive) break;
                            continue;
                        }

                        string dest = Path.Combine(parent, newName);
                        _fileServer.EvictCachedHandles(fullPath);
                        if (File.Exists(fullPath)) File.Move(fullPath, dest, overwrite: false);
                        else if (Directory.Exists(fullPath)) Directory.Move(fullPath, dest);
                        else
                        {
                            await SendJsonAsync(stream, 404, new { error = "Item not found." }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                            if (!keepAlive) break;
                            continue;
                        }

                        await SendJsonAsync(stream, 200, new { ok = true }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                        if (!keepAlive) break;
                        continue;
                    }

                    if (method == "POST" && path == "/api/delete")
                    {
                        if (_fileServer.Options.ReadOnly)
                        {
                            await DrainFixedBodyAsync(stream, leftover, contentLength, ct).ConfigureAwait(false);
                            await SendJsonAsync(stream, 403, new { error = "Host is in Read-Only mode." }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                            if (!keepAlive) break;
                            continue;
                        }

                        byte[] body = await ReadBoundedBodyAsync(stream, leftover, contentLength, 32 * 1024, ct).ConfigureAwait(false);
                        string reqPath = JsonDocument.Parse(body).RootElement.GetProperty("path").GetString() ?? string.Empty;
                        if (!_fileServer.TryResolveSafePath(reqPath, out string fullPath, out _) || fullPath == FileShareServer.DrivesVirtualPath)
                        {
                            await SendJsonAsync(stream, 400, new { error = "Invalid path." }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                            if (!keepAlive) break;
                            continue;
                        }

                        string trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (string.IsNullOrEmpty(trimmed) || (trimmed.Length == 2 && trimmed[1] == ':') ||
                            (!_fileServer.Options.AllowFullFilesystem && trimmed.Equals(Path.GetFullPath(_fileServer.Options.SharedDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
                        {
                            await SendJsonAsync(stream, 400, new { error = "Cannot delete a root or shared root folder." }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                            if (!keepAlive) break;
                            continue;
                        }

                        _fileServer.EvictCachedHandles(fullPath);
                        FileShareServer.DeleteFileOrDirectoryRobust(fullPath);
                        await SendJsonAsync(stream, 200, new { ok = true }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                        if (!keepAlive) break;
                        continue;
                    }

                    if (method == "GET" && path == "/api/text")
                    {
                        await SendJsonAsync(stream, 200, new { text = _latestHostText, hostName = _fileServer.Options.DeviceName }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                        if (!keepAlive) break;
                        continue;
                    }

                    if (method == "POST" && path == "/api/text")
                    {
                        byte[] body = await ReadBoundedBodyAsync(stream, leftover, contentLength, 256 * 1024, ct).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(body);
                        string text = doc.RootElement.TryGetProperty("text", out var tEl) ? (tEl.GetString() ?? string.Empty) : string.Empty;
                        string senderName = doc.RootElement.TryGetProperty("senderName", out var sEl) ? (sEl.GetString() ?? "Phone / Web") : "Phone / Web";
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            TextReceived?.Invoke(senderName, text);
                            Log?.Invoke($"Received text snippet via Web/Phone from {senderName} ({text.Length} chars).");
                        }
                        await SendJsonAsync(stream, 200, new { ok = true }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                        if (!keepAlive) break;
                        continue;
                    }

                    await DrainFixedBodyAsync(stream, leftover, contentLength, ct).ConfigureAwait(false);
                    await SendJsonAsync(stream, 404, new { error = "Not found." }, ct, keepAlive: keepAlive).ConfigureAwait(false);
                    if (!keepAlive) break;
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Log?.Invoke($"Web/Phone request error ({remoteIp}): {ex.Message}");
            }
            finally
            {
                try { tcpClient.Client.Shutdown(SocketShutdown.Send); } catch { }
            }
        }
    }

    private string CreateSession()
    {
        string token = GenerateToken(24);
        _sessions[token] = DateTime.UtcNow.AddHours(12);
        return token;
    }

    private bool IsValidSession(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (_sessions.TryGetValue(token, out var exp))
        {
            if (DateTime.UtcNow <= exp)
            {
                _sessions[token] = DateTime.UtcNow.AddHours(12);
                return true;
            }
            _sessions.TryRemove(token, out _);
        }
        return false;
    }

    private static string? ExtractSessionCookie(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("cookie", out string? cookieHeader))
            return null;

        foreach (string part in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Equals("sb_session", StringComparison.Ordinal))
                return part[(eq + 1)..];
        }
        return null;
    }

    private static async Task<(string Method, string Target, string HttpVersion, Dictionary<string, string> Headers, byte[] Leftover)?> ReadHttpRequestHeadersAsync(
        NetworkStream stream,
        byte[] seedBytes,
        int idleTimeoutMs,
        CancellationToken ct)
    {
        byte[] buf = new byte[16 * 1024];
        int total = 0;
        if (seedBytes.Length > 0)
        {
            total = Math.Min(seedBytes.Length, buf.Length);
            Array.Copy(seedBytes, 0, buf, 0, total);
        }

        // Fast-reject TLS ClientHello (0x16) so browsers with HTTPS-First fall back to plain HTTP immediately
        if (total > 0 && buf[0] == 0x16)
            return null;

        int headerEnd = total > 0 ? FindHeaderEnd(buf.AsSpan(0, total)) : -1;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(idleTimeoutMs);

        while (headerEnd < 0 && total < buf.Length)
        {
            int n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), timeoutCts.Token).ConfigureAwait(false);
            if (n == 0) return null;
            total += n;
            if (buf[0] == 0x16) return null;
            headerEnd = FindHeaderEnd(buf.AsSpan(0, total));
        }

        if (headerEnd < 0) return null;

        string headerText = Encoding.ASCII.GetString(buf, 0, headerEnd);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0) return null;

        string[] parts = lines[0].Split(' ', 3);
        if (parts.Length < 2) return null;
        string httpVersion = parts.Length >= 3 ? parts[2].ToUpperInvariant() : "HTTP/1.1";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0)
                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        int bodyStart = headerEnd + 4;
        byte[] leftover = total > bodyStart ? buf[bodyStart..total] : [];
        return (parts[0].ToUpperInvariant(), parts[1], httpVersion, headers, leftover);
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

    private static async Task<byte[]> ReadBoundedBodyAsync(NetworkStream stream, byte[] leftover, long contentLength, int maxBytes, CancellationToken ct)
    {
        if (contentLength <= 0)
            return leftover.Length > 0 ? leftover : [];
        if (contentLength > maxBytes)
            throw new InvalidOperationException("Request body too large.");

        int target = (int)contentLength;
        byte[] body = new byte[target];
        int copied = Math.Min(leftover.Length, target);
        if (copied > 0)
            Array.Copy(leftover, 0, body, 0, copied);

        while (copied < target)
        {
            int n = await stream.ReadAsync(body.AsMemory(copied, target - copied), ct).ConfigureAwait(false);
            if (n == 0) break;
            copied += n;
        }
        return copied == target ? body : body[..copied];
    }

    private static async Task DrainFixedBodyAsync(NetworkStream stream, byte[] leftover, long contentLength, CancellationToken ct)
    {
        long remaining = contentLength - leftover.Length;
        if (remaining <= 0) return;

        byte[] discard = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(discard.Length, remaining);
                int n = await stream.ReadAsync(discard.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (n == 0) break;
                remaining -= n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(discard);
        }
    }

    private static async Task<long> StreamFixedBodyToFileAsync(NetworkStream stream, byte[] leftover, long contentLength, string destPath, CancellationToken ct)
    {
        await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        long written = 0;

        if (leftover.Length > 0)
        {
            int slice = contentLength > 0 ? (int)Math.Min(leftover.Length, contentLength) : leftover.Length;
            await fs.WriteAsync(leftover.AsMemory(0, slice), ct).ConfigureAwait(false);
            written += slice;
        }

        if (contentLength <= 0)
            return written;

        byte[] buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (written < contentLength)
            {
                int toRead = (int)Math.Min(buf.Length, contentLength - written);
                int n = await stream.ReadAsync(buf.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (n == 0)
                    throw new EndOfStreamException("Upload interrupted before Content-Length completed.");
                await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                written += n;
            }
            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static async Task StreamFileDownloadAsync(NetworkStream stream, string filePath, string downloadName, string contentType, CancellationToken ct)
    {
        var fi = new FileInfo(filePath);
        string safeDisp = downloadName.Replace("\"", string.Empty);
        string encodedName = Uri.EscapeDataString(downloadName);
        string header =
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {fi.Length}\r\n" +
            $"Content-Disposition: attachment; filename=\"{safeDisp}\"; filename*=UTF-8''{encodedName}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);

        byte[] buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
            while (true)
            {
                int n = await fs.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n == 0) break;
                await stream.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static void CreateSafeZipFromDirectory(string sourceDir, string zipPath)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(sourceDir));

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = current.EnumerateFileSystemInfos(); }
            catch { continue; }

            foreach (var item in entries)
            {
                try
                {
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((item.Attributes & FileAttributes.System) != 0 && (item.Attributes & FileAttributes.Hidden) != 0) continue;

                    if (item is DirectoryInfo subDir)
                        stack.Push(subDir);
                    else if (item is FileInfo fi)
                    {
                        string rel = Path.GetRelativePath(sourceDir, fi.FullName).Replace('\\', '/');
                        zip.CreateEntryFromFile(fi.FullName, rel, CompressionLevel.Fastest);
                    }
                }
                catch { }
            }
        }
    }

    private static Task SendJsonAsync(NetworkStream stream, int statusCode, object payload, CancellationToken ct, string? setCookie = null, bool keepAlive = true)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        string statusText = statusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            _ => "Error"
        };
        return SendBytesAsync(stream, statusCode, statusText, "application/json; charset=utf-8", bytes, ct, setCookie, keepAlive);
    }

    private static async Task SendBytesAsync(NetworkStream stream, int statusCode, string statusText, string contentType, byte[] body, CancellationToken ct, string? setCookie = null, bool keepAlive = true)
    {
        string cookieLine = string.IsNullOrEmpty(setCookie) ? string.Empty : $"Set-Cookie: {setCookie}\r\n";
        string connHeader = keepAlive ? "Connection: keep-alive\r\nKeep-Alive: timeout=15, max=100\r\n" : "Connection: close\r\n";
        string header =
            $"HTTP/1.1 {statusCode} {statusText}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            cookieLine +
            "Cache-Control: no-store\r\n" +
            connHeader + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
        if (body.Length > 0)
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    private static Task SendHeadAsync(NetworkStream stream, int statusCode, string statusText, string contentType, int contentLength, CancellationToken ct, string? setCookie = null, bool keepAlive = true)
    {
        string cookieLine = string.IsNullOrEmpty(setCookie) ? string.Empty : $"Set-Cookie: {setCookie}\r\n";
        string connHeader = keepAlive ? "Connection: keep-alive\r\nKeep-Alive: timeout=15, max=100\r\n" : "Connection: close\r\n";
        string header =
            $"HTTP/1.1 {statusCode} {statusText}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {contentLength}\r\n" +
            cookieLine +
            "Cache-Control: no-store\r\n" +
            connHeader + "\r\n";
        return stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).AsTask();
    }

    private static Task SendEmptyAsync(NetworkStream stream, int statusCode, string statusText, bool keepAlive, CancellationToken ct, string extraHeaders = "")
    {
        string connHeader = keepAlive ? "Connection: keep-alive\r\nKeep-Alive: timeout=15, max=100\r\n" : "Connection: close\r\n";
        string header =
            $"HTTP/1.1 {statusCode} {statusText}\r\n" +
            extraHeaders +
            "Content-Length: 0\r\n" +
            "Cache-Control: no-store\r\n" +
            connHeader + "\r\n";
        return stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).AsTask();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return dict;
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq >= 0)
                dict[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            else
                dict[Uri.UnescapeDataString(pair)] = string.Empty;
        }
        return dict;
    }

    private static string GenerateToken(int byteCount) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private const string PortalHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1"/>
<title>SwiftBeam • Phone & Web Portal</title>
<style>
  :root { --bg:#0B1120; --card:#111827; --panel:#0F172A; --border:#1E293B; --purple:#8B5CF6; --purple2:#7C3AED; --mint:#34D399; --text:#F1F5F9; --muted:#94A3B8; --danger:#EF4444; }
  * { box-sizing:border-box; }
  body { margin:0; font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif; background:var(--bg); color:var(--text); min-height:100vh; }
  .wrap { max-width:980px; margin:0 auto; padding:12px; }
  .card { background:var(--card); border:1px solid var(--border); border-radius:14px; padding:14px; }
  .pill { background:var(--bg); border:1px solid var(--border); border-radius:10px; color:var(--text); padding:8px 12px; font-size:13px; outline:none; }
  .pill:focus { border-color:var(--purple); }
  .btn { background:#1E293B; color:#E2E8F0; border:1px solid transparent; border-radius:9px; padding:8px 13px; font-size:13px; font-weight:600; cursor:pointer; display:inline-flex; align-items:center; gap:6px; }
  .btn:hover { background:#334155; border-color:var(--purple); }
  .btn-primary { background:var(--purple); color:#fff; }
  .btn-primary:hover { background:#A78BFA; }
  .btn-danger { background:#450A0A; color:#FCA5A5; border-color:#7F1D1D; }
  .row { display:flex; align-items:center; justify-content:space-between; gap:8px; flex-wrap:wrap; }
  .crumbs { display:flex; flex-wrap:wrap; align-items:center; gap:4px; background:var(--bg); border:1px solid var(--border); border-radius:10px; padding:5px 8px; }
  .crumb { background:transparent; border:0; color:#C4B5FD; padding:4px 7px; border-radius:6px; cursor:pointer; font-size:12.5px; }
  .crumb:hover { background:#1E293B; }
  .quick { display:flex; gap:6px; overflow-x:auto; padding:6px 0; }
  .list { margin-top:10px; display:flex; flex-direction:column; gap:6px; }
  .item { background:#151C2C; border:1px solid var(--border); border-radius:10px; padding:10px 12px; display:flex; align-items:center; justify-content:space-between; gap:10px; }
  .item.hidden-item { opacity:0.62; }
  .item-left { display:flex; align-items:center; gap:10px; min-width:0; flex:1; cursor:pointer; }
  .item-name { font-weight:600; font-size:13.5px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .item-meta { font-size:11.5px; color:var(--muted); }
  .drop { margin-top:10px; border:1.5px dashed #4C1D95; background:#0F172A; border-radius:12px; padding:14px; text-align:center; }
  .drop.drag { border-color:#A78BFA; background:#1E1B4B; }
  .prog { width:100%; height:8px; background:#1E293B; border-radius:99px; overflow:hidden; margin-top:8px; display:none; }
  .prog > div { height:100%; background:#10B981; width:0%; transition:width .12s; }
  .badge { background:#2E1065; color:#DDD6FE; font-size:11px; padding:3px 8px; border-radius:99px; font-weight:600; }
  .modal-bg { position:fixed; inset:0; background:rgba(2,6,23,.78); display:none; align-items:center; justify-content:center; padding:16px; z-index:50; }
  .modal { background:var(--panel); border:1px solid #334155; border-radius:14px; padding:16px; max-width:460px; width:100%; }
  .toast { position:fixed; bottom:18px; right:18px; background:#064E3B; border:1px solid #10B981; color:#ECFDF5; padding:10px 14px; border-radius:10px; font-size:13px; display:none; z-index:60; }
  @media (max-width:600px) { .btn { padding:8px 10px; font-size:12px; } .item { flex-direction:column; align-items:stretch; } }
</style>
</head>
<body>
<div class="wrap">
  <div id="authView" class="card" style="max-width:380px;margin:60px auto;display:none">
    <h2 style="margin:0 0 6px;color:#F8FAFC">SwiftBeam Portal</h2>
    <p id="authSubtitle" style="margin:0 0 14px;color:var(--muted);font-size:13px">Enter the 6-digit PIN shown on the host PC.</p>
    <input id="pinInput" class="pill" style="width:100%;font-size:20px;text-align:center;letter-spacing:4px;font-family:monospace" placeholder="000000" inputmode="numeric"/>
    <div id="authErr" style="color:#FCA5A5;font-size:12px;margin-top:8px"></div>
    <button class="btn btn-primary" style="width:100%;justify-content:center;margin-top:12px;padding:10px" onclick="doPinAuth()">Connect</button>
  </div>

  <div id="appView" style="display:none">
    <div class="card">
      <div class="row">
        <div>
          <div style="display:flex;align-items:center;gap:8px">
            <span style="font-weight:700;font-size:17px">SwiftBeam</span>
            <span id="hostBadge" class="badge">PC</span>
            <span id="roBadge" class="badge" style="display:none;background:#78350F;color:#FDE68A">Read-Only</span>
          </div>
          <div id="freeSpace" style="font-size:12px;color:var(--mint);margin-top:3px"></div>
        </div>
        <div style="display:flex;gap:6px;flex-wrap:wrap">
          <button class="btn" onclick="openTextModal()">Text</button>
          <button class="btn" id="hiddenBtn" onclick="toggleHidden()">Hidden</button>
          <button class="btn" id="mkdirBtn" onclick="createFolder()">+ Folder</button>
          <button class="btn" onclick="loadList(currentPath)">Refresh</button>
        </div>
      </div>

      <div id="quickRow" class="quick" style="display:none"></div>

      <div class="row" style="margin-top:6px">
        <button class="btn" onclick="goUp()">Up</button>
        <div id="crumbs" class="crumbs" style="flex:1"></div>
        <input id="filterInput" class="pill" style="width:150px" placeholder="Filter..." oninput="renderEntries()"/>
      </div>

      <div id="dropZone" class="drop">
        <input id="filePicker" type="file" multiple style="display:none" onchange="uploadFiles(this.files)"/>
        <div class="row" style="justify-content:center">
          <button id="uploadBtn" class="btn btn-primary" onclick="document.getElementById('filePicker').click()">Upload Files / Photos</button>
          <span style="font-size:12px;color:var(--muted)">or drag &amp; drop files here</span>
        </div>
        <div class="prog" id="progWrap"><div id="progBar"></div></div>
        <div id="progText" style="font-size:12px;color:var(--muted);margin-top:4px"></div>
      </div>

      <div id="fileList" class="list"></div>
    </div>
  </div>
</div>

<div id="textModal" class="modal-bg">
  <div class="modal">
    <div class="row" style="margin-bottom:10px">
      <strong style="font-size:15px">Quick Text / Link Share</strong>
      <button class="btn" onclick="closeTextModal()">Close</button>
    </div>
    <div id="hostTextWrap" style="display:none;margin-bottom:10px">
      <div style="font-size:11px;color:var(--muted);margin-bottom:4px">LATEST FROM HOST PC</div>
      <div id="hostTextBox" class="pill" style="white-space:pre-wrap;max-height:110px;overflow:auto"></div>
    </div>
    <textarea id="sendTextBox" class="pill" style="width:100%;min-height:85px;resize:vertical" placeholder="Paste text, code, or link to send to the PC..."></textarea>
    <div class="row" style="justify-content:flex-end;margin-top:10px">
      <button class="btn btn-primary" onclick="sendSharedText()">Send to PC</button>
    </div>
  </div>
</div>

<div id="toast" class="toast"></div>

<script>
let statusInfo = {};
let currentPath = '';
let entries = [];
let showHidden = false;

function fmtBytes(b) {
  const u = ['B','KB','MB','GB','TB']; let i = 0; let v = b || 0;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return i === 0 ? Math.round(v) + ' ' + u[i] : v.toFixed(1) + ' ' + u[i];
}
function toast(msg) {
  const t = document.getElementById('toast');
  t.textContent = msg; t.style.display = 'block';
  clearTimeout(window._tt); window._tt = setTimeout(() => t.style.display = 'none', 3200);
}
async function init() {
  const qs = new URLSearchParams(location.search);
  const qr = qs.get('qr');
  let r = await fetch('/api/status'); statusInfo = await r.json();
  if (!statusInfo.authenticated && qr) {
    const ar = await fetch('/api/auth', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({qr}) });
    if (ar.ok) {
      r = await fetch('/api/status'); statusInfo = await r.json();
    }
  }
  if (statusInfo.authenticated && qr && history && history.replaceState) {
    history.replaceState(null, '', '/');
  }
  document.getElementById('hostBadge').textContent = statusInfo.deviceName || 'PC';
  if (statusInfo.readOnly) {
    document.getElementById('roBadge').style.display = 'inline-block';
    document.getElementById('dropZone').style.display = 'none';
    document.getElementById('mkdirBtn').style.display = 'none';
  }
  if (!statusInfo.authenticated) {
    document.getElementById('authSubtitle').textContent = 'Connect to ' + (statusInfo.deviceName || 'Host PC') + ' — enter 6-digit PIN.';
    document.getElementById('authView').style.display = 'block';
    document.getElementById('pinInput').focus();
    return;
  }
  showApp();
}
async function doPinAuth() {
  const pin = document.getElementById('pinInput').value;
  const r = await fetch('/api/auth', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({pin}) });
  const j = await r.json();
  if (!r.ok || !j.ok) { document.getElementById('authErr').textContent = j.error || 'Auth failed'; return; }
  document.getElementById('authView').style.display = 'none';
  showApp();
}
document.getElementById('pinInput').addEventListener('keydown', e => { if (e.key === 'Enter') doPinAuth(); });

function showApp() {
  document.getElementById('appView').style.display = 'block';
  if (statusInfo.fullFilesystem) {
    const q = document.getElementById('quickRow'); q.style.display = 'flex';
    const items = [['Drives','__DRIVES__'], ['Home', statusInfo.homePath], ['Desktop', statusInfo.desktopPath], ['Downloads', statusInfo.downloadsPath], ['Documents', statusInfo.documentsPath]];
    q.innerHTML = '';
    for (const [label, p] of items) {
      if (!p) continue;
      const b = document.createElement('button'); b.className = 'btn'; b.textContent = label;
      b.onclick = () => loadList(p); q.appendChild(b);
    }
  }
  loadList(statusInfo.fullFilesystem ? (statusInfo.homePath || '__DRIVES__') : '');
}
async function loadList(p) {
  currentPath = p || '';
  const r = await fetch('/api/list?path=' + encodeURIComponent(currentPath) + '&hidden=' + (showHidden ? '1' : '0'));
  if (r.status === 401) { location.reload(); return; }
  const j = await r.json();
  if (j.error) { toast(j.error); return; }
  currentPath = j.currentPath || '';
  entries = j.entries || [];
  document.getElementById('freeSpace').textContent = j.freeDiskBytes ? (fmtBytes(j.freeDiskBytes) + ' free on drive') : '';
  renderCrumbs();
  renderEntries();
}
function renderCrumbs() {
  const el = document.getElementById('crumbs'); el.innerHTML = '';
  if (!currentPath || currentPath === '__DRIVES__') {
    const b = document.createElement('button'); b.className='crumb'; b.textContent = statusInfo.fullFilesystem ? 'Drives / Roots' : 'Shared Folder';
    b.onclick = () => loadList(statusInfo.fullFilesystem ? '__DRIVES__' : ''); el.appendChild(b); return;
  }
  const rootBtn = document.createElement('button'); rootBtn.className='crumb'; rootBtn.textContent = statusInfo.fullFilesystem ? 'Roots' : 'Home';
  rootBtn.onclick = () => loadList(statusInfo.fullFilesystem ? '__DRIVES__' : ''); el.appendChild(rootBtn);
  const unix = currentPath.startsWith('/');
  const parts = currentPath.replace(/\\/g,'/').replace(/^\/+|\/+$/g,'').split('/').filter(Boolean);
  parts.forEach((seg, idx) => {
    const sep = document.createElement('span'); sep.textContent='›'; sep.style.color='#475569'; el.appendChild(sep);
    let target = unix ? ('/' + parts.slice(0, idx+1).join('/')) : (idx === 0 && seg.endsWith(':') ? seg + '/' : parts.slice(0, idx+1).join('/'));
    const b = document.createElement('button'); b.className='crumb'; b.textContent = seg;
    b.onclick = () => loadList(target); el.appendChild(b);
  });
}
function goUp() {
  if (!currentPath || currentPath === '__DRIVES__') return;
  let c = currentPath.replace(/\\/g,'/');
  if (c === '/' || /^[A-Za-z]:\/?$/.test(c)) { loadList(statusInfo.fullFilesystem ? '__DRIVES__' : ''); return; }
  c = c.replace(/\/+$/,'');
  const idx = c.lastIndexOf('/');
  if (idx <= 0) loadList(c.startsWith('/') ? '/' : '');
  else {
    let p = c.slice(0, idx); if (p.length === 2 && p[1] === ':') p += '/';
    loadList(p);
  }
}
function renderEntries() {
  const q = document.getElementById('filterInput').value.trim().toLowerCase();
  const list = document.getElementById('fileList'); list.innerHTML = '';
  const shown = entries.filter(e => !q || e.name.toLowerCase().includes(q));
  if (shown.length === 0) { list.innerHTML = '<div style="text-align:center;color:var(--muted);padding:24px">No items here.</div>'; return; }
  for (const e of shown) {
    const row = document.createElement('div'); row.className = 'item' + (e.isHidden ? ' hidden-item' : '');
    const left = document.createElement('div'); left.className = 'item-left';
    left.onclick = () => { if (e.isDirectory) loadList(e.relativePath); else location.href = '/api/download?path=' + encodeURIComponent(e.relativePath); };
    left.innerHTML = '<span style="font-size:18px">' + (e.isDirectory ? '📁' : '📄') + '</span>' +
      '<div style="min-width:0"><div class="item-name">' + esc(e.name) + '</div>' +
      '<div class="item-meta">' + (e.isDirectory ? 'Folder' : fmtBytes(e.size)) + '</div></div>';
    row.appendChild(left);

    const actions = document.createElement('div'); actions.style.display='flex'; actions.style.gap='6px'; actions.style.flexWrap='wrap';
    const dl = document.createElement('button'); dl.className = 'btn'; dl.textContent = e.isDirectory ? 'Zip ↓' : 'Download';
    dl.onclick = () => location.href = (e.isDirectory ? '/api/download-zip?path=' : '/api/download?path=') + encodeURIComponent(e.relativePath);
    actions.appendChild(dl);

    if (!statusInfo.readOnly && currentPath !== '__DRIVES__') {
      const ren = document.createElement('button'); ren.className = 'btn'; ren.textContent = 'Rename';
      ren.onclick = () => renameItem(e);
      const del = document.createElement('button'); del.className = 'btn btn-danger'; del.textContent = 'Delete';
      del.onclick = () => deleteItem(e);
      actions.appendChild(ren); actions.appendChild(del);
    }
    row.appendChild(actions);
    list.appendChild(row);
  }
}
function esc(s) { return s.replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
function toggleHidden() {
  showHidden = !showHidden;
  document.getElementById('hiddenBtn').textContent = showHidden ? 'Hidden ✓' : 'Hidden';
  loadList(currentPath);
}
async function createFolder() {
  const name = prompt('New folder name:'); if (!name) return;
  const target = !currentPath ? name : (currentPath.endsWith('/') ? currentPath + name : currentPath + '/' + name);
  const r = await fetch('/api/mkdir', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({path:target}) });
  const j = await r.json(); if (!r.ok) toast(j.error || 'Error'); else loadList(currentPath);
}
async function renameItem(e) {
  const newName = prompt('Rename "' + e.name + '" to:', e.name); if (!newName || newName === e.name) return;
  const r = await fetch('/api/rename', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({path:e.relativePath, newName}) });
  const j = await r.json(); if (!r.ok) toast(j.error || 'Error'); else loadList(currentPath);
}
async function deleteItem(e) {
  if (!confirm('Delete "' + e.name + '"?')) return;
  const r = await fetch('/api/delete', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({path:e.relativePath}) });
  const j = await r.json(); if (!r.ok) toast(j.error || 'Error'); else { toast('Deleted ' + e.name); loadList(currentPath); }
}
async function uploadFiles(files) {
  if (!files || files.length === 0) return;
  const wrap = document.getElementById('progWrap'), bar = document.getElementById('progBar'), txt = document.getElementById('progText');
  wrap.style.display = 'block';
  for (let i = 0; i < files.length; i++) {
    const f = files[i];
    await new Promise((resolve, reject) => {
      const xhr = new XMLHttpRequest();
      xhr.open('POST', '/api/upload?dir=' + encodeURIComponent(currentPath) + '&name=' + encodeURIComponent(f.name));
      const t0 = performance.now();
      xhr.upload.onprogress = ev => {
        if (ev.lengthComputable) {
          const pct = Math.round(ev.loaded * 100 / ev.total);
          const sec = Math.max(0.1, (performance.now() - t0) / 1000);
          bar.style.width = pct + '%';
          txt.textContent = 'Uploading (' + (i+1) + '/' + files.length + ') ' + f.name + ' • ' + pct + '% • ' + fmtBytes(ev.loaded / sec) + '/s';
        }
      };
      xhr.onload = () => xhr.status === 200 ? resolve() : reject(new Error(xhr.responseText || 'Upload failed'));
      xhr.onerror = () => reject(new Error('Network error'));
      xhr.send(f);
    }).catch(err => toast(err.message));
  }
  bar.style.width = '100%'; txt.textContent = 'Uploaded ' + files.length + ' file(s)!';
  toast('Uploaded ' + files.length + ' file(s)');
  document.getElementById('filePicker').value = '';
  loadList(currentPath);
}
const dz = document.getElementById('dropZone');
dz.addEventListener('dragover', e => { e.preventDefault(); dz.classList.add('drag'); });
dz.addEventListener('dragleave', () => dz.classList.remove('drag'));
dz.addEventListener('drop', e => { e.preventDefault(); dz.classList.remove('drag'); uploadFiles(e.dataTransfer.files); });

async function openTextModal() {
  document.getElementById('textModal').style.display = 'flex';
  const r = await fetch('/api/text'); const j = await r.json();
  const wrap = document.getElementById('hostTextWrap');
  if (j.text) { wrap.style.display = 'block'; document.getElementById('hostTextBox').textContent = j.text; }
  else wrap.style.display = 'none';
}
function closeTextModal() { document.getElementById('textModal').style.display = 'none'; }
async function sendSharedText() {
  const text = document.getElementById('sendTextBox').value; if (!text.trim()) return;
  const r = await fetch('/api/text', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({text, senderName:'Phone / Web'}) });
  if (r.ok) { document.getElementById('sendTextBox').value = ''; closeTextModal(); toast('Text sent to PC'); }
}
init();
</script>
</body>
</html>
""";
}
