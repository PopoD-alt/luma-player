package dev.sdas.luma.net;

import org.json.JSONObject;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.ByteBuffer;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.util.Objects;
import java.util.concurrent.atomic.AtomicLong;

/** Session-scoped Wi-Fi connections. Replacement routes disclose no credentials before host verification. */
public final class ScopedWifiConnections<N> implements LanApi.Connections,AutoCloseable {
    public interface Routes<N> {
        boolean usable(N route);
        N chooseWifi();
        HttpURLConnection open(N route,URL url)throws IOException;
    }
    public enum Reason { NO_WIFI, HOST_CHANGED, UNPINNED, CLOSED, INVALID_ORIGIN, PROBE_FAILED, ROUTE_CHANGED }
    public static final class RouteException extends IOException {
        public final Reason reason;
        public RouteException(Reason reason){super(message(reason));this.reason=reason;}
        private static String message(Reason reason){
            if(reason==Reason.HOST_CHANGED||reason==Reason.UNPINNED)return "The desktop sharing session changed. Return to the playlist and reconnect to pair again.";
            if(reason==Reason.NO_WIFI||reason==Reason.ROUTE_CHANGED)return "Wi-Fi changed or disconnected. Reconnect to the desktop's Wi-Fi and retry.";
            if(reason==Reason.PROBE_FAILED)return "The desktop could not be verified on this Wi-Fi. Return to the playlist and reconnect.";
            return "This desktop connection is no longer available. Reconnect to your desktop.";
        }
    }
    private static final class State<N> {
        final N route;final long epoch;final boolean verify;
        State(N route,long epoch,boolean verify){this.route=route;this.epoch=epoch;this.verify=verify;}
    }
    private final LanEndpoint endpoint;private final URL infoUrl;private final Routes<N> routes;private final Object lock=new Object();
    private volatile State<N> state;private volatile String expectedHostId;private volatile boolean closed;
    private final AtomicLong verifications=new AtomicLong(),replacements=new AtomicLong();
    public ScopedWifiConnections(LanEndpoint endpoint,N preferred,Routes<N> routes){
        this.endpoint=Objects.requireNonNull(endpoint);this.infoUrl=endpoint.api("/v1/info");this.routes=Objects.requireNonNull(routes);state=new State<>(preferred,0,false);
    }
    /** Called by the initial info worker before exposing the pairing UI; refreshes never repin. */
    public void pinExpectedHost(String id)throws RouteException{
        if(id==null||id.isEmpty()||id.length()>128||id.indexOf('\0')>=0)throw new RouteException(Reason.UNPINNED);
        synchronized(lock){if(closed)throw new RouteException(Reason.CLOSED);if(expectedHostId!=null){if(!expectedHostId.equals(id))throw new RouteException(Reason.HOST_CHANGED);return;}expectedHostId=id;State<N> old=state;state=new State<>(old.route,old.epoch+1,true);}
    }
    public N selectedRoute(){return state.route;}
    public long verificationCount(){return verifications.get();}
    public long replacementCount(){return replacements.get();}
    /** Only the debug Activity hook calls this to exercise real replacement verification without changing device Wi-Fi. */
    public void invalidateForTesting(){synchronized(lock){State<N> old=state;state=new State<>(old.route,old.epoch+1,true);}}
    @Override public void close(){synchronized(lock){closed=true;State<N> old=state;state=new State<>(old.route,old.epoch+1,true);}}
    @Override public HttpURLConnection open(URL url)throws IOException{
        requireOrigin(url);
        State<N> before=state;String expected=expectedHostId;
        if(closed)throw new RouteException(Reason.CLOSED);
        boolean publicInfo=infoUrl.getPath().equals(url.getPath())&&url.getQuery()==null;
        if(expected==null&&!publicInfo)throw new RouteException(Reason.UNPINNED);
        N candidate=before.route;
        boolean keep=routes.usable(candidate);
        if(!keep)candidate=routes.chooseWifi();
        if(candidate==null||!routes.usable(candidate))throw new RouteException(Reason.NO_WIFI);
        boolean verify=expected!=null&&(!keep||before.verify);
        if(verify){
            // Deliberately independent of url: the original URL can contain a
            // bearer token. No caller headers/body are available to this probe.
            String observed=probe(candidate);
            if(!expected.equals(observed))throw new RouteException(Reason.HOST_CHANGED);
            if(!routes.usable(candidate))throw new RouteException(Reason.ROUTE_CHANGED);
        }
        synchronized(lock){
            if(closed)throw new RouteException(Reason.CLOSED);
            if(state.epoch!=before.epoch)throw new RouteException(Reason.ROUTE_CHANGED);
            // A bootstrap info request racing the initial pin must not promote
            // an unverified replacement as though it already passed that pin.
            if(!Objects.equals(expectedHostId,expected))throw new RouteException(Reason.ROUTE_CHANGED);
            if(verify||!Objects.equals(before.route,candidate)){state=new State<>(candidate,before.epoch,false);if(verify){verifications.incrementAndGet();replacements.incrementAndGet();}}
        }
        if(!routes.usable(candidate)||closed)throw new RouteException(Reason.ROUTE_CHANGED);
        HttpURLConnection connection=routes.open(candidate,url);
        if(closed||state.epoch!=before.epoch){connection.disconnect();throw new RouteException(Reason.CLOSED);}
        return connection;
    }
    private void requireOrigin(URL url)throws RouteException{
        if(url==null||!infoUrl.getProtocol().equalsIgnoreCase(url.getProtocol())||!infoUrl.getHost().equalsIgnoreCase(url.getHost())||url.getPort()!=infoUrl.getPort()||url.getUserInfo()!=null||url.getRef()!=null)throw new RouteException(Reason.INVALID_ORIGIN);
    }
    private String probe(N candidate)throws RouteException{
        // Check the budget between blocking operations and body reads. Socket
        // timeouts bound inactivity; HttpURLConnection's internal header parser
        // does not provide a strict wall-clock deadline for trickling headers.
        HttpURLConnection connection=null;long deadline=System.nanoTime()+8_000_000_000L;
        try{
            if(closed||!routes.usable(candidate))throw new RouteException(Reason.ROUTE_CHANGED);
            connection=routes.open(candidate,infoUrl);connection.setInstanceFollowRedirects(false);connection.setUseCaches(false);
            connection.setConnectTimeout(Math.min(3000,remaining(deadline)));connection.setReadTimeout(Math.min(5000,remaining(deadline)));connection.setRequestMethod("GET");
            connection.setRequestProperty("Accept","application/json");connection.setRequestProperty("Connection","close");connection.setRequestProperty("User-Agent","LumaAndroid/0.1");
            if(closed)throw new RouteException(Reason.CLOSED);
            connection.connect();remaining(deadline);connection.setReadTimeout(Math.min(5000,remaining(deadline)));
            if(connection.getResponseCode()!=200||connection.getContentLengthLong()>65536)throw new RouteException(Reason.PROBE_FAILED);
            remaining(deadline);
            ByteArrayOutputStream bytes=new ByteArrayOutputStream();
            try(InputStream input=connection.getInputStream()){
                byte[] buffer=new byte[4096];int count;
                while(true){if(closed)throw new RouteException(Reason.CLOSED);connection.setReadTimeout(Math.min(5000,remaining(deadline)));count=input.read(buffer);remaining(deadline);if(count<0)break;if(bytes.size()+count>65536)throw new RouteException(Reason.PROBE_FAILED);bytes.write(buffer,0,count);}
            }
            String text=StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes.toByteArray())).toString();
            JSONObject object=new JSONObject(text);String id=object.getString("id");
            if(object.getInt("version")!=1||!object.getBoolean("pairingRequired")||id.isEmpty()||id.length()>128||id.indexOf('\0')>=0)throw new RouteException(Reason.PROBE_FAILED);
            return id;
        }catch(RouteException error){throw error;}
        catch(Exception error){throw new RouteException(Reason.PROBE_FAILED);}
        finally{if(connection!=null)try{connection.disconnect();}catch(RuntimeException ignored){}}
    }
    private static int remaining(long deadline)throws RouteException{long left=deadline-System.nanoTime();if(left<=0)throw new RouteException(Reason.PROBE_FAILED);return (int)Math.max(1,Math.min(8000,(left+999_999)/1_000_000));}
}
