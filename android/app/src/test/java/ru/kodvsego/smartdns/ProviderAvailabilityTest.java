package ru.kodvsego.smartdns;
import org.junit.Test;
import java.lang.reflect.Field;
import static org.junit.Assert.*;
public class ProviderAvailabilityTest {
    @Test public void everyGeminiProviderChecksWebsite() {
        for(Provider provider:Provider.ALL)if(java.util.Arrays.asList(provider.services).contains("Gemini")) {
            java.util.List<String> hosts=java.util.Arrays.asList(provider.testHosts());
            assertTrue(provider.name+" must check Gemini website",hosts.contains("gemini.google.com"));
            assertFalse(provider.name+" must omit Gemini API",hosts.contains("generativelanguage.googleapis.com"));
        }
    }
    @Test public void comssUsesCurrentAddresses(){assertArrayEquals(new String[]{"94.250.253.55","212.109.195.93"},Provider.at(0).servers);assertArrayEquals(new String[]{"94.250.253.55","212.109.195.93"},Provider.at(0).dotServers);assertEquals("dns.comss.one",Provider.at(0).dot);}
    @Test public void addedProvidersKeepIndependentEndpoints() {
        for(int id:new int[]{0,4,5,6,7,9,11,12,14,15,16})assertTrue(Provider.isAvailable(id));
        assertEquals("https://us.geohide.ru/dns-query",Provider.at(8).doh);
        assertEquals(4,Provider.at(8).servers.length);
        assertEquals("https://d.webshiftvpn.ru/dns-query",Provider.at(11).doh);
        assertTrue(Provider.at(11).encryptedOnly);assertTrue(Provider.at(13).encryptedOnly);assertTrue(Provider.at(16).encryptedOnly);
        assertArrayEquals(new String[]{"94.140.14.14","94.140.15.15"},Provider.at(12).servers);
        assertEquals("https://dns.adguard-dns.com/dns-query",Provider.at(12).doh);
        assertEquals("https://5u35p8m9i7.cloudflare-gateway.com/dns-query",Provider.at(13).doh);
        assertEquals("https://free.pereval.top/dns-query",Provider.at(14).doh);
        assertEquals("free.pereval.top",Provider.at(14).dot);
        assertEquals("https://cf.dns-ai.ru/dns-query",Provider.at(16).doh);
    }
    @Test public void disabledProvidersKeepStableIds() {
        for(int id:new int[]{1,2,3,8,10,13})assertFalse(Provider.isAvailable(id));
        assertFalse(Provider.isAvailable(-1));assertFalse(Provider.isAvailable(Provider.ALL.length));
        assertEquals("GeoHide",Provider.at(1).name);assertEquals("GeoHide EU",Provider.at(2).name);
        assertEquals("Xbox DNS",Provider.at(3).name);
        for(int id=0;id<Provider.ALL.length;id++)assertEquals(id==1||id==2||id==3||id==8||id==10||id==13?0:id,Provider.preferredIndex(id));
    }
    @Test public void androidDoesNotOfferAntigravityFeatures() {
        Provider magic=Provider.at(6);
        assertTrue(Provider.isAvailable(6));
        for(Provider provider:Provider.ALL){assertFalse(java.util.Arrays.asList(provider.services).contains("Antigravity"));assertFalse(java.util.Arrays.asList(provider.testHosts()).contains("cloudcode-pa.googleapis.com"));assertFalse(java.util.Arrays.asList(provider.testHosts()).contains("daily-cloudcode-pa.googleapis.com"));}
        DnsConfig config=new DnsConfig(6,true);
        assertTrue(config.encrypted);assertEquals("https://api.magicinfo.art/rpc",magic.doh);
    }
    @Test public void malwCloudflareAndDnsAiCfOmitAntigravityFromServices() {
        Provider retired=Provider.at(13);
        assertFalse(Provider.isAvailable(13));
        assertFalse(java.util.Arrays.asList(retired.services).contains("Antigravity"));
        Provider cf=Provider.at(16);
        assertTrue(Provider.isAvailable(16));
        assertTrue(java.util.Arrays.asList(cf.services).contains("Gemini"));
        assertFalse(java.util.Arrays.asList(cf.services).contains("Antigravity"));
        assertFalse(java.util.Arrays.asList(Provider.at(9).services).contains("Antigravity"));
    }
    @Test public void disabledProviderChoiceFallsBackWithoutRenumbering() throws Exception {
        Field field=ProviderAvailability.class.getDeclaredField("ENABLED");field.setAccessible(true);
        boolean[] flags=(boolean[])field.get(null),saved=flags.clone();
        try {
            flags[1]=false;flags[2]=false;
            assertFalse(Provider.isAvailable(1));assertFalse(Provider.isAvailable(2));
            assertEquals(0,Provider.preferredIndex(1));assertEquals(0,Provider.preferredIndex(2));
            flags[0]=false;assertEquals(4,Provider.preferredIndex(1));
            assertEquals("DNS-AI",Provider.at(5).name);assertEquals("MagicDNS",Provider.at(6).name);
        } finally {System.arraycopy(saved,0,flags,0,flags.length);}
    }
}
