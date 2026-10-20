package ru.kodvsego.smartdns;

import org.junit.Test;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicInteger;
import static org.junit.Assert.*;

public class SessionOwnerTest {
    @Test public void stopDoesNotWaitForBlockedCleanup() throws Exception {
        ExecutorService executor=Executors.newSingleThreadExecutor();CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);
        SessionOwner<AutoCloseable> owner=new SessionOwner<>(executor);
        try {
            owner.install(owner.begin(),()->{entered.countDown();release.await(3,TimeUnit.SECONDS);});
            long start=System.nanoTime();owner.stop();
            assertTrue("Stopping must not wait for network locks",System.nanoTime()-start<TimeUnit.SECONDS.toNanos(1));
            assertNull(owner.current());assertTrue(entered.await(1,TimeUnit.SECONDS));
            long next=owner.begin();assertTrue(owner.owns(next));
        }finally{release.countDown();executor.shutdown();assertTrue(executor.awaitTermination(3,TimeUnit.SECONDS));}
    }
    @Test public void cancelledConnectionCannotPublishLateSession() {
        AtomicInteger closed=new AtomicInteger();SessionOwner<AutoCloseable> owner=new SessionOwner<>(Runnable::run);
        long request=owner.begin();owner.stop();assertFalse(owner.install(request,closed::incrementAndGet));assertNull(owner.current());assertEquals(1,closed.get());
    }
    @Test public void olderAttemptCannotReplaceNewConnection() {
        AtomicInteger oldClosed=new AtomicInteger();SessionOwner<AutoCloseable> owner=new SessionOwner<>(Runnable::run);
        long old=owner.begin(),latest=owner.begin();AutoCloseable current=()->{};
        assertTrue(owner.install(latest,current));assertFalse(owner.install(old,oldClosed::incrementAndGet));assertSame(current,owner.current());assertEquals(1,oldClosed.get());
    }
    @Test public void repeatedStopClosesCurrentSessionOnlyOnce() {
        AtomicInteger closed=new AtomicInteger();SessionOwner<AutoCloseable> owner=new SessionOwner<>(Runnable::run);
        owner.install(owner.begin(),closed::incrementAndGet);owner.stop();owner.stop();assertEquals(1,closed.get());
    }
    @Test public void newSessionSurvivesPreviousCleanupFailure() {
        SessionOwner<AutoCloseable> owner=new SessionOwner<>(Runnable::run);owner.install(owner.begin(),()->{throw new Exception("old cleanup failed");});
        AutoCloseable replacement=()->{};assertTrue(owner.install(owner.begin(),replacement));assertSame(replacement,owner.current());
    }
    @Test public void installingSameSessionTwiceDoesNotCloseIt() {
        AtomicInteger closed=new AtomicInteger();SessionOwner<AutoCloseable> owner=new SessionOwner<>(Runnable::run);long token=owner.begin();AutoCloseable session=closed::incrementAndGet;
        owner.install(token,session);owner.install(token,session);assertEquals(0,closed.get());owner.stop();assertEquals(1,closed.get());
    }
}
