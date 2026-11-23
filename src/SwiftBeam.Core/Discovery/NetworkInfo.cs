using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SwiftBeam.Core.Discovery;

public enum LinkKind
{
    DirectCable = 0, // Link-local (169.254.x.x or fe80:: on Ethernet/USB)
    Wired = 1,       // Standard wired Ethernet LAN
    WiFi = 2,        // Wireless 802.11
    Other = 3        // VPN, bridge, unknown
}

public sealed record LocalInterfaceAddress(
    string InterfaceName,
    string Description,
    IPAddress Address,
    IPAddress? BroadcastAddress,
    int? IPv6InterfaceIndex,
    LinkKind LinkKind,
    long SpeedBitsPerSec)
{
    public string SpeedLabel
    {
        get
        {
            if (SpeedBitsPerSec <= 0) return string.Empty;
            if (SpeedBitsPerSec >= 1_000_000_000) return $"{SpeedBitsPerSec / 1_000_000_000.0:0.#} Gbps";
            if (SpeedBitsPerSec >= 1_000_000) return $"{SpeedBitsPerSec / 1_000_000.0:0} Mbps";
            return $"{SpeedBitsPerSec / 1_000.0:0} Kbps";
        }
    }

    public string LinkBadge => LinkKind switch
    {
        LinkKind.DirectCable => "Direct Cable",
        LinkKind.Wired => "Wired",
        LinkKind.WiFi => "Wi-Fi",
        _ => "Network"
    };

    public string DisplayAddress => Address.AddressFamily == AddressFamily.InterNetworkV6
        ? $"[{Address}]"
        : Address.ToString();
}

public static class NetworkInfo
{
    public static List<LocalInterfaceAddress> GetActiveAddresses()
    {
        var results = new List<LocalInterfaceAddress>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                // Skip common virtual hypervisor/docker adapters when better adapters exist, or tag as Other
                bool looksVirtual = LooksLikeVirtualAdapter(nic.Name, nic.Description);

                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); }
                catch { continue; }

                int? ipv6Index = null;
                try
                {
                    if (nic.Supports(NetworkInterfaceComponent.IPv6))
                        ipv6Index = props.GetIPv6Properties()?.Index;
                }
                catch { }

                bool isWired = IsWiredInterface(nic.NetworkInterfaceType, nic.Name, nic.Description);
                bool isWifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                              nic.Name.StartsWith("wl", StringComparison.OrdinalIgnoreCase);

                foreach (var uni in props.UnicastAddresses)
                {
                    var ip = uni.Address;
                    if (IPAddress.IsLoopback(ip)) continue;

                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        bool isLinkLocalV4 = IsIPv4LinkLocal(ip);
                        LinkKind kind = ResolveLinkKind(isWired, isWifi, looksVirtual, isLinkLocalV4);
                        IPAddress? broadcast = ComputeDirectedBroadcast(ip, uni.IPv4Mask);

                        results.Add(new LocalInterfaceAddress(
                            nic.Name,
                            nic.Description,
                            ip,
                            broadcast,
                            ipv6Index,
                            kind,
                            nic.Speed));
                    }
                    else if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv6LinkLocal)
                    {
                        LinkKind kind = ResolveLinkKind(isWired, isWifi, looksVirtual, isLinkLocal: true);
                        results.Add(new LocalInterfaceAddress(
                            nic.Name,
                            nic.Description,
                            ip,
                            null,
                            ipv6Index ?? (int)ip.ScopeId,
                            kind,
                            nic.Speed));
                    }
                }
            }
        }
        catch
        {
            // Ignore transient NIC enumeration errors
        }

        return results
            .OrderBy(a => (int)a.LinkKind)
            .ThenBy(a => a.Address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ThenByDescending(a => a.SpeedBitsPerSec)
            .ToList();
    }

    public static bool IsIPv4LinkLocal(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] bytes = ip.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    public static LinkKind ClassifyRemoteAddress(IPAddress remoteIp, IReadOnlyList<LocalInterfaceAddress> localAddresses)
    {
        if (remoteIp.IsIPv4MappedToIPv6)
            remoteIp = remoteIp.MapToIPv4();

        if (remoteIp.AddressFamily == AddressFamily.InterNetwork)
        {
            if (IsIPv4LinkLocal(remoteIp))
                return LinkKind.DirectCable;

            // Match subnet against local interfaces to tell Wi-Fi vs Wired!
            byte[] remoteBytes = remoteIp.GetAddressBytes();
            foreach (var local in localAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                byte[] localBytes = local.Address.GetAddressBytes();
                if (remoteBytes[0] == localBytes[0] && remoteBytes[1] == localBytes[1] && remoteBytes[2] == localBytes[2])
                    return local.LinkKind;
            }
        }
        else if (remoteIp.AddressFamily == AddressFamily.InterNetworkV6 && remoteIp.IsIPv6LinkLocal)
        {
            long scope = remoteIp.ScopeId;
            var match = localAddresses.FirstOrDefault(a => a.IPv6InterfaceIndex == scope);
            if (match is not null)
                return match.LinkKind == LinkKind.WiFi ? LinkKind.WiFi : LinkKind.DirectCable;
            return LinkKind.DirectCable;
        }

        return LinkKind.Other;
    }

    private static LinkKind ResolveLinkKind(bool isWired, bool isWifi, bool looksVirtual, bool isLinkLocal)
    {
        if (looksVirtual) return LinkKind.Other;
        if (isLinkLocal && isWired) return LinkKind.DirectCable;
        if (isWired) return LinkKind.Wired;
        if (isWifi) return LinkKind.WiFi;
        if (isLinkLocal) return LinkKind.DirectCable;
        return LinkKind.Other;
    }

    private static bool IsWiredInterface(NetworkInterfaceType type, string name, string desc)
    {
        if (type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit
            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.GigabitEthernet)
            return true;

        string lowerName = name.ToLowerInvariant();
        if (lowerName.StartsWith("eth") || lowerName.StartsWith("enp") || lowerName.StartsWith("eno") || lowerName.StartsWith("ens") || lowerName.StartsWith("enx"))
            return true;

        return desc.Contains("ethernet", StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("usb", StringComparison.OrdinalIgnoreCase) && desc.Contains("lan", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeVirtualAdapter(string name, string desc)
    {
        string combined = $"{name} {desc}";
        return combined.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("Docker", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("br-", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("virbr", StringComparison.OrdinalIgnoreCase);
    }

    private static IPAddress? ComputeDirectedBroadcast(IPAddress ip, IPAddress? mask)
    {
        if (mask is null || ip.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork)
            return null;

        byte[] ipBytes = ip.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();
        byte[] bcast = new byte[4];
        for (int i = 0; i < 4; i++)
            bcast[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
        return new IPAddress(bcast);
    }
}
