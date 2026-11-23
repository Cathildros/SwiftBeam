namespace SwiftBeam.Core.Discovery;

public sealed record PeerEndpoint(
    string Host,
    int Port,
    LinkKind LinkKind,
    DateTime LastSeenUtc)
{
    public string LinkBadge => LinkKind switch
    {
        LinkKind.DirectCable => "Direct Cable",
        LinkKind.Wired => "Wired",
        LinkKind.WiFi => "Wi-Fi",
        _ => "LAN"
    };

    public string DisplayLabel => $"{LinkBadge} ({Host}:{Port})";
}

public sealed class PeerInfo
{
    private readonly Dictionary<string, PeerEndpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);

    public string ServerId { get; }
    public string DeviceName { get; set; }
    public string OsPlatform { get; set; }
    public int Port { get; set; }
    public bool RequiresPin { get; set; }
    public bool AllowUnencrypted { get; set; }
    public DateTime LastSeenUtc { get; set; }

    public PeerInfo(string serverId, string deviceName, string osPlatform, int port, bool requiresPin, bool allowUnencrypted)
    {
        ServerId = serverId;
        DeviceName = deviceName;
        OsPlatform = osPlatform;
        Port = port;
        RequiresPin = requiresPin;
        AllowUnencrypted = allowUnencrypted;
        LastSeenUtc = DateTime.UtcNow;
    }

    public void UpsertEndpoint(string host, int port, LinkKind kind)
    {
        var now = DateTime.UtcNow;
        LastSeenUtc = now;
        _endpoints[host] = new PeerEndpoint(host, port, kind, now);
    }

    public void PruneEndpoints(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var stale = _endpoints.Where(kv => kv.Value.LastSeenUtc < cutoff).Select(kv => kv.Key).ToList();
        foreach (var k in stale) _endpoints.Remove(k);
    }

    public IReadOnlyList<PeerEndpoint> OrderedEndpoints => _endpoints.Values
        .OrderBy(e => (int)e.LinkKind)
        .ThenBy(e => e.Host.Contains(':') ? 1 : 0) // Prefer IPv4 when same LinkKind, use IPv6 link-local seamlessly for direct cable
        .ToList();

    public PeerEndpoint? BestEndpoint => OrderedEndpoints.FirstOrDefault();

    public bool HasDirectCable => _endpoints.Values.Any(e => e.LinkKind == LinkKind.DirectCable);
}
