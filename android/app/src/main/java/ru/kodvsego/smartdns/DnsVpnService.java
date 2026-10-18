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
import java.util.Arrays;
import java.util.concurrent.*;

public final class DnsVpnService extends VpnService {
    public static final String START="ru.kodvsego.smartdns.START", STOP="ru.kodvsego.smartdns.STOP";
    public static volatile boolean active,connecting;
    public static volatile String status="DNS выключен";
    public static volatile DnsConfig current;
    private final ExecutorService lifecycle=Executors.newSingleThreadExecutor();
    private volatile Tunnel tunnel;
    private volatile boolean destroyed;
    @Override public void onCreate() {
        super.onCreate();
        NotificationChannel channel=new NotificationChannel("dns","Подключение DNS",NotificationManager.IMPORTANCE_LOW);
        getSystemService(NotificationManager.class).createNotificationChannel(channel);
    }
    private Notification notification() {
        PendingIntent open=PendingIntent.getActivity(this,0,new Intent(this,MainActivity.class),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        PendingIntent stop=PendingIntent.getService(this,1,new Intent(this,DnsVpnService.class).setAction(STOP),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        return new Notification.Builder(this,"dns").setSmallIcon(R.drawable.ic_dns).setContentTitle("Код Всего · Smart DNS")
            .setContentText(status).setContentIntent(open).setOngoing(true).addAction(new Notification.Action.Builder(null,"Выключить",stop).build()).build();
    }
    @Override public int onStartCommand(Intent intent,int flags,int startId) {
        if(intent==null||STOP.equals(intent.getAction())) {disconnect("DNS выключен");stopSelf();return START_NOT_STICKY;}
        if(!START.equals(intent.getAction())) {stopSelf();return START_NOT_STICKY;}
        try {
            DnsConfig config=new DnsConfig(intent.getIntExtra("provider",0),intent.getBooleanExtra("encrypted",false),intent.getIntExtra("antigravity",-1));
            connecting=true;status="Подключаем "+Provider.at(config.provider).name+"…";
            if(Build.VERSION.SDK_INT>=34) startForeground(1,notification(),ServiceInfo.FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED);
            else startForeground(1,notification());
            lifecycle.execute(() -> connect(config,startId));
        }catch(Exception e){disconnect("Ошибка: "+e.getMessage());stopSelf();}
        return START_NOT_STICKY;
    }
    private void connect(DnsConfig config,int startId) {
        try {
            if(destroyed) return;
            Network network=PhysicalNetwork.find(this);PhysicalNetwork.checkPrivateDns(this,network);
            if(VpnService.prepare(this)!=null) throw new IOException("Разрешение VPN отозвано");
            // Validate the endpoint before replacing an existing working tunnel. No plain fallback for DoH.
            try(UpstreamResolver check=new UpstreamResolver(this,this)) {DnsWire.requireAddress(check.resolve(DnsWire.query("example.com"),Provider.at(config.provider),config.encrypted));}
            if(destroyed) return;
            Builder builder=new Builder().setSession("Smart DNS · "+Provider.at(config.provider).name)
                .addAddress("10.111.0.1",32).addDnsServer("10.111.0.2").addRoute("10.111.0.2",32)
                .allowFamily(OsConstants.AF_INET6).setMtu(1500).setBlocking(false)
                .setConfigureIntent(PendingIntent.getActivity(this,0,new Intent(this,MainActivity.class),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT));
            if(Build.VERSION.SDK_INT>=29) builder.setMetered(false);
            ParcelFileDescriptor descriptor=builder.establish();
            if(descriptor==null) throw new IOException("Android не разрешил VPN-подключение");
            synchronized(this) {
                if(destroyed) {descriptor.close();return;}
                if(tunnel!=null)tunnel.close();
                tunnel=new Tunnel(descriptor,config);current=config;active=true;connecting=false;
                status="DNS включён · "+Provider.at(config.provider).name+(config.encrypted?" · DoH":" · обычный DNS");
                tunnel.start();
            }
            updateNotification();
        }catch(Exception e) {
            connecting=false;
            if(!destroyed) {
                status="Ошибка подключения: "+e.getMessage()+(active?". Прежнее подключение сохранено.":"");updateNotification();
                if(!active) stopSelf(startId);
            }
        }
    }
    private void updateNotification(){if(!destroyed)getSystemService(NotificationManager.class).notify(1,notification());}
    private synchronized void disconnect(String message) {
        destroyed=true;if(tunnel!=null){tunnel.close();tunnel=null;}
        active=false;connecting=false;current=null;status=message;
        stopForeground(STOP_FOREGROUND_REMOVE);
    }
    @Override public void onRevoke(){disconnect("Разрешение VPN отозвано. DNS выключен.");stopSelf();}
    @Override public void onDestroy(){disconnect(status.startsWith("Ошибка")?status:"DNS выключен");lifecycle.shutdownNow();super.onDestroy();}

    private final class Tunnel implements Closeable {
        final ParcelFileDescriptor descriptor;final DnsConfig config;final UpstreamResolver resolver;
        final ThreadPoolExecutor workers=new ThreadPoolExecutor(4,4,0,TimeUnit.SECONDS,new ArrayBlockingQueue<>(64));
        final ScheduledExecutorService clock=Executors.newSingleThreadScheduledExecutor();
        final FileInputStream input;final FileOutputStream output;final TcpDnsProxy tcp;
        volatile boolean closed;
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
                if(closed)return;
                String next;
                try{
                    tcp.tick(System.currentTimeMillis());Network n=PhysicalNetwork.find(DnsVpnService.this);
                    PhysicalNetwork.checkPrivateDns(DnsVpnService.this,n);setUnderlyingNetworks(new Network[]{n});
                    next=lastFailure>lastSuccess?"DNS не отвечает · проверьте сеть и провайдера":
                        "DNS включён · "+Provider.at(config.provider).name+(config.encrypted?" · DoH":" · обычный DNS");
                }catch(Exception e){next="Проверьте сеть / частный DNS · туннель активен";}
                if(tunnel==this&&!connecting&&!closed&&!next.equals(status)){status=next;updateNotification();}
            },1,1,TimeUnit.SECONDS);
        }
        void submit(Runnable work,Runnable rejected){try{workers.execute(work);}catch(RejectedExecutionException e){rejected.run();}}
        byte[] answer(byte[] query) {
            try {byte[] response=resolver.resolve(query,config);lastSuccess=System.nanoTime();return response;}
            catch(Exception e) {lastFailure=System.nanoTime();try{return DnsWire.failure(query);}catch(IOException ignored){return null;}}
        }
        void read() {
            byte[] buffer=new byte[65535];
            try {
                StructPollfd poll=new StructPollfd();poll.fd=descriptor.getFileDescriptor();poll.events=(short)OsConstants.POLLIN;
                while(!closed) {
                    if(Os.poll(new StructPollfd[]{poll},1000)==0)continue;
                    if(closed)break;
                    int count=input.read(buffer);if(count<0)break;
                    IpPacket packet;try{packet=IpPacket.parse(buffer,count);}catch(IOException invalid){continue;}
                    if(packet.protocol==6) {tcp.accept(packet,System.currentTimeMillis());continue;}
                    try{DnsWire.question(packet.payload);}catch(IOException invalid){continue;}
                    submit(()->{
                        byte[] reply=answer(packet.payload);if(reply==null)return;
                        try{if(reply.length>512)reply=DnsWire.truncated(reply);write(packet.udpReply(reply));}catch(IOException ignored){}
                    },()->{try{write(packet.udpReply(DnsWire.failure(packet.payload)));}catch(IOException ignored){}});
                }
                if(!closed)throw new IOException("Интерфейс DNS закрыт Android");
            }catch(IOException | ErrnoException e) {
                if(!closed) new Handler(Looper.getMainLooper()).post(()->{
                    if(tunnel==this){disconnect("Ошибка DNS-туннеля: "+e.getMessage());stopSelf();}
                });
            } finally {close();}
        }
        synchronized void write(byte[] packet) throws IOException {if(!closed)output.write(packet);}
        @Override public void close() {
            synchronized(this){if(closed)return;closed=true;}
            try{descriptor.close();}catch(IOException ignored){}
            workers.shutdownNow();clock.shutdownNow();resolver.close();tcp.close();
        }
    }
}
