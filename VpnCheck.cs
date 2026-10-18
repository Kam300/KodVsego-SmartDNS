using System;
using System.Linq;
using System.Net.NetworkInformation;

namespace HbAi.XboxDns
{
    sealed class VpnStatus
    {
        internal bool Failed;
        internal string[] Connections = new string[0];
        internal bool NeedsAttention { get { return Failed || Connections.Length > 0; } }
        internal string Label { get { return Failed ? "VPN: проверить не удалось" :
            Connections.Length > 0 ? "VPN: возможен активный туннель" : "VPN: признаков нет"; } }
        internal string Details
        {
            get
            {
                string result = Failed ? "Не удалось прочитать состояние сетевых адаптеров." :
                    Connections.Length > 0 ? "Найдены признаки VPN или туннельного подключения:\n" + string.Join("\n", Connections) :
                    "На активных сетевых адаптерах признаки VPN не найдены.";
                return result + "\n\nVPN может использовать собственный DNS и мешать доступу через выбранный сервис. " +
                    "Для проверки доступа отключите VPN и повторите попытку.\n\n" +
                    "Проверка приблизительная: она не определяет все VPN, расширения браузера, прокси и VPN на роутере. " +
                    "Виртуальный адаптер или PPP-подключение также может не быть VPN. Программа сама ничего не отключает.";
            }
        }
    }
    static class VpnCheck
    {
        static readonly string[] Markers = {
            "wireguard", "wintun", "tap-windows", "tap windows", "vpn",
            "tailscale", "zerotier", "hamachi", "nordlynx", "fortinet", "forticlient",
            "anyconnect", "cisco secure client", "globalprotect", "pulse secure", "sonicwall",
            "amnezia", "sing-box", "hiddify", "nekoray", "outline tunnel", "clash", "v2ray",
            "happ", "xray", "tun2socks"
        };
        internal static bool IsCandidate(string name, string description, NetworkInterfaceType type,
            OperationalStatus status, bool hasAddress)
        {
            if (status != OperationalStatus.Up || !hasAddress || type == NetworkInterfaceType.Loopback) return false;
            if (type == NetworkInterfaceType.Ppp || type == NetworkInterfaceType.Tunnel) return true;
            string text = " " + (name ?? "") + " " + (description ?? "") + " ";
            return Markers.Any(marker => text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        internal static VpnStatus Scan()
        {
            try
            {
                var matches = NetworkInterface.GetAllNetworkInterfaces().Where(adapter =>
                    IsCandidate(adapter.Name, adapter.Description, adapter.NetworkInterfaceType,
                        adapter.OperationalStatus, adapter.GetIPProperties().UnicastAddresses.Count > 0))
                    .Select(adapter => adapter.Name).Distinct().ToArray();
                return new VpnStatus { Connections = matches };
            }
            catch (Exception) { return new VpnStatus { Failed = true }; }
        }
    }
}
