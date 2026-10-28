package ru.kodvsego.smartdns;

public final class Provider {
    public final String name;
    public final String[] servers, services;
    private final String[] hosts;
    public final String doh;
    public final String dot,url;
    public final String[] dotServers,dohServers;
    public final boolean encryptedOnly;
    Provider(String name, String[] servers, String doh, boolean encryptedOnly, String[] services, String[] hosts) {
        this(name,servers,doh,encryptedOnly,services,hosts,null,new String[0],null);
    }
    Provider(String name,String[] servers,String doh,boolean encryptedOnly,String[] services,String[] hosts,String dot,String[] dotServers,String url) {
        this(name,servers,doh,encryptedOnly,services,hosts,dot,dotServers,url,servers);
    }
    Provider(String name,String[] servers,String doh,boolean encryptedOnly,String[] services,String[] hosts,String dot,String[] dotServers,String url,String[] dohServers) {
        this.name = name; this.servers = servers; this.doh = doh;
        this.encryptedOnly = encryptedOnly; this.services = java.util.Arrays.stream(services).filter(s->!s.equals("Antigravity")).toArray(String[]::new); this.hosts=java.util.Arrays.stream(hosts).filter(h->!h.endsWith("cloudcode-pa.googleapis.com")).toArray(String[]::new);
        this.dot=dot;this.dotServers=dotServers==null?new String[0]:dotServers.clone();this.url=url;
        this.dohServers=dohServers==null?new String[0]:dohServers.clone();
    }
    public static volatile Provider[] ALL = ProviderAvailability.create();
    private static volatile boolean[] remoteEnabled;
    static void install(Provider[] entries,boolean[] enabled){ALL=entries;remoteEnabled=enabled.clone();}
    // Preserve stored IDs and active-session detection when a provider is disabled in the shared configuration.
    public static boolean isAvailable(int index) {
        return index>=0 && index<ALL.length && (remoteEnabled==null?ProviderAvailability.isEnabled(index):index<remoteEnabled.length&&remoteEnabled[index]);
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
