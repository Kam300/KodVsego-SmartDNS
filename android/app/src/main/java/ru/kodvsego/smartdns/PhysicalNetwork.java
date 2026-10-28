package ru.kodvsego.smartdns;

import android.content.Context;
import android.net.*;
import android.os.Build;
import java.io.IOException;

final class PhysicalNetwork {
    static Network find(Context context) throws IOException {
        ConnectivityManager cm=(ConnectivityManager)context.getSystemService(Context.CONNECTIVITY_SERVICE);
        Network active=cm.getActiveNetwork();
        if(usable(cm,active)) return active;
        Network fallback=null;
        for(Network network:cm.getAllNetworks()) if(usable(cm,network)) {
            NetworkCapabilities caps=cm.getNetworkCapabilities(network);
            if(caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)) return network;
            fallback=network;
        }
        if(fallback!=null) return fallback;
        throw new IOException("Нет подключения к Wi-Fi или мобильной сети");
    }
    private static boolean usable(ConnectivityManager cm,Network network) {
        NetworkCapabilities caps=network==null?null:cm.getNetworkCapabilities(network);
        return caps!=null&&caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)&&
            !caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)&&caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN);
    }
    static boolean otherVpn(Context context) {
        ConnectivityManager cm=(ConnectivityManager)context.getSystemService(Context.CONNECTIVITY_SERVICE);
        for(Network n:cm.getAllNetworks()) {
            NetworkCapabilities c=cm.getNetworkCapabilities(n);if(c==null||!c.hasTransport(NetworkCapabilities.TRANSPORT_VPN))continue;
            // Capabilities can outlive a disconnected VPN while Android tears it down.
            NetworkInfo info=cm.getNetworkInfo(n);
            if(info!=null&&!info.isConnectedOrConnecting())continue;
            if(info==null&&!n.equals(cm.getActiveNetwork()))continue;
            if(!ourVpn(cm,n,c))return true;
        }
        return false;
    }
    private static boolean ourVpn(ConnectivityManager cm,Network network,NetworkCapabilities caps){
        if(!DnsVpnService.active||DnsVpnService.current==null)return false;
        if(Build.VERSION.SDK_INT>=30&&caps.getOwnerUid()==android.os.Process.myUid())return true;
        LinkProperties properties=cm.getLinkProperties(network);if(properties==null)return false;
        boolean address=false,dns=false;for(LinkAddress value:properties.getLinkAddresses())if("10.111.0.1".equals(value.getAddress().getHostAddress()))address=true;
        for(java.net.InetAddress value:properties.getDnsServers())if("10.111.0.2".equals(value.getHostAddress()))dns=true;return address&&dns;
    }
    static void requireProbeNetwork(Context context) throws IOException {
        if(otherVpn(context))throw new IOException("Другой VPN включён. Отключите его и повторите Авто. VPN с постоянным подключением или блокировкой трафика может запрещать проверку DNS.");
        checkPrivateDns(context,find(context));
    }
    static void checkPrivateDns(Context context,Network network) throws IOException {
        if(Build.VERSION.SDK_INT<28) return;
        ConnectivityManager cm=(ConnectivityManager)context.getSystemService(Context.CONNECTIVITY_SERVICE);
        LinkProperties properties=cm.getLinkProperties(network);
        if(properties!=null&&properties.getPrivateDnsServerName()!=null)
            throw new IOException("В Android задан частный DNS. Выберите «Автоматически» или «Выкл.» в настройках частного DNS, затем повторите подключение.");
    }
}
