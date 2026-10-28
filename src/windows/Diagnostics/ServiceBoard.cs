using System;
using System.Linq;

namespace HbAi.XboxDns {
    enum BoardState { Untested, Checking, Response, Login, Browser, Failed, Unknown, Unsupported, NoTransport, Disabled, Cancelled }
    sealed class BoardCell {
        internal BoardState State; internal string Detail="";internal DateTime CheckedUtc;
        internal string Caption {get{switch(State){
            case BoardState.Checking:return "Проверяем…";case BoardState.Response:return "Ответ получен";
            case BoardState.Login:return "Нужен вход";case BoardState.Browser:return "Проверка браузера";
            case BoardState.Failed:return "Есть проблема";case BoardState.Unknown:return "Не подтверждено";
            case BoardState.Unsupported:return "Не заявлено";case BoardState.Disabled:return "Отключён";
            case BoardState.NoTransport:return "Протокол недоступен";
            case BoardState.Cancelled:return "Не завершено";default:return "Не проверено";}}}
        internal static BoardCell From(ServiceAvailabilityResult result) {
            var replies=result.Endpoints.Select(e=>e.Reply).ToArray();
            var state=result.Endpoints.Any(e=>e.Error!=null||e.Reply==ServiceReply.Failed||e.Reply==ServiceReply.Restricted||e.Reply==ServiceReply.ServerError)?BoardState.Failed:
                replies.Any(r=>r==ServiceReply.Unknown||r==ServiceReply.RateLimited)?BoardState.Unknown:
                replies.Contains(ServiceReply.BrowserCheck)?BoardState.Browser:replies.Contains(ServiceReply.Login)?BoardState.Login:BoardState.Response;
            return new BoardCell{State=state,Detail=result.Report,CheckedUtc=DateTime.UtcNow};
        }
    }
    static class ServiceBoard {
        internal static readonly ServiceTarget[] Targets=ServiceAvailability.Targets.Concat(new[]{
            new ServiceTarget{Name="Spotify",Endpoints=new[]{new ServiceEndpoint{Name="Сайт",Host="open.spotify.com",Path="/"}}},
            new ServiceTarget{Name="Antigravity",Endpoints=new[]{new ServiceEndpoint{Name="Основной API",Host="cloudcode-pa.googleapis.com",Path="/",Api=true},new ServiceEndpoint{Name="Daily API",Host="daily-cloudcode-pa.googleapis.com",Path="/",Api=true}}},
            new ServiceTarget{Name="Xbox",Endpoints=new[]{new ServiceEndpoint{Name="Сайт",Host="www.xbox.com",Path="/"}}}
        }).ToArray();
        internal static BoardCell Initial(int provider,int service,DnsTransport protocol) {
            if(!Dns.IsAvailable(provider))return new BoardCell{State=BoardState.Disabled,Detail="Провайдер отключён в каталоге."};
            if(!RuntimeProviders.All[provider].services.Contains(Targets[service].Name))return new BoardCell{State=BoardState.Unsupported,Detail="Провайдер не заявлен для этого сервиса в каталоге. Это не результат сетевой проверки."};
            if(protocol==DnsTransport.Plain&&Dns.EncryptedOnly(provider)||protocol==DnsTransport.Https&&EncryptedDns.Templates[provider]==null||protocol==DnsTransport.Tls&&!DotDns.Available(provider))
                return new BoardCell{State=BoardState.NoTransport,Detail="Этот протокол недоступен у провайдера. Выберите DoH, DoT или обычный DNS выше."};
            return new BoardCell();
        }
    }
}
