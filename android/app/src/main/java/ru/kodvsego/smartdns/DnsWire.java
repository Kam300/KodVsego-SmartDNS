package ru.kodvsego.smartdns;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.Arrays;
import java.util.Locale;

/** DNS parsing is independent of Android so malformed packets can be unit tested. */
public final class DnsWire {
    private static final SecureRandom RANDOM = new SecureRandom();
    public static final class Question {
        public final String host;
        public final int type, dnsClass, end;
        Question(String host, int type, int dnsClass, int end) { this.host=host; this.type=type; this.dnsClass=dnsClass; this.end=end; }
    }
    public static int u16(byte[] b, int p) throws IOException {
        if (p < 0 || p+2 > b.length) throw new IOException("Неполный пакет");
        return ((b[p]&255)<<8) | (b[p+1]&255);
    }
    public static void put16(byte[] b, int p, int value) { b[p]=(byte)(value>>>8); b[p+1]=(byte)value; }
    public static Question question(byte[] b) throws IOException {
        if (b.length < 17 || b.length > 65535 || u16(b,4)!=1 || (b[2]&0x78)!=0) throw new IOException("Некорректный DNS-запрос");
        int p=12, total=0; StringBuilder name=new StringBuilder();
        // Queries from Android have an uncompressed question. Reject pointers rather than following untrusted loops.
        while (true) {
            if (p>=b.length) throw new IOException("Неполное DNS-имя");
            int n=b[p++]&255;
            if (n==0) break;
            if (n>63 || p+n>b.length || (total+=n+1)>254) throw new IOException("Некорректное DNS-имя");
            if (name.length()>0) name.append('.');
            name.append(new String(b,p,n,StandardCharsets.US_ASCII)); p+=n;
        }
        return new Question(name.toString().toLowerCase(Locale.ROOT), u16(b,p),u16(b,p+2),p+4);
    }
    public static byte[] query(String host) throws IOException {
        ByteArrayOutputStream out=new ByteArrayOutputStream(); byte[] header=new byte[12];
        put16(header,0,RANDOM.nextInt(65536)); header[2]=1; header[5]=1; out.write(header);
        for (String label:host.split("\\.")) {
            byte[] value=label.getBytes(StandardCharsets.US_ASCII);
            if (value.length==0 || value.length>63) throw new IOException("Некорректный домен");
            out.write(value.length); out.write(value);
        }
        out.write(new byte[]{0,0,1,0,1}); byte[] result=out.toByteArray(); question(result); return result;
    }
    public static void validate(byte[] query, byte[] answer) throws IOException {
        Question q=question(query), a=question(answer);
        if (answer[0]!=query[0] || answer[1]!=query[1] || (answer[2]&128)==0 ||
            !q.host.equals(a.host) || q.type!=a.type || q.dnsClass!=a.dnsClass)
            throw new IOException("DNS-ответ не соответствует запросу");
        // NXDOMAIN, NODATA and other legitimate DNS response codes must reach the OS unchanged.
    }
    public static void requireAddress(byte[] response) throws IOException {
        Question q=question(response);int code=response[3]&15;
        if(code!=0)throw new IOException("DNS-код "+code);
        if((response[2]&2)!=0)throw new IOException("Обрезанный DNS-ответ");
        int count=u16(response,6),p=q.end;boolean found=false;
        for(int record=0;record<count;record++) {
            boolean ended=false;
            for(int labels=0;labels<128&&p<response.length;labels++) {
                int length=response[p++]&255;
                if(length==0){ended=true;break;}
                if((length&192)==192){if(p>=response.length||(((length&63)<<8)|(response[p++]&255))>=response.length)throw new IOException("Некорректный указатель DNS");ended=true;break;}
                if(length>63||p+length>response.length)throw new IOException("Некорректное имя ответа");p+=length;
            }
            if(!ended)throw new IOException("Неполный ответ");
            int type=u16(response,p),cls=u16(response,p+2),length=u16(response,p+8);p+=10;
            if(p+length>response.length)throw new IOException("Неполная запись DNS");
            if(type==1&&cls==1&&length==4) {
                int first=response[p]&255;
                if(first!=0&&first!=127)found=true;
            }
            p+=length;
        }
        if(!found)throw new IOException("Нет пригодного IPv4-ответа");
    }
    public static byte[] failure(byte[] query) throws IOException {
        Question q=question(query); byte[] result=Arrays.copyOf(query,q.end);
        result[2]=(byte)(0x80|(query[2]&1)); result[3]=(byte)(0x80|2); // QR, RD, RA, SERVFAIL
        for(int p=6;p<12;p++) result[p]=0;
        return result;
    }
    public static byte[] truncated(byte[] response) throws IOException {
        Question q=question(response); byte[] result=Arrays.copyOf(response,q.end);
        result[2]|=2; for(int p=6;p<12;p++) result[p]=0; return result;
    }
}
