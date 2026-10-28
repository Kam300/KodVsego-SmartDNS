package ru.kodvsego.smartdns;

import org.junit.Test;
import java.io.*;
import java.net.*;
import java.util.concurrent.*;
import javax.net.ssl.*;
import okhttp3.tls.*;
import static org.junit.Assert.*;

public class DotTransportTest {
    @Test public void framedRepliesRejectTruncationAndWrongTransaction() throws Exception {
        byte[] query=DnsWire.query("example.com"),reply=DnsWire.failure(query);ByteArrayOutputStream bytes=new ByteArrayOutputStream();DataOutputStream out=new DataOutputStream(bytes);out.writeShort(reply.length);out.write(reply);
        assertArrayEquals(reply,DotTransport.read(new ByteArrayInputStream(bytes.toByteArray()),query));
        try{DotTransport.read(new ByteArrayInputStream(new byte[]{0,1,0}),query);fail();}catch(IOException expected){}
        byte[] wrong=bytes.toByteArray();wrong[2]^=1;try{DotTransport.read(new ByteArrayInputStream(wrong),query);fail();}catch(IOException expected){}
        byte[] shortReply=java.util.Arrays.copyOf(bytes.toByteArray(),4);try{DotTransport.read(new ByteArrayInputStream(shortReply),query);fail();}catch(IOException expected){}
    }
    @Test public void tlsRoundTripVerifiesHostnameAndUsesDnsFrames() throws Exception {roundTrip("localhost",true);}
    @Test public void tlsRejectsHostnameMismatchBeforeSendingDns() throws Exception {roundTrip("wrong.example",false);}
    private void roundTrip(String hostname,boolean success) throws Exception {
        HeldCertificate cert=new HeldCertificate.Builder().commonName("localhost").addSubjectAlternativeName("localhost").build();
        HandshakeCertificates serverTls=new HandshakeCertificates.Builder().heldCertificate(cert).build(),clientTls=new HandshakeCertificates.Builder().addTrustedCertificate(cert.certificate()).build();
        ExecutorService worker=Executors.newSingleThreadExecutor();byte[] query=DnsWire.query("example.com");
        try(SSLServerSocket server=(SSLServerSocket)serverTls.sslContext().getServerSocketFactory().createServerSocket(0,1,InetAddress.getLoopbackAddress())){
            Future<?> peer=worker.submit(()->{try(SSLSocket socket=(SSLSocket)server.accept()){socket.setSoTimeout(5000);DataInputStream in=new DataInputStream(socket.getInputStream());byte[] incoming=new byte[in.readUnsignedShort()];in.readFully(incoming);if(!success)throw new AssertionError("DNS was sent before hostname verification");byte[] reply=DnsWire.failure(incoming);DataOutputStream out=new DataOutputStream(socket.getOutputStream());out.writeShort(reply.length);out.write(reply);out.flush();}catch(IOException error){if(success)throw new RuntimeException(error);}});
            try(Socket plain=new Socket(InetAddress.getLoopbackAddress(),server.getLocalPort());SSLSocket client=(SSLSocket)clientTls.sslSocketFactory().createSocket(plain,hostname,server.getLocalPort(),true)){
                try{byte[] reply=DotTransport.exchange(client,query,hostname);assertTrue(success);assertArrayEquals(DnsWire.failure(query),reply);assertEquals("HTTPS",client.getSSLParameters().getEndpointIdentificationAlgorithm());}catch(IOException error){if(success){try{peer.get(6,TimeUnit.SECONDS);}catch(Exception serverError){error.addSuppressed(serverError);}throw error;}assertTrue(error instanceof SSLException);}
            }peer.get(5,TimeUnit.SECONDS);
        }finally{worker.shutdownNow();}
    }
}
