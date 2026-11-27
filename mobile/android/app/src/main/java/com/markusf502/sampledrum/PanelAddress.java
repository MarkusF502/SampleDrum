package com.markusf502.sampledrum;

import java.net.URI;
import java.util.Locale;

/** User/QR input is untrusted. Only the PC's private LAN origin may enter the WebView. */
public final class PanelAddress {
    public final URI url;
    public final String origin;
    private PanelAddress(URI url, String origin) { this.url = url; this.origin = origin; }

    public static PanelAddress parse(String input) {
        try {
            String value = input.trim();
            if (!value.contains("://")) value = "http://" + value;
            URI u = new URI(value);
            String host = u.getHost() == null ? "" : u.getHost().toLowerCase(Locale.ROOT);
            int port = u.getPort() == -1 ? 8750 : u.getPort();
            if (!"http".equalsIgnoreCase(u.getScheme()) || u.getRawUserInfo() != null || u.getRawQuery() != null ||
                port < 1024 || port > 65535 || !(u.getRawPath().isEmpty() || u.getRawPath().equals("/")) ||
                !(isPrivateIPv4(host) || host.matches("[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.local")) ||
                (u.getRawFragment() != null && !u.getRawFragment().matches("pair=[0-9]{8}")))
                throw new IllegalArgumentException();
            String origin = "http://" + host + ":" + port;
            return new PanelAddress(new URI(origin + "/" + (u.getRawFragment() == null ? "" : "#" + u.getRawFragment())), origin);
        } catch (Exception e) {
            throw new IllegalArgumentException("Use o endereço ou QR mostrado no PC, como http://192.168.1.10:8750/.");
        }
    }

    public boolean allows(String value) {
        try {
            URI u = new URI(value);
            return "http".equalsIgnoreCase(u.getScheme()) && u.getRawUserInfo() == null &&
                url.getHost().equalsIgnoreCase(u.getHost()) && url.getPort() == u.getPort();
        } catch (Exception e) { return false; }
    }

    private static boolean isPrivateIPv4(String host) {
        String[] parts = host.split("\\.", -1);
        if (parts.length != 4) return false;
        int[] bytes = new int[4];
        for (int i = 0; i < 4; i++) {
            if (!parts[i].matches("0|[1-9][0-9]{0,2}")) return false;
            bytes[i] = Integer.parseInt(parts[i]);
            if (bytes[i] > 255) return false;
        }
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || (bytes[0] == 192 && bytes[1] == 168);
    }
}
