package ru.kodvsego.smartdns;

import java.io.IOException;
import java.util.Arrays;

/** Only the synthetic IPv4 DNS address is routed to TUN; IPv6 internet traffic bypasses it. */
public final class IpPacket {
    public final byte[] source, destination, payload;
    public final int protocol, sourcePort, destinationPort, flags, window;
    public final long sequence, acknowledgement;
    private IpPacket(byte[] source, byte[] destination, byte[] payload, int protocol,
                     int sourcePort, int destinationPort, int flags, int window, long seq, long ack) {
        this.source=source; this.destination=destination; this.payload=payload; this.protocol=protocol;
        this.sourcePort=sourcePort; this.destinationPort=destinationPort; this.flags=flags;
        this.window=window; this.sequence=seq; this.acknowledgement=ack;
    }
    public static long u32(byte[] b,int p) { return ((long)(b[p]&255)<<24)|((long)(b[p+1]&255)<<16)|((long)(b[p+2]&255)<<8)|(b[p+3]&255); }
    public static void put32(byte[] b,int p,long v) { for(int i=3;i>=0;i--) {b[p+i]=(byte)v;v>>>=8;} }
    public static IpPacket parse(byte[] bytes, int length) throws IOException {
        if(length<28 || length>bytes.length || (bytes[0]>>>4 &15)!=4) throw new IOException("Invalid IPv4 packet");
        int header=(bytes[0]&15)*4, total=DnsWire.u16(bytes,2), protocol=bytes[9]&255;
        if(header<20 || total>length || total<header+8 || (DnsWire.u16(bytes,6)&0x3fff)!=0 ||
            !Arrays.equals(Arrays.copyOfRange(bytes,16,20),new byte[]{10,111,0,2})) throw new IOException("Unrouted or fragmented packet");
        int start=header, end=total, flags=0, window=0; long seq=0,ack=0;
        if(protocol==17) {
            int size=DnsWire.u16(bytes,header+4);
            if(size<8 || header+size>total) throw new IOException("Invalid UDP size");
            end=header+size; start+=8;
        } else if(protocol==6) {
            if(total<header+20) throw new IOException("Invalid TCP header");
            int size=((bytes[header+12]&255)>>>4)*4;
            if(size<20 || header+size>total) throw new IOException("Invalid TCP offset");
            start+=size; flags=bytes[header+13]&255; window=DnsWire.u16(bytes,header+14);
            seq=u32(bytes,header+4); ack=u32(bytes,header+8);
        } else throw new IOException("Only DNS UDP/TCP is supported");
        int destinationPort=DnsWire.u16(bytes,header+2);
        if(destinationPort!=53) throw new IOException("Not a DNS packet");
        return new IpPacket(Arrays.copyOfRange(bytes,12,16),Arrays.copyOfRange(bytes,16,20),
            Arrays.copyOfRange(bytes,start,end),protocol,DnsWire.u16(bytes,header),destinationPort,flags,window,seq,ack);
    }
    public byte[] udpReply(byte[] answer) { return reply(17,0,0,0,answer); }
    public byte[] tcpReply(long seq,long ack,int flags,byte[] payload) {return reply(6,seq,ack,flags,payload);}
    private byte[] reply(int proto,long seq,long ack,int flags,byte[] data) {
        int transport=proto==17?8:20; byte[] out=new byte[20+transport+data.length];
        out[0]=0x45; DnsWire.put16(out,2,out.length); out[8]=64;out[9]=(byte)proto;
        System.arraycopy(destination,0,out,12,4);System.arraycopy(source,0,out,16,4);
        DnsWire.put16(out,10,checksum(out,0,20,0));
        DnsWire.put16(out,20,destinationPort);DnsWire.put16(out,22,sourcePort);
        if(proto==17) DnsWire.put16(out,24,transport+data.length);
        else {put32(out,24,seq);put32(out,28,ack);out[32]=0x50;out[33]=(byte)flags;DnsWire.put16(out,34,32768);}
        System.arraycopy(data,0,out,20+transport,data.length);
        int pseudo=proto+transport+data.length;
        for(int p=12;p<20;p+=2) pseudo+=((out[p]&255)<<8)|(out[p+1]&255);
        int sum=checksum(out,20,transport+data.length,pseudo);
        DnsWire.put16(out,proto==17?26:36,sum==0?65535:sum);return out;
    }
    public static int checksum(byte[] b,int offset,int length,int sum) {
        for(int i=0;i<length;i+=2) sum+=((b[offset+i]&255)<<8)|(i+1<length?(b[offset+i+1]&255):0);
        while((sum>>>16)!=0) sum=(sum&65535)+(sum>>>16);return (~sum)&65535;
    }
    public String key() { return Arrays.toString(source)+":"+sourcePort; }
}
