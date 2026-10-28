package ru.kodvsego.smartdns;

import org.junit.Test;
import java.io.*;
import java.util.concurrent.atomic.AtomicInteger;
import static org.junit.Assert.*;

public class VpnHandoffTest {
    @Test public void waitsUntilOldNetworkDisappears()throws Exception {AtomicInteger polls=new AtomicInteger(),waits=new AtomicInteger();VpnHandoff.await(()->polls.incrementAndGet()>=4,()->true,()->waits.incrementAndGet());assertEquals(5,polls.get());assertEquals(4,waits.get());}
    @Test public void transientGapDoesNotCompleteHandoff()throws Exception {AtomicInteger polls=new AtomicInteger();VpnHandoff.await(()->{int n=polls.incrementAndGet();return n==1||n>=4;},()->true,()->{});assertEquals(5,polls.get());}
    @Test public void persistentVpnHasBoundedWait()throws Exception {AtomicInteger polls=new AtomicInteger(),waits=new AtomicInteger();try{VpnHandoff.await(()->{polls.incrementAndGet();return false;},()->true,()->waits.incrementAndGet());fail();}catch(IOException error){assertTrue(error.getMessage().contains("Предыдущий VPN"));}assertEquals(40,polls.get());assertEquals(39,waits.get());}
    @Test public void stopCancelsBeforeNextPoll()throws Exception {AtomicInteger valid=new AtomicInteger(1),polls=new AtomicInteger();try{VpnHandoff.await(()->{polls.incrementAndGet();return false;},()->valid.get()==1,()->valid.set(0));fail();}catch(InterruptedIOException expected){}assertEquals(1,polls.get());}
    @Test public void interruptedWaitCancels()throws Exception {try{VpnHandoff.await(()->false,()->true,()->{throw new InterruptedException();});fail();}catch(InterruptedIOException expected){assertTrue(Thread.currentThread().isInterrupted());}finally{Thread.interrupted();}}
}
