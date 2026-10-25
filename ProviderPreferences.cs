using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace HbAi.XboxDns
{
    static class ProviderPreferences
    {
        internal const string Value="HiddenProviders";
        internal static int[] Normalize(IEnumerable<int> hidden)
        {return (hidden??new int[0]).Where(Dns.IsAvailable).Distinct().OrderBy(p=>p).ToArray();}
        internal static int[] Read(RegistryKey key)
        {
            var values=key==null?null:key.GetValue(Value) as string[];
            return Normalize((values??new string[0]).Select(value=>{int id;return int.TryParse(value,out id)?id:-1;}));
        }
        internal static void Save(RegistryKey key,IEnumerable<int> hidden)
        {
            var ids=Normalize(hidden);
            if(Dns.AvailableProviders.All(ids.Contains))throw new InvalidOperationException("Оставьте видимым хотя бы один DNS-провайдер.");
            key.SetValue(Value,ids.Select(id=>id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(),RegistryValueKind.MultiString);
        }
    }
    sealed class ProviderLatency
    {
        internal int Provider,Milliseconds;internal string Error,Path;internal DnsTransport Protocol;internal DateTime MeasuredUtc;
        internal string Label {get{return Error==null?Milliseconds+" мс":"Нет ответа";}}
        internal static ProviderLatency Measure(int provider,DnsTransport protocol,CancellationToken cancel,IAutoDnsProbe probe=null)
        {
            if(protocol==DnsTransport.Plain&&Dns.EncryptedOnly(provider))protocol=DnsTransport.Https;
            if(protocol==DnsTransport.Tls&&!DotDns.Available(provider))protocol=DnsTransport.Https;
            var result=new ProviderLatency{Provider=provider,Protocol=protocol};var clock=Stopwatch.StartNew();
            try{cancel.ThrowIfCancellationRequested();var addresses=(probe??new AutoDnsProbe(protocol==DnsTransport.Tls)).Resolve(provider,protocol!=DnsTransport.Plain,"example.com",cancel);cancel.ThrowIfCancellationRequested();if(addresses==null||addresses.Length==0)throw new InvalidOperationException("DNS не вернул адрес.");}
            catch(OperationCanceledException){throw;}
            catch(Exception error){result.Error=error.GetType().Name;}
            result.Milliseconds=(int)clock.ElapsedMilliseconds;result.MeasuredUtc=DateTime.UtcNow;return result;
        }
        internal static ProviderLatency MeasureActive(int provider,DnsTransport protocol,bool local,bool google,CancellationToken cancel,IAutoDnsProbe probe=null,Func<string,string,DnsProbeResult> loopback=null)
        {
            cancel.ThrowIfCancellationRequested();
            if(!local&&!google){var direct=Measure(provider,protocol,cancel,probe);direct.Path="Прямой DNS-запрос к выбранному провайдеру";return direct;}
            string address=local?"127.0.0.1":DnsTunnelNative.V4;
            var reply=(loopback??DnsProbe.Check)(address,"example.com");cancel.ThrowIfCancellationRequested();
            return new ProviderLatency{Provider=provider,Protocol=protocol,Milliseconds=reply.Milliseconds,Error=reply.Error,MeasuredUtc=DateTime.UtcNow,Path="Через DNS приложения: "+address+" → "+Dns.Name[provider]};
        }
    }
}
