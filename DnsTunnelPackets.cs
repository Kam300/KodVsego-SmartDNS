using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;

namespace HbAi.XboxDns
{
    // DNS-only endpoint, based on the bounded packet/TCP handling in our Android client.
    sealed class TunnelPacket
    {
        internal byte[] Source,Destination,Payload;
        internal int Protocol,SourcePort,DestinationPort,Flags,Window;
        internal uint Sequence,Acknowledgement;
        internal static int U16(byte[] b,int p){return (b[p]<<8)|b[p+1];}
        internal static uint U32(byte[] b,int p){return (uint)b[p]<<24|(uint)b[p+1]<<16|(uint)b[p+2]<<8|b[p+3];}
        internal static void Put16(byte[] b,int p,int v){b[p]=(byte)(v>>8);b[p+1]=(byte)v;}
        internal static void Put32(byte[] b,int p,uint v){for(int i=3;i>=0;i--){b[p+i]=(byte)v;v>>=8;}}
        internal static byte[] Slice(byte[] b,int start,int end){var result=new byte[end-start];Buffer.BlockCopy(b,start,result,0,result.Length);return result;}
        internal static int Checksum(byte[] b,int start,int length,int sum=0){for(int i=0;i<length;i+=2)sum+=(b[start+i]<<8)|(i+1<length?b[start+i+1]:0);while((sum>>16)!=0)sum=(sum&65535)+(sum>>16);return ~sum&65535;}
        internal static TunnelPacket Parse(byte[] b)
        {
            if(b==null||b.Length<28)throw new IOException("Короткий IP-пакет.");
            int version=b[0]>>4,header,total,protocol,source;
            if(version==4){header=(b[0]&15)*4;total=U16(b,2);protocol=b[9];source=12;if(header<20||(U16(b,6)&0x3fff)!=0||Checksum(b,0,Math.Min(header,b.Length))!=0)throw new IOException("Фрагмент или неверный IPv4.");}
            else if(version==6&&b.Length>=48){header=40;total=40+U16(b,4);protocol=b[6];source=8;}
            else throw new IOException("Неизвестный IP-пакет.");
            int length=version==4?4:16;
            if(total!=b.Length||total<header+8)throw new IOException("Неверный размер IP.");
            var p=new TunnelPacket{Source=Slice(b,source,source+length),Destination=Slice(b,source+length,source+2*length),Protocol=protocol};
            if(!p.Destination.SequenceEqual(IPAddress.Parse(version==4?DnsTunnelNative.V4:DnsTunnelNative.V6).GetAddressBytes()))throw new IOException("Адрес вне DNS-туннеля.");
            int start=header,end=total;
            if(protocol==17){int size=U16(b,header+4);if(size<8||header+size!=total)throw new IOException("Неверный UDP.");start+=8;}
            else if(protocol==6){if(total<header+20)throw new IOException("Короткий TCP.");int offset=(b[header+12]>>4)*4;if(offset<20||header+offset>total)throw new IOException("Неверный TCP.");start+=offset;p.Flags=b[header+13];p.Window=U16(b,header+14);p.Sequence=U32(b,header+4);p.Acknowledgement=U32(b,header+8);}
            else throw new IOException("Туннель принимает только DNS UDP/TCP.");
            p.SourcePort=U16(b,header);p.DestinationPort=U16(b,header+2);if(p.DestinationPort!=53)throw new IOException("Порт вне DNS-туннеля.");
            int pseudo=protocol+total-header;
            for(int i=source;i<source+2*length;i+=2)pseudo+=U16(b,i);
            if((protocol==6||version==6||U16(b,header+6)!=0)&&Checksum(b,header,total-header,pseudo)!=0)throw new IOException("Неверная сумма UDP/TCP.");
            p.Payload=Slice(b,start,end);return p;
        }
        internal byte[] Reply(byte[] data,uint seq=0,uint ack=0,int flags=0)
        {
            bool v6=Source.Length==16;int ip=v6?40:20,transport=Protocol==17?8:20;
            var b=new byte[ip+transport+data.Length];if(b.Length>65535)throw new IOException("Ответ слишком велик.");
            b[0]=(byte)(v6?0x60:0x45);Put16(b,v6?4:2,v6?transport+data.Length:b.Length);b[v6?6:9]=(byte)Protocol;b[v6?7:8]=64;
            int address=v6?8:12;Buffer.BlockCopy(Destination,0,b,address,Source.Length);Buffer.BlockCopy(Source,0,b,address+Source.Length,Source.Length);
            if(!v6)Put16(b,10,Checksum(b,0,20));
            Put16(b,ip,DestinationPort);Put16(b,ip+2,SourcePort);
            if(Protocol==17)Put16(b,ip+4,transport+data.Length);else{Put32(b,ip+4,seq);Put32(b,ip+8,ack);b[ip+12]=0x50;b[ip+13]=(byte)flags;Put16(b,ip+14,32768);}
            Buffer.BlockCopy(data,0,b,ip+transport,data.Length);int sum=Protocol+transport+data.Length;
            for(int i=address;i<address+2*Source.Length;i+=2)sum+=U16(b,i);
            int checksum=Checksum(b,ip,transport+data.Length,sum);Put16(b,ip+(Protocol==17?6:16),checksum==0?65535:checksum);return b;
        }
        internal string Key {get{return new IPAddress(Source)+":"+SourcePort;}}
    }
    sealed class TunnelTcp : IDisposable
    {
        internal static long Now {get{return (long)(Stopwatch.GetTimestamp()*1000.0/Stopwatch.Frequency);}}
        static readonly byte[] Empty=new byte[0];
        sealed class Session {
            internal TunnelPacket Packet;internal uint Recv,Send;internal long Last,Transmitted;internal int Retries,Window=65535,Advance,Offset;
            internal bool Established,Working,ClientFin,ServerFin;
            internal byte[] Unacked;internal MemoryStream Input=new MemoryStream();internal Queue<byte[]> Answers=new Queue<byte[]>();
        }
        readonly Dictionary<string,Session> sessions=new Dictionary<string,Session>();
        readonly Action<byte[]> output;readonly Action<byte[],Action<byte[]>> resolve;
        internal TunnelTcp(Action<byte[]> output,Action<byte[],Action<byte[]>> resolve){this.output=output;this.resolve=resolve;}
        static uint Add(uint n,int count){return unchecked(n+(uint)count);}
        void Send(Session s,int flags,byte[] data,int advance,long now){s.Unacked=s.Packet.Reply(data,s.Send,s.Recv,flags);s.Advance=advance;s.Transmitted=now;output(s.Unacked);}
        void Ack(Session s){output(s.Packet.Reply(Empty,Add(s.Send,s.Unacked==null?0:s.Advance),s.Recv,0x10));}
        void Reset(string key,Session s){sessions.Remove(key);output(s.Packet.Reply(Empty,s.Send,s.Recv,0x14));}
        internal void Accept(TunnelPacket p,long now)
        {lock(sessions){Session s;string key=p.Key;sessions.TryGetValue(key,out s);
            if((p.Flags&4)!=0){sessions.Remove(key);return;}
            if((p.Flags&2)!=0){if(s!=null&&!s.Established&&s.Recv==Add(p.Sequence,1)){if(s.Unacked!=null)output(s.Unacked);return;}if(sessions.Count>=64)return;
                var random=new byte[4];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(random);
                s=new Session{Packet=p,Recv=Add(p.Sequence,1),Send=BitConverter.ToUInt32(random,0),Last=now};sessions[key]=s;Send(s,0x12,Empty,1,now);return;}
            if(s==null)return;s.Last=now;s.Window=p.Window;
            if((p.Flags&16)!=0&&s.Unacked!=null&&p.Acknowledgement==Add(s.Send,s.Advance)){s.Send=Add(s.Send,s.Advance);s.Unacked=null;s.Retries=0;s.Established=true;if(s.ServerFin){sessions.Remove(key);return;}}
            if(!s.Established)return;if(p.Sequence!=s.Recv){Ack(s);return;}
            if(p.Payload.Length>0){if(s.Input.Length+p.Payload.Length>65537){Reset(key,s);return;}s.Input.Write(p.Payload,0,p.Payload.Length);s.Recv=Add(s.Recv,p.Payload.Length);Ack(s);}
            if((p.Flags&1)!=0){s.Recv=Add(s.Recv,1);s.ClientFin=true;Ack(s);}Drain(key,s);Flush(s,now);
        }}
        void Drain(string key,Session s)
        {
            if(s.Working||s.Answers.Count>=8)return;byte[] input=s.Input.ToArray();if(input.Length<2)return;int length=TunnelPacket.U16(input,0);
            if(length<17){Reset(key,s);return;}if(input.Length<length+2)return;var query=TunnelPacket.Slice(input,2,length+2);
            s.Input.SetLength(0);s.Input.Write(input,length+2,input.Length-length-2);s.Working=true;
            resolve(query,answer=>{lock(sessions){Session current;if(!sessions.TryGetValue(key,out current)||current!=s)return;s.Working=false;
                if(answer==null||answer.Length>65535){Reset(key,s);return;}var frame=new byte[answer.Length+2];TunnelPacket.Put16(frame,0,answer.Length);Buffer.BlockCopy(answer,0,frame,2,answer.Length);s.Answers.Enqueue(frame);Flush(s,Now);Drain(key,s);}});
        }
        void Flush(Session s,long now)
        {
            if(s.Unacked!=null||!s.Established)return;
            if(s.Answers.Count>0&&s.Window>0){var frame=s.Answers.Peek();int length=Math.Min(Math.Min(512,s.Window),frame.Length-s.Offset);var data=TunnelPacket.Slice(frame,s.Offset,s.Offset+length);s.Offset+=length;if(s.Offset==frame.Length){s.Answers.Dequeue();s.Offset=0;}Send(s,0x18,data,length,now);}
            else if(s.ClientFin&&!s.Working&&s.Answers.Count==0&&s.Input.Length==0){s.ServerFin=true;Send(s,0x11,Empty,1,now);}
        }
        internal void Tick(long now){lock(sessions){foreach(var pair in sessions.ToArray()){var s=pair.Value;if(now-s.Last>30000){Reset(pair.Key,s);continue;}if(s.Unacked!=null&&now-s.Transmitted>1000){if(++s.Retries>5){sessions.Remove(pair.Key);continue;}output(s.Unacked);s.Transmitted=now;}else if(s.Unacked==null)Flush(s,now);}}}
        public void Dispose(){lock(sessions)sessions.Clear();}
    }
}
