package ru.kodvsego.smartdns;

import android.content.Context;
import android.net.Network;
import okhttp3.*;
import org.json.*;
import java.io.*;
import java.net.*;
import java.util.*;
import java.util.concurrent.TimeUnit;

/** Read-only probes: explicit provider DNS followed by verified HTTPS, no credentials or redirects. */
final class ServiceHealth {
    enum State { UNTESTED, CHECKING, RESPONSE, LOGIN, BROWSER, FAILED, UNKNOWN, UNSUPPORTED, NO_PROTOCOL, DISABLED, CANCELLED }
    static final class Endpoint {
        final String host,path;final boolean api;
        Endpoint(String h,String p,boolean a){host=h;path=p;api=a;}
    }
    static final class Target {
        final String name;final Endpoint[] endpoints;
        Target(String name,String web,String api,String path){this.name=name;endpoints=api==null?new Endpoint[]{new Endpoint(web,"/",false)}:new Endpoint[]{new Endpoint(web,"/",false),new Endpoint(api,path,true)};}
    }
    static final Target[] TARGETS={new Target("Gemini","gemini.google.com","generativelanguage.googleapis.com","/v1beta/models"),new Target("ChatGPT","chatgpt.com","api.openai.com","/v1/models"),new Target("Claude","claude.ai","api.anthropic.com","/v1/models"),new Target("ElevenLabs","elevenlabs.io","api.elevenlabs.io","/v1/user"),new Target("Spotify","open.spotify.com",null,null)};
    static final class Reply {
        final int status;final String type,body,location,challenge;
        Reply(int status,String type,String body,String location,String challenge){this.status=status;this.type=type==null?"":type;this.body=body==null?"":body;this.location=location==null?"":location;this.challenge=challenge==null?"":challenge;}
    }
    static final class Result {
        final State state;final String detail;final long checked;
        Result(State state,String detail){this(state,detail,0);}
        Result(State state,String detail,long checked){this.state=state;this.detail=detail;this.checked=checked;}
        String caption(){switch(state){case CHECKING:return "Проверяем…";case RESPONSE:return "Ответ получен";case LOGIN:return "Нужен вход";case BROWSER:return "Проверка браузера";case FAILED:return "Есть проблема";case UNKNOWN:return "Не подтверждено";case UNSUPPORTED:return "Не заявлено";case NO_PROTOCOL:return "Протокол недоступен";case DISABLED:return "Отключён";case CANCELLED:return "Не завершено";default:return "Не проверено";}}
    }
    interface Probe { Reply request(Endpoint endpoint) throws Exception; }
    static Result initial(int provider,int target,int protocol){
        if(!Provider.isAvailable(provider))return new Result(State.DISABLED,"Провайдер отключён в каталоге.");
        Provider p=Provider.at(provider);
        if(!Arrays.asList(p.services).contains(TARGETS[target].name))return new Result(State.UNSUPPORTED,"Поддержка этого сервиса не заявлена в каталоге. Это не результат сетевой проверки.");
        if(protocol==DnsConfig.PLAIN&&p.encryptedOnly||protocol==DnsConfig.DOH&&p.doh==null||protocol==DnsConfig.DOT&&(p.dot==null||p.dotServers.length==0))return new Result(State.NO_PROTOCOL,"Этот протокол недоступен. Выберите другой протокол проверки.");
        return new Result(State.UNTESTED,"");
    }
    static Result check(Target target,Probe probe) throws InterruptedException {
        List<State> states=new ArrayList<>();StringBuilder details=new StringBuilder();
        for(Endpoint endpoint:target.endpoints){
            if(Thread.currentThread().isInterrupted())throw new InterruptedException();
            try{Reply reply=probe.request(endpoint);State state=classify(endpoint,reply);states.add(state);details.append(endpoint.api?"API: ":"Сайт: ").append(endpoint.host).append(endpoint.path).append("\nHTTP ").append(reply.status).append(" · ").append(new Result(state,"").caption()).append("\nDNS и сертификат HTTPS проверены.\n\n");}
            catch(InterruptedException cancelled){throw cancelled;}
            catch(Exception error){if(Thread.currentThread().isInterrupted())throw new InterruptedException();states.add(State.FAILED);details.append(endpoint.host).append("\nНет подтверждённого ответа DNS/HTTPS. ").append(AppJournal.scrub(error.getMessage())).append("\n\n");}
        }
        State state=states.contains(State.FAILED)?State.FAILED:states.contains(State.UNKNOWN)?State.UNKNOWN:states.contains(State.BROWSER)?State.BROWSER:states.contains(State.LOGIN)?State.LOGIN:State.RESPONSE;
        details.append("Без входа и API-ключа. Генерация и регион аккаунта не проверялись. Системный DNS не изменялся.");
        return new Result(state,details.toString(),System.currentTimeMillis());
    }
    private static void errors(Object value,StringBuilder output,int depth) {
        if(depth>6||value==null)return;
        if(value instanceof String){output.append(' ').append(value);return;}
        if(value instanceof JSONObject){JSONObject map=(JSONObject)value;for(String key:Arrays.asList("error","detail","details","message","code","status","reason","type"))if(map.has(key))errors(map.opt(key),output,depth+1);}
        if(value instanceof JSONArray){JSONArray array=(JSONArray)value;for(int i=0;i<Math.min(array.length(),64);i++)errors(array.opt(i),output,depth+1);}
    }
    static State classify(Endpoint endpoint,Reply reply){
        if(reply.challenge.equalsIgnoreCase("challenge"))return State.BROWSER;
        String error="";JSONObject json=null;
        if(endpoint.api&&reply.type.toLowerCase(Locale.ROOT).contains("json")){try{json=new JSONObject(reply.body);StringBuilder text=new StringBuilder();errors(json,text,0);error=text.toString().toLowerCase(Locale.ROOT);}catch(JSONException ignored){}}
        for(String marker:Arrays.asList("unsupported_country","unsupported_region","country_not_supported","region_not_supported","location is not supported","location is not allowed","not available in your country","not available in your region","access from your region is not supported"))if(error.contains(marker))return State.FAILED;
        if(reply.status==429)return State.UNKNOWN;if(reply.status>=500)return State.FAILED;
        if(reply.status==401)return State.LOGIN;
        if(endpoint.api&&(reply.status==400||reply.status==403))for(String marker:Arrays.asList("api key","api_key","api-key","unauthenticated","authentication","missing bearer","not authenticated","unregistered callers"))if(error.contains(marker))return State.LOGIN;
        if(reply.status>=300&&reply.status<400){try{URI redirect=new URI("https://"+endpoint.host).resolve(reply.location);if("accounts.google.com".equals(redirect.getHost())||redirect.getPath().toLowerCase(Locale.ROOT).contains("login")||redirect.getPath().toLowerCase(Locale.ROOT).contains("signin"))return State.LOGIN;}catch(Exception ignored){}return State.UNKNOWN;}
        if(reply.status>=200&&reply.status<300){if(endpoint.api&&(json==null||!error.isEmpty()||!(json.has("models")||json.has("data")||json.has("user_id"))))return State.UNKNOWN;return State.RESPONSE;}
        return State.UNKNOWN;
    }
    static List<InetAddress> addresses(byte[] query,byte[] answer) throws IOException {
        DnsWire.validate(query,answer);DnsWire.requireAddress(answer);List<InetAddress> result=new ArrayList<>();int pos=DnsWire.question(answer).end;
        for(int r=0;r<DnsWire.u16(answer,6);r++){
            for(int count=0;count<128;count++){if(pos>=answer.length)throw new IOException("Неполное DNS-имя");int n=answer[pos++]&255;if(n==0)break;if((n&192)==192){if(pos>=answer.length)throw new IOException("Неполный указатель");pos++;break;}if(n>63||pos+n>answer.length)throw new IOException("Некорректное DNS-имя");pos+=n;}
            int type=DnsWire.u16(answer,pos),cls=DnsWire.u16(answer,pos+2),size=DnsWire.u16(answer,pos+8);pos+=10;if(pos+size>answer.length)throw new IOException("Неполный DNS-ответ");
            if(type==1&&cls==1&&size==4){byte[] ip=Arrays.copyOfRange(answer,pos,pos+4);int a=ip[0]&255,b=ip[1]&255;
                if(a>0&&a<224&&a!=10&&a!=127&&!(a==100&&b>=64&&b<128)&&!(a==169&&b==254)&&!(a==172&&b>=16&&b<32)&&!(a==192&&(b==168||b==0))&&!(a==198&&(b==18||b==19))){InetAddress value=InetAddress.getByAddress(ip);if(!result.contains(value))result.add(value);}}
            pos+=size;
        }
        if(result.isEmpty())throw new IOException("DNS не вернул публичного IPv4-адреса");return result.subList(0,Math.min(3,result.size()));
    }
    static final class SystemProbe implements Probe,Closeable {
        private final UpstreamResolver resolver;private final OkHttpClient client;private volatile Call call;private volatile boolean closed;
        SystemProbe(Context context,DnsConfig config) throws IOException {
            Context app=context.getApplicationContext();PhysicalNetwork.requireProbeNetwork(app);Network network=PhysicalNetwork.find(app);resolver=new UpstreamResolver(app,null);
            client=new OkHttpClient.Builder().socketFactory(network.getSocketFactory()).dns(host->{
                if(closed||Thread.currentThread().isInterrupted())throw new UnknownHostException("Проверка отменена");
                try{byte[] q=DnsWire.query(host);return addresses(q,resolver.resolve(q,config));}catch(IOException error){UnknownHostException failed=new UnknownHostException("DNS: "+host);failed.initCause(error);throw failed;}
            }).proxy(Proxy.NO_PROXY).followRedirects(false).followSslRedirects(false).retryOnConnectionFailure(false).connectTimeout(4,TimeUnit.SECONDS).readTimeout(5,TimeUnit.SECONDS).callTimeout(12,TimeUnit.SECONDS).build();
        }
        public Reply request(Endpoint endpoint) throws Exception {
            if(closed||Thread.currentThread().isInterrupted())throw new InterruptedException();
            Request.Builder request=new Request.Builder().url("https://"+endpoint.host+endpoint.path).header("User-Agent","KodVsego-SmartDNS").header("Accept",endpoint.api?"application/json":"text/html");
            if(endpoint.host.equals("api.anthropic.com"))request.header("anthropic-version","2023-06-01");
            Call pending=client.newCall(request.build());call=pending;if(closed)pending.cancel();
            try(Response response=pending.execute()){
                String body="";if(endpoint.api&&response.body()!=null){ByteArrayOutputStream out=new ByteArrayOutputStream();InputStream input=response.body().byteStream();byte[] buffer=new byte[2048];int n;
                    while((n=input.read(buffer))!=-1){if(out.size()+n>65536)throw new IOException("Слишком большой ответ API");out.write(buffer,0,n);}body=out.toString("UTF-8");}
                return new Reply(response.code(),response.header("Content-Type"),body,response.header("Location"),response.header("cf-mitigated"));
            }finally{call=null;}
        }
        public void close(){closed=true;Call pending=call;if(pending!=null)pending.cancel();resolver.close();client.dispatcher().cancelAll();client.connectionPool().evictAll();client.dispatcher().executorService().shutdownNow();}
    }
}
