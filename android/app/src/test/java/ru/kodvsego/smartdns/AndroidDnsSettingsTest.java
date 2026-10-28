package ru.kodvsego.smartdns;

import android.app.Dialog;
import android.content.*;
import android.view.*;
import android.widget.*;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.*;
import org.robolectric.android.controller.ActivityController;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class) @Config(sdk=35) @LooperMode(LooperMode.Mode.PAUSED)
public class AndroidDnsSettingsTest {
    private ActivityController<MainActivity> activity;private MainActivity app;
    @Before public void before(){RuntimeEnvironment.getApplication().getSharedPreferences("catalog-updates",0).edit().putLong("lastCheck",System.currentTimeMillis()).apply();DnsVpnService.status="DNS выключен";DnsVpnService.switchError="";DnsVpnService.switchTarget=null;DnsVpnService.active=false;DnsVpnService.connecting=false;DnsVpnService.switching=false;DnsVpnService.current=null;RuntimeEnvironment.getApplication().getSharedPreferences("updates",0).edit().putLong("lastCheck",System.currentTimeMillis()).apply();activity=Robolectric.buildActivity(MainActivity.class).setup();app=activity.get();}
    @After public void after(){DnsVpnService.status="DNS выключен";DnsVpnService.switchError="";DnsVpnService.switchTarget=null;DnsVpnService.active=false;DnsVpnService.switching=false;DnsVpnService.current=null;activity.pause().stop().destroy();}
    private View tag(String tag){View quick=app.findViewById(android.R.id.content).findViewWithTag(tag);if(quick!=null)return quick;app.findViewById(android.R.id.content).findViewWithTag("app-settings").performClick();return org.robolectric.shadows.ShadowDialog.getLatestDialog().getWindow().getDecorView().findViewWithTag(tag);}
    private Button button(View view,String label){if(view instanceof Button&&((Button)view).getText().toString().equals(label))return (Button)view;if(view instanceof ViewGroup)for(int i=0;i<((ViewGroup)view).getChildCount();i++){Button found=button(((ViewGroup)view).getChildAt(i),label);if(found!=null)return found;}return null;}
    @Test public void quickControlsStayOnMainAndMaintenanceIsInGear(){
        View root=app.findViewById(android.R.id.content);assertNotNull(root.findViewWithTag("app-settings"));
        assertNotNull(root.findViewWithTag("dns-protocol"));assertNotNull(root.findViewWithTag("dns-auto"));assertNotNull(root.findViewWithTag("theme-toggle"));assertNotNull(root.findViewWithTag("provider-checker"));assertNull(app.findViewById(R.id.add_widget));assertNull(app.findViewById(R.id.check_updates));
        root.findViewWithTag("app-settings").performClick();View menu=org.robolectric.shadows.ShadowDialog.getLatestDialog().getWindow().getDecorView();
        assertNull(menu.findViewWithTag("dns-protocol"));assertNotNull(menu.findViewById(R.id.add_widget));assertNotNull(menu.findViewById(R.id.check_updates));assertNotNull(button(menu,"Скачать журнал (.txt)"));
    }
    @Test public void catalogOptionsAreInlineAndPersistToggle(){
        app.findViewById(android.R.id.content).findViewWithTag("app-settings").performClick();Dialog menu=org.robolectric.shadows.ShadowDialog.getLatestDialog();View root=menu.getWindow().getDecorView();
        assertNull(button(root,"Каталог DNS · обновление адресов"));Switch toggle=root.findViewWithTag("catalog-auto");assertNotNull(toggle);toggle.setChecked(false);assertFalse(app.getSharedPreferences("catalog-updates",0).getBoolean("automatic",true));
        assertNotNull(root.findViewWithTag("catalog-status"));assertNotNull(root.findViewWithTag("catalog-check"));assertSame(menu,org.robolectric.shadows.ShadowDialog.getLatestDialog());
    }
    @Test public void dotChoiceSurvivesPreferenceRoundTrip(){tag("protocol-2").performClick();assertEquals(DnsConfig.DOT,DnsConfig.saved(app.getSharedPreferences("smartdns",0),0).protocol);assertTrue(tag("protocol-2").isSelected());tag("protocol-1").performClick();assertEquals(DnsConfig.DOH,DnsConfig.saved(app.getSharedPreferences("smartdns",0),0).protocol);}
    @Test public void permissionErrorHintDoesNotBlameServer(){assertTrue(MainActivity.connectionErrorHint("Не удалось защитить DoT-сокет").contains("Android запретил"));assertTrue(MainActivity.connectionErrorHint("Binding socket failed: EPERM").contains("Постоянный VPN"));}

    @Test public void connectionFailureHasShortTitleAndReadableHint(){DnsVpnService.status="Ошибка подключения: Не удалось защитить DoT-сокет";org.robolectric.util.ReflectionHelpers.callInstanceMethod(app,"updateState");TextView title=org.robolectric.util.ReflectionHelpers.getField(app,"state");assertEquals("Не удалось подключиться",title.getText().toString());TextView detail=app.findViewById(android.R.id.content).findViewWithTag("connection-detail");assertEquals(View.VISIBLE,detail.getVisibility());assertTrue(detail.getText().toString().contains("Android запретил"));}
    @Test public void providerVisibilityIsDraftUntilSaved(){tag("dns-settings").performClick();Dialog dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();((CheckBox)dialog.getWindow().getDecorView().findViewWithTag("visibility-5")).setChecked(false);button(dialog.getWindow().getDecorView(),"Отмена").performClick();assertFalse(app.getSharedPreferences("smartdns",0).getStringSet("hiddenProviders",java.util.Collections.emptySet()).contains("5"));
        tag("dns-settings").performClick();dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();((CheckBox)dialog.getWindow().getDecorView().findViewWithTag("visibility-5")).setChecked(false);button(dialog.getWindow().getDecorView(),"Сохранить").performClick();app.findViewById(R.id.dns_provider).performClick();dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();assertNull(dialog.findViewById(R.id.provider_sheet).findViewWithTag("provider-5"));}
    @Test public void activeProviderChangeRequestsSwitchButKeepsOldSelectionBeforeAck(){DnsVpnService.current=new DnsConfig(0,true);DnsVpnService.active=true;app.findViewById(R.id.dns_provider).performClick();Dialog dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();dialog.findViewById(R.id.provider_sheet).findViewWithTag("provider-6").performClick();Intent intent=Shadows.shadowOf(RuntimeEnvironment.getApplication()).getNextStartedService();assertNotNull(intent);assertEquals(DnsVpnService.SWITCH,intent.getAction());assertEquals(6,intent.getIntExtra("provider",-1));assertTrue(((TextView)app.findViewById(R.id.dns_provider)).getText().toString().contains("проверяем"));assertEquals(0,DnsVpnService.current.provider);}
    @Test public void switchFailureRemainsVisibleWhenClockRestoresConnectedStatus(){DnsVpnService.switchError="Не удалось выбрать MagicDNS. Работает прежний DNS: Comss.one";DnsVpnService.status="DNS включён · Comss.one";org.robolectric.util.ReflectionHelpers.callInstanceMethod(app,"updateState");TextView detail=app.findViewById(android.R.id.content).findViewWithTag("connection-detail");assertEquals(View.VISIBLE,detail.getVisibility());assertTrue(detail.getText().toString().contains("MagicDNS"));}
    @Test public void journalRedactsSecretsAndRetainsErrors(){String unique="failure-"+System.nanoTime();AppJournal.record("fixture",unique+" api_key=private-key token=private-token https://example.com/check?secret=private-query");String report=AppJournal.report();assertTrue(report.contains(unique));assertFalse(report.contains("private-key"));assertFalse(report.contains("private-token"));assertFalse(report.contains("private-query"));}
    @Test public void documentExportCanResumeAfterActivityRecreation() throws Exception {
        AppJournal.record("fixture","export-after-rotation");java.io.File file=new java.io.File(app.getCacheDir(),"journal-export.txt");
        app.onActivityResult(402,android.app.Activity.RESULT_OK,new Intent().setData(android.net.Uri.fromFile(file)));
        assertTrue(new String(java.nio.file.Files.readAllBytes(file.toPath()),java.nio.charset.StandardCharsets.UTF_8).contains("export-after-rotation"));
    }
}
