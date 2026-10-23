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
    public static final String START="ru.kodvsego.smartdns.START", STOP="ru.kodvsego.smartdns.STOP";
    public static volatile boolean active,connecting;
    public static volatile String status="DNS выключен";
    public static volatile DnsConfig current;
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

    @Override public void onCreate() {
        super.onCreate();runningService=this;
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
            active=false;connecting=false;current=null;status="DNS выключен";
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
        if(!START.equals(intent.getAction())) {stopSelf(startId);return START_NOT_STICKY;}
        // Repeated taps or widget updates never enqueue duplicate connections.
        if(active||connecting)return START_NOT_STICKY;
        try {
            if(!Provider.isAvailable(intent.getIntExtra("provider",0)))throw new IllegalArgumentException("Провайдер временно недоступен. Выберите другой DNS.");
            DnsConfig config=new DnsConfig(intent.getIntExtra("provider",0),intent.getBooleanExtra("encrypted",false));
            long token=sessions.begin();connecting=true;status="Подключаем "+Provider.at(config.provider).name+"…";
            if(Build.VERSION.SDK_INT>=34)startForeground(1,notification(),ServiceInfo.FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED);
            else startForeground(1,notification());
            publish();connectingTask=lifecycle.submit(()->connect(config,token,startId));
        }catch(Exception e){disconnect("Ошибка: "+e.getMessage());stopSelf(startId);}
        return START_NOT_STICKY;
    }
    private void connect(DnsConfig config,long token,int startId) {
        ParcelFileDescriptor descriptor=null;
        try {
            if(!sessions.owns(token)||Thread.currentThread().isInterrupted())return;
            Network network=PhysicalNetwork.find(this);PhysicalNetwork.checkPrivateDns(this,network);
            if(VpnService.prepare(this)!=null)throw new IOException("Разрешение VPN отозвано");
            UpstreamResolver check=new UpstreamResolver(this,this);preflight=check;
            try {
                if(!sessions.owns(token))return;
                DnsWire.requireAddress(check.resolve(DnsWire.query("example.com"),Provider.at(config.provider),config.encrypted));
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
                current=config;active=true;connecting=false;
                status=connectedLabel(config);candidate.start();publish();
            }))sessions.dispose(candidate);
        }catch(Exception error) {
            main.post(()->{
                if(destroyed||!sessions.owns(token))return;
                connecting=false;status="Ошибка подключения: "+error.getMessage();publish();stopSelf(startId);
            });
        }finally{if(descriptor!=null)try{descriptor.close();}catch(IOException ignored){}}
    }
    private static String connectedLabel(DnsConfig config){return "DNS включён · "+Provider.at(config.provider).name+(config.encrypted?" · DoH":" · обычный DNS");}
    private void publish() {
        if(!destroyed&&(active||connecting))getSystemService(NotificationManager.class).notify(1,notification());
        DnsWidget.updateAll(this);
    }
    private void stopRequested(){disconnect("DNS выключен");stopSelf();}
    private void disconnect(String message) {
        // Invalidating the generation is immediate. Socket/TCP locks are handled only by cleanup workers.
        sessions.stop();
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
        final ParcelFileDescriptor descriptor;final DnsConfig config;final UpstreamResolver resolver;
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
                    if(sessions.current()==this&&!connecting&&!closed.get()&&!label.equals(status)){status=label;publish();}
                });
            },1,1,TimeUnit.SECONDS);
        }
        void submit(Runnable work,Runnable rejected){try{workers.execute(work);}catch(RejectedExecutionException e){rejected.run();}}
        byte[] answer(byte[] query) {
            try{byte[] response=resolver.resolve(query,config);lastSuccess=System.nanoTime();return response;}
            catch(Exception e){lastFailure=System.nanoTime();try{return DnsWire.failure(query);}catch(IOException ignored){return null;}}
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