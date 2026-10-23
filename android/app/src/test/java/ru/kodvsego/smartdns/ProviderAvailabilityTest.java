package ru.kodvsego.smartdns;
import org.junit.Test;
import java.lang.reflect.Field;
import static org.junit.Assert.*;
public class ProviderAvailabilityTest {
    @Test public void disabledProvidersKeepStableIds() {
        assertFalse(Provider.isAvailable(1));assertFalse(Provider.isAvailable(2));
        assertFalse(Provider.isAvailable(-1));assertFalse(Provider.isAvailable(Provider.ALL.length));
        assertEquals("GeoHide",Provider.at(1).name);assertEquals("GeoHide EU",Provider.at(2).name);
        for(int id=0;id<Provider.ALL.length;id++)assertEquals(id==1||id==2?0:id,Provider.preferredIndex(id));
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
    @Test public void disabledProviderChoiceFallsBackWithoutRenumbering() throws Exception {
        Field field=ProviderAvailability.class.getDeclaredField("ENABLED");field.setAccessible(true);
        boolean[] flags=(boolean[])field.get(null),saved=flags.clone();
        try {
            flags[1]=false;flags[2]=false;
            assertFalse(Provider.isAvailable(1));assertFalse(Provider.isAvailable(2));
            assertEquals(0,Provider.preferredIndex(1));assertEquals(0,Provider.preferredIndex(2));
            flags[0]=false;assertEquals(3,Provider.preferredIndex(1));
            assertEquals("DNS-AI",Provider.at(5).name);assertEquals("MagicDNS",Provider.at(6).name);
        } finally {System.arraycopy(saved,0,flags,0,flags.length);}
    }
}
