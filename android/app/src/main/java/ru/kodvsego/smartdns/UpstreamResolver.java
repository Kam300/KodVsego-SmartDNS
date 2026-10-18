package ru.kodvsego.smartdns;

import android.content.Context;
import android.net.Network;
import android.net.VpnService;
import okhttp3.*;
import java.io.*;
import java.net.*;
import java.util.Arrays;
import java.util.concurrent.TimeUnit;

/** All upstream connections are bound to the physical network, outside the local DNS VPN. */
final class UpstreamResolver implements Closeable {
    private final Context context;private final VpnService vpn;
    private Network clientNetwork;private OkHttpClient client;
    UpstreamResolver(Context context,VpnService vpn) {this.context=context;this.vpn=vpn;}
    byte[] resolve(byte[] query,DnsConfig config) throws IOException {
        DnsWire.Question question=DnsWire.question(query);
        return resolve(query,config.forHost(question.host),config.encrypted);
    }
    byte[] resolve(byte[] query,Provider provider,boolean encrypted) throws IOException {
        Network network=PhysicalNetwork.find(context);PhysicalNetwork.checkPrivateDns(context,network);
        if(encrypted||provider.encryptedOnly) {
            if(provider.doh==null) throw new IOException("DoH для провайдера недоступен");
            return DohTransport.exchange(client(network),provider.doh,query);
        }
        IOException last=new IOException("DNS не ответил");
        for(String address:provider.servers) {
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
    private synchronized OkHttpClient client(Network network) {
        if(client==null||!network.equals(clientNetwork)) {
            if(client!=null) {client.dispatcher().cancelAll();client.connectionPool().evictAll();client.dispatcher().executorService().shutdown();}
            clientNetwork=network;
            client=new OkHttpClient.Builder().socketFactory(network.getSocketFactory())
                .dns(host -> Arrays.asList(network.getAllByName(host))).proxy(Proxy.NO_PROXY)
                .connectTimeout(4,TimeUnit.SECONDS).readTimeout(5,TimeUnit.SECONDS).callTimeout(8,TimeUnit.SECONDS)
                .followRedirects(false).followSslRedirects(false).retryOnConnectionFailure(false).build();
        }
        return client;
    }
    private byte[] udp(Network network,String address,byte[] query) throws IOException {
        try(DatagramSocket socket=new DatagramSocket(null)) {
            if(vpn!=null&&!vpn.protect(socket)) throw new IOException("Не удалось защитить DNS-сокет");
            network.bindSocket(socket);socket.bind(new InetSocketAddress(0));socket.setSoTimeout(2500);
            socket.connect(InetAddress.getByName(address),53);socket.send(new DatagramPacket(query,query.length));
            byte[] buffer=new byte[65535];DatagramPacket packet=new DatagramPacket(buffer,buffer.length);socket.receive(packet);
            return Arrays.copyOf(buffer,packet.getLength());
        }
    }
    private byte[] tcp(Network network,String address,byte[] query) throws IOException {
        try(Socket socket=new Socket()) {
            if(vpn!=null&&!vpn.protect(socket)) throw new IOException("Не удалось защитить TCP DNS-сокет");
            network.bindSocket(socket);socket.connect(new InetSocketAddress(address,53),2500);socket.setSoTimeout(4000);
            DataOutputStream out=new DataOutputStream(socket.getOutputStream());out.writeShort(query.length);out.write(query);out.flush();
            DataInputStream in=new DataInputStream(socket.getInputStream());int size=in.readUnsignedShort();
            if(size<12) throw new IOException("Неполный TCP DNS-ответ");byte[] answer=new byte[size];in.readFully(answer);return answer;
        }
    }
    @Override public synchronized void close() {
        if(client!=null){client.dispatcher().cancelAll();client.connectionPool().evictAll();client.dispatcher().executorService().shutdown();client=null;}
    }
}
