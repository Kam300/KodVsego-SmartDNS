package ru.kodvsego.smartdns;

import java.util.concurrent.Executor;

/** Generation fencing and non-blocking handoff. No resource operation runs under the state lock. */
final class SessionOwner<T extends AutoCloseable> {
    private final Executor cleanup;
    private long generation;
    private T current;

    SessionOwner(Executor cleanup) { this.cleanup=cleanup; }
    synchronized long begin() { return ++generation; }
    synchronized boolean owns(long token) { return token==generation; }
    synchronized T current() { return current; }

    boolean install(long token,T candidate) {
        T old;
        synchronized(this) {
            if(token!=generation) {old=candidate;}
            else {old=current==candidate?null:current;current=candidate;}
        }
        dispose(old);
        synchronized(this) {return token==generation&&current==candidate;}
    }
    void stop() {
        T old;
        synchronized(this) {++generation;old=current;current=null;}
        dispose(old);
    }
    void dispose(AutoCloseable resource) {
        if(resource!=null)cleanup.execute(()->{try{resource.close();}catch(Exception ignored){}});
    }
}
