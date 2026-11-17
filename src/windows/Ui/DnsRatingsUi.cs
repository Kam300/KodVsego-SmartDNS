using System;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HbAi.XboxDns
{
    enum DnsVote { None, Works, Fails }

    static class DnsRatings
    {
        internal static string Preference(DnsTransport transport, string endpoint)
        {
            // Windows registry value names ignore case, whereas DoH URL paths can be case-sensitive.
            using (var hash = SHA256.Create())
                return "DnsVote-" + BitConverter.ToString(hash.ComputeHash(
                    Encoding.UTF8.GetBytes(transport + "|" + endpoint))).Replace("-", "");
        }

        internal static DnsVote Read(RegistryKey key, DnsTransport transport, string endpoint)
        {
            object value = key == null ? null : key.GetValue(Preference(transport, endpoint));
            if (Equals(value, (int)DnsVote.Works)) return DnsVote.Works;
            if (Equals(value, (int)DnsVote.Fails)) return DnsVote.Fails;
            return DnsVote.None;
        }

        internal static void Save(RegistryKey key, DnsTransport transport, string endpoint, DnsVote vote)
        {
            string name = Preference(transport, endpoint);
            if (vote == DnsVote.None) key.DeleteValue(name, false);
            else key.SetValue(name, (int)vote, RegistryValueKind.DWord);
        }

        internal static string Caption(DnsVote vote)
        {
            switch (vote)
            {
                case DnsVote.Works: return "Работает";
                case DnsVote.Fails: return "Не работает";
                default: return "Без оценки";
            }
        }

        internal static string[] Endpoints(int provider, DnsTransport transport)
        {
            if (transport == DnsTransport.Https)
                return Present(EncryptedDns.Templates[provider]);
            if (transport == DnsTransport.Tls)
                return Present(DotDns.Host(provider));
            return Dns.V4[provider].Concat(Dns.V6[provider]).Distinct().ToArray();
        }

        static string[] Present(string endpoint)
        {
            return string.IsNullOrEmpty(endpoint) ? new string[0] : new[] { endpoint };
        }
    }

    partial class MainForm
    {
        Rectangle DnsRatingBounds
        {
            get { return new Rectangle(DnsTestButton.Left - 70, DnsTestButton.Top, 62, DnsTestButton.Height); }
        }

        void ShowDnsRatings()
        {
            var transport = Transport;
            var endpoints = DnsRatings.Endpoints(dnsSet, transport);
            if (endpoints.Length == 0) return;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(CustomDns.RegistryPath))
                {
                    if (endpoints.Length == 1)
                    {
                        EditDnsVote(key, transport, endpoints[0]);
                        return;
                    }

                    string selected = null;
                    using (var dialog = CreateDialog("Выберите адрес. Оценки сохраняются на этом ПК.",
                        "Личная оценка DNS", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1))
                    {
                        dialog.SetPrimaryText("Закрыть");
                        dialog.AddBodyLinks(endpoints.Select(address =>
                            new KeyValuePair<string, Action>(address + " · " + DnsRatings.Caption(DnsRatings.Read(key, transport, address)),
                                () => { selected = address; dialog.Close(); })).ToArray());
                        dialog.ShowDialog(this);
                    }
                    if (selected != null) EditDnsVote(key, transport, selected);
                }
            }
            catch (Exception error)
            {
                DiagnosticReport.Record("Оценка DNS", error.GetType().Name);
                ShowMessage("Не удалось открыть или сохранить оценки DNS.", "Оценка DNS",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void EditDnsVote(RegistryKey key, DnsTransport transport, string endpoint)
        {
            var current = DnsRatings.Read(key, transport, endpoint);
            using (var dialog = CreateDialog(endpoint + "\n\nВаша оценка · " + DnsRatings.Caption(current) +
                "\nОценка сохраняется на этом ПК.",
                "Личная оценка DNS", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1))
            {
                dialog.SetPrimaryText("Закрыть");
                var options = current == DnsVote.None ? new[] { DnsVote.Works, DnsVote.Fails } :
                    new[] { DnsVote.Works, DnsVote.Fails, DnsVote.None };
                DnsVote? choice = null;
                dialog.AddBodyLinks(options.Select(vote => new KeyValuePair<string, Action>(
                    vote == DnsVote.None ? "Снять оценку" : DnsRatings.Caption(vote),
                    () => { choice = vote; dialog.Close(); })).ToArray());
                dialog.ShowDialog(this);
                if (choice.HasValue) DnsRatings.Save(key, transport, endpoint, choice.Value);
            }
        }
    }
}
