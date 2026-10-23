package ru.kodvsego.smartdns;

public final class Provider {
    public final String name;
    public final String[] servers, services;
    private final String[] hosts;
    public final String doh;
    public final boolean encryptedOnly;
    Provider(String name, String[] servers, String doh, boolean encryptedOnly, String[] services, String[] hosts) {
        this.name = name; this.servers = servers; this.doh = doh;
        this.encryptedOnly = encryptedOnly; this.services = services; this.hosts=hosts;
    }
    public static final Provider[] ALL = ProviderAvailability.create();
    // Preserve stored IDs and active-session detection when a provider is disabled in the shared configuration.
    public static boolean isAvailable(int index) {
        return index>=0 && index<ALL.length && ProviderAvailability.isEnabled(index);
    }
    public static int preferredIndex(int index) {
        if(isAvailable(index))return index;
        for(int id=0;id<ALL.length;id++)if(isAvailable(id))return id;
        throw new IllegalStateException("Нет доступных DNS-провайдеров");
    }
    public static Provider at(int index) {
        if (index < 0 || index >= ALL.length) throw new IllegalArgumentException("Неизвестный DNS-провайдер");
        return ALL[index];
    }
    public String[] testHosts() { return hosts.clone(); }
}
