package ru.kodvsego.smartdns;

import android.net.*;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.Config;
import org.robolectric.shadows.*;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class) @Config(sdk={28,35})
public class VpnPreflightTest {
    private android.content.Context context;private ShadowConnectivityManager networks;
    @Before public void before(){context=RuntimeEnvironment.getApplication();networks=Shadows.shadowOf(context.getSystemService(ConnectivityManager.class));networks.clearAllNetworks();DnsVpnService.active=false;DnsVpnService.current=null;}
    @After public void after(){DnsVpnService.active=false;DnsVpnService.current=null;}
    private NetworkCapabilities vpnCaps(){NetworkCapabilities caps=new NetworkCapabilities();Shadows.shadowOf(caps).addTransportType(NetworkCapabilities.TRANSPORT_VPN);return caps;}
    private Network vpn(int id){Network network=ShadowNetwork.newInstance(id);networks.addNetwork(network,ShadowNetworkInfo.newInstance(NetworkInfo.DetailedState.CONNECTED,ConnectivityManager.TYPE_VPN,0,true,NetworkInfo.State.CONNECTED));networks.setNetworkCapabilities(network,vpnCaps());return network;}
    @Test public void disconnectedVpnRecordDoesNotBlockConnection(){Network network=ShadowNetwork.newInstance(141);networks.addNetwork(network,ShadowNetworkInfo.newInstance(NetworkInfo.DetailedState.DISCONNECTED,ConnectivityManager.TYPE_VPN,0,false,NetworkInfo.State.DISCONNECTED));networks.setNetworkCapabilities(network,vpnCaps());assertFalse(PhysicalNetwork.otherVpn(context));}
    @Test public void foreignVpnBlocksAutoBeforeDnsQueries()throws Exception {vpn(137);assertTrue(PhysicalNetwork.otherVpn(context));try{PhysicalNetwork.requireProbeNetwork(context);fail();}catch(java.io.IOException e){assertTrue(e.getMessage().contains("Другой VPN"));}}
    @Test public void ownDnsVpnIsNotTreatedAsForeign()throws Exception {Network network=vpn(138);DnsVpnService.active=true;DnsVpnService.current=new DnsConfig(0,true);LinkProperties links=new LinkProperties();org.robolectric.util.ReflectionHelpers.callInstanceMethod(links,"addLinkAddress",org.robolectric.util.ReflectionHelpers.ClassParameter.from(LinkAddress.class,org.robolectric.util.ReflectionHelpers.callConstructor(LinkAddress.class,org.robolectric.util.ReflectionHelpers.ClassParameter.from(java.net.InetAddress.class,java.net.InetAddress.getByAddress(new byte[]{10,111,0,1})),org.robolectric.util.ReflectionHelpers.ClassParameter.from(int.class,32))));org.robolectric.util.ReflectionHelpers.callInstanceMethod(links,"addDnsServer",org.robolectric.util.ReflectionHelpers.ClassParameter.from(java.net.InetAddress.class,java.net.InetAddress.getByName("10.111.0.2")));networks.setLinkProperties(network,links);assertFalse(PhysicalNetwork.otherVpn(context));vpn(139);assertTrue(PhysicalNetwork.otherVpn(context));}
    @Test public void staleActiveFlagDoesNotHideAnotherVpn(){vpn(140);DnsVpnService.active=true;DnsVpnService.current=new DnsConfig(0,true);assertTrue(PhysicalNetwork.otherVpn(context));}
}
