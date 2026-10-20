package ru.kodvsego.smartdns;

import okhttp3.*;
import java.io.*;

/** Strict DNS-over-HTTPS wire transport. The supplied client uses platform TLS verification. */
final class DohTransport {
    interface CallFactory { Call create(Request request) throws IOException; }
    static byte[] exchange(OkHttpClient client,String endpoint,byte[] query) throws IOException {
        return exchange(client::newCall,endpoint,query);
    }
    static byte[] exchange(CallFactory factory,String endpoint,byte[] query) throws IOException {
        HttpUrl url=HttpUrl.parse(endpoint);
        if(url==null||!url.isHttps()||!url.username().isEmpty()||!url.password().isEmpty()||url.fragment()!=null)
            throw new IOException("Небезопасный адрес DoH");
        Request request=new Request.Builder().url(url).header("Accept","application/dns-message")
            .post(RequestBody.create(query,MediaType.get("application/dns-message"))).build();
        try(Response response=factory.create(request).execute()) {
            if(response.code()!=200)throw new IOException("DoH: HTTP "+response.code());
            ResponseBody body=response.body();
            if(body==null||body.contentLength()>65535)throw new IOException("Некорректный размер DoH-ответа");
            String type=response.header("Content-Type","").split(";",2)[0].trim();
            if(!type.equalsIgnoreCase("application/dns-message"))throw new IOException("Сервер вернул не DNS-ответ");
            ByteArrayOutputStream out=new ByteArrayOutputStream();byte[] buffer=new byte[4096];int read;
            InputStream in=body.byteStream();
            while((read=in.read(buffer))!=-1){if(out.size()+read>65535)throw new IOException("Слишком большой DNS-ответ");out.write(buffer,0,read);}
            byte[] answer=out.toByteArray();DnsWire.validate(query,answer);return answer;
        }
    }
}
