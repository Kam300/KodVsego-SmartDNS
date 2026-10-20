package ru.kodvsego.smartdns;

import android.app.Application;
import android.content.Context;
import android.content.Intent;
import android.view.View;
import android.widget.*;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.*;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class) @Config(sdk=28) @LooperMode(LooperMode.Mode.PAUSED)
public class WidgetTest {
    @After public void reset(){DnsVpnService.active=false;DnsVpnService.connecting=false;DnsVpnService.current=null;}
    @Test public void compactToggleLaunchesConsentActivityWhenStopped() {
        Context context=RuntimeEnvironment.getApplication();DnsVpnService.active=false;DnsVpnService.connecting=false;
        View widget=DnsWidget.views(context,42,R.layout.widget_dns_compact).apply(context,new FrameLayout(context));
        assertTrue(widget.findViewById(R.id.widget_toggle).performClick());
        Intent intent=Shadows.shadowOf((Application)context).getNextStartedActivity();assertNotNull(intent);assertEquals(DnsWidget.CONNECT,intent.getAction());assertEquals(MainActivity.class.getName(),intent.getComponent().getClassName());
    }
    @Test public void activeWidgetStopsServiceDirectlyInsteadOfLaunchingActivity() {
        Context context=RuntimeEnvironment.getApplication();DnsVpnService.active=true;
        View widget=DnsWidget.views(context,43,R.layout.widget_dns).apply(context,new FrameLayout(context));widget.findViewById(R.id.widget_toggle).performClick();
        Intent intent=Shadows.shadowOf((Application)context).getNextStartedService();assertNotNull(intent);assertEquals(DnsVpnService.STOP,intent.getAction());
    }
    @Test public void compactButtonFitsMinimumWidgetHeight() {
        Context context=RuntimeEnvironment.getApplication();View widget=DnsWidget.views(context,44,R.layout.widget_dns_compact).apply(context,new FrameLayout(context));
        float density=context.getResources().getDisplayMetrics().density;int width=(int)(110*density),height=(int)(64*density);
        widget.measure(View.MeasureSpec.makeMeasureSpec(width,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(height,View.MeasureSpec.EXACTLY));widget.layout(0,0,width,height);
        View button=widget.findViewById(R.id.widget_toggle);assertTrue(button.getHeight()>0);assertTrue(button.getBottom()<=height-widget.getPaddingBottom());assertTrue(button.getTop()>=widget.getPaddingTop());
    }
}
