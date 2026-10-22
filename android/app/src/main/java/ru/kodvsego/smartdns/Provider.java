package ru.kodvsego.smartdns;

public final class Provider {
    public final String name;
    public final String[] servers, services;
    public final String doh;
    public final boolean encryptedOnly;
    private Provider(String name, String[] servers, String doh, boolean encryptedOnly, String... services) {
        this.name = name; this.servers = servers; this.doh = doh;
        this.encryptedOnly = encryptedOnly; this.services = services;
    }
    public static final Provider[] ALL = {
        new Provider("Comss.one", new String[]{"83.220.169.155", "212.109.195.93"}, "https://dns.comss.one/dns-query", false, "ChatGPT", "Claude", "Gemini", "ElevenLabs"),
        new Provider("GeoHide", new String[]{"159.194.200.33", "193.233.112.67"}, "https://geohide.ru/dns-query", false, "ChatGPT", "Claude", "Gemini", "ElevenLabs"),
        new Provider("GeoHide EU", new String[]{"143.20.64.192", "45.13.237.213"}, null, false, "ChatGPT", "Claude", "Gemini", "ElevenLabs"),
        new Provider("Xbox DNS", new String[]{"111.88.96.54", "111.88.96.55"}, null, false, "Xbox"),
        new Provider("Bezmezhau", new String[]{"143.20.64.55", "31.58.216.201"}, "https://dns.bezmezhau.com/dns-query", false, "ChatGPT", "Gemini", "Spotify"),
        new Provider("DNS-AI", new String[]{"194.87.196.13", "186.246.49.127"}, "https://dns.dns-ai.ru/dns-query", true, "ChatGPT", "Claude", "Gemini", "ElevenLabs"),
        new Provider("MagicDNS", new String[]{"109.120.132.43", "94.249.177.102"}, "https://dns.magicdns.ru/dns-query", false, "ChatGPT", "Claude", "Gemini", "ElevenLabs")
    };
    public static Provider at(int index) {
        if (index < 0 || index >= ALL.length) throw new IllegalArgumentException("Неизвестный DNS-провайдер");
        return ALL[index];
    }
    public String[] testHosts() {
        if (this == ALL[3]) return new String[]{"example.com", "xbox.com", "user.auth.xboxlive.com", "xsts.auth.xboxlive.com"};
        if (this == ALL[4]) return new String[]{"example.com", "chatgpt.com", "gemini.google.com", "open.spotify.com"};
        return new String[]{"example.com", "chatgpt.com", "claude.ai", "gemini.google.com", "generativelanguage.googleapis.com", "api.elevenlabs.io"};
    }
}
