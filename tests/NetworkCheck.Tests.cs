using System;
using System.Net.NetworkInformation;
using HbAi.XboxDns;
static class NetworkTests
{
    static int count;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
    static void Main()
    {
        Check(NetworkCheck.IsConnected(OperationalStatus.Up,NetworkInterfaceType.Wireless80211,true,true),"Connected Wi-Fi recognized");
        Check(NetworkCheck.IsConnected(OperationalStatus.Up,NetworkInterfaceType.Ethernet,true,true),"Ethernet works without Wi-Fi");
        Check(!NetworkCheck.IsConnected(OperationalStatus.Down,NetworkInterfaceType.Wireless80211,true,true),"Disconnected Wi-Fi is offline");
        Check(!NetworkCheck.IsConnected(OperationalStatus.Up,NetworkInterfaceType.Wireless80211,false,true),"Wi-Fi without address is offline");
        Check(!NetworkCheck.IsConnected(OperationalStatus.Up,NetworkInterfaceType.Wireless80211,true,false),"No gateway is offline");
        Check(!NetworkCheck.IsConnected(OperationalStatus.Up,NetworkInterfaceType.Loopback,true,true),"Loopback is not a network connection");
        Check(!NetworkCheck.IsConnected(OperationalStatus.Up,NetworkInterfaceType.Tunnel,true,true),"Tunnel alone is not physical connection");
        Console.WriteLine("All " + count + " checks passed; no network changes.");
    }
}
