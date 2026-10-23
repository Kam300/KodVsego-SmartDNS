package ru.kodvsego.smartdns;

import android.content.Intent;
import android.os.Looper;
import android.os.SystemClock;
import android.view.MotionEvent;
import android.view.View;
import android.view.WindowInsets;
import android.widget.FrameLayout;
import android.widget.ScrollView;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.android.controller.*;
import org.robolectric.annotation.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicInteger;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk={28,35})
@LooperMode(LooperMode.Mode.PAUSED)
public class AndroidLifecycleTest {
    private ActivityController<MainActivity> activity;
    private ServiceController<DnsVpnService> service;
    private final CountDownLatch cleanupEntered=new CountDownLatch(1),releaseCleanup=new CountDownLatch(1);

    @Before public void before(){RuntimeEnvironment.getApplication().getSharedPreferences("updates",0).edit().putLong("lastCheck",System.currentTimeMillis()).apply();DnsVpnService.active=false;DnsVpnService.connecting=false;DnsVpnService.current=null;DnsVpnService.status="DNS выключен";}
    @After public void after(){releaseCleanup.countDown();if(activity!=null)activity.pause().stop().destroy();if(service!=null)service.destroy();}
    private MainActivity screen(){activity=Robolectric.buildActivity(MainActivity.class).setup();return activity.get();}
    private DnsVpnService blockedService(boolean connecting) {
        service=Robolectric.buildService(DnsVpnService.class).create();DnsVpnService vpn=service.get();
        vpn.sessions.install(vpn.sessions.begin(),()->{cleanupEntered.countDown();releaseCleanup.await(3,TimeUnit.SECONDS);});
        DnsVpnService.active=!connecting;DnsVpnService.connecting=connecting;DnsVpnService.status=connecting?"Подключаем…":"DNS включён";
        return vpn;
    }
    @Test public void powerButtonStillStopsVpnAfterBackground() throws Exception {
        blockedService(false);MainActivity app=screen();activity.pause().stop().start().resume().visible();
        long start=System.nanoTime();assertTrue(app.findViewById(R.id.dns_power).performClick());
        assertTrue("UI was blocked by teardown",System.nanoTime()-start<TimeUnit.SECONDS.toNanos(1));
        assertFalse(DnsVpnService.active);assertFalse(DnsVpnService.connecting);assertTrue(cleanupEntered.await(1,TimeUnit.SECONDS));
        assertTrue(app.findViewById(R.id.add_widget).performClick());
    }
    @Test public void cancellingPendingConnectionRejectsLateCompletion() {
        DnsVpnService vpn=blockedService(true);long old=vpn.sessions.begin();MainActivity app=screen();
        app.findViewById(R.id.dns_power).performClick();assertFalse(DnsVpnService.connecting);assertFalse(vpn.sessions.owns(old));
        assertFalse(vpn.sessions.install(old,()->{}));
    }
    @Test public void notificationStopDoesNotBlockOnResourceLock() throws Exception {
        DnsVpnService vpn=blockedService(false);long start=System.nanoTime();
        vpn.onStartCommand(new Intent(vpn,DnsVpnService.class).setAction(DnsVpnService.STOP),0,2);
        assertTrue(System.nanoTime()-start<TimeUnit.SECONDS.toNanos(1));assertFalse(DnsVpnService.active);assertTrue(cleanupEntered.await(1,TimeUnit.SECONDS));
    }
    @Test public void staleWidgetConnectDoesNotTurnOffActiveVpn() {
        blockedService(false);
        activity=Robolectric.buildActivity(MainActivity.class,new Intent().setAction(DnsWidget.CONNECT)).setup();
        Shadows.shadowOf(Looper.getMainLooper()).idle();
        assertTrue(DnsVpnService.active);
        assertNull(activity.get().getIntent().getAction());
    }
    @Test public void pressedViewAcceptsTouchAfterPauseAndResume() {
        MainActivity app=screen();PowerButton power=app.findViewById(R.id.dns_power);
        power.setPressed(true);power.setScaleX(.97f);power.setScaleY(.97f);activity.pause().stop().start().resume().visible();
        assertEquals(1,power.getScaleX(),.001);assertEquals(1,power.getScaleY(),.001);assertFalse(power.isPressed());
        AtomicInteger clicks=new AtomicInteger();power.setOnClickListener(v->clicks.incrementAndGet());
        power.layout(0,0,210,210);long now=SystemClock.uptimeMillis();
        MotionEvent down=MotionEvent.obtain(now,now,MotionEvent.ACTION_DOWN,100,100,0),up=MotionEvent.obtain(now,now+20,MotionEvent.ACTION_UP,100,100,0);
        try{power.dispatchTouchEvent(down);power.dispatchTouchEvent(up);Shadows.shadowOf(Looper.getMainLooper()).idle();assertEquals(1,clicks.get());}finally{down.recycle();up.recycle();}
    }
    @Test public void providerSheetSavesSelectionAndCloses() {
        MainActivity app=screen();app.findViewById(R.id.dns_provider).performClick();
        android.app.Dialog dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();assertTrue(dialog.isShowing());
        dialog.findViewById(R.id.provider_sheet).findViewWithTag("provider-5").performClick();
        assertFalse(dialog.isShowing());assertEquals(5,app.getSharedPreferences("smartdns",0).getInt("provider",-1));
        assertEquals("DNS-AI",((android.widget.TextView)app.findViewById(R.id.dns_provider)).getText().toString());
        activity.pause().stop().start().resume();assertEquals("DNS-AI",((android.widget.TextView)app.findViewById(R.id.dns_provider)).getText().toString());
    }
    @Test public void providerCannotBeChangedDuringConnection() {
        blockedService(true);MainActivity app=screen();assertFalse(app.findViewById(R.id.dns_provider).isEnabled());
    }
    @Test public void unsupportedEncryptionIsExplicitAndMagicDnsRemainsSelectable() {
        MainActivity app=screen();
        for(int provider:new int[]{2,3,6}){
            app.findViewById(R.id.dns_provider).performClick();android.app.Dialog dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();
            dialog.findViewById(R.id.provider_sheet).findViewWithTag("provider-"+provider).performClick();
            android.widget.Switch encryption=app.findViewById(R.id.encrypted_dns_switch);
            assertEquals(provider==6,encryption.isEnabled());
            assertEquals(provider!=6,encryption.getText().toString().contains("недоступен"));
            assertFalse(encryption.isChecked());
        }
    }
    @Test @Config(sdk=35) public void contentStaysOutsideSystemBarsWhenScrolled() {
        MainActivity app=screen();ScrollView scroll=(ScrollView)((FrameLayout)app.findViewById(android.R.id.content)).getChildAt(0);
        WindowInsets insets=new WindowInsets.Builder().setInsets(WindowInsets.Type.systemBars(),android.graphics.Insets.of(0,28,0,32)).build();
        scroll.dispatchApplyWindowInsets(insets);assertEquals(28,scroll.getPaddingTop());assertEquals(32,scroll.getPaddingBottom());assertTrue(scroll.getClipToPadding());
        assertFalse(scroll.isVerticalScrollBarEnabled());assertFalse(scroll.isHorizontalScrollBarEnabled());
        scroll.scrollTo(0,600);assertEquals(28,scroll.getPaddingTop());assertEquals(1,scroll.getChildAt(0).getAlpha(),.001);
    }
    @Test public void restoredProvidersVisibleAndSavedChoicePreserved() {
        android.content.Context context=RuntimeEnvironment.getApplication();
        context.getSharedPreferences("smartdns",0).edit().putInt("provider",1).commit();
        MainActivity app=screen();
        assertEquals(1,context.getSharedPreferences("smartdns",0).getInt("provider",-1));
        app.findViewById(R.id.dns_provider).performClick();
        android.app.Dialog dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();
        assertNotNull(dialog.findViewById(R.id.provider_sheet).findViewWithTag("provider-1"));
        assertNotNull(dialog.findViewById(R.id.provider_sheet).findViewWithTag("provider-2"));
        assertNotNull(dialog.findViewById(R.id.provider_sheet).findViewWithTag("provider-6"));
        dialog.dismiss();
    }
}
