package ru.kodvsego.smartdns;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.security.SecureRandom;
import java.util.*;

/** Bounded DNS-only TCP endpoint: ordered frames, duplicate ACKs, retransmission and flow control. */
public final class TcpDnsProxy {
    public interface Output { void write(byte[] packet) throws IOException; }
    public interface Resolver { void resolve(byte[] query, Completion completion); }
    public interface Completion { void complete(byte[] answer); }
    private static final byte[] EMPTY=new byte[0];
    private final Map<String,Session> sessions=new HashMap<>();
    private final Output output; private final Resolver resolver; private final SecureRandom random=new SecureRandom();
    private static long add(long value,int count) {return (value+count)&0xffffffffL;}
    public TcpDnsProxy(Output output,Resolver resolver) {this.output=output;this.resolver=resolver;}
    private static final class Session {
        IpPacket packet; long recv,send,last,transmitted; int retries,window=65535;
        boolean established,working,clientFin,serverFin;
        byte[] unacked; int advance;
        ByteArrayOutputStream input=new ByteArrayOutputStream();
        ArrayDeque<byte[]> answers=new ArrayDeque<>();int answerOffset;
    }
    public synchronized void accept(IpPacket p,long now) throws IOException {
        String key=p.key();Session s=sessions.get(key);
        if((p.flags&4)!=0) {sessions.remove(key);return;}
        if((p.flags&2)!=0) {
            if(s!=null && s.recv==add(p.sequence,1) && !s.established) { if(s.unacked!=null) output.write(s.unacked);return; }
            if(sessions.size()>=64) return;
            s=new Session();s.packet=p;s.recv=add(p.sequence,1);s.send=random.nextInt()&0xffffffffL;s.last=now;
            sessions.put(key,s);send(s,0x12,EMPTY,1,now);return;
        }
        if(s==null) return;
        s.last=now;s.window=p.window;
        if((p.flags&16)!=0 && s.unacked!=null && p.acknowledgement==add(s.send,s.advance)) {
            s.send=add(s.send,s.advance);s.unacked=null;s.retries=0;
            if(!s.established) s.established=true;
            if(s.serverFin) {sessions.remove(key);return;}
        }
        if(!s.established) return;
        if(p.sequence!=s.recv) {ack(s);return;}
        if(p.payload.length>0) {
            if(s.input.size()+p.payload.length>65537) {reset(key,s);return;}
            s.input.write(p.payload);s.recv=add(s.recv,p.payload.length);ack(s);
        }
        if((p.flags&1)!=0) {s.recv=add(s.recv,1);s.clientFin=true;ack(s);}
        drain(key,s);flush(s,now);
    }
    private void drain(String key,Session s) throws IOException {
        if(s.working || s.answers.size()>=8) return;
        byte[] buffered=s.input.toByteArray();if(buffered.length<2) return;
        int length=DnsWire.u16(buffered,0);
        if(length<17) {reset(key,s);return;}
        if(buffered.length<length+2) return;
        byte[] query=Arrays.copyOfRange(buffered,2,length+2);
        s.input.reset();s.input.write(buffered,length+2,buffered.length-length-2);s.working=true;
        resolver.resolve(query,answer -> {
            synchronized(TcpDnsProxy.this) {
                if(sessions.get(key)!=s) return;
                s.working=false;
                if(answer==null || answer.length>65535) {try {reset(key,s);}catch(IOException ignored){}return;}
                byte[] frame=new byte[answer.length+2];DnsWire.put16(frame,0,answer.length);System.arraycopy(answer,0,frame,2,answer.length);s.answers.add(frame);
                try {flush(s,System.currentTimeMillis());drain(key,s);}catch(IOException ignored){sessions.remove(key);}
            }
        });
    }
    private void flush(Session s,long now) throws IOException {
        if(s.unacked!=null || !s.established) return;
        if(!s.answers.isEmpty() && s.window>0) {
            byte[] frame=s.answers.peek();int length=Math.min(Math.min(512,s.window),frame.length-s.answerOffset);
            byte[] data=Arrays.copyOfRange(frame,s.answerOffset,s.answerOffset+length);s.answerOffset+=length;
            if(s.answerOffset==frame.length) {s.answers.remove();s.answerOffset=0;}
            send(s,0x18,data,length,now);
        } else if(s.clientFin && !s.working && s.answers.isEmpty() && s.input.size()==0) {
            s.serverFin=true;send(s,0x11,EMPTY,1,now);
        }
    }
    private void send(Session s,int flags,byte[] data,int advance,long now) throws IOException {
        s.unacked=s.packet.tcpReply(s.send,s.recv,flags,data);s.advance=advance;s.transmitted=now;output.write(s.unacked);
    }
    private void ack(Session s) throws IOException {output.write(s.packet.tcpReply(add(s.send,s.unacked==null?0:s.advance),s.recv,0x10,EMPTY));}
    private void reset(String key,Session s) throws IOException {sessions.remove(key);output.write(s.packet.tcpReply(s.send,s.recv,0x14,EMPTY));}
    public synchronized void tick(long now) throws IOException {
        Iterator<Map.Entry<String,Session>> it=sessions.entrySet().iterator();
        while(it.hasNext()) {
            Session s=it.next().getValue();
            if(now-s.last>30000) {it.remove();output.write(s.packet.tcpReply(s.send,s.recv,0x14,EMPTY));continue;}
            if(s.unacked!=null && now-s.transmitted>1000) {
                if(++s.retries>5) {it.remove();continue;}
                output.write(s.unacked);s.transmitted=now;
            } else if(s.unacked==null) flush(s,now);
        }
    }
    public synchronized void close() {sessions.clear();}
}
