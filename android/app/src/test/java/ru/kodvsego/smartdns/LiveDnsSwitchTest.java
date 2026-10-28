package ru.kodvsego.smartdns;

import android.content.Intent;
import android.os.*;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.*;
import org.robolectric.android.controller.ServiceController;
import java.io.*;
import java.lang.reflect.Constructor;
import java.util.concurrent.*;
import java.util.function.BooleanSupplier;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class) @Config(sdk=35) @LooperMode(LooperMode.Mode.PAUSED)
public class LiveDnsSwitchTest {
    private ServiceController<DnsVpnService> controller;private DnsVpnService service;private ParcelFileDescriptor[] pipe;private AutoCloseable original;
    @Before public void before() throws Exception {
        DnsVpnService.active=false;DnsVpnService.connecting=false;DnsVpnService.switching=false;
        controller=Robolectric.buildService(DnsVpnService.class).create();service=controller.get();pipe=ParcelFileDescriptor.createPipe();
        Class<?> type=java.util.Arrays.stream(DnsVpnService.class.getDeclaredClasses()).filter(c->c.getSimpleName().equals("Tunnel")).findFirst().get();Constructor<?> ctor=type.getDeclaredConstructors()[0];ctor.setAccessible(true);original=(AutoCloseable)ctor.newInstance(service,pipe[0],new DnsConfig(0,true));
        service.sessions.install(service.sessions.begin(),original);DnsVpnService.current=new DnsConfig(0,true);DnsVpnService.active=true;
    }
    @After public void after() throws Exception {controller.destroy();pipe[1].close();DnsVpnService.active=false;DnsVpnService.connecting=false;DnsVpnService.switching=false;DnsVpnService.current=null;}
    private Intent change(){return new Intent(service,DnsVpnService.class).setAction(DnsVpnService.SWITCH).putExtra("provider",5).putExtra("protocol",DnsConfig.DOT);}
    private void await(BooleanSupplier done) throws Exception {long until=System.nanoTime()+TimeUnit.SECONDS.toNanos(3);while(!done.getAsBoolean()&&System.nanoTime()<until){Shadows.shadowOf(Looper.getMainLooper()).idle();Thread.sleep(5);}assertTrue(done.getAsBoolean());}
    @Test public void successChangesOnlyConfigurationAfterProbeAndReusesTunnel() throws Exception {
        CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);service.switchProbe=c->{entered.countDown();release.await(2,TimeUnit.SECONDS);};service.onStartCommand(change(),0,1);assertTrue(entered.await(1,TimeUnit.SECONDS));assertEquals(0,DnsVpnService.current.provider);assertTrue(DnsVpnService.active);assertSame(original,service.sessions.current());release.countDown();await(()->!DnsVpnService.switching);
        assertEquals(5,DnsVpnService.current.provider);assertEquals(DnsConfig.DOT,DnsVpnService.current.protocol);assertSame(original,service.sessions.current());assertTrue(DnsVpnService.active);assertEquals(DnsConfig.DOT,service.getSharedPreferences("smartdns",0).getInt("protocol",-1));
    }
    @Test public void failedProbeRetainsOriginalTunnelAndProvider() throws Exception {
        service.switchProbe=c->{throw new IOException("fixture denied");};service.onStartCommand(change(),0,1);await(()->!DnsVpnService.switching);assertEquals(0,DnsVpnService.current.provider);assertSame(original,service.sessions.current());assertTrue(DnsVpnService.active);assertTrue(DnsVpnService.status.contains("сохранён"));assertTrue(DnsVpnService.switchError.contains("fixture denied"));assertNull(DnsVpnService.switchTarget);
    }
    @Test public void deadlineRejectsLateCompletionAndKeepsOriginal() throws Exception {
        CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);service.switchProbe=c->{entered.countDown();while(release.getCount()>0)try{release.await(50,TimeUnit.MILLISECONDS);}catch(InterruptedException ignored){}};
        service.onStartCommand(change(),0,1);assertTrue(entered.await(1,TimeUnit.SECONDS));assertEquals(5,DnsVpnService.switchTarget.provider);
        Shadows.shadowOf(Looper.getMainLooper()).idleFor(12,TimeUnit.SECONDS);assertFalse(DnsVpnService.switching);assertEquals(0,DnsVpnService.current.provider);assertTrue(DnsVpnService.switchError.contains("12 секунд"));
        release.countDown();Thread.sleep(60);Shadows.shadowOf(Looper.getMainLooper()).idle();assertEquals(0,DnsVpnService.current.provider);assertSame(original,service.sessions.current());
    }
    @Test public void stopDiscardsLateSwitchCompletion() throws Exception {
        CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);service.switchProbe=c->{entered.countDown();while(release.getCount()>0)try{release.await(100,TimeUnit.MILLISECONDS);}catch(InterruptedException ignored){}};service.onStartCommand(change(),0,1);assertTrue(entered.await(1,TimeUnit.SECONDS));service.onStartCommand(new Intent(service,DnsVpnService.class).setAction(DnsVpnService.STOP),0,2);release.countDown();await(()->!DnsVpnService.active);Thread.sleep(60);Shadows.shadowOf(Looper.getMainLooper()).idle();assertNull(DnsVpnService.current);assertNull(service.sessions.current());assertFalse(DnsVpnService.switching);
    }
}
