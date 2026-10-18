using System;
using System.Net.NetworkInformation;
using HbAi.XboxDns;
static class VpnCheckTests
{
    static int checks;
    static void Check(bool result, string name)
    {
        if (!result) throw new Exception(name);
        checks++; Console.WriteLine("PASS " + name);
    }
    static void Main()
    {
        Check(VpnCheck.IsCandidate("happ-xray", "Happ Tunnel", NetworkInterfaceType.Ethernet, OperationalStatus.Up, true), "Active Happ Xray recognized");
        Check(!VpnCheck.IsCandidate("happ-xray", "Happ Tunnel", NetworkInterfaceType.Ethernet, OperationalStatus.Down, true), "Disconnected Happ ignored");
        Check(VpnCheck.IsCandidate("wg0", "WireGuard Tunnel", NetworkInterfaceType.Ethernet, OperationalStatus.Up, true), "Active WireGuard adapter recognized");
        Check(VpnCheck.IsCandidate("ProtonVPN", "Virtual adapter", NetworkInterfaceType.Ethernet, OperationalStatus.Up, true), "VPN suffix recognized");
        Check(VpnCheck.IsCandidate("Corporate", "PPP", NetworkInterfaceType.Ppp, OperationalStatus.Up, true), "PPP connection treated as possible VPN, not proof");
        Check(VpnCheck.IsCandidate("Tunnel", "Virtual adapter", NetworkInterfaceType.Tunnel, OperationalStatus.Up, true), "Tunnel recognized without product name");
        Check(!VpnCheck.IsCandidate("WireGuard", "WireGuard", NetworkInterfaceType.Ethernet, OperationalStatus.Down, true), "Disconnected VPN ignored");
        Check(!VpnCheck.IsCandidate("VPN", "Wintun", NetworkInterfaceType.Ethernet, OperationalStatus.Up, false), "Unconfigured virtual adapter ignored");
        Check(!VpnCheck.IsCandidate("Ethernet", "Intel Ethernet", NetworkInterfaceType.Ethernet, OperationalStatus.Up, true), "Physical Ethernet not called VPN");
        Check(!VpnCheck.IsCandidate("Wi-Fi", "Intel Wireless", NetworkInterfaceType.Wireless80211, OperationalStatus.Up, true), "Wi-Fi not called VPN");
        Check(!VpnCheck.IsCandidate("vEthernet", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, OperationalStatus.Up, true), "Ordinary virtual adapter not automatically called VPN");
        Check(!VpnCheck.IsCandidate("VPN", "WireGuard", NetworkInterfaceType.Loopback, OperationalStatus.Up, true), "Loopback ignored");
        Check(new VpnStatus { Failed = true }.NeedsAttention, "Read failure produces warning");
        Check(new VpnStatus { Connections = new[] { "Test VPN" } }.NeedsAttention, "VPN candidate produces warning");
        Check(!new VpnStatus().NeedsAttention && new VpnStatus().Label == "VPN: признаков нет", "No candidates does not claim VPN is definitely off");
        var live = VpnCheck.Scan();
        Check(live != null && live.Connections != null, "Read-only live scan returns a result");
        Console.WriteLine("All " + checks + " VPN checks passed; no network settings changed.");
    }
}
