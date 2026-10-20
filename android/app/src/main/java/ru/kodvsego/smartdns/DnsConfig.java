package ru.kodvsego.smartdns;

public final class DnsConfig {
    public final int provider;
    public final boolean encrypted;
    public DnsConfig(int provider,boolean encrypted) {
        Provider main=Provider.at(provider);
        this.provider=provider;this.encrypted=encrypted||main.encryptedOnly;
        if(this.encrypted && main.doh==null) throw new IllegalArgumentException("Для этого провайдера DoH не настроен");
    }
}
