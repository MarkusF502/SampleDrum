package com.markusf502.sampledrum;
import org.junit.Test;
import static org.junit.Assert.*;

public class PanelAddressTest {
    @Test public void acceptsPrivateNetworksAndPcQrButDoesNotSaveCodeInOrigin() {
        for (String host : new String[]{"192.168.1.10","10.0.2.2","172.16.1.1","172.31.255.254","pc-igreja.local"}) {
            PanelAddress a = PanelAddress.parse("http://"+host+":8750/#pair=12345678");
            assertEquals("http://"+host+":8750",a.origin);
            assertTrue(a.allows(a.origin+"/api/events"));
            assertFalse(a.allows("http://evil.example:8750/"));
            assertFalse(a.allows("http://"+host+":8751/"));
        }
        assertEquals("http://192.168.1.10:8750/",PanelAddress.parse("192.168.1.10").url.toString());
    }
    @Test public void rejectsUntrustedQrAndAddressVariants() {
        for (String value : new String[]{"", "https://192.168.1.10:8750/", "http://8.8.8.8:8750/", "http://127.0.0.1:8750/",
            "http://localhost:8750/", "http://169.254.1.1:8750/", "http://172.32.1.1:8750/", "http://192.168.01.1:8750/",
            "http://192.168.1.256:8750/", "http://user@192.168.1.10:8750/", "file:///etc/passwd", "javascript:alert(1)",
            "http://192.168.1.10:8750/evil", "http://192.168.1.10:8750/?x=1", "http://192.168.1.10:8750/#pair=123456",
            "http://192.168.1.10:80/", "http://pc.local.evil.example:8750/", "http://192.168.1.10:8750/#pair=12345678&x=1"}) {
            try { PanelAddress.parse(value); fail(value); } catch (IllegalArgumentException expected) { }
        }
    }
}
