package ru.kodvsego.smartdns;

import android.content.*;
import android.os.*;
import java.io.File;
import java.util.concurrent.*;

/** Process-wide work survives Activity recreation, without retaining an Activity. */
final class UpdateController {
    enum Phase { IDLE, CHECKING, AVAILABLE, DOWNLOADING, READY, VERIFYING, ERROR }
    interface Listener {void changed(UpdateController update);}
    private static UpdateController instance;
    static synchronized UpdateController get(Context context){android.app.Application app=(android.app.Application)context.getApplicationContext();if(instance==null||instance.application!=app)instance=new UpdateController(app);return instance;}
    final android.app.Application application;final SharedPreferences prefs;final File readyFile;
    private final Handler main=new Handler(Looper.getMainLooper());
    private final ExecutorService worker=Executors.newSingleThreadExecutor(task->{Thread thread=new Thread(task,"smartdns-updates");thread.setDaemon(true);return thread;});
    Phase phase=Phase.IDLE;AndroidUpdate.Release release;String error="";int percent;
    private Listener listener;private AndroidUpdate.Cancel cancellation;
    private UpdateController(android.app.Application context){
        this.application=context;prefs=context.getSharedPreferences("updates",Context.MODE_PRIVATE);readyFile=new File(context.getCacheDir(),"updates/verified.apk");
        File abandoned=new File(context.getCacheDir(),"updates/download.part");if(abandoned.exists())abandoned.delete();
        try{AndroidUpdate.Release saved=AndroidUpdate.Release.restore(prefs.getString("ready",""));
            if(readyFile.isFile()&&AndroidUpdate.compare(saved.version,BuildConfig.VERSION_NAME)>0){release=saved;phase=Phase.READY;}
            else clearReady();
        }catch(Exception ignored){clearReady();}
    }
    boolean busy(){return phase==Phase.CHECKING||phase==Phase.DOWNLOADING||phase==Phase.VERIFYING;}
    void attach(Listener listener){this.listener=listener;notifyUi();}
    void detach(Listener listener){if(this.listener==listener)this.listener=null;}
    private void notifyUi(){if(listener!=null)listener.changed(this);}
    private void clearReady(){prefs.edit().remove("ready").apply();if(readyFile.exists())readyFile.delete();}
    void check(boolean manual){
        if(busy()){notifyUi();return;}
        if(phase==Phase.READY){notifyUi();return;}
        long now=System.currentTimeMillis(),last=prefs.getLong("lastCheck",0);
        if(!manual&&now>=last&&now-last<TimeUnit.HOURS.toMillis(1))return;
        // Failed automatic requests also back off; opening the app must not exhaust GitHub's limit.
        prefs.edit().putLong("lastCheck",now).apply();phase=Phase.CHECKING;error="";notifyUi();
        AndroidUpdate.Cancel cancel=new AndroidUpdate.Cancel();cancellation=cancel;
        worker.execute(()->{
            try{AndroidUpdate.Release found=AndroidUpdate.latest(BuildConfig.VERSION_NAME,cancel);main.post(()->{release=found;phase=found==null?Phase.IDLE:Phase.AVAILABLE;notifyUi();});}
            catch(Exception e){main.post(()->{phase=Phase.ERROR;error=e.getMessage()==null?"Не удалось связаться с GitHub":e.getMessage();notifyUi();});}
        });
    }
    void download(){
        if(busy()||release==null)return;
        clearReady();AndroidUpdate.Release selected=release;AndroidUpdate.Cancel cancel=new AndroidUpdate.Cancel();cancellation=cancel;
        phase=Phase.DOWNLOADING;percent=0;notifyUi();
        worker.execute(()->{
            try{
                int[] lastPercent={-1};AndroidUpdate.download(application,selected,cancel,count->{int value=(int)(count*100/selected.size);if(value==lastPercent[0])return;lastPercent[0]=value;main.post(()->{if(phase==Phase.DOWNLOADING&&percent!=value){percent=value;notifyUi();}});});
                String saved=selected.json().toString();main.post(()->{if(cancel.cancelled){clearReady();phase=Phase.AVAILABLE;}else{prefs.edit().putString("ready",saved).apply();phase=Phase.READY;}notifyUi();});
            }catch(Exception e){main.post(()->{phase=cancel.cancelled?Phase.AVAILABLE:Phase.ERROR;error=e.getMessage()==null?"Загрузка не удалась":e.getMessage();notifyUi();});}
        });
    }
    void cancelDownload(){if(phase==Phase.DOWNLOADING&&cancellation!=null)cancellation.cancel();}
    void verifyForInstall(Runnable verified){
        if(phase!=Phase.READY||release==null)return;
        AndroidUpdate.Release selected=release;AndroidUpdate.Cancel cancel=new AndroidUpdate.Cancel();cancellation=cancel;phase=Phase.VERIFYING;notifyUi();
        worker.execute(()->{
            try{AndroidUpdate.verifyReady(application,readyFile,selected,cancel);main.post(()->{phase=Phase.READY;notifyUi();verified.run();});}
            catch(Exception e){main.post(()->{clearReady();phase=Phase.ERROR;error=e.getMessage()==null?"APK не прошёл проверку":e.getMessage();notifyUi();});}
        });
    }
    void later(){if(release!=null)prefs.edit().putString("deferredVersion",release.version).putLong("deferredAt",System.currentTimeMillis()).apply();}
    void remindNow(){prefs.edit().remove("deferredVersion").remove("deferredAt").apply();}
    boolean shouldPrompt(){
        if(release==null)return false;
        long elapsed=System.currentTimeMillis()-prefs.getLong("deferredAt",0);
        return !release.version.equals(prefs.getString("deferredVersion",""))||elapsed<0||elapsed>=TimeUnit.HOURS.toMillis(24);
    }
}
