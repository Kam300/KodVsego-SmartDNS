package ru.kodvsego.smartdns;
import org.junit.Test;
import java.lang.reflect.Field;
import static org.junit.Assert.*;
public class ProviderAvailabilityTest {
    @Test public void addedProvidersKeepIndependentEndpoints() {
        for(int id=7;id<14;id++)assertTrue(Provider.isAvailable(id));
        assertEquals("https://us.geohide.ru/dns-query",Provider.at(8).doh);
        assertEquals(4,Provider.at(8).servers.length);
        assertEquals("https://d.webshiftvpn.ru/dns-query",Provider.at(11).doh);
        assertTrue(Provider.at(11).encryptedOnly);assertTrue(Provider.at(13).encryptedOnly);
        assertArrayEquals(new String[]{"94.140.14.14","94.140.15.15"},Provider.at(12).servers);
        assertEquals("https://dns.adguard-dns.com/dns-query",Provider.at(12).doh);
        assertEquals("https://5u35p8m9i7.cloudflare-gateway.com/dns-query",Provider.at(13).doh);
    }
    @Test public void disabledProvidersKeepStableIds() {
        assertFalse(Provider.isAvailable(1));assertFalse(Provider.isAvailable(2));assertFalse(Provider.isAvailable(3));
        assertFalse(Provider.isAvailable(-1));assertFalse(Provider.isAvailable(Provider.ALL.length));
        assertEquals("GeoHide",Provider.at(1).name);assertEquals("GeoHide EU",Provider.at(2).name);
        assertEquals("Xbox DNS",Provider.at(3).name);
        for(int id=0;id<Provider.ALL.length;id++)assertEquals(id==1||id==2||id==3?0:id,Provider.preferredIndex(id));
    }
    @Test public void magicDnsSupportsAntigravityWithEncryption() {
        Provider magic=Provider.at(6);
        assertTrue(Provider.isAvailable(6));
        assertTrue(java.util.Arrays.asList(magic.services).contains("Antigravity"));
        assertTrue(java.util.Arrays.asList(magic.testHosts()).contains("cloudcode-pa.googleapis.com"));
        assertTrue(java.util.Arrays.asList(magic.testHosts()).contains("daily-cloudcode-pa.googleapis.com"));
        DnsConfig config=new DnsConfig(6,true);
        assertTrue(config.encrypted);assertEquals("https://dns.magicdns.ru/dns-query",magic.doh);
    }
    @Test public void malwCloudflareRemainsAvailableWithoutAntigravity() {
        Provider cf=Provider.at(13);
        assertTrue(Provider.isAvailable(13));
        assertTrue(java.util.Arrays.asList(cf.services).contains("Gemini"));
        assertFalse(java.util.Arrays.asList(cf.services).contains("Antigravity"));
        assertTrue(java.util.Arrays.asList(Provider.at(9).services).contains("Antigravity"));
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
