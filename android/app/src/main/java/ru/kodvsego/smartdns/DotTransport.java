package ru.kodvsego.smartdns;

import java.io.*;
import javax.net.ssl.SSLParameters;
import javax.net.ssl.SSLSocket;

/** Length-prefixed DNS over a TLS socket with platform hostname verification. */
final class DotTransport {
    // Reuse the hostname verifier of the pinned OkHttp 4.12 transport without
    // constructing another HTTP client or initializing its platform globally.
    private static final javax.net.ssl.HostnameVerifier HOSTNAMES=okhttp3.internal.tls.OkHostnameVerifier.INSTANCE;
    static byte[] exchange(SSLSocket socket,byte[] query,String hostname) throws IOException {
        SSLParameters parameters=socket.getSSLParameters();parameters.setEndpointIdentificationAlgorithm("HTTPS");socket.setSSLParameters(parameters);
        socket.setSoTimeout(5000);socket.startHandshake();
        if(!HOSTNAMES.verify(hostname,socket.getSession()))throw new javax.net.ssl.SSLPeerUnverifiedException("DoT: имя сертификата не совпадает с сервером");
        DataOutputStream out=new DataOutputStream(socket.getOutputStream());out.writeShort(query.length);out.write(query);out.flush();
        return read(socket.getInputStream(),query);
    }
    static byte[] read(InputStream input,byte[] query) throws IOException {
        DataInputStream in=new DataInputStream(input);int size=in.readUnsignedShort();
        if(size<12)throw new IOException("DoT: неполный DNS-ответ");
        byte[] answer=new byte[size];in.readFully(answer);DnsWire.validate(query,answer);return answer;
    }
}
