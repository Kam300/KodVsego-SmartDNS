package ru.kodvsego.smartdns;

import java.util.*;
import java.util.concurrent.*;

/** Bounded DNS-only selection. It cannot confirm login, region or generation. */
final class DnsSelection {
    interface Probe {long measure(DnsConfig config) throws Exception;}
    static final class Result {
        final DnsConfig config;final long milliseconds;final String error;
        Result(DnsConfig c,long ms,String error){config=c;milliseconds=ms;this.error=error;}
        String label(){return error==null?milliseconds+" мс":"Не ответил";}
    }
    static List<DnsConfig> candidates(Set<String> hidden,boolean encryptedOnly,int except){List<DnsConfig> list=new ArrayList<>();
        for(int id=0;id<Provider.ALL.length;id++)if(id!=except&&Provider.isAvailable(id)&&!hidden.contains(String.valueOf(id))){Provider p=Provider.at(id);
            if(p.doh!=null)list.add(new DnsConfig(id,DnsConfig.DOH));if(p.dot!=null&&p.dotServers.length>0)list.add(new DnsConfig(id,DnsConfig.DOT));
            if(!encryptedOnly&&!p.encryptedOnly)list.add(new DnsConfig(id,DnsConfig.PLAIN));
        }return list;
    }
    static List<DnsConfig> alternatives(Set<String> hidden,DnsConfig current){List<DnsConfig> all=candidates(hidden,true,-1);all.removeIf(c->c.provider==current.provider&&c.protocol==current.protocol);return all;}
    static boolean mayApplyAuto(boolean enabled,Set<String> hidden,DnsConfig next,long failureAt,long lastSuccess){return enabled&&!hidden.contains(String.valueOf(next.provider))&&lastSuccess<=failureAt;}
    static List<Result> check(List<DnsConfig> configs,Probe probe,long timeoutMs) throws InterruptedException {
        ExecutorService pool=Executors.newFixedThreadPool(3);CompletionService<Result> complete=new ExecutorCompletionService<>(pool);List<Future<Result>> pending=new ArrayList<>();List<Result> results=new ArrayList<>();
        long until=System.nanoTime()+TimeUnit.MILLISECONDS.toNanos(timeoutMs);
        try{for(DnsConfig c:configs)pending.add(complete.submit(()->{try{return new Result(c,probe.measure(c),null);}catch(Exception e){return new Result(c,0,AppJournal.scrub(e.getMessage()));}}));
            for(int n=0;n<pending.size();n++){long left=until-System.nanoTime();if(left<=0)break;Future<Result> done=complete.poll(left,TimeUnit.NANOSECONDS);if(done==null)break;try{results.add(done.get());}catch(ExecutionException ignored){}}
        }finally{for(Future<Result> future:pending)future.cancel(true);pool.shutdownNow();}
        return results;
    }
    static Result best(List<Result> results){return results.stream().filter(r->r.error==null).min(Comparator.comparingLong(r->r.milliseconds)).orElse(null);}
    static final class Failover {
        private int failures;private long next;private final ArrayDeque<Long> attempts=new ArrayDeque<>();
        synchronized void success(){failures=0;}
        synchronized boolean failed(long now){if(++failures<3||now<next)return false;while(!attempts.isEmpty()&&now-attempts.peekFirst()>600000)attempts.removeFirst();if(attempts.size()>=3)return false;attempts.add(now);next=now+60000;failures=0;return true;}
    }
}
