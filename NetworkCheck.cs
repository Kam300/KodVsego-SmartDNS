using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace HbAi.XboxDns
{
    static class NetworkCheck
    {
        internal const string OfflineMessage = "Нет сетевого подключения. Подключитесь к Wi-Fi или Ethernet и повторите попытку. Настройки DNS не изменены.";
        internal static bool IsConnected(OperationalStatus status, NetworkInterfaceType type, bool address, bool gateway)
        {
            return status == OperationalStatus.Up && type != NetworkInterfaceType.Loopback && type != NetworkInterfaceType.Tunnel && address && gateway;
        }
        internal static bool HasConnection()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces().Any(adapter => {
                    var properties = adapter.GetIPProperties();
                    return IsConnected(adapter.OperationalStatus, adapter.NetworkInterfaceType,
                        properties.UnicastAddresses.Any(a => !IPAddress.IsLoopback(a.Address) && !a.Address.IsIPv6LinkLocal && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)),
                        properties.GatewayAddresses.Any(a => !a.Address.Equals(IPAddress.Any) && !a.Address.Equals(IPAddress.IPv6Any)));
                });
            }
            catch (NetworkInformationException) { return false; }
        }
    }
}
