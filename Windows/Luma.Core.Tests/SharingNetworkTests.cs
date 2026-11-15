using System.Net;
using System.Net.NetworkInformation;
using Luma.Core.Sharing;

namespace Luma.Core.Tests;
public static class SharingNetworkTests
{
    public static void Run()
    {
        SharingInterfaceAddress Item(string address, NetworkInterfaceType type, bool gateway, bool virtualAdapter = false) => new(IPAddress.Parse(address),type,gateway,virtualAdapter);
        var original = new[] {
            Item("172.20.0.1",NetworkInterfaceType.Ethernet,false,true),
            Item("10.7.0.2",NetworkInterfaceType.Tunnel,true,true),
            Item("192.168.2.8",NetworkInterfaceType.Ethernet,false),
            Item("192.168.1.8",NetworkInterfaceType.Wireless80211,true),
            Item("192.168.1.8",NetworkInterfaceType.Ethernet,false,true),
            Item("fd00::8",NetworkInterfaceType.Wireless80211,true),
            Item("8.8.8.8",NetworkInterfaceType.Ethernet,true),
            Item("127.0.0.1",NetworkInterfaceType.Loopback,false)
        };
        var ranked=SharingNetworkInterfaces.Rank(original);
        Check(ranked[0].Equals(IPAddress.Parse("192.168.1.8")),"Usable Wi-Fi/default gateway ranks before VPN, Hyper-V and no-gateway Ethernet");
        Check(ranked.Count==5 && ranked.Contains(IPAddress.Parse("172.20.0.1")) && ranked.Contains(IPAddress.Parse("10.7.0.2")) && ranked.Contains(IPAddress.Parse("192.168.2.8")),"Manual URLs retain all distinct private virtual/no-gateway interfaces");
        Check(SharingNetworkInterfaces.IsVirtual(NetworkInterfaceType.Ethernet,"vEthernet (Default Switch)","Hyper-V Virtual Ethernet Adapter") && SharingNetworkInterfaces.IsVirtual(NetworkInterfaceType.Ethernet,"Ethernet 2","WireGuard Tunnel") && !SharingNetworkInterfaces.IsVirtual(NetworkInterfaceType.Wireless80211,"Wi-Fi","Intel Wireless Adapter"),"Physical and common VPN/virtual adapters classified explicitly");
        foreach(string address in new[]{"0.0.0.0","::","127.0.0.1","224.0.0.1","ff02::1"})Check(!SharingNetworkInterfaces.UsableGateway(IPAddress.Parse(address)),"Unspecified/loopback/multicast is not a usable gateway");
        Check(SharingNetworkInterfaces.UsableGateway(IPAddress.Parse("192.168.1.1")) && SharingNetworkInterfaces.UsableGateway(IPAddress.Parse("fe80::1%3")),"Real IPv4 and scoped IPv6 gateway accepted");
        Console.WriteLine("PASS: discovery prefers physical Wi-Fi/Ethernet with a usable gateway, preserving all private manual URLs and rejecting unusable gateway metadata");
    }
    static void Check(bool value,string message){if(!value)throw new InvalidOperationException("Sharing adapter: "+message);}
}
