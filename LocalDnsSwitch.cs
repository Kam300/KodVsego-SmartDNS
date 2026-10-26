using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace HbAi.XboxDns
{
    interface ILocalDnsSwitchSession
    {
        bool Switching {get;}
        int CurrentProvider {get;}
        bool CurrentTls {get;}
        void SwitchProvider(int provider,bool tls);
    }
    interface ILocalAntigravitySwitchSession
    {
        int CurrentAntigravity {get;}
        void SwitchAntigravity(int provider);
    }
    sealed class LocalDnsSwitchCommand
    {
        internal int Provider;internal bool Tls,Antigravity;
        internal string Wire {get{return (Antigravity?"AGSWITCH ":"SWITCH ")+Provider+" "+(Tls?"tls":"https");}}
        internal static LocalDnsSwitchCommand Parse(string line)
        {
            var parts=(line??"").Split(' ');int provider;
            if(parts.Length!=3||(parts[0]!="SWITCH"&&parts[0]!="AGSWITCH")||!int.TryParse(parts[1],out provider)||
                (parts[2]!="https"&&parts[2]!="tls")||!Dns.IsAvailable(provider)||
                (parts[2]=="tls"?!DotDns.Available(provider):EncryptedDns.Templates[provider]==null))
                throw new IOException("Некорректная команда смены DNS.");
            bool special=parts[0]=="AGSWITCH";
            if(special&&(parts[2]!="https"||!LocalDnsRouting.Supports(provider)))throw new IOException("Провайдер Antigravity недоступен.");
            return new LocalDnsSwitchCommand{Provider=provider,Tls=parts[2]=="tls",Antigravity=special};
        }
    }
    // Preparing a replacement never changes the live route. Only the worker's
    // command loop commits a completed probe, so stop/EOF/timeout cannot apply it later.
    sealed class LocalDnsSwitchController
    {
        Task<Func<byte[],byte[]>> preparing,retired;LocalDnsSwitchCommand command;long started;
        readonly Func<long> now;
        internal LocalDnsSwitchController(Func<long> now=null){var clock=Stopwatch.StartNew();this.now=now??(()=>clock.ElapsedMilliseconds);}
        internal bool Pending {get{return preparing!=null;}}
        internal bool Begin(LocalDnsSwitchCommand request,Func<LocalDnsSwitchCommand,Func<byte[],byte[]>> prepare)
        {
            if(Pending||retired!=null&&!retired.IsCompleted)return false;
            command=request;started=now();preparing=Task.Factory.StartNew(()=>prepare(request));
            preparing.ContinueWith(t=>{var observed=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
            return true;
        }
        internal void Poll(LocalDnsRouting routes,Action flush,Action<string> reply)
        {
            if(!Pending)return;
            if(!preparing.IsCompleted&&now()-started<12000)return;
            var task=preparing;var request=command;preparing=null;command=null;
            if(!task.IsCompleted){retired=task;reply("SWITCH_FAILED Новый DNS не ответил за 12 с. Прежний DNS продолжает работать.");return;}
            try{var next=task.GetAwaiter().GetResult();if(request.Antigravity)routes.ReplaceAntigravity(next,flush);else routes.ReplaceCommon(next,flush);reply((request.Antigravity?"AGSWITCHED ":"SWITCHED ")+request.Provider+" "+(request.Tls?"tls":"https"));}
            catch(Exception error){reply("SWITCH_FAILED "+TunnelDiagnostics.Error(error)+" Прежний DNS сохранён.");}
        }
        internal void Cancel(){preparing=null;command=null;}
    }
}
