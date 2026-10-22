package ru.kodvsego.smartdns;

import android.app.Dialog;
import android.content.Context;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.view.*;
import android.widget.TextView;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.android.controller.ActivityController;
import org.robolectric.annotation.*;
import java.io.*;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class) @Config(sdk={28,35}) @LooperMode(LooperMode.Mode.PAUSED)
public class UpdateUiTest {
    private ActivityController<MainActivity> activity;
    @Before public void before(){RuntimeEnvironment.getApplication().getSharedPreferences("updates",0).edit().clear().putLong("lastCheck",System.currentTimeMillis()).apply();}
    @After public void after(){if(activity!=null)activity.pause().stop().destroy();}
    private View findText(View view,String text){
        if(view instanceof TextView&&((TextView)view).getText().toString().equals(text))return view;
        if(view instanceof ViewGroup)for(int i=0;i<((ViewGroup)view).getChildCount();i++){View found=findText(((ViewGroup)view).getChildAt(i),text);if(found!=null)return found;}return null;
    }
    @Test public void availableUpdateShowsPromptLaterDefersAndManualReopens(){
        activity=Robolectric.buildActivity(MainActivity.class).setup();MainActivity app=activity.get();UpdateController update=UpdateController.get(app);
        update.release=new AndroidUpdate.Release("9.0.0","future.apk","",new String(new char[64]).replace('\0','a'),"",10);update.phase=UpdateController.Phase.AVAILABLE;
        activity.pause().stop().start().resume();Dialog dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();assertTrue(dialog.isShowing());
        View root=dialog.getWindow().getDecorView();assertNotNull(findText(root,"Доступно обновление"));assertNotNull(findText(root,"Обновить"));assertNotNull(findText(root,"Позже"));
        findText(root,"Позже").performClick();Shadows.shadowOf(android.os.Looper.getMainLooper()).idle();assertFalse(dialog.isShowing());assertFalse(update.shouldPrompt());
        activity.pause().stop().start().resume();assertFalse(dialog.isShowing());
        app.findViewById(R.id.check_updates).performClick();assertTrue(org.robolectric.shadows.ShadowDialog.getLatestDialog().isShowing());
        assertEquals(UpdateController.Phase.AVAILABLE,update.phase); // No APK is downloaded without acceptance.
    }
    @Test public void providerOnlyExposesOneReadOnlyFile()throws Exception{
        Context context=RuntimeEnvironment.getApplication();UpdateFileProvider provider=Robolectric.buildContentProvider(UpdateFileProvider.class).create().get();
        File directory=new File(context.getCacheDir(),"updates");directory.mkdirs();File apk=new File(directory,"verified.apk");try(FileOutputStream out=new FileOutputStream(apk)){out.write(new byte[]{1,2,3});}
        Uri uri=UpdateFileProvider.uri(context);assertEquals("application/vnd.android.package-archive",provider.getType(uri));
        try(ParcelFileDescriptor descriptor=provider.openFile(uri,"r");FileInputStream in=new FileInputStream(descriptor.getFileDescriptor())){assertEquals(1,in.read());}
        try{provider.openFile(uri,"w");fail();}catch(FileNotFoundException expected){}
        try{provider.openFile(Uri.parse("content://"+context.getPackageName()+".updates/../private"),"r");fail();}catch(SecurityException expected){}
        try{provider.openFile(uri.buildUpon().appendQueryParameter("file","secret").build(),"r");fail();}catch(SecurityException expected){}
    }
    private void await(java.util.function.BooleanSupplier ready)throws Exception{
        long deadline=System.nanoTime()+java.util.concurrent.TimeUnit.SECONDS.toNanos(5);
        while(!ready.getAsBoolean()&&System.nanoTime()<deadline){Shadows.shadowOf(android.os.Looper.getMainLooper()).idle();Thread.sleep(5);}
        assertTrue("Expected UI transition did not complete",ready.getAsBoolean());
    }
    @Test public void permissionReturnOpensInstallerWithOnlyContentReadGrant()throws Exception{
        activity=Robolectric.buildActivity(MainActivity.class).setup();MainActivity app=activity.get();UpdateController update=UpdateController.get(app);
        byte[] bytes="signed-apk-fixture".getBytes("UTF-8");update.readyFile.getParentFile().mkdirs();try(FileOutputStream output=new FileOutputStream(update.readyFile)){output.write(bytes);}
        update.release=new AndroidUpdate.Release("9.0.0","future.apk","",AndroidUpdate.hex(java.security.MessageDigest.getInstance("SHA-256").digest(bytes)),"",bytes.length);update.phase=UpdateController.Phase.READY;
        android.content.pm.SigningInfo signing=new android.content.pm.SigningInfo();Shadows.shadowOf(signing).setSignatures(new android.content.pm.Signature[]{new android.content.pm.Signature(new byte[]{7,8,9})});
        android.content.pm.PackageInfo installed=new android.content.pm.PackageInfo();installed.packageName=app.getPackageName();installed.versionName=BuildConfig.VERSION_NAME;installed.setLongVersionCode(BuildConfig.VERSION_CODE);installed.signingInfo=signing;
        org.robolectric.shadows.ShadowPackageManager manager=Shadows.shadowOf(app.getPackageManager());manager.installPackage(installed);manager.setCanRequestPackageInstalls(false);
        android.content.pm.PackageInfo candidate=new android.content.pm.PackageInfo();candidate.packageName=app.getPackageName();candidate.versionName="9.0.0";candidate.setLongVersionCode(BuildConfig.VERSION_CODE+1);candidate.signingInfo=signing;candidate.applicationInfo=new android.content.pm.ApplicationInfo();candidate.applicationInfo.minSdkVersion=26;
        manager.setPackageArchiveInfo(update.readyFile.getAbsolutePath(),candidate);
        app.findViewById(R.id.check_updates).performClick();
        await(()->{Dialog d=org.robolectric.shadows.ShadowDialog.getLatestDialog();return d!=null&&d.isShowing()&&findText(d.getWindow().getDecorView(),"Открыть настройки")!=null;});
        Dialog permission=org.robolectric.shadows.ShadowDialog.getLatestDialog();findText(permission.getWindow().getDecorView(),"Открыть настройки").performClick();
        android.content.Intent settings=Shadows.shadowOf(app).getNextStartedActivityForResult().intent;assertEquals(android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,settings.getAction());assertEquals("package:"+app.getPackageName(),settings.getDataString());
        activity.pause().stop();manager.setCanRequestPackageInstalls(true);activity.start().resume();
        await(()->update.phase==UpdateController.Phase.READY);
        android.content.Intent installer=Shadows.shadowOf((android.app.Application)app.getApplicationContext()).getNextStartedActivity();
        assertNotNull(installer);assertEquals(android.content.Intent.ACTION_INSTALL_PACKAGE,installer.getAction());assertEquals(UpdateFileProvider.uri(app),installer.getData());
        assertEquals(android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION,installer.getFlags()&android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION);assertEquals(0,installer.getFlags()&android.content.Intent.FLAG_GRANT_WRITE_URI_PERMISSION);assertNotNull(installer.getClipData());
    }
}
