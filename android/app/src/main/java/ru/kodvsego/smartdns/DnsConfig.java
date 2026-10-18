package ru.kodvsego.smartdns;

public final class DnsConfig {
    public final int provider, antigravity;
    public final boolean encrypted;
    public DnsConfig(int provider,boolean encrypted,int antigravity) {
        Provider main=Provider.at(provider);
        this.provider=provider;this.encrypted=encrypted||main.encryptedOnly;this.antigravity=antigravity;
        if(this.encrypted && main.doh==null) throw new IllegalArgumentException("Для этого провайдера DoH не настроен");
        if(antigravity!=-1) {
            boolean allowed=false;for(int id:Provider.AG_PROVIDERS) if(id==antigravity) allowed=true;
            if(!allowed) throw new IllegalArgumentException("Недопустимый DNS Antigravity");
            if(this.encrypted && Provider.at(antigravity).doh==null)
                throw new IllegalArgumentException("GeoHide EU не поддерживает DoH. Выберите GeoHide или Bezmezhau для Antigravity.");
        }
    }
    public Provider forHost(String host) {return Provider.at(antigravity>=0&&Provider.antigravity(host)?antigravity:provider);}
}
