package ru.kodvsego.smartdns;

import android.content.Context;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.*;

/** App diagnostics only. Never stores DNS names from forwarded user traffic. */
final class AppJournal {
    private static File directory;
    static synchronized void init(Context context){directory=new File(context.getFilesDir(),"diagnostics");if(!directory.exists())directory.mkdirs();}
    static String scrub(String value){return (value==null?"":value)
        .replaceAll("(?i)(authorization|cookie|api[_-]?key|token|password|secret)\\s*[:=]\\s*[^\\s,;]+","$1=<hidden>")
        .replaceAll("(https?://[^\\s?]+)\\?[^\\s]+","$1?<hidden>")
        .replaceAll("(?i)/data/(?:user/\\d+|data)/[^\\s]+","<app-path>");}
    static synchronized void record(String event,String detail){if(directory==null)return;
        try{File current=new File(directory,"current.txt");if(current.length()>256*1024){File previous=new File(directory,"previous.txt");if(previous.exists()&&!previous.delete())return;if(!current.renameTo(previous))return;}
            String clean=scrub(detail);if(clean.length()>2000)clean=clean.substring(0,2000)+"…";
            SimpleDateFormat time=new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'",Locale.ROOT);time.setTimeZone(TimeZone.getTimeZone("UTC"));
            try(FileOutputStream out=new FileOutputStream(current,true)){out.write((time.format(new Date())+" "+event+": "+clean+"\n").getBytes(StandardCharsets.UTF_8));}
        }catch(IOException ignored){}
    }
    static synchronized String report(){StringBuilder out=new StringBuilder("Код Всего · Smart DNS Android "+BuildConfig.VERSION_NAME+"\nЖурнал приложения. Отправляется только пользователем. DNS-история и журналы других приложений не собираются.\n\n");
        if(directory!=null)for(String name:new String[]{"previous.txt","current.txt"})try{File file=new File(directory,name);if(file.length()>270*1024)continue;try(Reader reader=new InputStreamReader(new FileInputStream(file),StandardCharsets.UTF_8)){char[] buffer=new char[4096];int n;while((n=reader.read(buffer))!=-1)out.append(buffer,0,n);}}catch(IOException ignored){}
        return scrub(out.toString());
    }
}
