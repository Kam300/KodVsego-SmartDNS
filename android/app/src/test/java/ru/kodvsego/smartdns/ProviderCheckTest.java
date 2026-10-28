package ru.kodvsego.smartdns;

import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import java.io.IOException;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class) @Config(sdk=35)
public class ProviderCheckTest {
    static class Fake implements ProviderCheck.Io {
        String body="{}";int status=200,stamps,requests;boolean changed,failed,lookupFailed;String lookup="";
        public String stamp(){return changed&&stamps++>0?"new":"old";}
        public ProviderCheck.Reply get(String url)throws Exception {assertTrue(ProviderCheck.allowed(url));requests++;if(failed)throw new IOException("secret-server-detail");return new ProviderCheck.Reply(status,body);}
        public void lookup(String host)throws Exception {lookup=host;if(lookupFailed)throw new IOException();}
        public void pause(){}
    }
    @Test public void comssNeedsExactMarker()throws Exception {Fake f=new Fake();f.body="Comss.one DNS enabled\n";assertEquals(ProviderCheck.State.CONFIRMED,ProviderCheck.run(0,f).state);f.body="<html>error</html>";assertEquals(ProviderCheck.State.UNKNOWN,ProviderCheck.run(0,f).state);}
    @Test public void webshiftBooleanCannotBeStringOrMissing()throws Exception {Fake f=new Fake();f.body="{\"connected\":true}";assertEquals(ProviderCheck.State.CONFIRMED,ProviderCheck.run(11,f).state);f.body="{\"connected\":false}";assertEquals(ProviderCheck.State.NOT_CONFIRMED,ProviderCheck.run(11,f).state);f.body="{\"connected\":\"true\"}";assertEquals(ProviderCheck.State.UNKNOWN,ProviderCheck.run(11,f).state);f.body="{}";assertEquals(ProviderCheck.State.UNKNOWN,ProviderCheck.run(11,f).state);}
    @Test public void wlrusOnlyAcceptsKnownVia()throws Exception {Fake f=new Fake();f.body="{\"via\":\"ours\"}";assertEquals(ProviderCheck.State.CONFIRMED,ProviderCheck.run(10,f).state);f.body="{\"via\":\"public\"}";assertEquals(ProviderCheck.State.NOT_CONFIRMED,ProviderCheck.run(10,f).state);f.body="{\"via\":\"other\"}";assertEquals(ProviderCheck.State.UNKNOWN,ProviderCheck.run(10,f).state);}
    @Test public void malwDoesNotConfirmCloudflareProfile()throws Exception {for(int id:new int[]{9,13}){Fake f=new Fake();f.status=204;ProviderCheck.Result r=ProviderCheck.run(id,f);assertEquals(ProviderCheck.State.PROBE_PASSED,r.state);assertTrue(r.report().contains("не различает"));assertFalse(r.caption().contains("подтверждён"));}}
    @Test public void dnsAiNeedsBothRequests()throws Exception {Fake f=new Fake();f.status=204;assertEquals(ProviderCheck.State.CONFIRMED,ProviderCheck.run(5,f).state);assertEquals(2,f.requests);f=new Fake();f.status=404;assertEquals(ProviderCheck.State.UNKNOWN,ProviderCheck.run(5,f).state);assertEquals(1,f.requests);}
    @Test public void networkChangeInvalidatesSuccessAndErrorsStayNeutral()throws Exception {Fake f=new Fake();f.body="{\"connected\":true}";f.changed=true;assertEquals(ProviderCheck.State.CHANGED,ProviderCheck.run(11,f).state);f=new Fake();f.failed=true;ProviderCheck.Result r=ProviderCheck.run(11,f);assertEquals(ProviderCheck.State.UNKNOWN,r.state);assertFalse(r.report().contains("secret-server-detail"));}
    @Test public void zapretRequiresFreshLookupAndNeverConfirmsOnFalse()throws Exception {Fake f=new Fake();f.body="{\"seen\":true,\"protocol\":\"DNS-over-TLS\"}";ProviderCheck.Result r=ProviderCheck.run(7,f);assertEquals(ProviderCheck.State.CONFIRMED,r.state);assertEquals("DoT",r.protocol);assertTrue(f.lookup.matches("[a-f0-9]{16}\\.check\\.dns\\.zapret\\.moe"));f.body="{\"seen\":false}";assertEquals(ProviderCheck.State.NOT_CONFIRMED,ProviderCheck.run(7,f).state);f.lookupFailed=true;assertEquals(ProviderCheck.State.UNKNOWN,ProviderCheck.run(7,f).state);}
    @Test public void redirectsForeignHostsAndOversizedBodiesAreRejected()throws Exception {for(String url:new String[]{"http://check.comss.one/","https://check.comss.one.evil/","https://user:pass@check.comss.one/","https://check.comss.one:444/","https://check.comss.one/other","https://check.comss.one/#fragment","https://127.0.0.1/"})assertFalse(ProviderCheck.allowed(url));Fake f=new Fake();f.status=302;assertEquals(ProviderCheck.State.UNKNOWN,ProviderCheck.run(0,f).state);f.status=200;f.body=new String(new char[32769]);assertEquals(ProviderCheck.State.UNKNOWN,ProviderCheck.run(0,f).state);assertFalse(ProviderCheck.available(6));assertFalse(ProviderCheck.available(3));}
}
