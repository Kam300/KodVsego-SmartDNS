package ru.kodvsego.smartdns;

import android.content.*;
import android.os.*;
import android.util.AtomicFile;
import okhttp3.*;
import org.json.*;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;

/** Versioned data only; no remote code and no changes to an active VPN session. */
final class RemoteCatalog {
    static final int LIMIT=262144;
    static final class Document {
        final long revision;final String raw;final Provider[] entries;final boolean[] enabled;final JSONObject root;
        Document(long revision,String raw,Provider[] entries,boolean[] enabled,JSONObject root){this.revision=revision;this.raw=raw;this.entries=entries;this.enabled=enabled;this.root=root;}
        JSONObject checker(int id){return id>=0&&id<entries.length?root.optJSONObject("checkers").optJSONObject(root.optJSONArray("providers").optJSONObject(id).optString("key")):null;}
    }
    private static RemoteCatalog instance;private static volatile Document forChecks;
    static synchronized RemoteCatalog get(Context c){Context app=c.getApplicationContext();if(instance==null||instance.context!=app)instance=new RemoteCatalog(app);return instance;}
    final Context context;final SharedPreferences prefs;private final AtomicFile activeFile,pendingFile;
    private final ExecutorService worker=Executors.newSingleThreadExecutor(r->{Thread t=new Thread(r,"dns-catalog");t.setDaemon(true);return t;});
    private final Handler main=new Handler(Looper.getMainLooper());private Document active,pending;private long installed=-1;
    boolean busy;String result="Встроенный каталог";
    private RemoteCatalog(Context c){context=c;prefs=c.getSharedPreferences("catalog-updates",0);File root=new File(c.getFilesDir(),"catalog");root.mkdirs();activeFile=new AtomicFile(new File(root,"active.json"));pendingFile=new AtomicFile(new File(root,"pending.json"));
        try{active=parse(CatalogDefaults.JSON,CatalogDefaults.REVISION,null);}catch(Exception error){throw new IllegalStateException("Invalid bundled catalog",error);}
        try{String json=read(activeFile);if(json!=null)active=parse(json,CatalogDefaults.REVISION,null);}catch(Exception error){AppJournal.record("Каталог DNS","Локальная копия отклонена: "+error.getClass().getSimpleName());}
        try{String json=read(pendingFile);if(json!=null)pending=parse(json,active.revision,active.raw);}catch(Exception error){AppJournal.record("Каталог DNS","Ожидающая копия отклонена");}
    }
    boolean automatic(){return prefs.getBoolean("automatic",true);}void automatic(boolean on){prefs.edit().putBoolean("automatic",on).apply();}
    String status(){long checked=prefs.getLong("lastCheck",0);return "Каталог: "+active.revision+"\nПроверка: "+(checked==0?"не выполнялась":java.text.DateFormat.getDateTimeInstance().format(new Date(checked)))+"\n"+result+(pending!=null?"\nНовые данные применятся после выключения DNS.":"");}
    boolean applyIfIdle(boolean idle){if(!idle||DnsVpnService.active||DnsVpnService.connecting||DnsVpnService.switching)return false;
        if(pending!=null&&pending.revision>active.revision){try{write(activeFile,pending.raw);active=pending;pending=null;pendingFile.delete();result="Каталог применён.";}catch(IOException error){result="Не удалось сохранить каталог. Прежняя копия сохранена.";return false;}}
        if(installed==active.revision)return false;Provider.install(active.entries,active.enabled);forChecks=active;installed=active.revision;return true;
    }
    String summary(){if(pending!=null)return "Новый каталог применится после выключения DNS";if(result.startsWith("Онлайн-каталог"))return "Онлайн-каталог ещё не опубликован · используется сохранённый";return result;}
    static final class NotPublishedException extends IOException {}
    interface Fetch {String get()throws Exception;}
    Fetch download=RemoteCatalog::downloadHttps;
    private static String downloadHttps()throws Exception {
        OkHttpClient client=new OkHttpClient.Builder().proxy(Proxy.NO_PROXY).followRedirects(false).followSslRedirects(false).connectTimeout(4,TimeUnit.SECONDS).readTimeout(6,TimeUnit.SECONDS).callTimeout(12,TimeUnit.SECONDS).build();
        try(Response response=client.newCall(new Request.Builder().url(CatalogDefaults.URL).header("User-Agent","KodVsego-SmartDNS-Android-Catalog").build()).execute()){
            if(response.code()==404)throw new NotPublishedException();
            if(response.code()!=200||response.body()==null)throw new IOException("Catalog HTTP "+response.code());
            ByteArrayOutputStream bytes=new ByteArrayOutputStream();byte[] buffer=new byte[4096];int n;InputStream input=response.body().byteStream();while((n=input.read(buffer))!=-1){need(bytes.size()+n<=LIMIT);bytes.write(buffer,0,n);}return bytes.toString("UTF-8");
        }finally{client.connectionPool().evictAll();client.dispatcher().executorService().shutdown();}
    }
    void check(boolean manual,Runnable done){if(busy){if(done!=null)done.run();return;}long now=System.currentTimeMillis(),last=prefs.getLong("lastCheck",0);if(!manual&&(!automatic()||last>0&&now-last<TimeUnit.DAYS.toMillis(1)))return;
        prefs.edit().putLong("lastCheck",now).apply();busy=true;result="Проверяем каталог…";final long minimum=pending==null?active.revision:Math.max(active.revision,pending.revision);final String previous=pending!=null&&pending.revision==minimum?pending.raw:active.raw;
        worker.execute(()->{Document next=null;String message;
            try{next=parse(download.get(),minimum,previous);if(next.revision>active.revision){write(pendingFile,next.raw);message="Новая версия скачана и проверена.";}else{next=null;message="Каталог актуален.";}}
            catch(Exception error){next=null;message=error instanceof NotPublishedException?"Онлайн-каталог ещё не опубликован (HTTP 404). Используется встроенная или сохранённая копия.":"Не удалось обновить каталог. Сохранённая копия остаётся рабочей.";AppJournal.record("Каталог DNS",error.getClass().getSimpleName());}
            final Document accepted=next;final String text=message;main.post(()->{if(accepted!=null&&accepted.revision>active.revision)pending=accepted;result=text;busy=false;if(done!=null)done.run();});
        });
    }
    private static String read(AtomicFile file)throws IOException {if(!file.getBaseFile().exists()&&!new File(file.getBaseFile()+".bak").exists())return null;try(InputStream input=file.openRead()){ByteArrayOutputStream bytes=new ByteArrayOutputStream();byte[] buffer=new byte[4096];int n;while((n=input.read(buffer))!=-1){if(bytes.size()+n>LIMIT)throw new IOException("Catalog too large");bytes.write(buffer,0,n);}return bytes.toString("UTF-8");}}
    private static void write(AtomicFile file,String value)throws IOException {FileOutputStream output=null;try{output=file.startWrite();output.write(value.getBytes(StandardCharsets.UTF_8));file.finishWrite(output);}catch(IOException error){if(output!=null)file.failWrite(output);throw error;}}
    private static void need(boolean value)throws IOException {if(!value)throw new IOException("Каталог DNS не прошёл проверку формата или адресов");}
    private static boolean integer(Object value){return value instanceof Integer||value instanceof Long;}
    private static String text(JSONObject item,String name,int limit,boolean empty)throws Exception {Object value=item.get(name);need(value instanceof String);String s=(String)value;need(s.length()<=limit&&(empty||!s.isEmpty())&&!s.matches("(?s).*\\p{Cntrl}.*"));return s;}
    private static String nullable(JSONObject item,String name)throws Exception {return item.isNull(name)?null:text(item,name,512,false);}
    static boolean host(String value){return value!=null&&value.length()<=253&&value.matches("[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+")&&!value.matches("[0-9.]+")&&!value.toLowerCase(Locale.ROOT).endsWith(".local")&&!value.toLowerCase(Locale.ROOT).endsWith(".internal");}
    static boolean publicIp(String value){try{if(value==null||value.contains("%"))return false;boolean v6=value.contains(":");if(v6?!value.matches("[0-9a-fA-F:.]+"):!value.matches("[0-9]{1,3}(\\.[0-9]{1,3}){3}"))return false;
            InetAddress ip=InetAddress.getByName(value);byte[] b=ip.getAddress();if(ip.isAnyLocalAddress()||ip.isLoopbackAddress()||ip.isLinkLocalAddress()||ip.isSiteLocalAddress()||ip.isMulticastAddress())return false;
            if(v6)return ip instanceof Inet6Address&&(b[0]&224)==32&&!(b[0]==32&&b[1]==1&&b[2]==13&&(b[3]&255)==184);int a=b[0]&255,c=b[1]&255;return ip.getHostAddress().equals(value)&&a>0&&a<224&&!(a==100&&c>=64&&c<=127)&&!(a==198&&(c==18||c==19))&&!(a==192&&c==0&&(b[2]==0||b[2]==2))&&!(a==198&&c==51&&(b[2]&255)==100)&&!(a==203&&c==0&&(b[2]&255)==113);
        }catch(Exception error){return false;}}
    private static boolean https(String value,boolean query){try{HttpUrl u=HttpUrl.get(value);return value.length()<=512&&u.isHttps()&&u.port()==443&&u.username().isEmpty()&&u.password().isEmpty()&&u.fragment()==null&&(query||u.query()==null)&&(host(u.host())||publicIp(u.host()));}catch(Exception error){return false;}}
    private static String[] strings(JSONObject p,String field,int max)throws Exception {JSONArray a=p.getJSONArray(field);need(a.length()<=max);String[] out=new String[a.length()];for(int n=0;n<out.length;n++){need(a.get(n) instanceof String);out[n]=a.getString(n);need(!out[n].isEmpty()&&out[n].length()<=253&&!out[n].matches("(?s).*\\p{Cntrl}.*"));}return out;}
    private static String[] ips(JSONObject p,String field,boolean required,boolean v6)throws Exception {String[] out=strings(p,field,8);need(!required||out.length>0);for(String ip:out)need(publicIp(ip)&&ip.contains(":")==v6);return out;}
    static Document parse(String json,long minimum,String previous)throws Exception {
        need(json!=null&&json.getBytes(StandardCharsets.UTF_8).length<=LIMIT);JSONObject root=new JSONObject(json);need(integer(root.get("schema"))&&root.getInt("schema")==1&&integer(root.get("revision")));long revision=root.getLong("revision");need(revision>=minimum&&revision<=99999999999999L);
        java.time.Instant.parse(root.getString("updatedUtc"));if(revision==CatalogDefaults.REVISION)need(json.trim().equals(CatalogDefaults.JSON.trim()));if(revision==minimum&&previous!=null)need(json.trim().equals(previous.trim()));
        JSONObject prior=previous==null?null:new JSONObject(previous);
        JSONArray base=new JSONObject(CatalogDefaults.JSON).getJSONArray("providers"),list=root.getJSONArray("providers");need(list.length()>=base.length()&&list.length()<=64);need(prior==null||list.length()>=prior.getJSONArray("providers").length());Provider[] entries=new Provider[list.length()];boolean[] enabled=new boolean[list.length()];Set<String> keys=new HashSet<>();boolean any=false;
        for(int id=0;id<entries.length;id++){JSONObject p=list.getJSONObject(id);need(integer(p.get("id"))&&p.getLong("id")==id&&p.get("enabled") instanceof Boolean&&p.get("encryptedOnly") instanceof Boolean);String key=text(p,"key",48,false);need(key.matches("[a-z0-9-]+")&&keys.add(key)&&(id>=base.length()||key.equals(base.getJSONObject(id).getString("key"))));need(prior==null||id>=prior.getJSONArray("providers").length()||key.equals(prior.getJSONArray("providers").getJSONObject(id).getString("key")));enabled[id]=p.getBoolean("enabled");need(!(id==1||id==2||id==3)||!enabled[id]);any|=enabled[id];
            String name=text(p,"name",48,false);text(p,"description",256,true);text(p,"site",80,true);String[] v4=ips(p,"ipv4",true,false);ips(p,"ipv6",false,true);String[] dohIps=ips(p,"windowsDohServers",true,false),dotIps=ips(p,"windowsDotServers",false,false);
            String doh=nullable(p,"doh"),dot=nullable(p,"dot"),url=nullable(p,"url");boolean encrypted=p.getBoolean("encryptedOnly");need(doh==null||https(doh,false));need(!encrypted||doh!=null);need(dot==null?dotIps.length==0:host(dot)&&dotIps.length>0);need(url==null||https(url,true));
            String[] services=strings(p,"services",16),hosts=strings(p,"testHosts",32),goals=strings(p,"autoGoals",8);need(hosts.length>0);for(String h:hosts)need(host(h));for(String goal:goals)need(Arrays.asList("ChatGPT","Claude","Gemini","ElevenLabs","Spotify","Xbox","Antigravity").contains(goal));
            JSONArray legacy=p.getJSONArray("legacyIpv4");need(legacy.length()<=32);for(int n=0;n<legacy.length();n++){JSONArray values=legacy.getJSONArray(n);need(values.length()>0&&values.length()<=8);for(int m=0;m<values.length();m++)need(values.get(m) instanceof String&&publicIp(values.getString(m))&&!values.getString(m).contains(":"));}
            entries[id]=new Provider(name,v4,doh,encrypted,services,hosts,dot,dotIps,url,dohIps);
        }
        need(any);JSONObject checkers=root.getJSONObject("checkers");need(checkers.length()<=64);Iterator<String> names=checkers.keys();while(names.hasNext()){String key=names.next();JSONObject c=checkers.getJSONObject(key);String kind=text(c,"kind",20,false);need(keys.contains(key)&&Arrays.asList("comss","dns-ai","zapret","wlrus","webshift","malw").contains(kind)&&https(c.getString("url"),false));String control=c.isNull("control")?null:c.getString("control"),suffix=c.isNull("lookupSuffix")?null:c.getString("lookupSuffix");need(control==null||https(control,false));need(!kind.equals("dns-ai")||control!=null);need(suffix==null||host(suffix));need(!kind.equals("zapret")||suffix!=null);}
        return new Document(revision,json.trim(),entries,enabled,root);
    }
    private static Document checks(){Document doc=forChecks;if(doc==null)try{doc=parse(CatalogDefaults.JSON,CatalogDefaults.REVISION,null);forChecks=doc;}catch(Exception e){throw new IllegalStateException(e);}return doc;}
    static String kind(int id){JSONObject c=checks().checker(id);return c==null?null:c.optString("kind",null);}
    static String url(int id,String field,String fallback){JSONObject c=checks().checker(id);return c==null?fallback:c.optString(field,fallback);}
    static boolean allowed(String url){try{HttpUrl u=HttpUrl.get(url);if(!u.isHttps()||u.port()!=443||!u.username().isEmpty()||!u.password().isEmpty()||u.fragment()!=null)return false;String endpoint=u.newBuilder().query(null).build().toString();JSONObject checkers=checks().root.getJSONObject("checkers");Iterator<String> keys=checkers.keys();while(keys.hasNext()){JSONObject c=checkers.getJSONObject(keys.next());if(endpoint.equals(c.optString("url"))||endpoint.equals(c.optString("control")))return true;}return false;}catch(Exception e){return false;}}
    static boolean allowedLookup(String value){JSONObject checkers=checks().root.optJSONObject("checkers");Iterator<String> keys=checkers.keys();while(keys.hasNext()){JSONObject c=checkers.optJSONObject(keys.next());if("zapret".equals(c.optString("kind"))&&value.matches("[a-f0-9]{16}\\."+java.util.regex.Pattern.quote(c.optString("lookupSuffix"))))return true;}return false;}
}
