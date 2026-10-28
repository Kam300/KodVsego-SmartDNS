package ru.kodvsego.smartdns;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.Network;
import okhttp3.*;
import org.json.JSONObject;
import java.io.*;
import java.net.*;
import java.util.*;
import java.util.concurrent.*;

/** Official probes through the current Android DNS path, never a forced upstream. */
final class ProviderCheck {
    enum State { CONFIRMED, NOT_CONFIRMED, PROBE_PASSED, UNKNOWN, CHANGED }
    static final class Reply {final int status;final String body;Reply(int s,String b){status=s;body=b;}}
    interface Io {String stamp() throws Exception;Reply get(String url) throws Exception;void lookup(String host) throws Exception;void pause() throws Exception;}
    static final class Result {
        State state=State.UNKNOWN;final int provider;final String source;String protocol="",detail="";
        Result(int p){provider=p;source=source(p);}
        String caption(){String name=Provider.at(provider).name;switch(state){case CONFIRMED:return "Чекер: "+name+" подтверждён";case NOT_CONFIRMED:return "Чекер: "+name+" не подтверждён";case PROBE_PASSED:return "Чекер: Malw отвечает · профиль не определён";case CHANGED:return "Чекер: подключение изменилось · повторите";default:return "Чекер: не удалось проверить";}}
        String report(){return caption()+"\nИсточник: "+source+"\n"+(protocol.isEmpty()?"":"Протокол чекера: "+protocol+"\n")+
            (state==State.PROBE_PASSED?"Проба Malw не различает обычный DNS и Malw CF.\n":"")+
            "Проверен текущий DNS-путь приложения Android. DNS провайдера принудительно не подставлялся. Собственный DNS браузера, вход в аккаунт и ответы ИИ не проверяются. Недоступность чекера не доказывает неисправность DNS.\n\n"+detail;}
    }
    static boolean available(int p){return Provider.isAvailable(p)&&RemoteCatalog.kind(p)!=null;}
    static String source(int p){return p==0?"comss.ru/page.php?id=7315":p==5?"dns-ai.ru/ip":p==7?"dns.zapret.moe":p==10?"site.dns.wlrus.cfd":p==11?"dns.webshiftvpn.ru":"info.dns.malw.link";}
    static Result run(int p,Io io) throws InterruptedException {
        if(!available(p))throw new IllegalArgumentException("У провайдера нет поддерживаемого чекера");
        Result result=new Result(p);String before=null;StringBuilder trace=new StringBuilder("UTC="+java.time.Instant.now()+"; path=Android system DNS; provider="+p+"\n");
        String nonce=UUID.randomUUID().toString().replace("-", "").substring(0,16);
        try {before=io.stamp();
            if("comss".equals(RemoteCatalog.kind(p))){if(!"Comss.one DNS enabled".equals(get(io,RemoteCatalog.url(p,"url","https://check.comss.one/")+"?c="+nonce,false,trace).body.trim()))throw new IOException("Invalid marker");result.state=State.CONFIRMED;}
            else if("wlrus".equals(RemoteCatalog.kind(p))){Object via=json(get(io,RemoteCatalog.url(p,"url","https://probe.dns.wlrus.cfd/whoami")+"?c="+nonce,false,trace)).get("via");if("ours".equals(via))result.state=State.CONFIRMED;else if("public".equals(via))result.state=State.NOT_CONFIRMED;else throw new IOException("Invalid via");}
            else if("webshift".equals(RemoteCatalog.kind(p))){result.state=bool(json(get(io,RemoteCatalog.url(p,"url","https://dns.webshiftvpn.ru/api/dns-check")+"?t="+System.currentTimeMillis(),false,trace)),"connected")?State.CONFIRMED:State.NOT_CONFIRMED;}
            else if("zapret".equals(RemoteCatalog.kind(p))){boolean lookupTimeout=false;try{io.lookup(nonce+"."+RemoteCatalog.url(p,"lookupSuffix","check.dns.zapret.moe"));}catch(InterruptedException e){throw e;}catch(TimeoutException e){lookupTimeout=true;}catch(Exception ignored){lookupTimeout=true;}
                for(int n=0;n<4;n++){io.pause();JSONObject data=json(get(io,RemoteCatalog.url(p,"url","https://dns.zapret.moe/api/dns-check")+"?token="+nonce,false,trace));if(bool(data,"seen")){result.state=State.CONFIRMED;String protocol=data.optString("protocol","").toUpperCase(Locale.ROOT);result.protocol=protocol.contains("HTTPS")?"DoH":protocol.contains("TLS")?"DoT":protocol.equals("TCP")||protocol.equals("UDP")?protocol:"Не указан";break;}if(n==3)result.state=lookupTimeout?State.UNKNOWN:State.NOT_CONFIRMED;}}
            else if("dns-ai".equals(RemoteCatalog.kind(p))){get(io,RemoteCatalog.url(p,"url","https://dns-ai.ru/dnscheck")+"?c="+nonce,true,trace);get(io,RemoteCatalog.url(p,"control","https://check.dns-ai.ru/dnscheck")+"?c="+nonce,true,trace);result.state=State.CONFIRMED;}
            else {get(io,RemoteCatalog.url(p,"url","https://test.dns.malw.link/")+"?c="+nonce,true,trace);result.state=State.PROBE_PASSED;}
        }catch(InterruptedException e){throw e;}catch(Exception error){result.state=State.UNKNOWN;trace.append("Error=").append(error.getClass().getSimpleName()).append('\n');}
        if(Thread.currentThread().isInterrupted())throw new InterruptedException();
        try{if(before==null)result.state=State.UNKNOWN;else if(!before.equals(io.stamp()))result.state=State.CHANGED;}catch(Exception e){result.state=State.UNKNOWN;}
        if(io instanceof SystemIo&&android.os.Build.VERSION.SDK_INT<29){
            trace.append("Android 8–9: системный DNS-кэш может сохранять прежний адрес; свежесть пути не подтверждена.\n");
            if(result.state==State.CONFIRMED||result.state==State.NOT_CONFIRMED||result.state==State.PROBE_PASSED)result.state=State.UNKNOWN;
        }
        trace.append("Result=").append(result.state);result.detail=trace.toString();return result;
    }
    private static JSONObject json(Reply r) throws Exception {if(r.body==null||r.body.length()>32768)throw new IOException("Invalid body");return new JSONObject(r.body);}
    private static boolean bool(JSONObject data,String name) throws Exception {Object value=data.get(name);if(!(value instanceof Boolean))throw new IOException("Invalid boolean");return (Boolean)value;}
    private static Reply get(Io io,String url,boolean probe,StringBuilder trace) throws Exception {if(Thread.currentThread().isInterrupted())throw new InterruptedException();if(!allowed(url))throw new IOException("Forbidden URL");Reply r=io.get(url);trace.append(HttpUrl.get(url).host()).append(' ').append(HttpUrl.get(url).encodedPath()).append(" HTTP=").append(r.status).append('\n');if(r.status!=200&&!(probe&&r.status==204))throw new IOException("Unexpected HTTP");if(r.body==null||r.body.length()>32768)throw new IOException("Invalid body");return r;}
    static boolean allowed(String url){try{HttpUrl u=HttpUrl.get(url);if(!u.isHttps()||u.port()!=443||!u.username().isEmpty()||!u.password().isEmpty()||u.fragment()!=null)return false;if(RemoteCatalog.allowed(url))return true;String h=u.host(),p=u.encodedPath();return h.equals("check.comss.one")&&p.equals("/")||h.equals("probe.dns.wlrus.cfd")&&p.equals("/whoami")||h.equals("dns.webshiftvpn.ru")&&p.equals("/api/dns-check")||h.equals("dns.zapret.moe")&&p.equals("/api/dns-check")||(h.equals("dns-ai.ru")||h.equals("check.dns-ai.ru"))&&p.equals("/dnscheck")||h.equals("test.dns.malw.link")&&p.equals("/");}catch(Exception e){return false;}}

    static final class SystemIo implements Io,Closeable {
        private static final ExecutorService LOOKUPS=new ThreadPoolExecutor(2,2,0,TimeUnit.SECONDS,new ArrayBlockingQueue<>(2),r->{Thread t=new Thread(r,"checker-dns");t.setDaemon(true);return t;});
        final Context context;final OkHttpClient client;final long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(25);volatile Call call;volatile boolean closed;
        SystemIo(Context c){context=c;client=new OkHttpClient.Builder().proxy(Proxy.NO_PROXY).dns(this::addresses).connectTimeout(4,TimeUnit.SECONDS).readTimeout(5,TimeUnit.SECONDS).callTimeout(8,TimeUnit.SECONDS).followRedirects(false).followSslRedirects(false).retryOnConnectionFailure(false).build();}
        public String stamp() throws Exception {ConnectivityManager cm=context.getSystemService(ConnectivityManager.class);Network network=cm.getActiveNetwork();if(network==null)throw new IOException("No network");DnsConfig c=DnsVpnService.current;Network physical=PhysicalNetwork.find(context);return network+"/"+cm.getLinkProperties(network)+"/"+physical+"/"+cm.getLinkProperties(physical)+"/"+DnsVpnService.active+"/"+DnsVpnService.switching+"/"+System.identityHashCode(c);}
        void check() throws IOException {if(closed||Thread.currentThread().isInterrupted()||System.nanoTime()>deadline)throw new IOException("Cancelled or timeout");}
        private List<InetAddress> addresses(String host) throws UnknownHostException {
            Future<?> pending=null;android.os.CancellationSignal cancel=new android.os.CancellationSignal();
            try{check();InetAddress[] found;
                long timeout=Math.min(2500,Math.max(1,TimeUnit.NANOSECONDS.toMillis(deadline-System.nanoTime())));
                if(android.os.Build.VERSION.SDK_INT>=29){
                    CompletableFuture<List<InetAddress>> answer=new CompletableFuture<>();pending=answer;
                    Network network=context.getSystemService(ConnectivityManager.class).getActiveNetwork();if(network==null)throw new UnknownHostException("No network");
                    android.net.DnsResolver.getInstance().query(network,host,android.net.DnsResolver.FLAG_NO_CACHE_LOOKUP,LOOKUPS,cancel,new android.net.DnsResolver.Callback<List<InetAddress>>(){
                        public void onAnswer(List<InetAddress> addresses,int code){if(code==0)answer.complete(addresses);else answer.completeExceptionally(new IOException("DNS error"));}
                        public void onError(android.net.DnsResolver.DnsException e){answer.completeExceptionally(e);}
                    });found=answer.get(timeout,TimeUnit.MILLISECONDS).toArray(new InetAddress[0]);
                }else {Future<InetAddress[]> answer=LOOKUPS.submit(()->InetAddress.getAllByName(host));pending=answer;found=answer.get(timeout,TimeUnit.MILLISECONDS);}
                List<InetAddress> publicAddresses=new ArrayList<>();for(InetAddress ip:found){byte[] bytes=ip.getAddress();if(!ip.isAnyLocalAddress()&&!ip.isLoopbackAddress()&&!ip.isLinkLocalAddress()&&!ip.isSiteLocalAddress()&&!ip.isMulticastAddress()&&!(bytes.length==16&&(bytes[0]&254)==252))publicAddresses.add(ip);}if(publicAddresses.isEmpty())throw new UnknownHostException("No public address");return publicAddresses;
            }catch(InterruptedException e){Thread.currentThread().interrupt();throw new UnknownHostException("Cancelled");}catch(Exception e){throw new UnknownHostException("System lookup failed");}finally{cancel.cancel();if(pending!=null)pending.cancel(true);}
        }
        public Reply get(String url) throws Exception {check();if(!allowed(url))throw new IOException("Forbidden URL");call=client.newCall(new Request.Builder().url(url).header("Cache-Control","no-cache").header("User-Agent","KodVsego-SmartDNS-Android-Provider-Check").build());call.timeout().timeout(Math.min(8000,Math.max(1,TimeUnit.NANOSECONDS.toMillis(deadline-System.nanoTime()))),TimeUnit.MILLISECONDS);if(closed)call.cancel();try(Response r=call.execute()){if(r.body()==null)throw new IOException("No body");ByteArrayOutputStream out=new ByteArrayOutputStream();byte[] buffer=new byte[2048];int n;InputStream in=r.body().byteStream();while((n=in.read(buffer))!=-1){check();if(out.size()+n>32768)throw new IOException("Body too large");out.write(buffer,0,n);}return new Reply(r.code(),out.toString("UTF-8"));}finally{call=null;}}
        public void lookup(String host) throws Exception {if(!RemoteCatalog.allowedLookup(host)&&!host.matches("[a-f0-9]{16}\\.check\\.dns\\.zapret\\.moe"))throw new IOException("Forbidden name");addresses(host);}
        public void pause() throws Exception {check();Thread.sleep(700);}
        public void close(){closed=true;Call c=call;if(c!=null)c.cancel();client.dispatcher().cancelAll();client.connectionPool().evictAll();client.dispatcher().executorService().shutdownNow();}
    }
}
