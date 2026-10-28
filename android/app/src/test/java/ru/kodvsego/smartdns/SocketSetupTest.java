package ru.kodvsego.smartdns;

import java.io.IOException;
import java.net.*;
import java.util.*;
import org.junit.Test;
import static org.junit.Assert.*;

public class SocketSetupTest {
    @Test public void initializesTcpDescriptorBeforeProtectAndNetworkBinding()throws Exception {
        List<String> order=new ArrayList<>();try(Socket socket=new Socket(){@Override public boolean getReuseAddress(){order.add("descriptor");return false;}}){
            SocketSetup.prepare(socket,()->{assertEquals(Arrays.asList("descriptor"),order);order.add("protect");return true;},()->order.add("bind"),"DoT");
            assertEquals(Arrays.asList("descriptor","protect","bind"),order);assertFalse(socket.isConnected());assertFalse(socket.isBound());
        }
    }
    @Test public void refusedProtectionNeverBindsNetwork()throws Exception {
        try(Socket socket=new Socket()){try{SocketSetup.prepare(socket,()->false,()->fail("must not bind"),"DoT");fail();}catch(IOException e){assertTrue(e.getMessage().contains("DoT"));}assertFalse(socket.isConnected());}
    }
    @Test public void initializesUdpBeforeProtectAndBinding()throws Exception {
        List<String> order=new ArrayList<>();try(DatagramSocket socket=new DatagramSocket((SocketAddress)null){@Override public boolean getReuseAddress(){order.add("descriptor");return false;}}){
            SocketSetup.prepare(socket,()->{assertEquals(Arrays.asList("descriptor"),order);order.add("protect");return true;},()->order.add("bind"),"DNS");
            assertEquals(Arrays.asList("descriptor","protect","bind"),order);assertFalse(socket.isConnected());
        }
    }
    @Test public void closedSocketNeverReachesProtection()throws Exception {
        Socket socket=new Socket();socket.close();try{SocketSetup.prepare(socket,()->{fail("must not protect closed socket");return true;},()->fail(),"TCP DNS");fail();}catch(IOException expected){}
    }
}
