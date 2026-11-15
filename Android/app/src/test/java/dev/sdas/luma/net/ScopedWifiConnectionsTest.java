package dev.sdas.luma.net;

import org.json.JSONObject;
import org.junit.Test;
import static org.junit.Assert.*;
import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;

/** Original injected-route tests: no device networking or real credentials. */
public class ScopedWifiConnectionsTest {
    private static final LanEndpoint ENDPOINT=LanEndpoint.parse("http://192.168.5.10:4567");
    private static final String HOST="original-desktop-session",TOKEN="a".repeat(43);

    @Test public void firstPinnedUseVerifiesBeforeReusingValidPreferredRoute()throws Exception{
        try(Rig rig=new Rig()){
            LanApi.Info observed=new LanApi(rig.connections).info(ENDPOINT);
            assertEquals(HOST,observed.id);
            rig.connections.pinExpectedHost(observed.id);
            rig.routes.clear();
            HttpURLConnection actual=rig.connections.open(media());
            assertEquals(media(),actual.getURL());assertSame(rig.first,rig.connections.selectedRoute());
            assertEquals(2,rig.routes.snapshot().size());assertSame(rig.first,rig.routes.snapshot().get(0).route);
            assertEquals("First pinned operation has its own credential-free proof",ENDPOINT.api("/v1/info"),rig.routes.snapshot().get(0).connection.getURL());
            assertNull(rig.routes.snapshot().get(0).connection.getURL().getQuery());
            assertEquals(media(),rig.routes.snapshot().get(1).connection.getURL());
            assertEquals(1,rig.connections.verificationCount());assertEquals(1,rig.connections.replacementCount());
            rig.routes.clear();rig.connections.open(media());
            assertEquals("Subsequent valid-route use does not repeat verification",1,rig.routes.snapshot().size());
            assertEquals(media(),rig.routes.snapshot().get(0).connection.getURL());assertEquals(1,rig.connections.verificationCount());
        }
    }

    @Test public void repeatingSameHostPinDoesNotInvalidateAPreviouslyVerifiedRoute()throws Exception{
        try(Rig rig=new Rig()){
            rig.pin();rig.connections.open(media());rig.routes.clear();
            long verified=rig.connections.verificationCount(),replaced=rig.connections.replacementCount();
            rig.connections.pinExpectedHost(HOST);rig.connections.pinExpectedHost(HOST);
            rig.connections.open(media());assertEquals(1,rig.routes.snapshot().size());
            assertEquals(media(),rig.routes.snapshot().get(0).connection.getURL());
            assertEquals(verified,rig.connections.verificationCount());assertEquals(replaced,rig.connections.replacementCount());
        }
    }

    @Test public void bootstrapPromotionRacingTheFirstPinCannotApproveADifferentHost()throws Exception{
        try(Rig rig=new Rig()){
            // The first info response belongs to the original desktop, but its
            // consumer has not yet pinned that observed identity.
            LanApi.Info original=new LanApi(rig.connections).info(ENDPOINT);rig.routes.clear();
            rig.first.usable=false;rig.second.reply=Reply.info("different-desktop");
            CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);
            // open() promotes the bootstrap route before invoking this injected
            // factory. Pin exactly in that promotion-to-return interval.
            rig.second.onOpen=blocked(entered,release);ExecutorService worker=Executors.newSingleThreadExecutor();
            try{
                Future<String> bootstrap=worker.submit(()->reason(()->rig.connections.open(ENDPOINT.api("/v1/info"))));
                assertTrue("Replacement bootstrap did not reach its factory",entered.await(2,TimeUnit.SECONDS));
                rig.connections.pinExpectedHost(original.id);release.countDown();
                String staleReason=bootstrap.get(2,TimeUnit.SECONDS);
                assertTrue("Racing bootstrap must fail closed",staleReason.equals("CLOSED")||staleReason.equals("ROUTE_CHANGED"));
                rig.second.onOpen=null;
                reject("HOST_CHANGED",()->rig.connections.open(media()));
                assertOnlyPublicProbes(rig.routes);
                assertEquals("A mismatched bootstrap never counts as verified",0,rig.connections.verificationCount());
                assertEquals(0,rig.connections.replacementCount());
            }finally{release.countDown();worker.shutdownNow();}
        }
    }

    @Test public void noPairingOrAuthenticatedUrlCanOpenBeforeHostPin()throws Exception{
        try(Rig rig=new Rig()){
            for(URL url:new URL[]{media(),ENDPOINT.api("/v1/pair"),ENDPOINT.api("/v1/playlist"),
                    new URL(ENDPOINT.origin+"/v1/info?token="+TOKEN)}){
                reject("UNPINNED",()->rig.connections.open(url));
            }
            assertTrue("No credential-bearing target reached a route",rig.routes.snapshot().isEmpty());
        }
    }

    @Test public void changedSchemeAddressPortAndUserInfoNeverReachAnyRoute()throws Exception{
        try(Rig rig=new Rig()){
            rig.pin();
            for(String url:new String[]{"https://192.168.5.10:4567/v1/info","http://192.168.5.11:4567/v1/info",
                    "http://192.168.5.10:4568/v1/info","http://name:secret@192.168.5.10:4567/v1/info",
                    "http://example.invalid:4567/v1/info",ENDPOINT.origin+"/v1/info#credential"}){
                reject("INVALID_ORIGIN",()->rig.connections.open(new URL(url)));
            }
            assertTrue(rig.routes.snapshot().isEmpty());
        }
    }

    @Test public void lostRouteRequiresBoundedCredentialFreeHostProofBeforeMedia()throws Exception{
        try(Rig rig=new Rig()){
            rig.pin();rig.first.usable=false;
            HttpURLConnection actual=rig.connections.open(media());
            List<Opened> opened=rig.routes.snapshot();assertEquals(2,opened.size());
            Opened proof=opened.get(0);assertSame(rig.second,proof.route);assertEquals("/v1/info",proof.connection.getURL().getPath());
            assertNull(proof.connection.getURL().getQuery());assertNull(proof.connection.getURL().getUserInfo());
            assertEquals("GET",proof.connection.methodAtConnect);assertFalse(proof.connection.outputAtConnect);
            assertEquals(3000,proof.connection.getConnectTimeout());assertEquals(5000,proof.connection.getReadTimeout());
            assertFalse(proof.connection.getInstanceFollowRedirects());assertEquals(0,proof.connection.requestBody.size());
            assertTrue(proof.connection.headersAtConnect.keySet().stream().noneMatch(k->k.equalsIgnoreCase("Authorization")||k.equalsIgnoreCase("Cookie")));
            assertTrue("Completed proof connection is closed",proof.connection.disconnected);
            assertEquals(media(),opened.get(1).connection.getURL());assertSame(actual,opened.get(1).connection);
            assertSame(rig.second,rig.connections.selectedRoute());assertEquals(1,rig.connections.verificationCount());assertEquals(1,rig.connections.replacementCount());
        }
    }

    @Test public void differentDesktopCannotReceivePairingOrMediaCredentials()throws Exception{
        try(Rig rig=new Rig()){
            rig.pin();rig.first.usable=false;rig.second.reply=Reply.info("different-desktop");
            reject("HOST_CHANGED",()->rig.connections.open(media()));
            assertOnlyPublicProbes(rig.routes);assertNotSame(rig.second,rig.connections.selectedRoute());assertEquals(0,rig.connections.replacementCount());
        }
    }

    @Test public void hostPinCannotBeChangedToApproveAnotherDesktop()throws Exception{
        try(Rig rig=new Rig()){
            rig.pin();reject("HOST_CHANGED",()->rig.connections.pinExpectedHost("different-desktop"));
            rig.first.usable=false;rig.connections.open(media());
            assertSame("Original host pin remains effective",rig.second,rig.connections.selectedRoute());
        }
    }

    @Test public void missingOrUnusableReplacementNeverFallsBackToOtherTraffic()throws Exception{
        try(Rig rig=new Rig()){
            rig.pin();rig.first.usable=false;rig.routes.candidate=null;
            reject("NO_WIFI",()->rig.connections.open(media()));assertTrue(rig.routes.snapshot().isEmpty());
            rig.routes.candidate=rig.second;rig.second.usable=false;
            reject("NO_WIFI",()->rig.connections.open(media()));assertTrue(rig.routes.snapshot().isEmpty());
        }
    }

    @Test public void redirectsMalformedAndNonSuccessProofsAreRejected()throws Exception{
        for(Reply reply:new Reply[]{new Reply(302,"",0),new Reply(503,"unavailable",-1),new Reply(200,"not JSON",-1),
                new Reply(200,new JSONObject().put("version",2).put("id",HOST).put("name","Desktop").put("pairingRequired",true).toString(),-1)}){
            try(Rig rig=new Rig()){
                rig.pin();rig.first.usable=false;rig.second.reply=reply;
                reject("PROBE_FAILED",()->rig.connections.open(media()));
                assertOnlyPublicProbes(rig.routes);assertFalse(rig.routes.snapshot().get(0).connection.getInstanceFollowRedirects());
                assertTrue(rig.routes.snapshot().get(0).connection.disconnected);
            }
        }
    }

    @Test public void declaredAndStreamingProofBodiesAreBounded()throws Exception{
        try(Rig rig=new Rig()){
            rig.pin();rig.first.usable=false;rig.second.reply=new Reply(200,"{}",65537);
            reject("PROBE_FAILED",()->rig.connections.open(media()));
            assertEquals("An excessive declared length is rejected before reading",0,rig.routes.snapshot().get(0).connection.bodyBytesRead);
            assertOnlyPublicProbes(rig.routes);
        }
        try(Rig rig=new Rig()){
            rig.pin();rig.first.usable=false;
            String large=new JSONObject().put("version",1).put("id",HOST).put("name","Desktop").put("pairingRequired",true).put("padding","x".repeat(1024*1024)).toString();
            rig.second.reply=new Reply(200,large,-1);
            reject("PROBE_FAILED",()->rig.connections.open(media()));
            assertTrue("No full oversized response was read",rig.routes.snapshot().get(0).connection.bodyBytesRead<=65536+8192);
            assertOnlyPublicProbes(rig.routes);
        }
    }

    @Test public void candidateIsRecheckedAfterItsPublicProof()throws Exception{
        try(Rig rig=new Rig()){
            rig.pin();rig.first.usable=false;rig.second.onResponse=()->rig.second.usable=false;
            reject("ROUTE_CHANGED",()->rig.connections.open(media()));assertOnlyPublicProbes(rig.routes);
            assertNotSame(rig.second,rig.connections.selectedRoute());assertEquals(0,rig.connections.replacementCount());
        }
    }

    @Test public void closeDoesNotWaitOnProbeIoOrPermitAStaleCredentialReturn()throws Exception{
        try(Rig rig=new Rig()){
            rig.pin();rig.first.usable=false;CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);
            rig.second.onResponse=blocked(entered,release);ExecutorService workers=Executors.newFixedThreadPool(2);
            try{
                Future<String> opening=workers.submit(()->reason(()->rig.connections.open(media())));
                assertTrue("Public verification did not start",entered.await(2,TimeUnit.SECONDS));assertOnlyPublicProbes(rig.routes);
                workers.submit(rig.connections::close).get(1,TimeUnit.SECONDS);
                release.countDown();assertEquals("CLOSED",opening.get(2,TimeUnit.SECONDS));assertOnlyPublicProbes(rig.routes);
                reject("CLOSED",()->rig.connections.open(ENDPOINT.api("/v1/info")));
            }finally{release.countDown();workers.shutdownNow();}
        }
    }

    @Test public void invalidationDoesNotWaitAndInvalidatesAnInFlightProof()throws Exception{
        try(Rig rig=new Rig()){
            rig.pin();rig.first.usable=false;CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);
            rig.second.onResponse=blocked(entered,release);ExecutorService workers=Executors.newFixedThreadPool(2);
            try{
                Future<String> opening=workers.submit(()->reason(()->rig.connections.open(media())));
                assertTrue(entered.await(2,TimeUnit.SECONDS));
                workers.submit(rig.connections::invalidateForTesting).get(1,TimeUnit.SECONDS);
                release.countDown();assertEquals("ROUTE_CHANGED",opening.get(2,TimeUnit.SECONDS));assertOnlyPublicProbes(rig.routes);
                rig.second.onResponse=null;rig.connections.open(media());assertSame(rig.second,rig.connections.selectedRoute());
                assertEquals(1,rig.routes.snapshot().stream().filter(o->o.connection.getURL().getQuery()!=null).count());
            }finally{release.countDown();workers.shutdownNow();}
        }
    }

    private static URL media(){return ENDPOINT.media("/v1/media/original-item?token="+TOKEN,"original-item",TOKEN);}
    private static Runnable blocked(CountDownLatch entered,CountDownLatch release){return ()->{entered.countDown();try{if(!release.await(4,TimeUnit.SECONDS))throw new AssertionError("Synthetic probe was not released");}catch(InterruptedException error){Thread.currentThread().interrupt();throw new AssertionError(error);}};}
    private interface Throwing {void run()throws Exception;}
    private static String reason(Throwing action)throws Exception{try{action.run();fail("Unsafe route operation succeeded");return "";}catch(ScopedWifiConnections.RouteException expected){return expected.reason.name();}}
    private static void reject(String expected,Throwing action)throws Exception{assertEquals(expected,reason(action));}
    private static void assertOnlyPublicProbes(FakeRoutes routes){for(Opened opened:routes.snapshot()){assertEquals("/v1/info",opened.connection.getURL().getPath());assertNull(opened.connection.getURL().getQuery());assertFalse(opened.connection.getURL().toString().contains(TOKEN));}}

    private static final class Rig implements AutoCloseable {
        final Route first=new Route("initial"),second=new Route("replacement");
        final FakeRoutes routes=new FakeRoutes(second);
        final ScopedWifiConnections<Route> connections=new ScopedWifiConnections<>(ENDPOINT,first,routes);
        void pin()throws Exception{connections.pinExpectedHost(new LanApi(connections).info(ENDPOINT).id);routes.clear();}
        public void close(){connections.close();}
    }
    private static final class Route {
        final String name;volatile boolean usable=true;volatile Reply reply=Reply.info(HOST);volatile Runnable onResponse,onOpen;
        Route(String name){this.name=name;}
    }
    private static final class Reply {
        final int status;final byte[] body;final long declared;
        Reply(int status,String body,long declared){this.status=status;this.body=body.getBytes(StandardCharsets.UTF_8);this.declared=declared;}
        static Reply info(String id){try{String body=new JSONObject().put("version",1).put("id",id).put("name","Original desktop").put("pairingRequired",true).toString();return new Reply(200,body,body.getBytes(StandardCharsets.UTF_8).length);}catch(Exception error){throw new AssertionError(error);}}
    }
    private static final class Opened {
        final Route route;final FakeConnection connection;
        Opened(Route route,FakeConnection connection){this.route=route;this.connection=connection;}
    }
    private static final class FakeRoutes implements ScopedWifiConnections.Routes<Route> {
        volatile Route candidate;final List<Opened> opened=Collections.synchronizedList(new ArrayList<>());
        FakeRoutes(Route candidate){this.candidate=candidate;}
        public boolean usable(Route route){return route!=null&&route.usable;}
        public Route chooseWifi(){return candidate;}
        public HttpURLConnection open(Route route,URL url)throws IOException{
            FakeConnection connection=new FakeConnection(url,route.reply,route.onResponse);opened.add(new Opened(route,connection));if(route.onOpen!=null)route.onOpen.run();return connection;
        }
        List<Opened> snapshot(){synchronized(opened){return new ArrayList<>(opened);}}
        void clear(){opened.clear();}
    }
    private static final class FakeConnection extends HttpURLConnection {
        final Reply reply;final Runnable onResponse;final ByteArrayOutputStream requestBody=new ByteArrayOutputStream();
        volatile boolean disconnected;volatile long bodyBytesRead;volatile String methodAtConnect;volatile boolean outputAtConnect;
        Map<String,List<String>> headersAtConnect=new LinkedHashMap<>();boolean responseHookRun;
        FakeConnection(URL url,Reply reply,Runnable onResponse){super(url);this.reply=reply;this.onResponse=onResponse;}
        public void connect(){if(!connected){methodAtConnect=getRequestMethod();outputAtConnect=getDoOutput();headersAtConnect=new LinkedHashMap<>(getRequestProperties());connected=true;}}
        public void disconnect(){disconnected=true;}
        public boolean usingProxy(){return false;}
        public int getResponseCode(){connect();if(!responseHookRun){responseHookRun=true;if(onResponse!=null)onResponse.run();}return reply.status;}
        public String getHeaderField(String name){if(name.equalsIgnoreCase("Content-Length"))return reply.declared<0?null:Long.toString(reply.declared);if(name.equalsIgnoreCase("Content-Type"))return "application/json";if(name.equalsIgnoreCase("Location")&&reply.status==302)return "http://external.invalid/stolen";return null;}
        public long getContentLengthLong(){return reply.declared;}
        public InputStream getInputStream(){connect();return new ByteArrayInputStream(reply.body){public synchronized int read(){int value=super.read();if(value>=0)bodyBytesRead++;return value;}public synchronized int read(byte[] bytes,int offset,int length){int count=super.read(bytes,offset,length);if(count>0)bodyBytesRead+=count;return count;}};}
        public OutputStream getOutputStream(){return requestBody;}
    }
}
