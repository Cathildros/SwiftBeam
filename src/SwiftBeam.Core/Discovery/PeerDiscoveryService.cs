using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SwiftBeam.Core.Discovery;

internal sealed record DiscoveryBeacon(
    string Magic,
    string Kind,
    string ServerId,
    string DeviceName,
    string OsPlatform,
    int TransferPort,
    bool RequiresPin,
    bool AllowUnencrypted);

public sealed class PeerDiscoveryService : IAsyncDisposable
{
    private const string BeaconMagic = "SWB1";
    private static readonly IPAddress IPv6MulticastGroup = IPAddress.Parse("ff02::53:4201");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _peersLock = new();
    private readonly Dictionary<string, PeerInfo> _peers = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _localServerId;
    private readonly Func<(string DeviceName, int TransferPort, bool RequiresPin, bool AllowUnencrypted, bool IsServerRunning)> _stateProvider;
    private readonly int _discoveryPort;

    private Socket? _ipv4Listener;
    private Socket? _ipv6Listener;
    private CancellationTokenSource? _cts;
    private readonly List<Task> _tasks = [];

    public event Action<IReadOnlyList<PeerInfo>>? PeersChanged;
    public event Action<string>? Log;

    public PeerDiscoveryService(
        string localServerId,
        int discoveryPort,
        Func<(string DeviceName, int TransferPort, bool RequiresPin, bool AllowUnencrypted, bool IsServerRunning)> stateProvider)
    {
        _localServerId = localServerId;
        _discoveryPort = discoveryPort;
        _stateProvider = stateProvider;
    }

    public IReadOnlyList<PeerInfo> GetPeersSnapshot()
    {
        lock (_peersLock)
        {
            return _peers.Values
                .Where(p => p.BestEndpoint is not null)
                .OrderBy(p => (int)(p.BestEndpoint?.LinkKind ?? LinkKind.Other))
                .ThenBy(p => p.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();

        StartIPv4Listener(_cts.Token);
        StartIPv6MulticastListener(_cts.Token);

        _tasks.Add(Task.Run(() => BroadcastLoopAsync(_cts.Token)));
        _tasks.Add(Task.Run(() => PruneLoopAsync(_cts.Token)));
    }

    public Task SendProbeNowAsync()
    {
        return BroadcastPacketAsync("PROBE", CancellationToken.None);
    }

    private void StartIPv4Listener(CancellationToken ct)
    {
        try
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.EnableBroadcast = true;
            sock.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
            _ipv4Listener = sock;
            _tasks.Add(Task.Run(() => ReceiveLoopAsync(sock, ct)));
        }
        catch (Exception ex)
        {
            Log?.Invoke($"IPv4 discovery listener warning: {ex.Message}");
        }
    }

    private void StartIPv6MulticastListener(CancellationToken ct)
    {
        try
        {
            var sock = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.Bind(new IPEndPoint(IPAddress.IPv6Any, _discoveryPort));

            JoinMulticastOnActiveInterfaces(sock);

            _ipv6Listener = sock;
            _tasks.Add(Task.Run(() => ReceiveLoopAsync(sock, ct)));
        }
        catch (Exception ex)
        {
            Log?.Invoke($"IPv6 direct-cable discovery listener warning: {ex.Message}");
        }
    }

    private void JoinMulticastOnActiveInterfaces(Socket sock)
    {
        var interfaces = NetworkInfo.GetActiveAddresses()
            .Where(a => a.IPv6InterfaceIndex.HasValue)
            .Select(a => a.IPv6InterfaceIndex!.Value)
            .Distinct()
            .ToList();

        foreach (int idx in interfaces)
        {
            try
            {
                sock.SetSocketOption(
                    SocketOptionLevel.IPv6,
                    SocketOptionName.AddMembership,
                    new IPv6MulticastOption(IPv6MulticastGroup, idx));
            }
            catch
            {
                // Ignore if already joined or interface doesn't support multicast
            }
        }
    }

    private async Task ReceiveLoopAsync(Socket sock, CancellationToken ct)
    {
        byte[] buffer = new byte[4096];
        EndPoint anyRemote = sock.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await sock.ReceiveFromAsync(buffer, SocketFlags.None, anyRemote, ct).ConfigureAwait(false);
                if (result.ReceivedBytes <= 0 || result.RemoteEndPoint is not IPEndPoint remoteEp)
                    continue;

                string json = Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes);
                var beacon = JsonSerializer.Deserialize<DiscoveryBeacon>(json, JsonOptions);
                if (beacon is null || beacon.Magic != BeaconMagic)
                    continue;

                // Ignore our own beacons
                if (string.Equals(beacon.ServerId, _localServerId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (beacon.Kind == "PROBE")
                {
                    _ = BroadcastPacketAsync("HELLO", ct);
                }

                if (beacon.TransferPort > 0)
                {
                    RegisterDiscoveredPeer(beacon, remoteEp.Address);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                // Ignore malformed UDP packets
            }
        }
    }

    private void RegisterDiscoveredPeer(DiscoveryBeacon beacon, IPAddress remoteIp)
    {
        if (remoteIp.IsIPv4MappedToIPv6)
            remoteIp = remoteIp.MapToIPv4();

        var localAddrs = NetworkInfo.GetActiveAddresses();
        var linkKind = NetworkInfo.ClassifyRemoteAddress(remoteIp, localAddrs);
        string host = remoteIp.ToString();

        bool changed = false;
        lock (_peersLock)
        {
            if (!_peers.TryGetValue(beacon.ServerId, out var peer))
            {
                peer = new PeerInfo(
                    beacon.ServerId,
                    beacon.DeviceName,
                    beacon.OsPlatform,
                    beacon.TransferPort,
                    beacon.RequiresPin,
                    beacon.AllowUnencrypted);
                _peers[beacon.ServerId] = peer;
                changed = true;
            }
            else
            {
                if (peer.DeviceName != beacon.DeviceName ||
                    peer.Port != beacon.TransferPort ||
                    peer.RequiresPin != beacon.RequiresPin ||
                    peer.AllowUnencrypted != beacon.AllowUnencrypted)
                {
                    peer.DeviceName = beacon.DeviceName;
                    peer.Port = beacon.TransferPort;
                    peer.RequiresPin = beacon.RequiresPin;
                    peer.AllowUnencrypted = beacon.AllowUnencrypted;
                    changed = true;
                }
            }

            bool hadEndpoint = peer.OrderedEndpoints.Any(e => e.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
            peer.UpsertEndpoint(host, beacon.TransferPort, linkKind);
            if (!hadEndpoint) changed = true;
        }

        if (changed)
            PeersChanged?.Invoke(GetPeersSnapshot());
    }

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        // Send an immediate probe + hello on startup
        await BroadcastPacketAsync("PROBE", ct).ConfigureAwait(false);

        int tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Periodically re-join IPv6 multicast on newly plugged-in Ethernet cables!
            tick++;
            if (tick % 4 == 0 && _ipv6Listener is not null)
            {
                JoinMulticastOnActiveInterfaces(_ipv6Listener);
            }

            await BroadcastPacketAsync("HELLO", ct).ConfigureAwait(false);
        }
    }

    private async Task BroadcastPacketAsync(string kind, CancellationToken ct)
    {
        var state = _stateProvider();
        var beacon = new DiscoveryBeacon(
            BeaconMagic,
            kind,
            _localServerId,
            state.DeviceName,
            GetOsLabel(),
            state.IsServerRunning ? state.TransferPort : 0,
            state.RequiresPin,
            state.AllowUnencrypted);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(beacon, JsonOptions);
        var activeAddresses = NetworkInfo.GetActiveAddresses();

        // 1. Send IPv4 broadcasts bound to EACH active IPv4 interface (ensures direct Ethernet cable + Wi-Fi both receive beacons)
        foreach (var addr in activeAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
        {
            try
            {
                using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sender.EnableBroadcast = true;
                sender.Bind(new IPEndPoint(addr.Address, 0));

                if (addr.BroadcastAddress is not null)
                {
                    await sender.SendToAsync(payload, SocketFlags.None, new IPEndPoint(addr.BroadcastAddress, _discoveryPort), ct).ConfigureAwait(false);
                }

                await sender.SendToAsync(payload, SocketFlags.None, new IPEndPoint(IPAddress.Broadcast, _discoveryPort), ct).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort per interface
            }
        }

        // 2. Send IPv6 link-local multicast on EACH active IPv6 interface (zero-config direct cable discovery between Windows & Linux!)
        var ipv6Indices = activeAddresses
            .Where(a => a.IPv6InterfaceIndex.HasValue)
            .Select(a => a.IPv6InterfaceIndex!.Value)
            .Distinct()
            .ToList();

        foreach (int ifIndex in ipv6Indices)
        {
            try
            {
                using var sender6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                sender6.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, ifIndex);
                sender6.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 1);
                await sender6.SendToAsync(payload, SocketFlags.None, new IPEndPoint(IPv6MulticastGroup, _discoveryPort), ct).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort per interface
            }
        }
    }

    private async Task PruneLoopAsync(CancellationToken ct)
    {
        var maxAge = TimeSpan.FromSeconds(12);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            bool removedAny = false;
            var cutoff = DateTime.UtcNow - maxAge;

            lock (_peersLock)
            {
                foreach (var peer in _peers.Values)
                    peer.PruneEndpoints(maxAge);

                var staleIds = _peers.Where(kv => kv.Value.LastSeenUtc < cutoff || kv.Value.BestEndpoint is null)
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var id in staleIds)
                {
                    _peers.Remove(id);
                    removedAny = true;
                }
            }

            if (removedAny)
                PeersChanged?.Invoke(GetPeersSnapshot());
        }
    }

    private static string GetOsLabel()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS";
        return "Other";
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            try { _ipv4Listener?.Dispose(); } catch { }
            try { _ipv6Listener?.Dispose(); } catch { }
            try { await Task.WhenAll(_tasks).ConfigureAwait(false); } catch { }
            _cts.Dispose();
            _cts = null;
        }
    }
}
