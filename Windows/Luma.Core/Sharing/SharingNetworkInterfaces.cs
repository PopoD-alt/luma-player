using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Luma.Core.Sharing;

public sealed record SharingInterfaceAddress(IPAddress Address, NetworkInterfaceType InterfaceType, bool HasUsableGateway, bool IsVirtual);

/// <summary>Ranks discovery candidates while retaining every local address for manual connections.</summary>
public static class SharingNetworkInterfaces
{
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var candidates = new List<SharingInterfaceAddress>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                    var properties = adapter.GetIPProperties();
                    bool virtualInterface = IsVirtual(adapter.NetworkInterfaceType, adapter.Name, adapter.Description);
                    foreach (var item in properties.UnicastAddresses)
                    {
                        var address = item.Address;
                        bool gateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == address.AddressFamily && UsableGateway(g.Address));
                        candidates.Add(new(address, adapter.NetworkInterfaceType, gateway, virtualInterface));
                    }
                }
                catch (Exception error) when (error is NetworkInformationException or PlatformNotSupportedException) { }
            }
        }
        catch (Exception error) when (error is NetworkInformationException or PlatformNotSupportedException) { }
        var ranked = Rank(candidates);
        return ranked.Count == 0 ? [IPAddress.Loopback] : ranked;
    }
    public static IReadOnlyList<IPAddress> Rank(IEnumerable<SharingInterfaceAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        return addresses.Where(a => !IPAddress.IsLoopback(a.Address) && PlaylistSharingServer.IsPrivateAddress(a.Address))
            .OrderBy(Priority).ThenBy(a => a.Address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(a => a.Address.ToString(), StringComparer.Ordinal).Select(a => a.Address).Distinct().ToArray();
    }
    static int Priority(SharingInterfaceAddress item)
    {
        bool lan = item.InterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx;
        if (!item.IsVirtual && lan && item.HasUsableGateway) return 0;
        if (!item.IsVirtual && lan) return 1;
        if (!item.IsVirtual && item.HasUsableGateway) return 2;
        if (item.HasUsableGateway) return 3;
        return item.IsVirtual ? 5 : 4;
    }
    public static bool IsVirtual(NetworkInterfaceType type, string name, string description)
    {
        if (type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp or NetworkInterfaceType.Loopback) return true;
        string text = name + " " + description;
        return new[] { "virtual", "vethernet", "hyper-v", "vmware", "virtualbox", "wireguard", "tailscale", "zerotier", "openvpn", "vpn", "tap-", "tap ", "tun ", "docker", "wsl", "hamachi" }
            .Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
    public static bool UsableGateway(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        byte[] bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork ? bytes[0] is > 0 and < 224 : address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6Multicast;
    }
}
