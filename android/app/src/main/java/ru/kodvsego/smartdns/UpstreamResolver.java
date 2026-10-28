package ru.kodvsego.smartdns;

import android.content.Context;
import android.net.Network;
import android.net.VpnService;
import okhttp3.*;
import java.io.*;
import java.net.*;
import java.util.Arrays;
import java.util.concurrent.TimeUnit;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicBoolean;

/** All upstream connections are bound to the physical network, outside the local DNS VPN. */
final class UpstreamResolver implements Closeable {
    private final Context context;private final VpnService vpn;
    private Network clientNetwork;private final java.util.Map<String,OkHttpClient> clients=new java.util.HashMap<>();
    private final AtomicBoolean closed=new AtomicBoolean();
    private final Set<Closeable> sockets=ConcurrentHashMap.newKeySet();
    private final Set<Call> calls=ConcurrentHashMap.newKeySet();
    UpstreamResolver(Context context,VpnService vpn) {this.context=context;this.vpn=vpn;}
    byte[] resolve(byte[] query,DnsConfig config) throws IOException {
        DnsWire.question(query);
        if(config.protocol==DnsConfig.DOT)return dot(query,Provider.at(config.provider));
        return resolve(query,Provider.at(config.provider),config.encrypted);
    }
    private byte[] dot(byte[] query,Provider provider) throws IOException {
        ensureOpen();Network network=PhysicalNetwork.find(context);PhysicalNetwork.checkPrivateDns(context,network);IOException last=null;
        for(String address:provider.dotServers){Socket plain=new Socket();javax.net.ssl.SSLSocket secure=null;
            try{register(plain);SocketSetup.prepare(plain,vpn==null?null:()->vpn.protect(plain),()->network.bindSocket(plain),"DoT");
                plain.connect(new InetSocketAddress(address,853),4000);plain.setSoTimeout(5000);
                secure=(javax.net.ssl.SSLSocket)((javax.net.ssl.SSLSocketFactory)javax.net.ssl.SSLSocketFactory.getDefault()).createSocket(plain,provider.dot,853,true);register(secure);
                return DotTransport.exchange(secure,query,provider.dot);
            }catch(IOException error){last=error;}finally{if(secure!=null){sockets.remove(secure);try{secure.close();}catch(IOException ignored){}}sockets.remove(plain);try{plain.close();}catch(IOException ignored){}}
        }
        throw new IOException("DoT не ответил: "+(last==null?"нет сервера":last.getMessage()),last);
    }
    byte[] resolve(byte[] query,Provider provider,boolean encrypted) throws IOException {
        ensureOpen();Network network=PhysicalNetwork.find(context);PhysicalNetwork.checkPrivateDns(context,network);
        if(encrypted||provider.encryptedOnly) {
            if(provider.doh==null) throw new IOException("DoH для провайдера недоступен");
            final Call[] pending=new Call[1];
            try {
                return DohTransport.exchange(request->{
                    ensureOpen();Call call=client(network,provider).newCall(request);pending[0]=call;calls.add(call);
                    if(closed.get())call.cancel();return call;
                },provider.doh,query);
            }finally{if(pending[0]!=null)calls.remove(pending[0]);}
        }
        IOException last=new IOException("DNS не ответил");
        for(String address:provider.servers) {
            ensureOpen();
            try {
                byte[] answer=plain(network,address,query);
                // SERVFAIL/REFUSED on primary may be temporary; try the secondary before returning it.
                int code=answer[3]&15;
                if((code==2||code==5)&&!address.equals(provider.servers[provider.servers.length-1])) continue;
                return answer;
            }catch(IOException e){last=e;}
        }
        throw new IOException("Нет ответа от "+provider.name+": "+last.getMessage(),last);
    }
    byte[] probe(byte[] query,Provider provider,boolean encrypted,String server) throws IOException {
        if(encrypted||provider.encryptedOnly)return resolve(query,provider,true);
        Network network=PhysicalNetwork.find(context);PhysicalNetwork.checkPrivateDns(context,network);
        return plain(network,server,query);
    }
    private byte[] plain(Network network,String address,byte[] query) throws IOException {
        byte[] answer=udp(network,address,query);DnsWire.validate(query,answer);
        if((answer[2]&2)!=0){answer=tcp(network,address,query);DnsWire.validate(query,answer);}
        return answer;
    }
    private synchronized OkHttpClient client(Network network,Provider provider) throws IOException {
        ensureOpen();
        if(!network.equals(clientNetwork)) {
            closeClients();
            clientNetwork=network;
        }
        String key=provider.doh+Arrays.toString(provider.dohServers);OkHttpClient client=clients.get(key);
        if(client==null){String endpoint=HttpUrl.get(provider.doh).host();
            client=new OkHttpClient.Builder().socketFactory(network.getSocketFactory())
                .dns(host -> {if(host.equalsIgnoreCase(endpoint)&&provider.dohServers.length>0){java.util.List<InetAddress> addresses=new java.util.ArrayList<>();for(String address:provider.dohServers)addresses.add(InetAddress.getByName(address));return addresses;}return Arrays.asList(network.getAllByName(host));}).proxy(Proxy.NO_PROXY)
                .connectTimeout(4,TimeUnit.SECONDS).readTimeout(5,TimeUnit.SECONDS).callTimeout(8,TimeUnit.SECONDS)
                .followRedirects(false).followSslRedirects(false).retryOnConnectionFailure(true).build();clients.put(key,client);
        }
        return client;
    }
    private void closeClients(){for(OkHttpClient client:clients.values()){client.dispatcher().cancelAll();client.connectionPool().evictAll();client.dispatcher().executorService().shutdown();}clients.clear();}
    private byte[] udp(Network network,String address,byte[] query) throws IOException {
        DatagramSocket socket=new DatagramSocket(null);
        try {
            register(socket);
            SocketSetup.prepare(socket,vpn==null?null:()->vpn.protect(socket),()->network.bindSocket(socket),"DNS");socket.bind(new InetSocketAddress(0));socket.setSoTimeout(2500);
            socket.connect(InetAddress.getByName(address),53);socket.send(new DatagramPacket(query,query.length));
            byte[] buffer=new byte[65535];DatagramPacket packet=new DatagramPacket(buffer,buffer.length);socket.receive(packet);
            return Arrays.copyOf(buffer,packet.getLength());
        }finally{sockets.remove(socket);socket.close();}
    }
    private byte[] tcp(Network network,String address,byte[] query) throws IOException {
        Socket socket=new Socket();
        try {
            register(socket);
            SocketSetup.prepare(socket,vpn==null?null:()->vpn.protect(socket),()->network.bindSocket(socket),"TCP DNS");socket.connect(new InetSocketAddress(address,53),2500);socket.setSoTimeout(4000);
            DataOutputStream out=new DataOutputStream(socket.getOutputStream());out.writeShort(query.length);out.write(query);out.flush();
            DataInputStream in=new DataInputStream(socket.getInputStream());int size=in.readUnsignedShort();
            if(size<12) throw new IOException("Неполный TCP DNS-ответ");byte[] answer=new byte[size];in.readFully(answer);return answer;
        }finally{sockets.remove(socket);socket.close();}
    }
    private void ensureOpen() throws IOException {if(closed.get()||Thread.currentThread().isInterrupted())throw new IOException("Запрос отменён");}
    private void register(Closeable socket) throws IOException {
        ensureOpen();sockets.add(socket);
        if(closed.get()){sockets.remove(socket);socket.close();throw new IOException("Запрос отменён");}
    }
    @Override public synchronized void close() {
        if(!closed.compareAndSet(false,true))return;
        for(Call call:calls)call.cancel();calls.clear();
        for(Closeable socket:sockets)try{socket.close();}catch(IOException ignored){}sockets.clear();
        closeClients();
    }
}
