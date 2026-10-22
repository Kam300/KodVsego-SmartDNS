package ru.kodvsego.smartdns;

import android.content.Context;
import android.content.pm.*;
import org.json.*;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.Config;
import java.io.*;
import java.security.MessageDigest;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class) @Config(sdk=28)
public class AndroidUpdateTest {
    private static final String HASH=new String(new char[64]).replace('\0','a');
    interface Work{void run()throws Exception;}
    private void fails(Work work)throws Exception{try{work.run();fail("Invalid update was accepted");}catch(IOException|IllegalArgumentException expected){}}
    private JSONObject asset(String version)throws Exception{
        String name="KodVsego-SmartDNS-Android-"+version+".apk";
        return new JSONObject().put("name",name).put("size",10).put("state","uploaded").put("digest","sha256:"+HASH).put("browser_download_url",url(version,name));
    }
    private String url(String tag,String name){return "https://github.com/Kam300/KodVsego-SmartDNS/releases/download/"+tag+"/"+name;}
    private JSONObject release(JSONObject... assets)throws Exception{JSONArray list=new JSONArray();for(JSONObject asset:assets)list.put(asset);return new JSONObject().put("draft",false).put("prerelease",true).put("tag_name","android-test").put("assets",list);}
    @Test public void androidPrereleaseIsDetectedByApkVersion()throws Exception{
        AndroidUpdate.Release found=AndroidUpdate.find(new JSONArray().put(release(asset("1.9.0"))).put(release(asset("1.10.0"))),"1.5.0");
        assertEquals("1.10.0",found.version);assertEquals(HASH,found.hash);
    }
    @Test public void currentAndOlderVersionsAreNotUpdates()throws Exception{assertNull(AndroidUpdate.find(new JSONArray().put(release(asset("1.4.1"),asset("1.5.0"))),"1.5.0"));}
    @Test public void windowsOnlyReleaseIsIgnored()throws Exception{JSONObject exe=asset("9.0.0").put("name","KodVsego_SmartDNS.9.0.0.exe");assertNull(AndroidUpdate.find(new JSONArray().put(release(exe)),"1.5.0"));}
    @Test public void draftAndIncompleteUploadsAreIgnored()throws Exception{assertNull(AndroidUpdate.find(new JSONArray().put(release(asset("2.0.0")).put("draft",true)).put(release(asset("2.1.0").put("state","new"))),"1.5.0"));}
    @Test public void arbitraryHostAndWrongRepositoryAreRejected()throws Exception{
        for(String link:new String[]{"http://github.com/Kam300/KodVsego-SmartDNS/releases/download/a/x.apk","https://evil.example/x.apk","https://github.com/other/project/releases/download/a/x.apk","https://user:password@github.com/Kam300/KodVsego-SmartDNS/releases/download/a/x.apk"})
            fails(()->AndroidUpdate.find(new JSONArray().put(release(asset("2.0.0").put("browser_download_url",link))),"1.5.0"));
    }
    @Test public void missingHashAndInvalidSizeAreRejected()throws Exception{
        fails(()->AndroidUpdate.find(new JSONArray().put(release(asset("2.0.0").put("digest",""))),"1.5.0"));
        fails(()->AndroidUpdate.find(new JSONArray().put(release(asset("2.0.0").put("size",AndroidUpdate.MAX_APK+1))),"1.5.0"));
    }
    @Test public void checksumFileFallbackIsExactAndUnambiguous()throws Exception{
        String name="KodVsego-SmartDNS-Android-2.0.0.apk";
        JSONObject sums=new JSONObject().put("name","SHA256SUMS.txt").put("state","uploaded").put("browser_download_url",url("2.0.0","SHA256SUMS.txt"));
        AndroidUpdate.Release found=AndroidUpdate.find(new JSONArray().put(release(asset("2.0.0").put("digest",""),sums)),"1.5.0");assertFalse(found.checksumUrl.isEmpty());
        assertEquals(HASH,AndroidUpdate.checksum(HASH+"  "+name,name));
        assertEquals(HASH,AndroidUpdate.checksum("\uFEFF"+HASH+"  *"+name+"\r\n",name));
        fails(()->AndroidUpdate.checksum(HASH+"  other.apk",name));fails(()->AndroidUpdate.checksum(HASH+"  "+name+"\n"+HASH+"  "+name,name));
    }
    @Test public void duplicateAssetsAreRejected()throws Exception{fails(()->AndroidUpdate.find(new JSONArray().put(release(asset("2.0.0"),asset("2.0.0"))),"1.5.0"));}
    @Test public void onlyGithubAssetRedirectsAreAllowed(){assertTrue(AndroidUpdate.allowedRedirect("https://release-assets.githubusercontent.com/a?signature=b"));assertFalse(AndroidUpdate.allowedRedirect("https://release-assets.githubusercontent.com.evil.example/a"));assertFalse(AndroidUpdate.allowedRedirect("http://objects.githubusercontent.com/a"));}
    @Test public void packageStreamRequiresExactSizeAndHash()throws Exception{
        byte[] bytes="signed-package-data".getBytes("UTF-8");String digest=AndroidUpdate.hex(MessageDigest.getInstance("SHA-256").digest(bytes));
        ByteArrayOutputStream output=new ByteArrayOutputStream();AndroidUpdate.copyVerified(new ByteArrayInputStream(bytes),output,bytes.length,digest,new AndroidUpdate.Cancel(),n->{});assertArrayEquals(bytes,output.toByteArray());
        fails(()->AndroidUpdate.copyVerified(new ByteArrayInputStream(bytes),new ByteArrayOutputStream(),bytes.length,HASH,new AndroidUpdate.Cancel(),n->{}));
        fails(()->AndroidUpdate.copyVerified(new ByteArrayInputStream(bytes),new ByteArrayOutputStream(),bytes.length-1,digest,new AndroidUpdate.Cancel(),n->{}));
        fails(()->AndroidUpdate.copyVerified(new ByteArrayInputStream(bytes),new ByteArrayOutputStream(),bytes.length+1,digest,new AndroidUpdate.Cancel(),n->{}));
    }
    @Test public void cancelledDownloadCannotBeVerified()throws Exception{AndroidUpdate.Cancel cancel=new AndroidUpdate.Cancel();cancel.cancel();fails(()->AndroidUpdate.copyVerified(new ByteArrayInputStream(new byte[]{1}),new ByteArrayOutputStream(),1,HASH,cancel,n->{}));}
    private PackageInfo info(String name,String version,int code,int minSdk,String signature){
        PackageInfo info=new PackageInfo();info.packageName=name;info.versionName=version;info.versionCode=code;if(android.os.Build.VERSION.SDK_INT>=28)info.setLongVersionCode(code);info.applicationInfo=new ApplicationInfo();info.applicationInfo.packageName=name;info.applicationInfo.minSdkVersion=minSdk;
        Signature[] signatures={new Signature(signature.getBytes())};if(android.os.Build.VERSION.SDK_INT>=28){info.signingInfo=new SigningInfo();Shadows.shadowOf(info.signingInfo).setSignatures(signatures);}else info.signatures=signatures;return info;
    }
    @Test @Config(sdk={26,28,35}) public void installerRequiresSamePackageNewCodeCompatibleSdkAndSigner()throws Exception{
        Context context=RuntimeEnvironment.getApplication();org.robolectric.shadows.ShadowPackageManager pm=Shadows.shadowOf(context.getPackageManager());
        pm.installPackage(info(context.getPackageName(),BuildConfig.VERSION_NAME,BuildConfig.VERSION_CODE,26,"original"));
        File file=new File(context.getCacheDir(),"candidate.apk");AndroidUpdate.Release release=new AndroidUpdate.Release("2.0.0","x.apk","",HASH,"",10);
        PackageInfo good=info(context.getPackageName(),"2.0.0",BuildConfig.VERSION_CODE+1,26,"original");pm.setPackageArchiveInfo(file.getAbsolutePath(),good);AndroidUpdate.inspect(context,file,release);
        for(PackageInfo invalid:new PackageInfo[]{info("foreign.app","2.0.0",999,26,"original"),info(context.getPackageName(),"2.0.0",BuildConfig.VERSION_CODE,26,"original"),info(context.getPackageName(),"2.0.0",999,99,"original"),info(context.getPackageName(),"2.0.0",999,26,"different"),info(context.getPackageName(),"1.0.0",999,26,"original")}){
            pm.setPackageArchiveInfo(file.getAbsolutePath(),invalid);fails(()->AndroidUpdate.inspect(context,file,release));
        }
    }
}
