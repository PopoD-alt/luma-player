package dev.sdas.luma.net;

import java.net.InetAddress;
import java.net.URI;
import java.net.URISyntaxException;
import java.net.URL;
import java.util.Locale;

/** Numeric addresses only: no cloud DNS or second-resolution/rebinding window. */
public final class LanEndpoint {
    public final String origin;
    private LanEndpoint(String origin) { this.origin = origin; }

    public static LanEndpoint parse(String input) {
        try {
            if (input == null || input.length() > 512 || input.indexOf('\\') >= 0 || !input.equals(input.trim())) throw invalid();
            URI uri = new URI(input);
            if (!"http".equalsIgnoreCase(uri.getScheme()) || uri.getRawUserInfo() != null || uri.getRawQuery() != null || uri.getRawFragment() != null
                    || uri.getPort() < 1 || uri.getPort() > 65535 || uri.getHost() == null
                    || !(uri.getRawPath() == null || uri.getRawPath().isEmpty() || uri.getRawPath().equals("/"))) throw invalid();
            String host = uri.getHost(); if (host.startsWith("[")) host = host.substring(1, host.length()-1);
            host = host.replace("%25", "%");
            if (!host.matches("[0-9.]+") && !host.matches("[0-9a-fA-F:.]+(?:%[a-zA-Z0-9_.-]{1,24})?")) throw invalid();
            if (host.indexOf(':') < 0) {
                String[] pieces = host.split("\\.", -1); if (pieces.length != 4) throw invalid();
                for (String p : pieces) if (p.isEmpty() || p.length() > 3 || p.length() > 1 && p.startsWith("0") || Integer.parseInt(p) > 255) throw invalid();
            }
            InetAddress address = InetAddress.getByName(host);
            if (!isLocal(address)) throw invalid();
            String canonical = address.getHostAddress();
            if (canonical.indexOf(':') >= 0) canonical = "[" + canonical.replace("%", "%25") + "]";
            return new LanEndpoint("http://" + canonical.toLowerCase(Locale.ROOT) + ":" + uri.getPort());
        } catch (Exception error) { throw invalid(); }
    }

    public static LanEndpoint discovered(InetAddress address, int port) {
        if (address == null || !isLocal(address)) throw invalid();
        String host = address.getHostAddress(); if (host.indexOf(':') >= 0) host = "[" + host.replace("%", "%25") + "]";
        return parse("http://" + host + ":" + port);
    }

    public static boolean isLocal(InetAddress address) {
        byte[] a = address.getAddress();
        if (a.length == 4) {
            int x = a[0] & 255, y = a[1] & 255;
            return x == 10 || x == 127 || x == 172 && y >= 16 && y <= 31 || x == 192 && y == 168 || x == 169 && y == 254;
        }
        if (a.length != 16) return false;
        if (address.isLoopbackAddress()) return true;
        return (a[0] & 254) == 252 || (a[0] & 255) == 254 && (a[1] & 192) == 128;
    }

    public URL api(String path) {
        if (!path.equals("/v1/info") && !path.equals("/v1/pair") && !path.equals("/v1/playlist")) throw invalid();
        return url(path);
    }

    public URL media(String streamPath, String id, String token) {
        if (id == null || !id.matches("[A-Za-z0-9_-]{1,200}") || !validToken(token)) throw invalid();
        try {
            URI value = new URI(streamPath);
            if (value.isAbsolute() || value.getRawAuthority() != null || value.getRawFragment() != null
                    || !value.getRawPath().equals("/v1/media/" + id) || !("token=" + token).equals(value.getRawQuery())) throw invalid();
            return url(streamPath);
        } catch (URISyntaxException | NullPointerException error) { throw invalid(); }
    }

    public static boolean validToken(String token) { return token != null && token.matches("[A-Za-z0-9_-]{32,128}"); }
    private URL url(String path) { try { return new URI(origin + path).toURL(); } catch (Exception e) { throw invalid(); } }
    private static IllegalArgumentException invalid() { return new IllegalArgumentException("Use the desktop's local http://IP-address:port. Public hosts, credentials and extra URL paths are not allowed."); }
    @Override public boolean equals(Object other) { return other instanceof LanEndpoint && origin.equals(((LanEndpoint)other).origin); }
    @Override public int hashCode() { return origin.hashCode(); }
    @Override public String toString() { return origin; }
}
