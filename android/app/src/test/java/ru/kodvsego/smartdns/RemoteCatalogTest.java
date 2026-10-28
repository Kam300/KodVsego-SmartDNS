package ru.kodvsego.smartdns;
import android.content.Context;
import android.os.Looper;
import org.json.*;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.concurrent.atomic.AtomicInteger;
import static org.junit.Assert.*;
@RunWith(RobolectricTestRunner.class) @Config(sdk={26,35}) @LooperMode(LooperMode.Mode.PAUSED)
public class RemoteCatalogTest {
    Context context;RemoteCatalog catalog;Provider[] original;
    @Before public void before()throws Exception {context=RuntimeEnvironment.getApplication();context.getSharedPreferences("catalog-updates",0).edit().clear().commit();original=Provider.ALL;DnsVpnService.active=false;DnsVpnService.connecting=false;DnsVpnService.switching=false;clean();catalog=create();}
    @After public void after(){DnsVpnService.active=false;DnsVpnService.connecting=false;DnsVpnService.switching=false;Provider.install(original,flags());clean();}
    boolean[] flags(){boolean[] b=new boolean[original.length];for(int n=0;n<b.length;n++)b[n]=ProviderAvailability.isEnabled(n);return b;}
    void clean(){File root=new File(context.getFilesDir(),"catalog");File[] files=root.listFiles();if(files!=null)for(File f:files)assertTrue(f.delete());}
    RemoteCatalog create()throws Exception {java.lang.reflect.Constructor<RemoteCatalog> ctor=RemoteCatalog.class.getDeclaredConstructor(Context.class);ctor.setAccessible(true);return ctor.newInstance(context);}
    JSONObject next()throws Exception {return new JSONObject(CatalogDefaults.JSON).put("revision",CatalogDefaults.REVISION+1);}
    interface Throwing {void run()throws Exception;}
    void reject(Throwing action)throws Exception {try{action.run();}catch(Exception expected){return;}fail("Invalid catalog accepted");}
    void fetch()throws Exception {catalog.check(true,null);long deadline=System.currentTimeMillis()+5000;while(catalog.busy&&System.currentTimeMillis()<deadline){Thread.sleep(10);Shadows.shadowOf(Looper.getMainLooper()).idle();}assertFalse(catalog.busy);}
    @Test public void validatesBundledAndEndpoints()throws Exception {
        assertEquals(ProviderAvailability.create().length,RemoteCatalog.parse(CatalogDefaults.JSON,CatalogDefaults.REVISION,null).entries.length);
        for(String ip:new String[]{"127.0.0.1","10.0.0.1","169.254.1.1","100.64.0.1","224.0.0.1","::1","fc00::1","fe80::1","fec0::1","2001:db8::1","203.0.113.53","resolver.example","123","1.2"})assertFalse(ip,RemoteCatalog.publicIp(ip));
        for(String url:new String[]{"http://resolver.example/dns-query","https://127.0.0.1/dns-query","https://10.0.0.1/dns-query","https://user:pass@resolver.example/dns-query","https://resolver.example:444/dns-query","https://resolver.example/dns-query?secret=x","https://resolver.example/#fragment"}){JSONObject d=next();d.getJSONArray("providers").getJSONObject(0).put("doh",url);reject(()->RemoteCatalog.parse(d.toString(),CatalogDefaults.REVISION,null));}
        reject(()->RemoteCatalog.parse(new String(new char[RemoteCatalog.LIMIT+1]),CatalogDefaults.REVISION,null));
    }
    @Test public void rejectsChangedIdentityRollbackRetiredAndUnknownChecker()throws Exception {
        JSONObject d=next();d.getJSONArray("providers").getJSONObject(0).put("id",2);reject(()->RemoteCatalog.parse(d.toString(),CatalogDefaults.REVISION,null));
        JSONObject k=next();k.getJSONArray("providers").getJSONObject(0).put("key","different");reject(()->RemoteCatalog.parse(k.toString(),CatalogDefaults.REVISION,null));
        JSONObject x=next();x.getJSONArray("providers").getJSONObject(3).put("enabled",true);reject(()->RemoteCatalog.parse(x.toString(),CatalogDefaults.REVISION,null));
        JSONObject c=next();c.getJSONObject("checkers").getJSONObject("comss").put("kind","execute");reject(()->RemoteCatalog.parse(c.toString(),CatalogDefaults.REVISION,null));
        JSONObject old=next().put("revision",CatalogDefaults.REVISION-1);reject(()->RemoteCatalog.parse(old.toString(),CatalogDefaults.REVISION,null));
        JSONObject same=next().put("revision",CatalogDefaults.REVISION);same.getJSONArray("providers").getJSONObject(0).put("name","Changed");reject(()->RemoteCatalog.parse(same.toString(),CatalogDefaults.REVISION,CatalogDefaults.JSON));
    }
    @Test public void stagesUntilIdleAndKeepsOfflineCopy()throws Exception {
        catalog.applyIfIdle(true);JSONObject d=next();d.getJSONArray("providers").getJSONObject(0).put("name","Updated DNS");d.getJSONObject("checkers").getJSONObject("comss").put("url","https://newcheck.example/");AtomicInteger calls=new AtomicInteger();catalog.download=()->{calls.incrementAndGet();return d.toString();};fetch();
        assertNotEquals("Updated DNS",Provider.at(0).name);catalog.check(false,null);assertEquals(1,calls.get());assertFalse(catalog.applyIfIdle(false));
        DnsVpnService.active=true;assertFalse(catalog.applyIfIdle(true));DnsVpnService.active=false;DnsVpnService.connecting=true;assertFalse(catalog.applyIfIdle(true));DnsVpnService.connecting=false;
        assertTrue(catalog.applyIfIdle(true));assertEquals("Updated DNS",Provider.at(0).name);assertTrue(RemoteCatalog.allowed("https://newcheck.example/?c=1"));assertFalse(RemoteCatalog.allowed("https://newcheck.example/other"));
        catalog.download=()->{throw new IOException("offline");};fetch();assertEquals("Updated DNS",Provider.at(0).name);assertTrue(catalog.status().contains("Сохранённая копия"));
        RemoteCatalog restarted=create();restarted.applyIfIdle(true);assertEquals("Updated DNS",Provider.at(0).name);
        catalog.download=()->CatalogDefaults.JSON;fetch();assertFalse(catalog.applyIfIdle(true));assertEquals("Updated DNS",Provider.at(0).name);
    }
    @Test public void missingRemoteKeepsBaseline()throws Exception {
        catalog.applyIfIdle(true);catalog.download=()->{throw new RemoteCatalog.NotPublishedException();};fetch();assertTrue(catalog.status().contains("HTTP 404"));assertTrue(catalog.summary().contains("ещё не опубликован"));assertEquals(original[0].name,Provider.at(0).name);
    }
    @Test public void corruptCacheAndMalformedDownloadRetainBaseline()throws Exception {
        File root=new File(context.getFilesDir(),"catalog");Files.write(new File(root,"active.json").toPath(),"broken".getBytes(StandardCharsets.UTF_8));catalog=create();assertTrue(catalog.applyIfIdle(true));assertEquals(original[0].name,Provider.at(0).name);
        catalog.download=()->"broken";fetch();assertFalse(catalog.applyIfIdle(true));assertEquals(original[0].name,Provider.at(0).name);
        catalog.automatic(false);assertFalse(create().automatic());
    }
}
