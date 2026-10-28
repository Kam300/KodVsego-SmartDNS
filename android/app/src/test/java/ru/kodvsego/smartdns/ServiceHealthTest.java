package ru.kodvsego.smartdns;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import java.io.IOException;
import java.util.*;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class) @Config(sdk=28)
public class ServiceHealthTest {
    private ServiceHealth.Reply reply(int status,String body){return new ServiceHealth.Reply(status,"application/json",body,"","");}
    @Test public void explicitAuthIsNotFailureAndUnknownIsNotGreen(){
        ServiceHealth.Endpoint api=ServiceHealth.TARGETS[0].endpoints[1];
        assertEquals(ServiceHealth.State.LOGIN,ServiceHealth.classify(api,reply(403,"{\"error\":{\"message\":\"unregistered callers\"}}")));
        assertEquals(ServiceHealth.State.UNKNOWN,ServiceHealth.classify(api,reply(403,"{}")));
        assertEquals(ServiceHealth.State.UNKNOWN,ServiceHealth.classify(api,reply(200,"{}")));
        assertEquals(ServiceHealth.State.UNKNOWN,ServiceHealth.classify(api,new ServiceHealth.Reply(200,"text/html","<html>login</html>","","")));
        assertEquals(ServiceHealth.State.RESPONSE,ServiceHealth.classify(api,reply(200,"{\"models\":[]}")));
    }
    @Test public void regionAndChallengeHaveDifferentStates(){
        ServiceHealth.Endpoint api=ServiceHealth.TARGETS[0].endpoints[1];
        assertEquals(ServiceHealth.State.FAILED,ServiceHealth.classify(api,reply(403,"{\"error\":{\"message\":\"User location is not supported; API key\"}}")));
        assertEquals(ServiceHealth.State.BROWSER,ServiceHealth.classify(api,new ServiceHealth.Reply(403,"text/html","","","challenge")));
        assertEquals(ServiceHealth.State.UNKNOWN,ServiceHealth.classify(api,reply(429,"{}")));
    }
    @Test public void oneFailedEndpointCannotHideBehindWebsiteSuccess() throws Exception {
        ServiceHealth.Result result=ServiceHealth.check(ServiceHealth.TARGETS[0],endpoint->{if(endpoint.api)throw new IOException("DNS timeout");return reply(200,"{}");});
        assertEquals(ServiceHealth.State.FAILED,result.state);assertTrue(result.checked>0);assertTrue(result.detail.contains("DNS/HTTPS"));
    }
    @Test public void unsupportedAndRetiredRemainNeutral(){
        assertEquals(ServiceHealth.State.DISABLED,ServiceHealth.initial(1,0,DnsConfig.DOH).state);
        assertEquals(ServiceHealth.State.UNSUPPORTED,ServiceHealth.initial(12,0,DnsConfig.DOH).state);
        assertEquals(ServiceHealth.State.NO_PROTOCOL,ServiceHealth.initial(5,0,DnsConfig.PLAIN).state);
        assertEquals(ServiceHealth.State.UNTESTED,ServiceHealth.initial(6,0,DnsConfig.DOH).state);
        for(ServiceHealth.Target target:ServiceHealth.TARGETS)assertNotEquals("Antigravity",target.name);
    }
    @Test public void cancellationDoesNotPublishSuccess() throws Exception {
        Thread.currentThread().interrupt();try{ServiceHealth.check(ServiceHealth.TARGETS[0],endpoint->reply(200,"{}"));fail();}catch(InterruptedException expected){}finally{Thread.interrupted();}
    }
    @Test public void dnsParserRejectsPrivateAndMalformedAnswers() throws Exception {
        byte[] q=DnsWire.query("gemini.google.com");assertEquals("93.2.3.4",ServiceHealth.addresses(q,DnsCoreTest.answer(q,93)).get(0).getHostAddress());
        try{ServiceHealth.addresses(q,DnsCoreTest.answer(q,127));fail();}catch(IOException expected){}
        byte[] answer=DnsCoreTest.answer(q,93);answer[0]^=1;try{ServiceHealth.addresses(q,answer);fail();}catch(IOException expected){}
        try{ServiceHealth.addresses(q,Arrays.copyOf(answer,14));fail();}catch(IOException expected){}
    }
    @Test public void panelExpandsAndCanCancelWithoutNetwork(){
        ServiceHealthPanel panel=new ServiceHealthPanel(RuntimeEnvironment.getApplication(),0xffeeeeee,0xffaaaaaa,0xff02a5cf,0xff151821,(title,body)->{});
        assertNotNull(panel.findViewWithTag("service-health-expand"));panel.findViewWithTag("service-health-expand").performClick();
        assertNotNull(panel.findViewWithTag("health-cell-6-0"));panel.cancel("Остановлено");assertFalse(panel.busy());
        panel.findViewWithTag("health-protocol-0").performClick();assertTrue(((android.widget.TextView)panel.findViewWithTag("health-cell-5-0")).getText().toString().contains("Протокол недоступен"));
    }
    @Test public void offlineRetryClearsPreviousGreenResult() throws Exception {
        ServiceHealthPanel panel=new ServiceHealthPanel(RuntimeEnvironment.getApplication(),0xffeeeeee,0xffaaaaaa,0xff02a5cf,0xff151821,(title,body)->{});
        panel.findViewWithTag("service-health-expand").performClick();java.lang.reflect.Field field=ServiceHealthPanel.class.getDeclaredField("results");field.setAccessible(true);
        ServiceHealth.Result[][] results=(ServiceHealth.Result[][])field.get(panel);results[6][0]=new ServiceHealth.Result(ServiceHealth.State.RESPONSE,"old",1);
        panel.findViewWithTag("service-health-run").performClick();assertFalse(panel.busy());assertEquals(ServiceHealth.State.UNTESTED,((ServiceHealth.Result[][])field.get(panel))[6][0].state);
    }
}
