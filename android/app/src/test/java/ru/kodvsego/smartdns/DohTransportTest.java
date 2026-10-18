package ru.kodvsego.smartdns;

import okhttp3.*;
import okhttp3.mockwebserver.*;
import okhttp3.tls.*;
import okio.Buffer;
import org.junit.*;
import java.io.IOException;
import java.util.Arrays;
import java.util.concurrent.TimeUnit;
import static org.junit.Assert.*;

public class DohTransportTest {
    private MockWebServer server;private OkHttpClient client;private byte[] query;
    @Before public void setup() throws Exception {
        HeldCertificate certificate=new HeldCertificate.Builder().commonName("localhost").addSubjectAlternativeName("localhost").addSubjectAlternativeName("127.0.0.1").build();
        HandshakeCertificates identity=new HandshakeCertificates.Builder().heldCertificate(certificate).build();
        HandshakeCertificates trusted=new HandshakeCertificates.Builder().addTrustedCertificate(certificate.certificate()).build();
        server=new MockWebServer();server.useHttps(identity.sslSocketFactory(),false);server.start(java.net.InetAddress.getByAddress(new byte[]{127,0,0,1}),0);
        client=new OkHttpClient.Builder().sslSocketFactory(trusted.sslSocketFactory(),trusted.trustManager()).followRedirects(false).followSslRedirects(false).retryOnConnectionFailure(false).callTimeout(3,TimeUnit.SECONDS).build();
        query=DnsWire.query("gemini.google.com");
    }
    @After public void close() throws Exception{client.dispatcher().cancelAll();client.connectionPool().evictAll();client.dispatcher().executorService().shutdown();server.shutdown();}
    private MockResponse valid() throws Exception{return new MockResponse().setHeader("Content-Type","application/dns-message").setBody(new Buffer().write(DnsCoreTest.answer(query,93)));}
    private HttpUrl url(){return server.url("/dns-query").newBuilder().host("127.0.0.1").build();}
    private byte[] exchange() throws Exception{return DohTransport.exchange(client,url().toString(),query);}
    private void rejects(String expected) throws Exception {try{exchange();fail("Expected rejection");}catch(IOException error){assertTrue(error.toString(),error.getMessage().contains(expected));}assertEquals(1,server.getRequestCount());}
    @Test public void postsDnsAndNegotiatesHttp2() throws Exception {
        server.enqueue(valid());DnsWire.requireAddress(exchange());RecordedRequest request=server.takeRequest();
        assertEquals("POST",request.getMethod());assertEquals("application/dns-message",request.getHeader("Content-Type"));
        assertArrayEquals(query,request.getBody().readByteArray());assertTrue(request.getRequestLine().contains("HTTP/1.1")||request.getRequestLine().contains("HTTP/2"));
        // A second request on the same client confirms HTTP/2 ALPN, independent of request-line formatting.
        server.enqueue(valid());try(Response response=client.newCall(new Request.Builder().url(url()).build()).execute()){assertEquals(Protocol.HTTP_2,response.protocol());}
    }
    @Test public void rejectsCleartextAndCredentials(){DnsCoreTest.fails(()->DohTransport.exchange(client,"http://localhost/dns-query",query));DnsCoreTest.fails(()->DohTransport.exchange(client,"https://user:password@localhost/dns-query",query));}
    @Test public void refusesRedirectWithoutFollowing() throws Exception {server.enqueue(new MockResponse().setResponseCode(302).addHeader("Location","https://example.com/dns-query"));rejects("HTTP 302");}
    @Test public void rejectsHtmlInsteadOfDns() throws Exception {server.enqueue(new MockResponse().setHeader("Content-Type","text/html").setBody("error"));rejects("не DNS-ответ");}
    @Test public void rejectsHttpError() throws Exception{server.enqueue(new MockResponse().setResponseCode(505));rejects("HTTP 505");}
    @Test public void rejectsForgedDnsAnswer() throws Exception{byte[] wrong=DnsCoreTest.answer(query,93);wrong[0]^=1;server.enqueue(new MockResponse().setHeader("Content-Type","application/dns-message").setBody(new Buffer().write(wrong)));rejects("не соответствует");}
    @Test public void rejectsOversizedChunkedBody() throws Exception{server.enqueue(new MockResponse().setHeader("Content-Type","application/dns-message").setChunkedBody(new Buffer().write(new byte[65536]),4096));rejects("Слишком большой");}
    @Test public void defaultTlsRejectsUntrustedCertificate() throws Exception{
        server.enqueue(valid());OkHttpClient untrusted=new OkHttpClient.Builder().callTimeout(3,TimeUnit.SECONDS).build();
        try{try{DohTransport.exchange(untrusted,url().toString(),query);fail("Expected certificate rejection");}catch(javax.net.ssl.SSLHandshakeException expected){assertEquals(0,server.getRequestCount());}}
        finally{untrusted.dispatcher().cancelAll();untrusted.connectionPool().evictAll();untrusted.dispatcher().executorService().shutdown();}
    }
}
