package ru.kodvsego.smartdns;

import android.content.Context;
import android.content.pm.*;
import android.os.Build;
import okhttp3.*;
import org.json.*;
import java.io.*;
import java.security.MessageDigest;
import java.util.*;
import java.util.concurrent.TimeUnit;
import java.util.regex.*;

final class AndroidUpdate {
    static final String REPO="Kam300/KodVsego-SmartDNS";
    static final String API="https://api.github.com/repos/"+REPO+"/releases?per_page=20&page=";
    static final long MAX_APK=64L*1024*1024;
    private static final Pattern APK=Pattern.compile("KodVsego-SmartDNS-Android-(\\d+\\.\\d+\\.\\d+)\\.apk");
    private static final OkHttpClient HTTP=new OkHttpClient.Builder().followRedirects(false).followSslRedirects(false)
        .connectTimeout(8,TimeUnit.SECONDS).readTimeout(20,TimeUnit.SECONDS).callTimeout(120,TimeUnit.SECONDS).build();
    static final class Release {
        final String version,name,url,checksumUrl;String hash;final long size;
        Release(String version,String name,String url,String hash,String checksumUrl,long size){this.version=version;this.name=name;this.url=url;this.hash=hash;this.checksumUrl=checksumUrl;this.size=size;}
        JSONObject json()throws JSONException{return new JSONObject().put("version",version).put("name",name).put("url",url).put("hash",hash).put("checksumUrl",checksumUrl).put("size",size);}
        static Release restore(String json)throws JSONException{JSONObject value=new JSONObject(json);return new Release(value.getString("version"),value.getString("name"),value.getString("url"),value.getString("hash"),value.getString("checksumUrl"),value.getLong("size"));}
    }
    static final class Cancel {
        volatile boolean cancelled;volatile Call call;
        void cancel(){cancelled=true;Call pending=call;if(pending!=null)pending.cancel();}
        void check()throws IOException{if(cancelled||Thread.currentThread().isInterrupted())throw new IOException("Загрузка отменена");}
    }
    interface Progress {void bytes(long count);}
    static int compare(String left,String right){
        if(!left.matches("\\d{1,9}\\.\\d{1,9}\\.\\d{1,9}")||!right.matches("\\d{1,9}\\.\\d{1,9}\\.\\d{1,9}"))throw new IllegalArgumentException("Некорректная версия");
        String[] a=left.split("\\."),b=right.split("\\.");for(int i=0;i<3;i++){int c=Integer.compare(Integer.parseInt(a[i]),Integer.parseInt(b[i]));if(c!=0)return c;}return 0;
    }
    static boolean allowedAsset(String url,String name){
        HttpUrl parsed=HttpUrl.parse(url);if(parsed==null||!parsed.isHttps()||parsed.port()!=443||!parsed.username().isEmpty()||!parsed.password().isEmpty()||parsed.fragment()!=null||parsed.query()!=null||!parsed.host().equals("github.com"))return false;
        List<String> p=parsed.pathSegments();return p.size()==6&&p.get(0).equals("Kam300")&&p.get(1).equals("KodVsego-SmartDNS")&&p.get(2).equals("releases")&&p.get(3).equals("download")&&!p.get(4).isEmpty()&&p.get(5).equals(name);
    }
    static boolean allowedRedirect(String url){
        HttpUrl parsed=HttpUrl.parse(url);return parsed!=null&&parsed.isHttps()&&parsed.port()==443&&parsed.username().isEmpty()&&parsed.password().isEmpty()&&parsed.fragment()==null&&
            (parsed.host().equals("release-assets.githubusercontent.com")||parsed.host().equals("objects.githubusercontent.com"));
    }
    static Release find(JSONArray releases,String installed)throws JSONException,IOException{
        Release best=null;
        for(int i=0;i<releases.length();i++){
            JSONObject release=releases.getJSONObject(i);if(release.optBoolean("draft",true))continue;
            JSONArray assets=release.optJSONArray("assets");if(assets==null)continue;
            String checksum="";Set<String> names=new HashSet<>();
            for(int j=0;j<assets.length();j++){
                JSONObject asset=assets.getJSONObject(j);String name=asset.optString("name");
                if(!asset.optString("state").equals("uploaded"))continue;
                if(!names.add(name))throw new IOException("В релизе повторяются имена файлов");
                if(name.equals("SHA256SUMS.txt")&&allowedAsset(asset.optString("browser_download_url"),name))checksum=asset.getString("browser_download_url");
            }
            for(int j=0;j<assets.length();j++){
                JSONObject asset=assets.getJSONObject(j);String name=asset.optString("name");Matcher match=APK.matcher(name);
                if(!match.matches()||!asset.optString("state").equals("uploaded"))continue;
                String version=match.group(1);if(compare(version,installed)<=0||best!=null&&compare(version,best.version)<=0)continue;
                long size=asset.optLong("size",-1);String url=asset.optString("browser_download_url"),digest=asset.optString("digest");
                if(size<=0||size>MAX_APK||!allowedAsset(url,name))throw new IOException("Недопустимый APK в релизе");
                String hash=digest.matches("sha256:[a-fA-F0-9]{64}")?digest.substring(7).toLowerCase(Locale.ROOT):"";
                if(hash.isEmpty()&&checksum.isEmpty())throw new IOException("У APK нет контрольной суммы SHA-256");
                best=new Release(version,name,url,hash,checksum,size);
            }
        }
        return best;
    }
    static Release latest(String installed,Cancel cancel)throws Exception{
        Release best=null;
        for(int page=1;page<=3;page++){
            String url=API+page;JSONArray data=new JSONArray(text(url,1024*1024,cancel,true));Release candidate=find(data,installed);
            if(candidate!=null&&(best==null||compare(candidate.version,best.version)>0))best=candidate;
            if(data.length()<20)break;
        }
        return best;
    }
    private static Response response(String url,Cancel cancel,boolean metadata)throws IOException{
        String current=url;
        for(int hop=0;hop<6;hop++){
            cancel.check();boolean valid=metadata?current.matches(Pattern.quote(API)+"[1-3]"):hop==0?allowedAsset(current,new File(HttpUrl.get(current).encodedPath()).getName()):allowedRedirect(current);
            if(!valid)throw new IOException("Недопустимый адрес обновления");
            Request request=new Request.Builder().url(current).header("User-Agent","KodVsego-SmartDNS-Android/"+BuildConfig.VERSION_NAME).header("Accept",metadata?"application/vnd.github+json":"application/octet-stream").build();
            Call call=HTTP.newCall(request);cancel.call=call;if(cancel.cancelled)call.cancel();Response response=call.execute();
            if(response.isRedirect()){
                String location=response.header("Location");HttpUrl next=location==null?null:response.request().url().resolve(location);response.close();
                if(next==null)throw new IOException("Неверное перенаправление GitHub");current=next.toString();continue;
            }
            if(!response.isSuccessful()){int code=response.code();response.close();throw new IOException(code==403||code==429?"GitHub ограничил частоту запросов. Повторите позже.":"GitHub ответил HTTP "+code);}
            if(response.body()==null){response.close();throw new IOException("GitHub вернул пустой ответ");}return response;
        }
        throw new IOException("Слишком много перенаправлений GitHub");
    }
    private static String text(String url,int limit,Cancel cancel,boolean metadata)throws IOException{
        try(Response response=response(url,cancel,metadata);InputStream in=response.body().byteStream();ByteArrayOutputStream out=new ByteArrayOutputStream()){
            byte[] buffer=new byte[8192];int count;while((count=in.read(buffer))!=-1){cancel.check();if(out.size()+count>limit)throw new IOException("Ответ GitHub слишком большой");out.write(buffer,0,count);}return out.toString("UTF-8");
        }
    }
    static String checksum(String sums,String name)throws IOException{
        if(sums.startsWith("\uFEFF"))sums=sums.substring(1);
        String found=null;for(String line:sums.split("\\r?\\n")){String[] fields=line.trim().split("\\s+",2);if(fields.length!=2)continue;
            if(fields[1].replaceFirst("^\\*","").equals(name)){if(found!=null||!fields[0].matches("[a-fA-F0-9]{64}"))throw new IOException("Некорректная контрольная сумма");found=fields[0].toLowerCase(Locale.ROOT);}}
        if(found==null)throw new IOException("В SHA256SUMS нет нужного APK");return found;
    }
    static File download(Context context,Release release,Cancel cancel,Progress progress)throws Exception{
        File directory=new File(context.getCacheDir(),"updates");if(!directory.exists()&&!directory.mkdirs())throw new IOException("Не удалось создать папку обновления");
        File partial=new File(directory,"download.part"),verified=new File(directory,"verified.apk");
        String hash=release.hash.isEmpty()?checksum(text(release.checksumUrl,65536,cancel,false),release.name):release.hash;
        release.hash=hash;
        try{
            try(Response response=response(release.url,cancel,false);InputStream input=response.body().byteStream();OutputStream output=new FileOutputStream(partial)){
                copyVerified(input,output,release.size,hash,cancel,progress);
            }
            inspect(context,partial,release);cancel.check();
            if(verified.exists()&&!verified.delete())throw new IOException("Не удалось заменить кэш обновления");
            if(!partial.renameTo(verified))throw new IOException("Не удалось сохранить APK");return verified;
        }finally{if(partial.exists())partial.delete();}
    }
    static void copyVerified(InputStream in,OutputStream out,long expected,String hash,Cancel cancel,Progress progress)throws Exception{
        if(expected<=0||expected>MAX_APK||!hash.matches("[a-fA-F0-9]{64}"))throw new IOException("Неверные параметры APK");
        MessageDigest digest=MessageDigest.getInstance("SHA-256");byte[] buffer=new byte[32768];long total=0;int count;
        while((count=in.read(buffer))!=-1){cancel.check();total+=count;if(total>expected)throw new IOException("Размер APK превышает ожидаемый");digest.update(buffer,0,count);out.write(buffer,0,count);progress.bytes(total);}
        cancel.check();if(total!=expected||!hex(digest.digest()).equalsIgnoreCase(hash))throw new IOException("APK не прошёл проверку размера или SHA-256");
    }
    static String hex(byte[] bytes){StringBuilder value=new StringBuilder();for(byte b:bytes)value.append(String.format(Locale.ROOT,"%02x",b&255));return value.toString();}
    @SuppressWarnings("deprecation") private static Signature[] signatures(PackageInfo info){
        return Build.VERSION.SDK_INT>=28?(info.signingInfo==null?new Signature[0]:info.signingInfo.getApkContentsSigners()):(info.signatures==null?new Signature[0]:info.signatures);
    }
    @SuppressWarnings("deprecation") static void inspect(Context context,File apk,Release release)throws Exception{
        PackageManager manager=context.getPackageManager();int flags=Build.VERSION.SDK_INT>=28?PackageManager.GET_SIGNING_CERTIFICATES:PackageManager.GET_SIGNATURES;
        PackageInfo candidate=manager.getPackageArchiveInfo(apk.getAbsolutePath(),flags),installed=manager.getPackageInfo(context.getPackageName(),flags);
        if(candidate==null||!context.getPackageName().equals(candidate.packageName)||!release.version.equals(candidate.versionName)||
            (Build.VERSION.SDK_INT>=28?candidate.getLongVersionCode():candidate.versionCode)<=BuildConfig.VERSION_CODE||candidate.applicationInfo==null||candidate.applicationInfo.minSdkVersion>Build.VERSION.SDK_INT)
            throw new IOException("APK не является совместимым обновлением этого приложения");
        Set<String> original=new HashSet<>(),updated=new HashSet<>();for(Signature s:signatures(installed))original.add(hex(s.toByteArray()));for(Signature s:signatures(candidate))updated.add(hex(s.toByteArray()));
        if(original.isEmpty()||!original.equals(updated))throw new IOException("Подпись APK отличается от установленного приложения");
    }
    static void verifyReady(Context context,File apk,Release release,Cancel cancel)throws Exception{
        String hash=release.hash.isEmpty()?checksum(text(release.checksumUrl,65536,cancel,false),release.name):release.hash;
        try(InputStream input=new FileInputStream(apk)){copyVerified(input,new OutputStream(){public void write(int ignored){}public void write(byte[] b,int o,int n){}},release.size,hash,cancel,n->{});}inspect(context,apk,release);
    }
}
