package ru.kodvsego.smartdns;

import org.junit.Test;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicInteger;
import static org.junit.Assert.*;

public class DnsSelectionTest {
    @Test public void candidatesRespectVisibilityAndNeverDowngradeEncryption(){
        List<DnsConfig> list=DnsSelection.candidates(new HashSet<>(Arrays.asList("5")),true,6);
        assertFalse(list.isEmpty());for(DnsConfig c:list){assertTrue(c.encrypted);assertNotEquals(5,c.provider);assertNotEquals(6,c.provider);assertTrue(Provider.isAvailable(c.provider));}
        assertTrue(list.stream().anyMatch(c->c.protocol==DnsConfig.DOT));
    }
    @Test public void failedProbeCannotWin() throws Exception {
        List<DnsConfig> configs=Arrays.asList(new DnsConfig(0,true),new DnsConfig(5,true));
        List<DnsSelection.Result> results=DnsSelection.check(configs,c->{if(c.provider==0)throw new Exception("fixture timeout");return 25;},2000);
        assertEquals(5,DnsSelection.best(results).config.provider);assertEquals(2,results.size());
        assertNull(DnsSelection.best(Collections.singletonList(new DnsSelection.Result(configs.get(0),0,"timeout"))));
    }
    @Test public void probesAreBoundedAndLateWorkCannotSelectAnything() throws Exception {
        AtomicInteger active=new AtomicInteger(),maximum=new AtomicInteger();List<DnsConfig> configs=new ArrayList<>();for(int i=0;i<12;i++)configs.add(new DnsConfig(0,true));
        long began=System.nanoTime();List<DnsSelection.Result> results=DnsSelection.check(configs,c->{int n=active.incrementAndGet();maximum.accumulateAndGet(n,Math::max);try{Thread.sleep(1000);return 10;}finally{active.decrementAndGet();}},70);
        assertTrue(maximum.get()<=3);assertNull(DnsSelection.best(results));assertTrue(System.nanoTime()-began<TimeUnit.SECONDS.toNanos(2));
    }
    @Test public void automaticFailoverHasStreakCooldownAndAttemptLimits(){
        DnsSelection.Failover p=new DnsSelection.Failover();assertFalse(p.failed(0));p.success();assertFalse(p.failed(1));assertFalse(p.failed(2));assertTrue(p.failed(3));
        assertFalse(p.failed(4));assertFalse(p.failed(5));assertFalse(p.failed(6));assertTrue(p.failed(60003));
        assertFalse(p.failed(120003));assertFalse(p.failed(120004));assertTrue(p.failed(120005));
        assertFalse(p.failed(180005));assertFalse(p.failed(180006));assertFalse(p.failed(180007));assertTrue(p.failed(720006));
    }
    @Test public void dotMetadataAndProtocolAreExplicit(){
        assertEquals("dns.comss.one",Provider.at(0).dot);assertTrue(Provider.at(0).dotServers.length>0);
        assertEquals(DnsConfig.DOT,new DnsConfig(5,DnsConfig.DOT).protocol);assertTrue(new DnsConfig(5,DnsConfig.PLAIN).encrypted);
        try{new DnsConfig(0,99);fail();}catch(IllegalArgumentException expected){}
    }
    @Test public void failedDohDoesNotExcludeDotAndChangedSettingsCancelAutomaticCommit(){
        List<DnsConfig> choices=DnsSelection.alternatives(Collections.emptySet(),new DnsConfig(5,DnsConfig.DOH));assertTrue(choices.stream().anyMatch(c->c.provider==5&&c.protocol==DnsConfig.DOT));assertFalse(choices.stream().anyMatch(c->c.provider==5&&c.protocol==DnsConfig.DOH));
        DnsConfig next=new DnsConfig(6,true);assertTrue(DnsSelection.mayApplyAuto(true,Collections.emptySet(),next,100,99));assertFalse(DnsSelection.mayApplyAuto(false,Collections.emptySet(),next,100,99));assertFalse(DnsSelection.mayApplyAuto(true,Collections.singleton("6"),next,100,99));assertFalse(DnsSelection.mayApplyAuto(true,Collections.emptySet(),next,100,101));
    }
}
