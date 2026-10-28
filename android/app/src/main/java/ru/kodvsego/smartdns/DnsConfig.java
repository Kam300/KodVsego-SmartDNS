package ru.kodvsego.smartdns;

public final class DnsConfig {
    public static final int PLAIN=0,DOH=1,DOT=2;
    public final int provider;
    public final int protocol;
    public final boolean encrypted;
    public DnsConfig(int provider,boolean encrypted) {
        this(provider,encrypted?DOH:PLAIN);
    }
    public DnsConfig(int provider,int protocol) {
        Provider main=Provider.at(provider);
        if(protocol<PLAIN||protocol>DOT)throw new IllegalArgumentException("Неизвестный протокол DNS");
        this.provider=provider;this.protocol=protocol==PLAIN&&main.encryptedOnly?DOH:protocol;this.encrypted=this.protocol!=PLAIN;
        if(this.protocol==DOH&&main.doh==null) throw new IllegalArgumentException("Для этого провайдера DoH не настроен");
        if(this.protocol==DOT&&(main.dot==null||main.dotServers.length==0))throw new IllegalArgumentException("Для этого провайдера DoT не настроен");
    }
    public String label(){return protocol==DOT?"DoT · TLS":protocol==DOH?"DoH · HTTPS":"Без шифрования";}
    static DnsConfig saved(android.content.SharedPreferences prefs,int provider){int protocol=prefs.getInt("protocol",prefs.getBoolean("encrypted",false)?DOH:PLAIN);try{return new DnsConfig(provider,protocol);}catch(IllegalArgumentException unavailable){return new DnsConfig(provider,Provider.at(provider).doh!=null?DOH:PLAIN);}}
}
