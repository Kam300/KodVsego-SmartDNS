package ru.kodvsego.smartdns;

import android.app.*;
import android.content.*;
import android.content.pm.ServiceInfo;
import android.net.*;
import android.os.*;
import android.system.OsConstants;
import android.system.Os;
import android.system.StructPollfd;
import android.system.ErrnoException;
import java.io.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;

public final class DnsVpnService extends VpnService {
    public static final String START="ru.kodvsego.smartdns.START", STOP="ru.kodvsego.smartdns.STOP",SWITCH="ru.kodvsego.smartdns.SWITCH";
    public static volatile boolean active,connecting,switching;
    public static volatile String status="DNS выключен";
    public static volatile DnsConfig current,switchTarget;
    public static volatile String switchError="";
    private static volatile DnsVpnService runningService;
    private static final ExecutorService CLEANUP=Executors.newFixedThreadPool(2,task->{
        Thread thread=new Thread(task,"smartdns-cleanup");thread.setDaemon(true);return thread;
    });
    final SessionOwner<AutoCloseable> sessions=new SessionOwner<>(CLEANUP);
    private final Handler main=new Handler(Looper.getMainLooper());
    private final ExecutorService lifecycle=Executors.newSingleThreadExecutor();
    private volatile UpstreamResolver preflight;
    private Future<?> connectingTask;
    private boolean destroyed;
    private int switchRevision;
    interface SwitchProbe {void check(DnsConfig config) throws Exception;}
    SwitchProbe switchProbe;

    @Override public void onCreate() {
        super.onCreate();RemoteCatalog.get(this).applyIfIdle(true);runningService=this;switchError="";switchTarget=null;AppJournal.init(this);AppJournal.record("Служба","Создана; версия "+BuildConfig.VERSION_NAME);
        NotificationChannel channel=new NotificationChannel("dns","Подключение DNS",NotificationManager.IMPORTANCE_LOW);
        getSystemService(NotificationManager.class).createNotificationChannel(channel);
    }
    public static void requestStop(Context context) {
        DnsVpnService service=runningService;
        if(service!=null) {
            if(Looper.myLooper()==Looper.getMainLooper())service.stopRequested();
            else service.main.post(service::stopRequested);
        } else {
            context.stopService(new Intent(context,DnsVpnService.class));
            active=false;connecting=false;switching=false;current=null;status="DNS выключен";
            DnsWidget.updateAll(context);
        }
    }
    private Notification notification() {
        PendingIntent open=PendingIntent.getActivity(this,0,new Intent(this,MainActivity.class),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        PendingIntent stop=PendingIntent.getService(this,1,new Intent(this,DnsVpnService.class).setAction(STOP),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        return new Notification.Builder(this,"dns").setSmallIcon(R.drawable.ic_dns).setContentTitle("Код Всего · Smart DNS")
            .setContentText(status).setContentIntent(open).setOngoing(true)
            .addAction(new Notification.Action.Builder(null,"Выключить",stop).build()).build();
    }
    @Override public int onStartCommand(Intent intent,int flags,int startId) {
        if(intent==null||STOP.equals(intent.getAction())) {stopRequested();return START_NOT_STICKY;}
        if(SWITCH.equals(intent.getAction())){if(active&&!switching&&!connecting)try{switchDns(readConfig(intent));}catch(Exception error){AppJournal.record("Ошибка смены",error.toString());}return START_NOT_STICKY;}
        if(!START.equals(intent.getAction())) {stopSelf(startId);return START_NOT_STICKY;}
        // Repeated taps or widget updates never enqueue duplicate connections.
        if(active||connecting)return START_NOT_STICKY;
        try {
            if(!Provider.isAvailable(intent.getIntExtra("provider",0)))throw new IllegalArgumentException("Провайдер временно недоступен. Выберите другой DNS.");
            DnsConfig config=readConfig(intent);AppJournal.record("Подключение",Provider.at(config.provider).name+"; "+config.label());
            long token=sessions.begin();switchError="";switchTarget=null;connecting=true;status="Подключаем "+Provider.at(config.provider).name+"…";
            if(Build.VERSION.SDK_INT>=34)startForeground(1,notification(),ServiceInfo.FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED);
            else startForeground(1,notification());
            publish();connectingTask=lifecycle.submit(()->connect(config,token,startId));
        }catch(Exception e){disconnect("Ошибка: "+e.getMessage());stopSelf(startId);}
        return START_NOT_STICKY;
    }
    private void connect(DnsConfig config,long token,int startId) {
        ParcelFileDescriptor descriptor=null;
        try {
            long waiting=SystemClock.elapsedRealtime();boolean handoff=PhysicalNetwork.otherVpn(this);
            AppJournal.record("VPN-переход","Ожидание прежнего подключения; другой VPN="+handoff);
            if(handoff)main.post(()->{if(sessions.owns(token)&&connecting){status="Переключаем VPN…";publish();}});
            VpnHandoff.await(()->!PhysicalNetwork.otherVpn(this),()->sessions.owns(token)&&!destroyed,()->Thread.sleep(200));
            AppJournal.record("VPN-переход","Прежний VPN отключён; ожидание="+(SystemClock.elapsedRealtime()-waiting)+" мс");
            PhysicalNetwork.requireProbeNetwork(this);
            if(!sessions.owns(token)||Thread.currentThread().isInterrupted())return;
            Network network=PhysicalNetwork.find(this);PhysicalNetwork.checkPrivateDns(this,network);
            if(VpnService.prepare(this)!=null)throw new IOException("Разрешение VPN отозвано");
            UpstreamResolver check=new UpstreamResolver(this,this);preflight=check;
            try {
                if(!sessions.owns(token))return;
                DnsWire.requireAddress(check.resolve(DnsWire.query("example.com"),config));
            }finally{if(preflight==check)preflight=null;check.close();}
            if(!sessions.owns(token)||Thread.currentThread().isInterrupted())return;
            Builder builder=new Builder().setSession("Smart DNS · "+Provider.at(config.provider).name)
                .addAddress("10.111.0.1",32).addDnsServer("10.111.0.2").addRoute("10.111.0.2",32)
                .allowFamily(OsConstants.AF_INET6).setMtu(1500).setBlocking(false)
                .setConfigureIntent(PendingIntent.getActivity(this,0,new Intent(this,MainActivity.class),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT));
            if(Build.VERSION.SDK_INT>=29)builder.setMetered(false);
            descriptor=builder.establish();
            if(descriptor==null)throw new IOException("Android не разрешил VPN-подключение");
            Tunnel candidate=new Tunnel(descriptor,config);descriptor=null;
            if(!main.post(()->{
                if(destroyed||!sessions.owns(token)){sessions.dispose(candidate);return;}
                if(!sessions.install(token,candidate))return;
                current=config;active=true;connecting=false;AppJournal.record("Подключено",Provider.at(config.provider).name+"; "+config.label());
                status=connectedLabel(config);candidate.start();publish();
            }))sessions.dispose(candidate);
        }catch(Exception error) {
            main.post(()->{
                if(destroyed||!sessions.owns(token))return;
                connecting=false;status="Ошибка подключения: "+error.getMessage();AppJournal.record("Ошибка подключения",error.toString());publish();stopSelf(startId);
            });
        }finally{if(descriptor!=null)try{descriptor.close();}catch(IOException ignored){}}
    }
    private DnsConfig readConfig(Intent intent){int provider=intent.getIntExtra("provider",0);if(!Provider.isAvailable(provider))throw new IllegalArgumentException("Провайдер недоступен");return new DnsConfig(provider,intent.getIntExtra("protocol",intent.getBooleanExtra("encrypted",false)?DnsConfig.DOH:DnsConfig.PLAIN));}
    private static String connectedLabel(DnsConfig config){return "DNS включён · "+Provider.at(config.provider).name+" · "+config.label();}
    private void switchDns(DnsConfig next){
        switchDns(next,false);
    }
    private void switchDns(DnsConfig next,boolean automatic){
        if(!(sessions.current() instanceof Tunnel))return;Tunnel original=(Tunnel)sessions.current();int revision=++switchRevision;switching=true;switchTarget=next;switchError="";AppJournal.record("Запрос смены DNS",Provider.at(original.config.provider).name+" -> "+Provider.at(next.provider).name+"; "+next.label());status="Проверяем "+Provider.at(next.provider).name+" · прежний DNS работает";publish();
        long failureAt=original.lastFailure;
        main.postDelayed(()->{if(revision==switchRevision&&switching&&sessions.current()==original){failedSwitch(revision,original,next,new IOException("Новый DNS не ответил за 12 секунд. Прежнее подключение сохранено."));if(connectingTask!=null)connectingTask.cancel(true);UpstreamResolver waiting=preflight;preflight=null;sessions.dispose(waiting);}},12000);
        connectingTask=lifecycle.submit(()->{try{verifySwitch(next);
            main.post(()->{if(destroyed||revision!=switchRevision||sessions.current()!=original||!active||original.closed.get())return;
                if(automatic&&!DnsSelection.mayApplyAuto(getSharedPreferences("smartdns",MODE_PRIVATE).getBoolean("autoDns",false),getSharedPreferences("smartdns",MODE_PRIVATE).getStringSet("hiddenProviders",java.util.Collections.emptySet()),next,failureAt,original.lastSuccess)){switching=false;switchTarget=null;status=connectedLabel(original.config);AppJournal.record("Авто DNS","Изменились настройки или прежний DNS восстановился; смена отменена");publish();return;}
                original.config=next;original.lastSuccess=original.lastFailure=0;original.failover.success();current=next;switching=false;switchTarget=null;switchError="";status=connectedLabel(next);
                getSharedPreferences("smartdns",MODE_PRIVATE).edit().putInt("provider",next.provider).putInt("protocol",next.protocol).putBoolean("encrypted",next.encrypted).apply();
                AppJournal.record("DNS сменён",Provider.at(next.provider).name+"; "+next.label()+"; VPN не пересоздан; доступ к сервисам не проверен");publish();});
        }catch(Exception error){main.post(()->failedSwitch(revision,original,next,error));}finally{preflight=null;}});
    }
    private void failedSwitch(int revision,Tunnel original,DnsConfig requested,Exception error){
        if(destroyed||revision!=switchRevision||sessions.current()!=original||!active)return;
        ++switchRevision;switching=false;switchTarget=null;
        switchError="Не удалось выбрать "+Provider.at(requested.provider).name+" · "+requested.label()+".\n"+AppJournal.scrub(error.getMessage())+"\nРаботает прежний DNS: "+Provider.at(original.config.provider).name;
        status="Прежний DNS сохранён · "+Provider.at(original.config.provider).name;
        AppJournal.record("Смена не применена",switchError);publish();
    }
    private void verifySwitch(DnsConfig next) throws Exception {
        if(switchProbe!=null){switchProbe.check(next);return;}
        try(UpstreamResolver check=new UpstreamResolver(this,this)){preflight=check;Network network=PhysicalNetwork.find(this);DnsWire.requireAddress(check.resolve(DnsWire.query("example.com"),next));if(!network.equals(PhysicalNetwork.find(this)))throw new IOException("Сеть изменилась при проверке");}
    }
    private void autoDns(Tunnel original){
        if(destroyed||!active||switching||connecting||sessions.current()!=original||!getSharedPreferences("smartdns",MODE_PRIVATE).getBoolean("autoDns",false))return;
        try{PhysicalNetwork.find(this);}catch(IOException offline){return;}
        switching=true;int revision=++switchRevision;DnsConfig old=original.config;long failureAt=original.lastFailure;status="Авто · beta: подбираем DNS; прежний работает";publish();
        connectingTask=lifecycle.submit(()->{try(UpstreamResolver check=new UpstreamResolver(this,this)){preflight=check;
            java.util.Set<String> hidden=getSharedPreferences("smartdns",MODE_PRIVATE).getStringSet("hiddenProviders",java.util.Collections.emptySet());
            java.util.List<DnsSelection.Result> results=DnsSelection.check(DnsSelection.alternatives(hidden,old),c->{long began=System.nanoTime();DnsWire.requireAddress(check.resolve(DnsWire.query("example.com"),c));return (System.nanoTime()-began)/1000000;},30000);
            for(DnsSelection.Result item:results)AppJournal.record("Авто DNS",Provider.at(item.config.provider).name+"; "+item.config.label()+"; "+(item.error==null?item.milliseconds+" мс":item.error));
            DnsSelection.Result best=DnsSelection.best(results);main.post(()->{if(destroyed||revision!=switchRevision||sessions.current()!=original||!active||original.closed.get())return;switching=false;
                if(!getSharedPreferences("smartdns",MODE_PRIVATE).getBoolean("autoDns",false)||best!=null&&getSharedPreferences("smartdns",MODE_PRIVATE).getStringSet("hiddenProviders",java.util.Collections.emptySet()).contains(String.valueOf(best.config.provider))){status=connectedLabel(original.config);AppJournal.record("Авто DNS","Настройки изменились; результат поиска не применяется");publish();return;}
                if(original.lastSuccess>failureAt){status=connectedLabel(original.config);AppJournal.record("Авто DNS","Прежний DNS снова отвечает; результат поиска не применяется");publish();return;}
                if(best==null){status="Авто: замена не найдена · прежний DNS сохранён";publish();}else switchDns(best.config,true);});
        }catch(Exception error){AppJournal.record("Авто DNS",error.toString());main.post(()->{if(revision==switchRevision&&sessions.current()==original){switching=false;status="Авто не завершено · прежний DNS сохранён";publish();}});}finally{preflight=null;}});
    }
    private void publish() {
        if(!destroyed&&(active||connecting))getSystemService(NotificationManager.class).notify(1,notification());
        DnsWidget.updateAll(this);
    }
    private void stopRequested(){disconnect("DNS выключен");stopSelf();}
    private void disconnect(String message) {
        // Invalidating the generation is immediate. Socket/TCP locks are handled only by cleanup workers.
        switchRevision++;switching=false;switchTarget=null;switchError="";sessions.stop();AppJournal.record("Отключение",message);
        if(connectingTask!=null){connectingTask.cancel(true);connectingTask=null;}
        UpstreamResolver pending=preflight;preflight=null;sessions.dispose(pending);
        active=false;connecting=false;current=null;status=message;
        stopForeground(STOP_FOREGROUND_REMOVE);publish();
    }
    @Override public void onRevoke(){main.post(()->{disconnect("Разрешение VPN отозвано. DNS выключен.");stopSelf();});}
    @Override public void onDestroy(){
        destroyed=true;disconnect(status.startsWith("Ошибка")?status:"DNS выключен");
        if(runningService==this)runningService=null;lifecycle.shutdownNow();super.onDestroy();
    }

    private final class Tunnel implements Closeable {
        final ParcelFileDescriptor descriptor;volatile DnsConfig config;final UpstreamResolver resolver;
        final DnsSelection.Failover failover=new DnsSelection.Failover();
        long loggedFailure;final java.util.concurrent.atomic.AtomicLong answers=new java.util.concurrent.atomic.AtomicLong(),errors=new java.util.concurrent.atomic.AtomicLong();
        final ThreadPoolExecutor workers=new ThreadPoolExecutor(4,4,0,TimeUnit.SECONDS,new ArrayBlockingQueue<>(64));
        final ScheduledExecutorService clock=Executors.newSingleThreadScheduledExecutor();
        final FileInputStream input;final FileOutputStream output;final TcpDnsProxy tcp;
        final AtomicBoolean closed=new AtomicBoolean();
        volatile long lastSuccess,lastFailure;
        Tunnel(ParcelFileDescriptor descriptor,DnsConfig config) {
            this.descriptor=descriptor;this.config=config;resolver=new UpstreamResolver(DnsVpnService.this,DnsVpnService.this);
            input=new FileInputStream(descriptor.getFileDescriptor());output=new FileOutputStream(descriptor.getFileDescriptor());
            tcp=new TcpDnsProxy(this::write,(query,completion)->submit(()->completion.complete(answer(query)),()->{
                try{completion.complete(DnsWire.failure(query));}catch(IOException e){completion.complete(null);}
            }));
        }
        void start() {
            new Thread(this::read,"smartdns-tun").start();
            clock.scheduleWithFixedDelay(()->{
                if(closed.get())return;
                String next;
                try{
                    tcp.tick(System.currentTimeMillis());Network network=PhysicalNetwork.find(DnsVpnService.this);
                    PhysicalNetwork.checkPrivateDns(DnsVpnService.this,network);setUnderlyingNetworks(new Network[]{network});
                    next=lastFailure>lastSuccess?"DNS не отвечает · проверьте сеть и провайдера":connectedLabel(config);
                }catch(Exception e){next="Проверьте сеть / частный DNS · туннель активен";}
                final String label=next;
                main.post(()->{
                    if(sessions.current()==this&&!connecting&&!switching&&!closed.get()&&!label.equals(status)){status=label;publish();}
                });
            },1,1,TimeUnit.SECONDS);
        }
        void submit(Runnable work,Runnable rejected){try{workers.execute(work);}catch(RejectedExecutionException e){rejected.run();}}
        byte[] answer(byte[] query) {
            DnsConfig used=config;
            try{byte[] response=resolver.resolve(query,used);int code=response[3]&15;if(code==2||code==5)throw new IOException("DNS вернул код "+code);if(config==used){lastSuccess=System.nanoTime();failover.success();}answers.incrementAndGet();return response;}
            catch(Exception e){if(config==used){lastFailure=System.nanoTime();if(failover.failed(SystemClock.elapsedRealtime()))main.post(()->autoDns(this));}errors.incrementAndGet();
                synchronized(this){long now=SystemClock.elapsedRealtime();if(now-loggedFailure>10000){loggedFailure=now;AppJournal.record("Ошибка DNS",Provider.at(used.provider).name+"; "+used.label()+"; "+e.getClass().getSimpleName()+": "+e.getMessage()+"; ответы="+answers.get()+"; ошибки="+errors.get());}}
                try{return DnsWire.failure(query);}catch(IOException ignored){return null;}}
        }
        void read() {
            byte[] buffer=new byte[65535];
            try {
                StructPollfd poll=new StructPollfd();poll.fd=descriptor.getFileDescriptor();poll.events=(short)OsConstants.POLLIN;
                while(!closed.get()) {
                    if(Os.poll(new StructPollfd[]{poll},1000)==0)continue;
                    if(closed.get())break;
                    int count=input.read(buffer);if(count<0)break;
                    IpPacket packet;try{packet=IpPacket.parse(buffer,count);}catch(IOException invalid){continue;}
                    if(packet.protocol==6){tcp.accept(packet,System.currentTimeMillis());continue;}
                    try{DnsWire.question(packet.payload);}catch(IOException invalid){continue;}
                    submit(()->{
                        byte[] reply=answer(packet.payload);if(reply==null)return;
                        try{if(reply.length>512)reply=DnsWire.truncated(reply);write(packet.udpReply(reply));}catch(IOException ignored){}
                    },()->{try{write(packet.udpReply(DnsWire.failure(packet.payload)));}catch(IOException ignored){}});
                }
                if(!closed.get())throw new IOException("Интерфейс DNS закрыт Android");
            }catch(IOException|ErrnoException error) {
                if(!closed.get())main.post(()->{
                    if(sessions.current()==this){disconnect("Ошибка DNS-туннеля: "+error.getMessage());stopSelf();}
                });
            }finally{sessions.dispose(this);}
        }
        synchronized void write(byte[] packet)throws IOException{if(!closed.get())output.write(packet);}
        @Override public void close() {
            if(!closed.compareAndSet(false,true))return;
            // Close the OS interface first. No UI thread waits for protocol or socket cleanup.
            try{descriptor.close();}catch(IOException ignored){}
            workers.shutdownNow();clock.shutdownNow();resolver.close();tcp.close();
        }
    }
}
