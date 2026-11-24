using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;
using SwiftBeam.Core.Client;
using SwiftBeam.Core.Protocol;
using SwiftBeam.Core.Security;

namespace SwiftBeam.Core.Server;

public sealed record IncomingPeerActivity(
    string ClientName,
    string RemoteAddress,
    bool TlsActive,
    int ActiveStreams,
    string CurrentAction,
    double BytesPerSec,
    long TotalBytesTransferred);

public sealed class FileShareServerOptions
{
    public string ServerId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string DeviceName { get; set; } = Environment.MachineName;
    public int Port { get; set; } = ProtocolConstants.DefaultTransferPort;
    public string SharedDirectory { get; set; } = string.Empty;
    public bool AllowFullFilesystem { get; set; } = false;
    public bool RequirePin { get; set; } = true;
    public string Pin { get; set; } = PinAuth.GeneratePin();
    public bool AllowUnencrypted { get; set; } = true;
    public bool ReadOnly { get; set; } = false;
    public bool RequireHostApproval { get; set; } = false;
}

public sealed class FileShareServer : IAsyncDisposable
{
    public const string DrivesVirtualPath = "__DRIVES__";

    private readonly FileShareServerOptions _options;
    private readonly X509Certificate2 _certificate;
    private readonly byte[] _certHash;
    private readonly string _certFingerprint;
    private sealed class PushHandleEntry
    {
        public SafeFileHandle Handle { get; }
        public long LastUsedTicks;
        public PushHandleEntry(SafeFileHandle handle)
        {
            Handle = handle;
            LastUsedTicks = DateTime.UtcNow.Ticks;
        }
    }

    private sealed class PullHandleEntry
    {
        public SafeFileHandle Handle { get; }
        public long FileSize { get; }
        public long LastWriteTicks { get; }
        public long LastUsedTicks;
        public PullHandleEntry(SafeFileHandle handle, long fileSize, long lastWriteTicks)
        {
            Handle = handle;
            FileSize = fileSize;
            LastWriteTicks = lastWriteTicks;
            LastUsedTicks = DateTime.UtcNow.Ticks;
        }
    }

    private sealed class ClientSession
    {
        public string SessionId { get; } = Guid.NewGuid().ToString("N")[..12];
        public string ClientName { get; }
        public string RemoteAddress { get; }
        public bool TlsActive { get; }
        public CancellationTokenSource SessionCts { get; } = new();
        public string CurrentAction = "Connected (idle)";
        public long TotalBytes;

        public ClientSession(string clientName, string remoteAddress, bool tlsActive)
        {
            ClientName = clientName;
            RemoteAddress = remoteAddress;
            TlsActive = tlsActive;
        }
    }

    private sealed class PeerRateState
    {
        public long LastBytes;
        public long LastTicks;
        public double SmoothedBps;
        public long CumulativeBytes;
    }

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pushFileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PushHandleEntry> _activePushHandles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PullHandleEntry> _activePullHandles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pullFileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PeerRateState> _peerRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _approvedPeers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _activityEmitLock = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private Task? _reaperLoop;
    private int _activeConnections;
    private long _totalBytesServed;
    private long _lastStatsNotifyTicks;
    private static readonly long StatsNotifyIntervalTicks = Stopwatch.Frequency / 4; // 250 ms

    public bool IsRunning => _listener is not null && _cts is { IsCancellationRequested: false };
    public int ListeningPort { get; private set; }
    public int ActiveConnections => Volatile.Read(ref _activeConnections);
    public long TotalBytesServed => Volatile.Read(ref _totalBytesServed);
    public string CertFingerprint => _certFingerprint;
    public FileShareServerOptions Options => _options;

    public event Action<string>? Log;
    public event Action<int, long>? StatsChanged;
    public event Action<IReadOnlyList<IncomingPeerActivity>>? IncomingActivityChanged;
    public event Action<string, string>? TextReceived;
    public Func<string, string, bool, Task<bool>>? ConnectionApprovalHandler { get; set; }

    public FileShareServer(FileShareServerOptions options)
    {
        _options = options;
        _certificate = TlsHelper.CreateEphemeralCertificate(options.DeviceName);
        _certHash = TlsHelper.GetCertHash(_certificate);
        _certFingerprint = TlsHelper.FormatFingerprint(_certHash);
    }

    public void UpdateSharedDirectory(string directory)
    {
        _options.SharedDirectory = directory;
        try { Directory.CreateDirectory(directory); } catch { }
        Log?.Invoke($"Shared folder set to: {directory}");
    }

    public void UpdateFullFilesystemAccess(bool allowFull)
    {
        _options.AllowFullFilesystem = allowFull;
        Log?.Invoke(allowFull
            ? "Access mode: Full computer browsing (PIN-protected)"
            : $"Access mode: Restricted to {_options.SharedDirectory}");
    }

    public void UpdatePin(string pin, bool requirePin)
    {
        _options.Pin = PinAuth.NormalizePin(pin);
        _options.RequirePin = requirePin;
    }

    public void UpdateAllowUnencrypted(bool allowUnencrypted)
    {
        _options.AllowUnencrypted = allowUnencrypted;
    }

    public void UpdateReadOnly(bool readOnly)
    {
        _options.ReadOnly = readOnly;
        Log?.Invoke(readOnly
            ? "Access mode updated: Read-Only (downloads allowed, uploads/edits blocked)."
            : "Access mode updated: Read & Write enabled.");
    }

    public void UpdateRequireHostApproval(bool requireApproval)
    {
        _options.RequireHostApproval = requireApproval;
        if (requireApproval)
            _approvedPeers.Clear();
    }

    public void Start()
    {
        if (IsRunning) return;

        if (!_options.AllowFullFilesystem && !string.IsNullOrWhiteSpace(_options.SharedDirectory))
        {
            try { Directory.CreateDirectory(_options.SharedDirectory); } catch { }
        }

        _cts = new CancellationTokenSource();

        try
        {
            _listener = new TcpListener(IPAddress.IPv6Any, _options.Port);
            _listener.Server.DualMode = true;
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start(backlog: 64);
        }
        catch (SocketException)
        {
            _listener = new TcpListener(IPAddress.Any, _options.Port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start(backlog: 64);
        }

        ListeningPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _reaperLoop = Task.Run(() => ReapIdlePushHandlesLoopAsync(_cts.Token));
        Log?.Invoke($"Server listening on port {ListeningPort} (TLS cert {_certFingerprint})");
    }

    private async Task ReapIdlePushHandlesLoopAsync(CancellationToken ct)
    {
        long maxPushIdleTicks = TimeSpan.FromSeconds(90).Ticks;
        long maxPullIdleTicks = TimeSpan.FromSeconds(60).Ticks;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            long nowTicks = DateTime.UtcNow.Ticks;
            foreach (var kv in _activePushHandles)
            {
                if (nowTicks - Interlocked.Read(ref kv.Value.LastUsedTicks) > maxPushIdleTicks)
                {
                    if (_activePushHandles.TryRemove(kv.Key, out var entry))
                    {
                        try { entry.Handle.Dispose(); } catch { }
                    }
                }
            }

            foreach (var kv in _activePullHandles)
            {
                if (nowTicks - Interlocked.Read(ref kv.Value.LastUsedTicks) > maxPullIdleTicks)
                {
                    if (_activePullHandles.TryRemove(kv.Key, out var entry))
                    {
                        try { entry.Handle.Dispose(); } catch { }
                    }
                }
            }
        }
    }

    public IReadOnlyList<IncomingPeerActivity> GetIncomingActivitySnapshot()
    {
        return BuildIncomingSnapshot(updateRates: false);
    }

    public Task DisconnectIncomingClientsAsync()
    {
        _approvedPeers.Clear();
        foreach (var kv in _sessions)
        {
            try { kv.Value.SessionCts.Cancel(); } catch { }
        }
        Log?.Invoke("Disconnected incoming client session(s).");
        return Task.CompletedTask;
    }

    private void RecordTransferredBytes(ClientSession? session, int bytes)
    {
        Interlocked.Add(ref _totalBytesServed, bytes);
        if (session is not null)
            Interlocked.Add(ref session.TotalBytes, bytes);

        long now = Stopwatch.GetTimestamp();
        long prev = Interlocked.Read(ref _lastStatsNotifyTicks);
        if (now - prev >= StatsNotifyIntervalTicks &&
            Interlocked.CompareExchange(ref _lastStatsNotifyTicks, now, prev) == prev)
        {
            EmitStatsAndActivity(updateRates: true);
        }
    }

    private void EmitStatsAndActivity(bool updateRates)
    {
        StatsChanged?.Invoke(ActiveConnections, TotalBytesServed);
        if (IncomingActivityChanged is not null)
        {
            var snap = BuildIncomingSnapshot(updateRates);
            IncomingActivityChanged.Invoke(snap);
        }
    }

    private IReadOnlyList<IncomingPeerActivity> BuildIncomingSnapshot(bool updateRates)
    {
        lock (_activityEmitLock)
        {
            var sessions = _sessions.Values.ToList();
            if (sessions.Count == 0)
            {
                _peerRates.Clear();
                return [];
            }

            long nowTicks = Stopwatch.GetTimestamp();
            var grouped = sessions.GroupBy(s => (s.ClientName, s.RemoteAddress));
            var list = new List<IncomingPeerActivity>();

            foreach (var g in grouped)
            {
                string key = $"{g.Key.ClientName}|{g.Key.RemoteAddress}";
                long currentSessionBytes = g.Sum(s => Interlocked.Read(ref s.TotalBytes));
                bool tls = g.Any(s => s.TlsActive);
                int streams = g.Count();

                string action = g
                    .Select(s => Volatile.Read(ref s.CurrentAction))
                    .FirstOrDefault(a => !string.Equals(a, "Connected (idle)", StringComparison.Ordinal))
                    ?? "Connected (idle)";

                var rate = _peerRates.GetOrAdd(key, _ => new PeerRateState
                {
                    LastBytes = currentSessionBytes,
                    LastTicks = nowTicks,
                    SmoothedBps = 0,
                    CumulativeBytes = 0
                });

                if (updateRates)
                {
                    double dt = (double)(nowTicks - rate.LastTicks) / Stopwatch.Frequency;
                    if (dt >= 0.2)
                    {
                        long delta = Math.Max(0, currentSessionBytes - rate.LastBytes);
                        double instBps = delta / dt;
                        rate.SmoothedBps = instBps > 0
                            ? (rate.SmoothedBps <= 0 ? instBps : rate.SmoothedBps * 0.75 + instBps * 0.25)
                            : rate.SmoothedBps * 0.5;
                        if (rate.SmoothedBps < 1) rate.SmoothedBps = 0;
                        rate.LastBytes = currentSessionBytes;
                        rate.LastTicks = nowTicks;
                    }
                }

                list.Add(new IncomingPeerActivity(
                    g.Key.ClientName,
                    g.Key.RemoteAddress,
                    tls,
                    streams,
                    action,
                    rate.SmoothedBps,
                    currentSessionBytes));
            }

            return list;
        }
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
        if (_reaperLoop is not null)
        {
            try { await _reaperLoop.ConfigureAwait(false); } catch { }
        }
        _listener = null;
        _approvedPeers.Clear();
        _cts.Dispose();
        _cts = null;
        Log?.Invoke("Server stopped.");
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
            catch (Exception ex)
            {
                Log?.Invoke($"Accept error: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken ct)
    {
        string remoteDesc = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
        string remoteIpOnly = (tcpClient.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv4IfMapped() ?? remoteDesc;
        int count = Interlocked.Increment(ref _activeConnections);
        StatsChanged?.Invoke(count, TotalBytesServed);

        ClientSession? session = null;

        try
        {
            tcpClient.NoDelay = true;
            tcpClient.SendBufferSize = ProtocolConstants.SocketBufferBytes;
            tcpClient.ReceiveBufferSize = ProtocolConstants.SocketBufferBytes;

            using var rawStream = tcpClient.GetStream();

            byte[] magic = new byte[4];
            await FrameProtocol.ReadExactAsync(rawStream, magic, ct).ConfigureAwait(false);

            bool useTls;
            if (magic.AsSpan().SequenceEqual(ProtocolConstants.MagicTls))
            {
                useTls = true;
            }
            else if (magic.AsSpan().SequenceEqual(ProtocolConstants.MagicPlain))
            {
                if (!_options.AllowUnencrypted)
                {
                    await rawStream.WriteAsync(new[] { ProtocolConstants.PreambleTlsRequired }, ct).ConfigureAwait(false);
                    Log?.Invoke($"Rejected unencrypted connection from {remoteDesc} (TLS required).");
                    return;
                }
                useTls = false;
            }
            else
            {
                await rawStream.WriteAsync(new[] { ProtocolConstants.PreambleInvalid }, ct).ConfigureAwait(false);
                return;
            }

            await rawStream.WriteAsync(new[] { ProtocolConstants.PreambleOk }, ct).ConfigureAwait(false);
            await rawStream.FlushAsync(ct).ConfigureAwait(false);

            Stream activeStream = rawStream;
            SslStream? sslStream = null;

            if (useTls)
            {
                sslStream = new SslStream(rawStream, leaveInnerStreamOpen: true);
                await sslStream.AuthenticateAsServerAsync(TlsHelper.CreateServerOptions(_certificate), ct).ConfigureAwait(false);
                activeStream = sslStream;
            }

            try
            {
                byte[] serverNonce = PinAuth.GenerateNonce();
                var challenge = new AuthChallengeMessage(
                    _options.ServerId,
                    _options.DeviceName,
                    GetOsLabel(),
                    Convert.ToBase64String(serverNonce),
                    _options.RequirePin,
                    useTls,
                    useTls ? _certFingerprint : null);

                await FrameProtocol.WriteJsonFrameAsync(activeStream, FrameType.AuthChallenge, challenge, ct).ConfigureAwait(false);

                var authResp = await FrameProtocol.ReadJsonFrameAsync<AuthResponseMessage>(activeStream, FrameType.AuthResponse, ct).ConfigureAwait(false);

                if (_options.RequirePin)
                {
                    byte[] clientNonce = Convert.FromBase64String(authResp.ClientNonceB64);
                    byte[] proof = Convert.FromBase64String(authResp.ProofB64);
                    byte[]? certBinding = useTls ? _certHash : null;

                    if (!PinAuth.VerifyProof(_options.Pin, serverNonce, clientNonce, proof, certBinding))
                    {
                        await FrameProtocol.WriteJsonFrameAsync(
                            activeStream,
                            FrameType.AuthResult,
                            new AuthResultMessage(false, "Invalid PIN. Check the 6-digit pairing PIN on the host.", null),
                            ct).ConfigureAwait(false);
                        Log?.Invoke($"Failed PIN auth from {authResp.ClientName} ({remoteDesc})");
                        return;
                    }
                }

                string clientLabel = string.IsNullOrWhiteSpace(authResp.ClientName) ? remoteIpOnly : authResp.ClientName;
                if (_options.RequireHostApproval && ConnectionApprovalHandler is not null)
                {
                    string approvalKey = $"{clientLabel}|{remoteIpOnly}";
                    var approvalLazy = _approvedPeers.GetOrAdd(approvalKey, _ =>
                        new Lazy<Task<bool>>(() => RequestApprovalWithTimeoutAsync(clientLabel, remoteIpOnly, useTls), LazyThreadSafetyMode.ExecutionAndPublication));

                    bool approved = await approvalLazy.Value.ConfigureAwait(false);
                    if (!approved)
                    {
                        _approvedPeers.TryRemove(approvalKey, out _);
                        await FrameProtocol.WriteJsonFrameAsync(
                            activeStream,
                            FrameType.AuthResult,
                            new AuthResultMessage(false, "Connection declined by host.", null),
                            ct).ConfigureAwait(false);
                        Log?.Invoke($"Declined connection from {clientLabel} ({remoteDesc})");
                        return;
                    }
                }

                string rootName = _options.AllowFullFilesystem
                    ? _options.DeviceName
                    : Path.GetFileName(_options.SharedDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(rootName)) rootName = _options.SharedDirectory;

                var (home, desktop, downloads, documents) = _options.AllowFullFilesystem
                    ? GetStandardLocations()
                    : (null, null, null, null);

                await FrameProtocol.WriteJsonFrameAsync(
                    activeStream,
                    FrameType.AuthResult,
                    new AuthResultMessage(true, null, rootName, _options.AllowFullFilesystem, home, desktop, downloads, documents, _options.ReadOnly),
                    ct).ConfigureAwait(false);

                session = new ClientSession(clientLabel, remoteIpOnly, useTls);
                _sessions[session.SessionId] = session;

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.SessionCts.Token);
                using var closeReg = linkedCts.Token.Register(() => { try { tcpClient.Dispose(); } catch { } });

                EmitStatsAndActivity(updateRates: false);
                await CommandLoopAsync(activeStream, session, linkedCts.Token).ConfigureAwait(false);
            }
            finally
            {
                if (sslStream is not null)
                    await sslStream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Log?.Invoke($"Client {remoteDesc} error: {ex.Message}");
        }
        finally
        {
            if (session is not null)
            {
                _sessions.TryRemove(session.SessionId, out _);
                session.SessionCts.Dispose();
            }
            tcpClient.Dispose();
            Interlocked.Decrement(ref _activeConnections);
            EmitStatsAndActivity(updateRates: false);
        }
    }

    private async Task CommandLoopAsync(Stream stream, ClientSession session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await FrameProtocol.ReadFrameAsync(stream, ct).ConfigureAwait(false);
            if (frame is null) break;

            switch (frame.Value.Type)
            {
                case FrameType.Ping:
                    await FrameProtocol.WriteFrameAsync(stream, FrameType.Pong, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                    break;

                case FrameType.ListDirRequest:
                {
                    SetSessionAction(session, "Browsing files");
                    var req = FrameProtocol.DeserializePayload<ListDirRequestMessage>(frame.Value.Payload);
                    var resp = HandleListDir(req);
                    await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.ListDirResponse, resp, ct).ConfigureAwait(false);
                    SetSessionAction(session, "Connected (idle)");
                    break;
                }

                case FrameType.FileInfoRequest:
                {
                    var req = FrameProtocol.DeserializePayload<FileInfoRequestMessage>(frame.Value.Payload);
                    var resp = await HandleFileInfoAsync(req, ct).ConfigureAwait(false);
                    await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.FileInfoResponse, resp, ct).ConfigureAwait(false);
                    break;
                }

                case FrameType.CreateDirRequest:
                {
                    var req = FrameProtocol.DeserializePayload<CreateDirRequestMessage>(frame.Value.Payload);
                    var resp = HandleCreateDir(req);
                    await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.CreateDirResponse, resp, ct).ConfigureAwait(false);
                    break;
                }

                case FrameType.DeletePathRequest:
                {
                    var req = FrameProtocol.DeserializePayload<DeletePathRequestMessage>(frame.Value.Payload);
                    var resp = HandleDeletePath(req);
                    await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.DeletePathResponse, resp, ct).ConfigureAwait(false);
                    break;
                }

                case FrameType.RenamePathRequest:
                {
                    var req = FrameProtocol.DeserializePayload<RenamePathRequestMessage>(frame.Value.Payload);
                    var resp = HandleRenamePath(req);
                    await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.RenamePathResponse, resp, ct).ConfigureAwait(false);
                    break;
                }

                case FrameType.PullRangeRequest:
                {
                    var req = FrameProtocol.DeserializePayload<PullRangeRequestMessage>(frame.Value.Payload);
                    string fileName = Path.GetFileName(req.RelativePath.Replace('\\', '/'));
                    SetSessionAction(session, $"Sending {fileName}");
                    await HandlePullRangeAsync(stream, req, session, ct).ConfigureAwait(false);
                    SetSessionAction(session, "Connected (idle)");
                    break;
                }

                case FrameType.PushChunkRequest:
                {
                    var req = FrameProtocol.DeserializePayload<PushChunkRequestMessage>(frame.Value.Payload);
                    string fileName = Path.GetFileName(req.RelativePath.Replace('\\', '/'));
                    SetSessionAction(session, $"Receiving {fileName}");
                    await HandlePushChunkAsync(stream, req, session, ct).ConfigureAwait(false);
                    SetSessionAction(session, "Connected (idle)");
                    break;
                }

                case FrameType.PushFinalizeRequest:
                {
                    var req = FrameProtocol.DeserializePayload<PushFinalizeRequestMessage>(frame.Value.Payload);
                    var resp = await HandlePushFinalizeAsync(req, ct).ConfigureAwait(false);
                    await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.PushFinalizeAck, resp, ct).ConfigureAwait(false);
                    break;
                }

                case FrameType.SendTextRequest:
                {
                    var req = FrameProtocol.DeserializePayload<SendTextRequestMessage>(frame.Value.Payload);
                    if (string.IsNullOrEmpty(req.Text) || System.Text.Encoding.UTF8.GetByteCount(req.Text) > 256 * 1024)
                    {
                        await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.SendTextAck,
                            new SendTextAckMessage(false, "Text message is empty or exceeds 256 KB limit."), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        string sender = string.IsNullOrWhiteSpace(req.SenderName) ? session.ClientName : req.SenderName;
                        Log?.Invoke($"Received text snippet from {sender} ({req.Text.Length} chars)");
                        TextReceived?.Invoke(sender, req.Text);
                        await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.SendTextAck,
                            new SendTextAckMessage(true), ct).ConfigureAwait(false);
                    }
                    break;
                }

                default:
                    await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.Error, new ErrorMessage($"Unsupported frame type {frame.Value.Type}"), ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    private void SetSessionAction(ClientSession session, string action)
    {
        string prevAction = Volatile.Read(ref session.CurrentAction);
        if (!string.Equals(prevAction, action, StringComparison.Ordinal))
        {
            Volatile.Write(ref session.CurrentAction, action);
            bool idleTransition = string.Equals(prevAction, "Connected (idle)", StringComparison.Ordinal) ||
                                  string.Equals(action, "Connected (idle)", StringComparison.Ordinal);

            long now = Stopwatch.GetTimestamp();
            long prev = Interlocked.Read(ref _lastStatsNotifyTicks);
            if (idleTransition || (now - prev >= StatsNotifyIntervalTicks &&
                Interlocked.CompareExchange(ref _lastStatsNotifyTicks, now, prev) == prev))
            {
                EmitStatsAndActivity(updateRates: false);
            }
        }
    }

    private static (string? Home, string? Desktop, string? Downloads, string? Documents) GetStandardLocations()
    {
        static string? Clean(string? p) =>
            !string.IsNullOrWhiteSpace(p) && Directory.Exists(p) ? p.Replace('\\', '/').TrimEnd('/') : null;

        string? home = Clean(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string? desktop = Clean(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        string? documents = Clean(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        string? downloads = home is not null ? Clean(Path.Combine(home.Replace('/', Path.DirectorySeparatorChar), "Downloads")) : null;
        return (home, desktop, downloads, documents);
    }

    private bool TryResolveSafePath(string relativePath, out string fullPath, out string normalizedPath)
    {
        fullPath = string.Empty;
        normalizedPath = string.Empty;

        if (_options.AllowFullFilesystem)
        {
            string raw = (relativePath ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(raw) || raw.Equals(DrivesVirtualPath, StringComparison.OrdinalIgnoreCase))
            {
                fullPath = DrivesVirtualPath;
                normalizedPath = DrivesVirtualPath;
                return true;
            }

            try
            {
                string osPath = raw.Replace('/', Path.DirectorySeparatorChar);
                // Windows drive letter like "C:" -> "C:\"
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && osPath.Length == 2 && osPath[1] == ':')
                    osPath += Path.DirectorySeparatorChar;

                fullPath = Path.GetFullPath(osPath);
                normalizedPath = fullPath.Replace('\\', '/');
                if (normalizedPath.Length > 1 && !(normalizedPath.Length == 3 && normalizedPath[1] == ':'))
                    normalizedPath = normalizedPath.TrimEnd('/');
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Strict SharedDirectory sandbox mode
        string rootFull = Path.GetFullPath(_options.SharedDirectory);
        string rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        string cleaned = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(cleaned))
        {
            fullPath = rootFull;
            normalizedPath = string.Empty;
            return true;
        }

        string[] segments = cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string seg in segments)
        {
            if (seg == "." || seg == "..")
                return false;
        }

        string combined = Path.Combine(rootFull, Path.Combine(segments));
        string resolved = Path.GetFullPath(combined);

        var cmp = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!resolved.Equals(rootFull, cmp) && !resolved.StartsWith(rootWithSep, cmp))
            return false;

        fullPath = resolved;
        normalizedPath = string.Join('/', segments);
        return true;
    }

    private string FormatWirePath(string fullPath)
    {
        if (_options.AllowFullFilesystem)
        {
            string norm = fullPath.Replace('\\', '/');
            if (norm.Length > 1 && !(norm.Length == 3 && norm[1] == ':'))
                norm = norm.TrimEnd('/');
            return norm;
        }

        string rootFull = Path.GetFullPath(_options.SharedDirectory);
        return Path.GetRelativePath(rootFull, fullPath).Replace('\\', '/');
    }

    private ListDirResponseMessage HandleListDir(ListDirRequestMessage req)
    {
        if (!TryResolveSafePath(req.RelativePath, out string targetDir, out string normalizedPath))
            return new ListDirResponseMessage(req.RelativePath, [], "Access denied: invalid path.");

        if (_options.AllowFullFilesystem && targetDir == DrivesVirtualPath)
        {
            return new ListDirResponseMessage(DrivesVirtualPath, EnumerateRoots());
        }

        if (!Directory.Exists(targetDir))
            return new ListDirResponseMessage(normalizedPath, [], $"Directory not found: {targetDir}");

        var entries = new List<RemoteEntry>();

        try
        {
            if (!req.Recursive)
            {
                var dirInfo = new DirectoryInfo(targetDir);
                IEnumerable<DirectoryInfo> dirs;
                try { dirs = dirInfo.EnumerateDirectories().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase); }
                catch { dirs = []; }

                foreach (var d in dirs)
                {
                    try
                    {
                        if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((d.Attributes & FileAttributes.System) != 0 && (d.Attributes & FileAttributes.Hidden) != 0) continue;
                        entries.Add(new RemoteEntry(d.Name, FormatWirePath(d.FullName), true, 0, d.LastWriteTimeUtc));
                    }
                    catch { }
                }

                IEnumerable<FileInfo> files;
                try { files = dirInfo.EnumerateFiles().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase); }
                catch { files = []; }

                foreach (var f in files)
                {
                    try
                    {
                        if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((f.Attributes & FileAttributes.System) != 0 && (f.Attributes & FileAttributes.Hidden) != 0) continue;
                        entries.Add(new RemoteEntry(f.Name, FormatWirePath(f.FullName), false, f.Length, f.LastWriteTimeUtc));
                    }
                    catch { }
                }
            }
            else
            {
                var stack = new Stack<DirectoryInfo>();
                stack.Push(new DirectoryInfo(targetDir));
                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    IEnumerable<FileSystemInfo> items;
                    try { items = current.EnumerateFileSystemInfos(); }
                    catch { continue; }

                    foreach (var item in items)
                    {
                        try
                        {
                            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if (item is DirectoryInfo subDir)
                            {
                                entries.Add(new RemoteEntry(subDir.Name, FormatWirePath(subDir.FullName), true, 0, subDir.LastWriteTimeUtc));
                                stack.Push(subDir);
                            }
                            else if (item is FileInfo fi)
                            {
                                entries.Add(new RemoteEntry(fi.Name, FormatWirePath(fi.FullName), false, fi.Length, fi.LastWriteTimeUtc));
                            }
                        }
                        catch { }
                    }
                }
            }

            return new ListDirResponseMessage(normalizedPath, entries);
        }
        catch (Exception ex)
        {
            return new ListDirResponseMessage(normalizedPath, [], ex.Message);
        }
    }

    public static List<RemoteEntry> EnumerateRoots()
    {
        var entries = new List<RemoteEntry>();
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!drive.IsReady) continue;
                        string label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                            ? drive.Name.TrimEnd('\\')
                            : $"{drive.Name.TrimEnd('\\')} ({drive.VolumeLabel})";
                        entries.Add(new RemoteEntry(label, drive.Name.Replace('\\', '/'), true, drive.TotalSize, DateTime.UtcNow));
                    }
                    catch { }
                }
            }
            else
            {
                entries.Add(new RemoteEntry("/ (Root)", "/", true, 0, DateTime.UtcNow));
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home))
                    entries.Add(new RemoteEntry($"Home ({home})", home, true, 0, DateTime.UtcNow));

                foreach (string mountRoot in new[] { "/mnt", "/media" })
                {
                    if (!Directory.Exists(mountRoot)) continue;
                    try
                    {
                        foreach (string sub in Directory.GetDirectories(mountRoot))
                            entries.Add(new RemoteEntry(sub, sub, true, 0, DateTime.UtcNow));
                    }
                    catch { }
                }
            }
        }
        catch { }
        return entries;
    }

    private async Task<bool> RequestApprovalWithTimeoutAsync(string clientName, string remoteAddr, bool tlsActive)
    {
        var handler = ConnectionApprovalHandler;
        if (handler is null) return true;
        try
        {
            var promptTask = handler(clientName, remoteAddr, tlsActive);
            var completed = await Task.WhenAny(promptTask, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false);
            if (completed != promptTask)
                return false;
            return await promptTask.ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private CreateDirResponseMessage HandleCreateDir(CreateDirRequestMessage req)
    {
        if (_options.ReadOnly)
            return new CreateDirResponseMessage(false, "Host is in Read-Only mode.");
        if (!TryResolveSafePath(req.RelativePath, out string fullPath, out _) || fullPath == DrivesVirtualPath)
            return new CreateDirResponseMessage(false, "Invalid directory path.");

        try
        {
            Directory.CreateDirectory(fullPath);
            return new CreateDirResponseMessage(true);
        }
        catch (Exception ex)
        {
            return new CreateDirResponseMessage(false, ex.Message);
        }
    }

    private DeletePathResponseMessage HandleDeletePath(DeletePathRequestMessage req)
    {
        if (_options.ReadOnly)
            return new DeletePathResponseMessage(false, "Host is in Read-Only mode.");
        if (!TryResolveSafePath(req.RelativePath, out string fullPath, out _) || fullPath == DrivesVirtualPath)
            return new DeletePathResponseMessage(false, "Invalid path.");

        // Safety guard: never allow deleting filesystem roots or the shared root itself
        string trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(trimmed) || (trimmed.Length == 2 && trimmed[1] == ':'))
            return new DeletePathResponseMessage(false, "Cannot delete a root drive.");

        if (!_options.AllowFullFilesystem &&
            trimmed.Equals(Path.GetFullPath(_options.SharedDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return new DeletePathResponseMessage(false, "Cannot delete the shared root folder.");

        try
        {
            EvictPullHandle(fullPath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                Log?.Invoke($"Deleted file: {Path.GetFileName(fullPath)}");
                return new DeletePathResponseMessage(true);
            }
            if (Directory.Exists(fullPath))
            {
                EvictPullHandlesUnderDirectory(fullPath);
                Directory.Delete(fullPath, recursive: true);
                Log?.Invoke($"Deleted folder: {Path.GetFileName(trimmed)}");
                return new DeletePathResponseMessage(true);
            }
            return new DeletePathResponseMessage(false, "Item not found.");
        }
        catch (Exception ex)
        {
            return new DeletePathResponseMessage(false, ex.Message);
        }
    }

    private RenamePathResponseMessage HandleRenamePath(RenamePathRequestMessage req)
    {
        if (_options.ReadOnly)
            return new RenamePathResponseMessage(false, null, "Host is in Read-Only mode.");
        if (!TryResolveSafePath(req.RelativePath, out string fullPath, out _) || fullPath == DrivesVirtualPath)
            return new RenamePathResponseMessage(false, null, "Invalid path.");

        string newName = (req.NewName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(newName) || newName.Contains('/') || newName.Contains('\\') || newName is "." or "..")
            return new RenamePathResponseMessage(false, null, "Invalid new name.");

        string? parentDir = Path.GetDirectoryName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(parentDir))
            return new RenamePathResponseMessage(false, null, "Cannot rename a root drive.");

        string newFullPath = Path.Combine(parentDir, newName);
        try
        {
            EvictPullHandle(fullPath);
            if (File.Exists(fullPath))
            {
                File.Move(fullPath, newFullPath, overwrite: false);
                return new RenamePathResponseMessage(true, FormatWirePath(newFullPath));
            }
            if (Directory.Exists(fullPath))
            {
                EvictPullHandlesUnderDirectory(fullPath);
                Directory.Move(fullPath, newFullPath);
                return new RenamePathResponseMessage(true, FormatWirePath(newFullPath));
            }
            return new RenamePathResponseMessage(false, null, "Item not found.");
        }
        catch (Exception ex)
        {
            return new RenamePathResponseMessage(false, null, ex.Message);
        }
    }

    private void EvictPullHandle(string fullPath)
    {
        if (_activePullHandles.TryRemove(fullPath, out var entry))
        {
            try { entry.Handle.Dispose(); } catch { }
        }
    }

    private void EvictPullHandlesUnderDirectory(string dirPath)
    {
        string prefix = dirPath.EndsWith(Path.DirectorySeparatorChar) ? dirPath : dirPath + Path.DirectorySeparatorChar;
        foreach (var kv in _activePullHandles)
        {
            if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (_activePullHandles.TryRemove(kv.Key, out var entry))
                {
                    try { entry.Handle.Dispose(); } catch { }
                }
            }
        }
    }

    private async Task<FileInfoResponseMessage> HandleFileInfoAsync(FileInfoRequestMessage req, CancellationToken ct)
    {
        if (!TryResolveSafePath(req.RelativePath, out string fullPath, out _) || fullPath == DrivesVirtualPath)
            return new FileInfoResponseMessage(false, 0, DateTime.MinValue, null, "Invalid path.");

        if (!File.Exists(fullPath))
            return new FileInfoResponseMessage(false, 0, DateTime.MinValue, null, "File not found.");

        try
        {
            var fi = new FileInfo(fullPath);
            string? sha256 = null;
            if (req.IncludeSha256)
            {
                await using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
                byte[] hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
                sha256 = Convert.ToHexString(hash).ToLowerInvariant();
            }

            return new FileInfoResponseMessage(true, fi.Length, fi.LastWriteTimeUtc, sha256);
        }
        catch (Exception ex)
        {
            return new FileInfoResponseMessage(false, 0, DateTime.MinValue, null, ex.Message);
        }
    }

    private async Task<SafeFileHandle> GetOrOpenPullHandleAsync(string fullPath, long fileSize, long lastWriteTicks, CancellationToken ct)
    {
        if (_activePullHandles.TryGetValue(fullPath, out var existing) &&
            !existing.Handle.IsClosed && !existing.Handle.IsInvalid &&
            existing.FileSize == fileSize && existing.LastWriteTicks == lastWriteTicks)
        {
            Interlocked.Exchange(ref existing.LastUsedTicks, DateTime.UtcNow.Ticks);
            return existing.Handle;
        }

        var fileLock = _pullFileLocks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_activePullHandles.TryGetValue(fullPath, out existing) &&
                !existing.Handle.IsClosed && !existing.Handle.IsInvalid &&
                existing.FileSize == fileSize && existing.LastWriteTicks == lastWriteTicks)
            {
                Interlocked.Exchange(ref existing.LastUsedTicks, DateTime.UtcNow.Ticks);
                return existing.Handle;
            }

            if (existing is not null)
            {
                try { existing.Handle.Dispose(); } catch { }
            }

            var opened = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var created = new PullHandleEntry(opened, fileSize, lastWriteTicks);
            _activePullHandles[fullPath] = created;
            return opened;
        }
        finally
        {
            fileLock.Release();
        }
    }

    private async Task HandlePullRangeAsync(Stream stream, PullRangeRequestMessage req, ClientSession session, CancellationToken ct)
    {
        if (!TryResolveSafePath(req.RelativePath, out string fullPath, out _) || fullPath == DrivesVirtualPath)
        {
            await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.PullRangeHeader,
                new PullRangeHeaderMessage(false, 0, DateTime.MinValue, 0, 0, "Invalid path."), ct).ConfigureAwait(false);
            return;
        }

        if (!File.Exists(fullPath))
        {
            await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.PullRangeHeader,
                new PullRangeHeaderMessage(false, 0, DateTime.MinValue, 0, 0, "File not found."), ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var fi = new FileInfo(fullPath);
            long fileSize = fi.Length;
            long lastWriteTicks = fi.LastWriteTimeUtc.Ticks;
            long offset = Math.Clamp(req.Offset, 0, fileSize);
            long requestedLen = req.Length < 0 ? (fileSize - offset) : Math.Min(req.Length, fileSize - offset);

            await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.PullRangeHeader,
                new PullRangeHeaderMessage(true, fileSize, fi.LastWriteTimeUtc, offset, requestedLen), ct).ConfigureAwait(false);

            if (requestedLen > 0)
            {
                bool allowCompress = req.AllowCompression && CompressionHelper.ShouldCompressFile(fullPath);
                var handle = await GetOrOpenPullHandleAsync(fullPath, fileSize, lastWriteTicks, ct).ConfigureAwait(false);
                int frameBufSize = 5 + ProtocolConstants.DataFrameChunkBytes;
                int compBufSize = 9 + ProtocolConstants.DataFrameChunkBytes;
                byte[] bufA = ArrayPool<byte>.Shared.Rent(frameBufSize);
                byte[] bufB = ArrayPool<byte>.Shared.Rent(frameBufSize);
                byte[]? compA = allowCompress ? ArrayPool<byte>.Shared.Rent(compBufSize) : null;
                byte[]? compB = allowCompress ? ArrayPool<byte>.Shared.Rent(compBufSize) : null;
                Task? pendingSend = null;
                try
                {
                    long remaining = requestedLen;
                    long pos = offset;
                    bool useA = true;

                    while (remaining > 0)
                    {
                        byte[] curBuf = useA ? bufA : bufB;
                        byte[]? curComp = useA ? compA : compB;
                        int toRead = (int)Math.Min(remaining, ProtocolConstants.DataFrameChunkBytes);
                        int read = await RandomAccess.ReadAsync(handle, curBuf.AsMemory(5, toRead), pos, ct).ConfigureAwait(false);
                        if (read == 0) break;

                        // Wait for previous network write before starting the next send on the same stream
                        if (pendingSend is not null)
                            await pendingSend.ConfigureAwait(false);

                        if (curComp is not null && CompressionHelper.TryCompressPreframed(curBuf.AsSpan(5, read), curComp, out int compPacketBytes))
                        {
                            pendingSend = stream.WriteAsync(curComp.AsMemory(0, compPacketBytes), ct).AsTask();
                        }
                        else
                        {
                            pendingSend = FrameProtocol.WritePreframedDataAsync(stream, curBuf, read, ct).AsTask();
                        }
                        pos += read;
                        remaining -= read;
                        useA = !useA;

                        RecordTransferredBytes(session, read);
                    }

                    if (pendingSend is not null)
                        await pendingSend.ConfigureAwait(false);
                }
                finally
                {
                    if (pendingSend is not null)
                    {
                        try { await pendingSend.ConfigureAwait(false); } catch { }
                    }
                    ArrayPool<byte>.Shared.Return(bufA);
                    ArrayPool<byte>.Shared.Return(bufB);
                    if (compA is not null) ArrayPool<byte>.Shared.Return(compA);
                    if (compB is not null) ArrayPool<byte>.Shared.Return(compB);
                }
            }

            await FrameProtocol.WriteFrameAsync(stream, FrameType.DataEnd, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException and not ObjectDisposedException)
        {
            await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.PullRangeHeader,
                new PullRangeHeaderMessage(false, 0, DateTime.MinValue, 0, 0, ex.Message), ct).ConfigureAwait(false);
        }
    }

    private async Task HandlePushChunkAsync(Stream stream, PushChunkRequestMessage req, ClientSession session, CancellationToken ct)
    {
        if (_options.ReadOnly)
        {
            await DrainUntilDataEndAsync(stream, ct).ConfigureAwait(false);
            await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.PushChunkAck, new PushChunkAckMessage(false, "Host is in Read-Only mode."), ct).ConfigureAwait(false);
            return;
        }

        if (!TryResolveSafePath(req.RelativePath, out string fullPath, out _) || fullPath == DrivesVirtualPath)
        {
            await DrainUntilDataEndAsync(stream, ct).ConfigureAwait(false);
            await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.PushChunkAck, new PushChunkAckMessage(false, "Invalid target path."), ct).ConfigureAwait(false);
            return;
        }

        string partPath = fullPath + ".sbpart";
        var fileLock = _pushFileLocks.GetOrAdd(partPath, _ => new SemaphoreSlim(1, 1));

        try
        {
            PushHandleEntry entry;
            if (!_activePushHandles.TryGetValue(partPath, out entry!) || entry.Handle.IsClosed || entry.Handle.IsInvalid)
            {
                await fileLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (!_activePushHandles.TryGetValue(partPath, out entry!) || entry.Handle.IsClosed || entry.Handle.IsInvalid)
                    {
                        string? dir = Path.GetDirectoryName(partPath);
                        if (!string.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir);

                        var opened = File.OpenHandle(partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.Asynchronous);
                        SparseFileHelper.TryMarkSparse(opened);
                        if (RandomAccess.GetLength(opened) < req.TotalFileSize)
                            RandomAccess.SetLength(opened, req.TotalFileSize);
                        entry = new PushHandleEntry(opened);
                        _activePushHandles[partPath] = entry;
                    }
                }
                finally
                {
                    fileLock.Release();
                }
            }

            SafeFileHandle handle = entry.Handle;
            Interlocked.Exchange(ref entry.LastUsedTicks, DateTime.UtcNow.Ticks);

            byte[] bufA = ArrayPool<byte>.Shared.Rent(ProtocolConstants.DataFrameChunkBytes);
            byte[] bufB = ArrayPool<byte>.Shared.Rent(ProtocolConstants.DataFrameChunkBytes);
            byte[] compBuf = ArrayPool<byte>.Shared.Rent(ProtocolConstants.DataFrameChunkBytes + 1024);
            Task? pendingDiskWrite = null;
            try
            {
                long writePos = req.Offset;
                long received = 0;
                bool useA = true;

                while (true)
                {
                    var frameHeader = await FrameProtocol.ReadFrameHeaderAsync(stream, ct).ConfigureAwait(false)
                        ?? throw new EndOfStreamException("Connection closed during push chunk.");

                    if (frameHeader.Type == FrameType.DataEnd)
                        break;

                    if (frameHeader.Type == FrameType.CompressedDataChunk)
                    {
                        if (frameHeader.Length > compBuf.Length)
                        {
                            ArrayPool<byte>.Shared.Return(compBuf);
                            compBuf = ArrayPool<byte>.Shared.Rent(frameHeader.Length);
                        }
                        await FrameProtocol.ReadExactAsync(stream, compBuf.AsMemory(0, frameHeader.Length), ct).ConfigureAwait(false);
                        byte[] curBuf = useA ? bufA : bufB;
                        int rawLen = CompressionHelper.DecompressFramePayload(compBuf.AsSpan(0, frameHeader.Length), curBuf.AsSpan(0, ProtocolConstants.DataFrameChunkBytes));

                        if (pendingDiskWrite is not null)
                            await pendingDiskWrite.ConfigureAwait(false);

                        long targetOffset = writePos;
                        pendingDiskWrite = RandomAccess.WriteAsync(handle, curBuf.AsMemory(0, rawLen), targetOffset, ct).AsTask();
                        writePos += rawLen;
                        received += rawLen;
                        useA = !useA;
                        RecordTransferredBytes(session, rawLen);
                        continue;
                    }

                    if (frameHeader.Type != FrameType.DataChunk)
                        throw new InvalidDataException($"Expected DataChunk during push, got {frameHeader.Type}.");

                    int remainingFrame = frameHeader.Length;
                    while (remainingFrame > 0)
                    {
                        byte[] curBuf = useA ? bufA : bufB;
                        int slice = Math.Min(remainingFrame, ProtocolConstants.DataFrameChunkBytes);
                        await FrameProtocol.ReadExactAsync(stream, curBuf.AsMemory(0, slice), ct).ConfigureAwait(false);

                        if (pendingDiskWrite is not null)
                            await pendingDiskWrite.ConfigureAwait(false);

                        long targetOffset = writePos;
                        pendingDiskWrite = RandomAccess.WriteAsync(handle, curBuf.AsMemory(0, slice), targetOffset, ct).AsTask();
                        writePos += slice;
                        received += slice;
                        remainingFrame -= slice;
                        useA = !useA;

                        RecordTransferredBytes(session, slice);
                    }
                }

                if (pendingDiskWrite is not null)
                    await pendingDiskWrite.ConfigureAwait(false);

                if (received != req.Length)
                {
                    await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.PushChunkAck,
                        new PushChunkAckMessage(false, $"Expected {req.Length} bytes, got {received}."), ct).ConfigureAwait(false);
                    return;
                }
            }
            finally
            {
                if (pendingDiskWrite is not null)
                {
                    try { await pendingDiskWrite.ConfigureAwait(false); } catch { }
                }
                ArrayPool<byte>.Shared.Return(bufA);
                ArrayPool<byte>.Shared.Return(bufB);
                ArrayPool<byte>.Shared.Return(compBuf);
            }

            await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.PushChunkAck, new PushChunkAckMessage(true), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException and not EndOfStreamException)
        {
            await FrameProtocol.WriteJsonFrameAsync(stream, FrameType.PushChunkAck, new PushChunkAckMessage(false, ex.Message), ct).ConfigureAwait(false);
        }
    }

    private static async Task DrainUntilDataEndAsync(Stream stream, CancellationToken ct)
    {
        byte[] discard = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var header = await FrameProtocol.ReadFrameHeaderAsync(stream, ct).ConfigureAwait(false);
                if (header is null || header.Value.Type == FrameType.DataEnd) break;
                int rem = header.Value.Length;
                while (rem > 0)
                {
                    int n = Math.Min(rem, discard.Length);
                    await FrameProtocol.ReadExactAsync(stream, discard.AsMemory(0, n), ct).ConfigureAwait(false);
                    rem -= n;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(discard);
        }
    }

    private async Task<PushFinalizeAckMessage> HandlePushFinalizeAsync(PushFinalizeRequestMessage req, CancellationToken ct)
    {
        if (_options.ReadOnly)
            return new PushFinalizeAckMessage(false, "Host is in Read-Only mode.");
        if (!TryResolveSafePath(req.RelativePath, out string fullPath, out _) || fullPath == DrivesVirtualPath)
            return new PushFinalizeAckMessage(false, "Invalid target path.");

        string partPath = fullPath + ".sbpart";
        var fileLock = _pushFileLocks.GetOrAdd(partPath, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_activePushHandles.TryRemove(partPath, out var openEntry))
            {
                try { openEntry.Handle.Dispose(); } catch { }
            }

            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (req.TotalFileSize == 0)
            {
                await File.WriteAllBytesAsync(fullPath, Array.Empty<byte>(), ct).ConfigureAwait(false);
                try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
                try { File.SetLastWriteTimeUtc(fullPath, req.LastModifiedUtc); } catch { }
                Log?.Invoke($"Received file: {Path.GetFileName(fullPath)} (0 B)");
                return new PushFinalizeAckMessage(true);
            }

            if (!File.Exists(partPath))
                return new PushFinalizeAckMessage(false, "Partial file missing on host.");

            if (!string.IsNullOrWhiteSpace(req.ExpectedSha256Hex))
            {
                await using var fs = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
                byte[] hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
                string actualHex = Convert.ToHexString(hash).ToLowerInvariant();
                if (!actualHex.Equals(req.ExpectedSha256Hex, StringComparison.OrdinalIgnoreCase))
                    return new PushFinalizeAckMessage(false, "SHA-256 verification failed on remote host.");
            }

            File.Move(partPath, fullPath, overwrite: true);
            try { File.SetLastWriteTimeUtc(fullPath, req.LastModifiedUtc); } catch { }
            Log?.Invoke($"Received file: {Path.GetFileName(fullPath)} ({req.TotalFileSize / (1024.0 * 1024.0):0.##} MB)");
            return new PushFinalizeAckMessage(true);
        }
        catch (Exception ex)
        {
            return new PushFinalizeAckMessage(false, ex.Message);
        }
        finally
        {
            fileLock.Release();
            _pushFileLocks.TryRemove(partPath, out _);
        }
    }

    private static string GetOsLabel()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS";
        return RuntimeInformation.OSDescription;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        foreach (var kv in _activePushHandles)
        {
            try { kv.Value.Handle.Dispose(); } catch { }
        }
        _activePushHandles.Clear();
        foreach (var kv in _activePullHandles)
        {
            try { kv.Value.Handle.Dispose(); } catch { }
        }
        _activePullHandles.Clear();
        _certificate.Dispose();
    }
}

internal static class IpEndPointAddressExtensions
{
    public static string MapToIPv4IfMapped(this IPAddress addr)
    {
        return addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4().ToString() : addr.ToString();
    }
}
