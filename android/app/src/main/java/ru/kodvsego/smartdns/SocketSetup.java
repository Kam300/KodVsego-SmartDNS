package ru.kodvsego.smartdns;

import java.io.IOException;
import java.net.Socket;
import java.net.DatagramSocket;

/** Match Network.bindSocket: initialize the native descriptor before VPN protect. */
final class SocketSetup {
    interface Guard {boolean protect() throws IOException;}
    interface Binding {void bind() throws IOException;}
    static void prepare(Socket socket,Guard guard,Binding binding,String label) throws IOException {
        socket.getReuseAddress();
        if(guard!=null&&!guard.protect())throw new IOException("Не удалось защитить "+label+"-сокет");
        binding.bind();
    }
    static void prepare(DatagramSocket socket,Guard guard,Binding binding,String label) throws IOException {
        socket.getReuseAddress();
        if(guard!=null&&!guard.protect())throw new IOException("Не удалось защитить "+label+"-сокет");
        binding.bind();
    }
}
