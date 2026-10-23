using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HbAi.XboxDns
{
    sealed class DnsFollowSnapshot
    {
        internal string NetworkKey, RestoreKey;
        internal bool Configured, Managed;
        internal static string NetworkIdentity()
        {
            return string.Join("|", NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.Id + ":" + string.Join(",", n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).OrderBy(a => a)) + ":" +
                    string.Join(",", n.GetIPProperties().GatewayAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).OrderBy(a => a)))
                .OrderBy(n => n));
        }
        internal static DnsFollowSnapshot Capture(IDnsSystem system, IDnsBackupStore store, int provider, bool encrypted, string identity)
        {
            var active = system.Adapters(true).Where(a => a.V4).ToArray();
            var available = system.Adapters(false);
            var records = store.Load(); RegistryDnsBackupStore.Validate(records);
            var result = new DnsFollowSnapshot { NetworkKey = identity,
                Managed = active.Any(a => records.Any(r => a.Id == new Guid(r.Guid))),
                RestoreKey = string.Join(",", records.Where(r => {
                    if (!available.Any(a => a.Id == new Guid(r.Guid))) return false;
                    try { return DnsSettingsEngine.SafeToRestore(system, r); } catch (Exception) { return false; }
                }).Select(r => r.Guid).OrderBy(id => id)) };
            if (!Dns.IsAvailable(provider) || active.Length == 0) return result;
            result.Configured = active.All(a => {
                var current = system.Read(a.Id, "IPv4");
                if (encrypted) {
                    var bindings = system as IDnsBindingSystem;
                    return current.Servers.SequenceEqual(EncryptedDns.ServersFor(provider)) && EncryptedDns.ExplicitlyConfigured(current, provider) &&
                        bindings != null && !bindings.ReadIpv6Binding(a.Id);
                }
                return current.Servers.SequenceEqual(Dns.V4[provider]) && (current.Doh == null || current.Doh.Length == 0);
            });
            return result;
        }
    }
    enum DnsFollowAction { None, Apply, Restore }
    sealed class DnsFollowPlan
    {
        internal DnsFollowAction Action;
        internal int Provider;
        internal bool Encrypted, KeepAntigravity;
    }
    // Session intent is separate from observed DNS: a new DHCP adapter must not erase
    // the provider selected by the user. Only one attempt per stable network is made.
    sealed class NetworkDnsFollow
    {
        internal bool Enabled { get; private set; }
        internal int Provider { get; private set; }
        internal bool Encrypted { get; private set; }
        bool keepAntigravity, restoreWanted;
        string goodNetwork, seenNetwork;
        readonly HashSet<string> attemptedNetworks = new HashSet<string>();
        readonly HashSet<string> attemptedRestores = new HashSet<string>();
        internal void Enable(int provider, bool encrypted, bool keep)
        {
            Enabled = true; Provider = provider; Encrypted = encrypted; keepAntigravity = keep;
            restoreWanted = false; goodNetwork = seenNetwork = null;
            attemptedNetworks.Clear(); attemptedRestores.Clear();
        }
        internal void Stop()
        { Enabled = false; restoreWanted = false; goodNetwork = seenNetwork = null; attemptedNetworks.Clear(); attemptedRestores.Clear(); }
        internal void DeferRestore()
        { Stop(); restoreWanted = true; }
        internal DnsFollowPlan Observe(DnsFollowSnapshot snapshot, bool connected, bool vpn)
        {
            var none = new DnsFollowPlan();
            if (snapshot == null || vpn) { seenNetwork = null; return none; }
            if (!Enabled) {
                if (!restoreWanted || string.IsNullOrEmpty(snapshot.RestoreKey) || !attemptedRestores.Add(snapshot.RestoreKey)) return none;
                return new DnsFollowPlan { Action = DnsFollowAction.Restore };
            }
            if (!Dns.IsAvailable(Provider) || !connected) { seenNetwork = null; return none; }
            if (snapshot.Configured) { goodNetwork = seenNetwork = snapshot.NetworkKey; return none; }
            // Respect an external DNS edit on the same network. A topology/address
            // change must settle for two observations before reapplying our intent.
            if (string.IsNullOrEmpty(snapshot.NetworkKey) || snapshot.NetworkKey == goodNetwork || attemptedNetworks.Contains(snapshot.NetworkKey)) return none;
            if (snapshot.NetworkKey != seenNetwork) { seenNetwork = snapshot.NetworkKey; return none; }
            attemptedNetworks.Add(snapshot.NetworkKey);
            return new DnsFollowPlan { Action = DnsFollowAction.Apply, Provider = Provider, Encrypted = Encrypted, KeepAntigravity = keepAntigravity };
        }
    }
}
