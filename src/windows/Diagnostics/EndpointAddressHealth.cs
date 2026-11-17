using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace HbAi.XboxDns {
    // Service connection addresses, separate from DoH/DoT resolver health.
    // Only fresh answers from the currently tested provider can be selected.
    sealed class EndpointAddressHealth {
        sealed class State {internal long TransportUntil,RegionUntil,Success,Seen;}
        readonly object gate=new object();readonly Func<long> now;
        readonly Dictionary<string,State> states=new Dictionary<string,State>(StringComparer.Ordinal);
        string context;long generation;
        internal EndpointAddressHealth(){var watch=Stopwatch.StartNew();now=()=>watch.ElapsedMilliseconds;}
        internal EndpointAddressHealth(Func<long> clock){now=clock;}
        static string Key(string host,string address){return host.ToLowerInvariant()+"|"+address;}
        internal void Context(string value){lock(gate){if(context==value)return;context=value;generation++;foreach(var state in states.Values){state.TransportUntil=0;state.Success=0;}}}
        State At(string host,string address){
            string key=Key(host,address);State state;if(states.TryGetValue(key,out state))return state;
            if(states.Count>=512){var oldest=states.OrderBy(p=>p.Value.Seen).First().Key;states.Remove(oldest);}
            return states[key]=new State{Seen=now()};
        }
        internal string[] Ready(string host,IEnumerable<string> answers,out long version){lock(gate){
            version=generation;long time=now();foreach(var key in states.Where(p=>time-p.Value.Seen>900000&&p.Value.RegionUntil<=time).Select(p=>p.Key).ToArray())states.Remove(key);
            return answers.Where(AutoDnsProbe.PublicAddress).Distinct().Take(16).Select((ip,index)=>new{Ip=ip,Index=index,State=At(host,ip)})
                .Where(item=>{item.State.Seen=time;return item.State.TransportUntil<=time&&item.State.RegionUntil<=time;})
                .OrderByDescending(item=>time-item.State.Success<120000?item.State.Success:0).ThenBy(item=>item.Index).Take(8).Select(item=>item.Ip).ToArray();
        }}
        internal void Reachable(string host,string address,long version){lock(gate){if(version!=generation)return;var state=At(host,address);state.TransportUntil=0;state.Success=now()+1;state.Seen=now();}}
        internal void TransportFailed(string host,string address,long version){lock(gate){if(version!=generation)return;var state=At(host,address);state.TransportUntil=now()+30000;state.Success=0;state.Seen=now();}}
        internal void RegionFailed(string host,string address,long version){lock(gate){if(version!=generation)return;var state=At(host,address);state.RegionUntil=now()+300000;state.Success=0;state.Seen=now();}}
        internal void ClientFailure(string host,string address,bool region){if(string.IsNullOrEmpty(host)||!AutoDnsProbe.PublicAddress(address))return;lock(gate){if(region)RegionFailed(host,address,generation);else TransportFailed(host,address,generation);}}
        internal void ClientAnswer(string host,string address){if(string.IsNullOrEmpty(host)||!AutoDnsProbe.PublicAddress(address))return;lock(gate){var state=At(host,address);state.RegionUntil=state.TransportUntil=0;state.Success=now()+1;state.Seen=now();}}
        internal bool OnlyRegionBlocked(string host,IEnumerable<string> answers){lock(gate){var publicAnswers=answers.Where(AutoDnsProbe.PublicAddress).Distinct().Take(16).ToArray();return publicAnswers.Length>0&&publicAnswers.All(ip=>{State state;return states.TryGetValue(Key(host,ip),out state)&&state.RegionUntil>now();});}}
    }
    partial class MainForm {
        readonly EndpointAddressHealth endpointHealth=new EndpointAddressHealth();
        void RefreshEndpointHealthContext(){string stamp;try{stamp=new MangoResolverCheckIo().Stamp();}catch(Exception){stamp="unknown";}endpointHealth.Context(stamp);}
    }
}
