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
        for(Network n:cm.getAllNetworks()) {NetworkCapabilities c=cm.getNetworkCapabilities(n);if(c!=null&&c.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) return true;}
        return false;
    }
    static void checkPrivateDns(Context context,Network network) throws IOException {
        if(Build.VERSION.SDK_INT<28) return;
        ConnectivityManager cm=(ConnectivityManager)context.getSystemService(Context.CONNECTIVITY_SERVICE);
        LinkProperties properties=cm.getLinkProperties(network);
        if(properties!=null&&properties.getPrivateDnsServerName()!=null)
            throw new IOException("В Android задан частный DNS. Выберите «Автоматически» или «Выкл.» в настройках частного DNS, затем повторите подключение.");
    }
}
