package ru.kodvsego.smartdns;

import org.junit.Test;
import java.io.IOException;
import java.util.*;
import static org.junit.Assert.*;

public class DnsCoreTest {
    interface Checked {void run() throws Exception;}
    static void fails(Checked work){try{work.run();fail("Expected rejection");}catch(IllegalArgumentException|IOException expected){}catch(Exception e){throw new AssertionError(e);}}
    static byte[] answer(byte[] q,int first) throws Exception {
        byte[] result=Arrays.copyOf(q,q.length+16);result[2]=(byte)0x81;result[3]=(byte)0x80;result[7]=1;
        byte[] rr={(byte)0xc0,12,0,1,0,1,0,0,0,60,0,4,(byte)first,2,3,4};System.arraycopy(rr,0,result,q.length,rr.length);return result;
    }
    static byte[] packet(int protocol,long seq,long ack,int flags,byte[] payload) {
        int transport=protocol==17?8:20;byte[] p=new byte[20+transport+payload.length];p[0]=0x45;DnsWire.put16(p,2,p.length);p[9]=(byte)protocol;
        p[12]=10;p[13]=111;p[15]=1;p[16]=10;p[17]=111;p[19]=2;
        DnsWire.put16(p,20,43000);DnsWire.put16(p,22,53);
        if(protocol==17)DnsWire.put16(p,24,payload.length+8);
        else{IpPacket.put32(p,24,seq);IpPacket.put32(p,28,ack);p[32]=0x50;p[33]=(byte)flags;DnsWire.put16(p,34,32768);}
        System.arraycopy(payload,0,p,20+transport,payload.length);return p;
    }
    static IpPacket tcp(long seq,long ack,int flags,byte[] data) throws Exception {byte[] p=packet(6,seq,ack,flags,data);return IpPacket.parse(p,p.length);}
    static final byte[] EMPTY=new byte[0];
    @Test public void sixProvidersMatchWindows(){assertEquals(6,Provider.ALL.length);assertEquals("DNS-AI",Provider.at(5).name);assertEquals("194.87.196.13",Provider.at(5).servers[0]);}
    @Test public void invalidProviderRejected(){fails(()->Provider.at(-1));fails(()->Provider.at(6));}
    @Test public void encryptedOnlyCannotDowngrade(){assertTrue(new DnsConfig(5,false,-1).encrypted);}
    @Test public void missingDohCannotDowngrade(){fails(()->new DnsConfig(2,true,-1));fails(()->new DnsConfig(3,true,-1));}
    @Test public void unsupportedAntigravityRejected(){fails(()->new DnsConfig(0,false,0));fails(()->new DnsConfig(0,true,2));}
    @Test public void antigravityMatchesExactDomainsOnly(){DnsConfig c=new DnsConfig(0,false,4);assertSame(Provider.at(4),c.forHost("CLOUDCODE-PA.GOOGLEAPIS.COM"));assertSame(Provider.at(0),c.forHost("evil.cloudcode-pa.googleapis.com"));assertSame(Provider.at(0),c.forHost("cloudcode-pa.googleapis.com.evil"));}
    @Test public void dohAntigravityRemainsEncrypted(){DnsConfig c=new DnsConfig(0,true,4);assertTrue(c.encrypted);assertNotNull(c.forHost(Provider.AG_DOMAINS[0]).doh);}
    @Test public void queryRoundTrip() throws Exception{byte[] q=DnsWire.query("Gemini.Google.Com");assertEquals("gemini.google.com",DnsWire.question(q).host);assertEquals(1,DnsWire.question(q).type);}
    @Test public void corruptQueriesRejected(){fails(()->DnsWire.question(new byte[12]));fails(()->DnsWire.query("bad..host"));fails(()->DnsWire.query(String.join("",Collections.nCopies(64,"a"))+".com"));}
    @Test public void compressedQueryRejected() throws Exception{byte[] q=DnsWire.query("example.com");q[12]=(byte)0xc0;q[13]=12;fails(()->DnsWire.question(q));}
    @Test public void forgedTransactionRejected() throws Exception{byte[] q=DnsWire.query("example.com"),a=answer(q,93);a[0]^=1;fails(()->DnsWire.validate(q,a));}
    @Test public void differentQuestionRejected() throws Exception{byte[] q=DnsWire.query("example.com"),a=answer(q,93);a[13]='z';fails(()->DnsWire.validate(q,a));}
    @Test public void queryCannotBeUsedAsResponse() throws Exception{byte[] q=DnsWire.query("example.com");fails(()->DnsWire.validate(q,q));}
    @Test public void nxdomainIsForwardedButNotReportedAsAccess() throws Exception{byte[] q=DnsWire.query("example.com"),a=DnsWire.failure(q);a[3]=(byte)0x83;DnsWire.validate(q,a);fails(()->DnsWire.requireAddress(a));}
    @Test public void servfailPreservesQuestionAndId() throws Exception{byte[] q=DnsWire.query("example.com"),a=DnsWire.failure(q);DnsWire.validate(q,a);assertEquals(2,a[3]&15);assertEquals(0,DnsWire.u16(a,6));}
    @Test public void validAddressAccepted() throws Exception{DnsWire.requireAddress(answer(DnsWire.query("example.com"),93));}
    @Test public void blockedAddressRejected() throws Exception{byte[] q=DnsWire.query("example.com");fails(()->DnsWire.requireAddress(answer(q,0)));fails(()->DnsWire.requireAddress(answer(q,127)));}
    @Test public void incompleteRecordRejected() throws Exception{byte[] a=answer(DnsWire.query("example.com"),93);fails(()->DnsWire.requireAddress(Arrays.copyOf(a,a.length-1)));}
    @Test public void truncationKeepsOnlyQuestion() throws Exception{byte[] q=DnsWire.query("example.com"),a=DnsWire.truncated(answer(q,93));assertEquals(q.length,a.length);assertTrue((a[2]&2)!=0);assertEquals(0,DnsWire.u16(a,6));DnsWire.validate(q,a);}
    @Test public void udpReplyHasCorrectChecksums() throws Exception{
        byte[] q=DnsWire.query("example.com"),wire=packet(17,0,0,0,q);IpPacket p=IpPacket.parse(wire,wire.length);byte[] out=p.udpReply(answer(q,93));
        assertEquals(0,IpPacket.checksum(out,0,20,0));assertEquals(53,DnsWire.u16(out,20));assertEquals(43000,DnsWire.u16(out,22));
        int pseudo=17+out.length-20;for(int i=12;i<20;i+=2)pseudo+=DnsWire.u16(out,i);assertEquals(0,IpPacket.checksum(out,20,out.length-20,pseudo));
    }
    @Test public void udpPayloadIsolated() throws Exception{byte[] q=DnsWire.query("example.com"),wire=packet(17,0,0,0,q);assertArrayEquals(q,IpPacket.parse(wire,wire.length).payload);}
    @Test public void otherTrafficAndFragmentsRejected() {byte[] p=packet(17,0,0,0,new byte[20]);p[19]=3;fails(()->IpPacket.parse(p,p.length));p[19]=2;p[6]=0x20;fails(()->IpPacket.parse(p,p.length));}
    @Test public void malformedPacketLengthsRejected(){byte[] p=packet(17,0,0,0,new byte[20]);DnsWire.put16(p,24,4);fails(()->IpPacket.parse(p,p.length));fails(()->IpPacket.parse(new byte[3],3));}
    @Test public void tcpReplyHasCorrectChecksum() throws Exception{byte[] out=tcp(1,0,2,EMPTY).tcpReply(700,2,0x12,EMPTY);int pseudo=6+20;for(int i=12;i<20;i+=2)pseudo+=DnsWire.u16(out,i);assertEquals(0,IpPacket.checksum(out,20,20,pseudo));}
    @Test public void handshakeAndSegmentedDnsQuery() throws Exception {
        List<byte[]> out=new ArrayList<>();List<byte[]> queries=new ArrayList<>();TcpDnsProxy proxy=new TcpDnsProxy(out::add,(q,done)->{queries.add(q);try{done.complete(answer(q,93));}catch(Exception e){throw new AssertionError(e);}});
        long now=System.currentTimeMillis();proxy.accept(tcp(100,0,2,EMPTY),now);long server=IpPacket.u32(out.get(0),24);
        proxy.accept(tcp(101,(server+1)&0xffffffffL,16,EMPTY),now);
        byte[] query=DnsWire.query("example.com"),frame=new byte[query.length+2];DnsWire.put16(frame,0,query.length);System.arraycopy(query,0,frame,2,query.length);
        proxy.accept(tcp(101,(server+1)&0xffffffffL,24,Arrays.copyOfRange(frame,0,5)),now);assertEquals(0,queries.size());
        proxy.accept(tcp(106,(server+1)&0xffffffffL,24,Arrays.copyOfRange(frame,5,frame.length)),now);assertEquals(1,queries.size());assertArrayEquals(query,queries.get(0));
        byte[] response=out.get(out.size()-1);assertTrue(response.length>40);assertEquals(query.length+16,DnsWire.u16(response,40));
    }
    @Test public void retransmissionAndIdleEviction() throws Exception {
        List<byte[]> out=new ArrayList<>();TcpDnsProxy proxy=new TcpDnsProxy(out::add,(q,c)->{});long now=System.currentTimeMillis();
        proxy.accept(tcp(100,0,2,EMPTY),now);byte[] syn=out.get(0);proxy.tick(now+1100);assertArrayEquals(syn,out.get(1));
        proxy.tick(now+31000);assertEquals(0x14,out.get(out.size()-1)[33]&255);
    }
    @Test public void tcpSessionLimitIsBounded() throws Exception {
        List<byte[]> out=new ArrayList<>();TcpDnsProxy proxy=new TcpDnsProxy(out::add,(q,c)->{});for(int i=0;i<100;i++){byte[] p=packet(6,100,0,2,EMPTY);DnsWire.put16(p,20,1000+i);proxy.accept(IpPacket.parse(p,p.length),System.currentTimeMillis());}assertEquals(64,out.size());
    }
    @Test public void dnsAiTestsGeminiWebsiteAndApi(){List<String> hosts=Arrays.asList(Provider.at(5).testHosts());assertTrue(hosts.contains("gemini.google.com"));assertTrue(hosts.contains("generativelanguage.googleapis.com"));}
}
