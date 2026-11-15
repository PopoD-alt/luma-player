package dev.sdas.luma.net;

import org.json.JSONArray;
import org.json.JSONObject;
import org.junit.Test;
import static org.junit.Assert.*;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;

public class LanProtocolTest {
    static final String TOKEN="a".repeat(64);
    @Test public void onlyPrivateNumericOriginsAndCanonicalPorts(){
        for(String value:new String[]{"http://10.0.2.2:1234","http://172.16.1.3:4","http://192.168.1.2:65535","http://169.254.1.2:80","http://127.0.0.1:9999","http://[fd12::1234]:1234","http://[::1]:1234"})assertNotNull(LanEndpoint.parse(value));
        for(String value:new String[]{"http://8.8.8.8:1234","http://172.32.1.3:4","https://192.168.1.2:1234","http://example.com:80","http://desktop.local:1234","http://2130706433:80","http://010.0.0.1:80","http://0.0.0.0:80","http://[::]:80","http://[2001:4860:4860::8888]:80","http://[::ffff:8.8.8.8]:80","http://user:pass@192.168.1.2:80","http://192.168.1.2:80/x","http://192.168.1.2:80?token=x","http://192.168.1.2:80#x","http://192.168.1.2","http://192.168.1.2:0","http://192.168.1.2:65536"," http://192.168.1.2:80"})reject(()->LanEndpoint.parse(value));
    }
    @Test public void streamLinksMustRemainExactSameOriginSession(){
        LanEndpoint e=LanEndpoint.parse("http://192.168.1.2:1234");assertEquals("192.168.1.2",e.media("/v1/media/episode-1?token="+TOKEN,"episode-1",TOKEN).getHost());
        for(String value:new String[]{"http://8.8.8.8/a","//8.8.8.8/a","/v1/media/episode-1?token=wrong","/v1/media/other?token="+TOKEN,"/v1/media/episode-1?token="+TOKEN+"&token=x","/v1/media/episode-1?token="+TOKEN+"#x","/v1/media/%2e%2e?token="+TOKEN})reject(()->e.media(value,"episode-1",TOKEN));
        assertFalse(LanEndpoint.validToken("x\r\nAuthorization: secret"));
    }
    @Test public void pairingAndPlaylistUseExactProductionProtocol()throws Exception{
        try(Server server=new Server()){
            LanApi api=new LanApi();LanEndpoint endpoint=server.endpoint();server.add(200,"{\"version\":1,\"name\":\"Luma desktop\",\"id\":\"sharing-session\",\"pairingRequired\":true}");
            LanApi.Info info=api.info(endpoint);Request request=server.request();assertEquals("GET /v1/info HTTP/1.1",request.line);assertFalse(request.headers.containsKey("authorization"));
            server.add(200,"{\"token\":\""+TOKEN+"\",\"name\":\"Luma desktop\"}");LanApi.Session session=api.pair(endpoint,info,"123456");request=server.request();assertEquals("POST /v1/pair HTTP/1.1",request.line);assertEquals("123456",new JSONObject(request.body).getString("code"));assertFalse(request.headers.containsKey("authorization"));
            server.add(200,playlist(TOKEN).toString());List<LanApi.Item> items=api.playlist(session);request=server.request();assertEquals("Bearer "+TOKEN,request.headers.get("authorization"));assertEquals(1,items.size());assertEquals(2,items.get(0).resumeSeconds,0);assertEquals("Episode",items.get(0).title);
            try{api.pair(endpoint,info,"123");fail();}catch(LanApi.ApiException expected){assertEquals(0,expected.status);}
        }
    }
    @Test public void malformedAndCrossSessionPlaylistsRejected()throws Exception{
        LanApi.Session s=new LanApi.Session(LanEndpoint.parse("http://127.0.0.1:1234"),new LanApi.Info("session","Desktop"),TOKEN);
        JSONObject valid=playlist(TOKEN);assertEquals(1,LanApi.parsePlaylist(valid,s).size());
        JSONObject bad=playlist("b".repeat(64));rejectApi(()->LanApi.parsePlaylist(bad,s));
        JSONObject duplicate=playlist(TOKEN);duplicate.getJSONArray("items").put(duplicate.getJSONArray("items").getJSONObject(0));rejectApi(()->LanApi.parsePlaylist(duplicate,s));
        JSONObject negative=playlist(TOKEN);negative.getJSONArray("items").getJSONObject(0).put("resumeSeconds",-1);rejectApi(()->LanApi.parsePlaylist(negative,s));
        JSONObject path=playlist(TOKEN);path.getJSONArray("items").getJSONObject(0).put("streamPath","https://cloud.example/video");rejectApi(()->LanApi.parsePlaylist(path,s));
        JSONObject version=playlist(TOKEN);version.put("version",2);rejectApi(()->LanApi.parsePlaylist(version,s));
    }
    @Test public void explicitTruncationIsPreservedAndShownWithoutHidingIt()throws Exception{
        LanApi.Session session=new LanApi.Session(LanEndpoint.parse("http://127.0.0.1:1234"),new LanApi.Info("session","Desktop"),TOKEN);
        JSONObject response=playlist(TOKEN).put("truncated",true).put("totalItems",1300);
        LanApi.Playlist value=LanApi.parsePlaylistState(response,session);
        assertTrue(value.truncated);assertEquals(1300,value.totalItems);assertEquals(1,value.items.size());
        assertEquals("Showing first 1 of 1300 items · live desktop playlist",value.summary());
        response.put("truncated",false).put("totalItems",1);value=LanApi.parsePlaylistState(response,session);assertFalse(value.truncated);assertEquals(1,value.totalItems);
        response.put("truncated",false).put("totalItems",2);rejectApi(()->LanApi.parsePlaylistState(response,session));
        response.put("truncated",true).put("totalItems",0);rejectApi(()->LanApi.parsePlaylistState(response,session));
        response.put("totalItems",1.5);rejectApi(()->LanApi.parsePlaylistState(response,session));
        response.remove("totalItems");rejectApi(()->LanApi.parsePlaylistState(response,session));
    }
    @Test public void sharedWireItemTitleAndTimeBoundsAreEnforced()throws Exception{
        LanApi.Session session=new LanApi.Session(LanEndpoint.parse("http://127.0.0.1:1234"),new LanApi.Info("session","Desktop"),TOKEN);
        JSONObject response=playlist(TOKEN);JSONObject item=response.getJSONArray("items").getJSONObject(0);
        item.put("title","x".repeat(512)).put("durationSeconds",31536000).put("resumeSeconds",31536000);
        assertEquals(512,LanApi.parsePlaylistState(response,session).items.get(0).title.length());
        item.put("title","x".repeat(513));rejectApi(()->LanApi.parsePlaylistState(response,session));item.put("title","Valid");
        item.put("durationSeconds",31536001);rejectApi(()->LanApi.parsePlaylistState(response,session));item.put("durationSeconds",1).put("resumeSeconds",0);
        JSONArray items=response.getJSONArray("items");for(int i=1;i<=1000;i++)items.put(new JSONObject(item.toString()).put("id","id_"+i).put("streamPath","/v1/media/id_"+i+"?token="+TOKEN));
        rejectApi(()->LanApi.parsePlaylistState(response,session));
    }
    @Test public void redirectsAndAuthorizationErrorsDoNotLeakTokens()throws Exception{
        try(Server server=new Server()){
            LanApi api=new LanApi();server.responses.add(new Response(302,"",Map.of("Location","http://8.8.8.8:1/stolen")));
            try{api.info(server.endpoint());fail();}catch(LanApi.ApiException error){assertEquals(302,error.status);assertFalse(error.getMessage().contains(TOKEN));}
            server.request();server.add(401,"{\"secret\":\""+TOKEN+"\"}");
            try{api.playlist(new LanApi.Session(server.endpoint(),new LanApi.Info("session","Desktop"),TOKEN));fail();}catch(LanApi.ApiException error){assertEquals(401,error.status);assertFalse(error.getMessage().contains(TOKEN));}
        }
    }
    @Test public void relayForwardsRangesAndHeadWithoutPersistingOrExposingDesktopToken()throws Exception{
        try(Server server=new Server()){
            LanApi.Session session=new LanApi.Session(server.endpoint(),new LanApi.Info("session","Desktop"),TOKEN);LanApi.Item item=LanApi.parsePlaylist(playlist(TOKEN),session).get(0);
            try(StreamRelay relay=new StreamRelay(session,item,LanApi.DIRECT,f->{})){
                assertFalse(relay.playbackUrl().contains(TOKEN));server.responses.add(new Response(206,"2345",Map.of("Content-Range","bytes 2-5/10")));
                HttpURLConnection client=(HttpURLConnection)new URL(relay.playbackUrl()).openConnection();client.setRequestProperty("Range","bytes=2-5");assertEquals(206,client.getResponseCode());assertEquals("bytes 2-5/10",client.getHeaderField("Content-Range"));assertEquals("2345",new String(client.getInputStream().readAllBytes(),StandardCharsets.US_ASCII));client.disconnect();
                Request upstream=server.request();assertEquals("bytes=2-5",upstream.headers.get("range"));assertEquals("GET /v1/media/episode-1?token="+TOKEN+" HTTP/1.1",upstream.line);
                server.responses.add(new Response(200,"",Map.of("Content-Length","10")));client=(HttpURLConnection)new URL(relay.playbackUrl()).openConnection();client.setRequestMethod("HEAD");assertEquals(200,client.getResponseCode());assertEquals(10,client.getContentLengthLong());client.disconnect();assertTrue(server.request().line.startsWith("HEAD "));
            }
        }
    }
    @Test public void relayNeverFollowsRedirects()throws Exception{
        try(Server server=new Server()){
            LanApi.Session session=new LanApi.Session(server.endpoint(),new LanApi.Info("session","Desktop"),TOKEN);LanApi.Item item=LanApi.parsePlaylist(playlist(TOKEN),session).get(0);List<StreamRelay.Failure> failures=new java.util.concurrent.CopyOnWriteArrayList<>();
            try(StreamRelay relay=new StreamRelay(session,item,LanApi.DIRECT,failures::add)){
                server.responses.add(new Response(302,"",Map.of("Location","https://cloud.example/private")));HttpURLConnection client=(HttpURLConnection)new URL(relay.playbackUrl()).openConnection();assertEquals(502,client.getResponseCode());client.disconnect();server.request();
                long deadline=System.currentTimeMillis()+1000;while(failures.isEmpty()&&System.currentTimeMillis()<deadline)Thread.sleep(10);assertTrue(failures.contains(StreamRelay.Failure.UNSAFE_RESPONSE));
            }
        }
    }
    static JSONObject playlist(String token)throws Exception{return new JSONObject().put("version",1).put("name","Desktop").put("items",new JSONArray().put(new JSONObject().put("id","episode-1").put("title","Episode").put("durationSeconds",12).put("resumeSeconds",2).put("mimeType","video/mp4").put("streamPath","/v1/media/episode-1?token="+token)));}
    interface Throwing{void run()throws Exception;}
    static void reject(Runnable run){try{run.run();fail("Unsafe endpoint accepted");}catch(IllegalArgumentException expected){}}
    static void rejectApi(Throwing run)throws Exception{try{run.run();fail("Unsafe response accepted");}catch(LanApi.ApiException expected){}}
    static final class Request{final String line,body;final Map<String,String>headers;Request(String line,String body,Map<String,String>headers){this.line=line;this.body=body;this.headers=headers;}}
    static final class Response{final int status;final String body;final Map<String,String>headers;Response(int s,String b,Map<String,String>h){status=s;body=b;headers=h;}}
    static final class Server implements AutoCloseable{
        final ServerSocket listener;final LinkedBlockingQueue<Response>responses=new LinkedBlockingQueue<>();final LinkedBlockingQueue<Request>requests=new LinkedBlockingQueue<>();volatile boolean closed;
        Server()throws Exception{listener=new ServerSocket(0,8,InetAddress.getByName("127.0.0.1"));Thread t=new Thread(()->{while(!closed)try(Socket socket=listener.accept()){
            socket.setSoTimeout(5000);InputStream input=socket.getInputStream();String first=line(input);Map<String,String>headers=new HashMap<>();String h;while(!(h=line(input)).isEmpty()){int colon=h.indexOf(':');headers.put(h.substring(0,colon).toLowerCase(java.util.Locale.ROOT),h.substring(colon+1).trim());}
            int count=Integer.parseInt(headers.getOrDefault("content-length","0"));String body=new String(input.readNBytes(count),StandardCharsets.UTF_8);requests.add(new Request(first,body,headers));Response r=responses.poll(5,TimeUnit.SECONDS);if(r==null)continue;byte[]data=r.body.getBytes(StandardCharsets.UTF_8);
            StringBuilder reply=new StringBuilder("HTTP/1.1 ").append(r.status).append(" Response\r\nConnection: close\r\n");if(!r.headers.containsKey("Content-Length"))reply.append("Content-Length: ").append(data.length).append("\r\n");for(Map.Entry<String,String>e:r.headers.entrySet())reply.append(e.getKey()).append(": ").append(e.getValue()).append("\r\n");reply.append("\r\n");socket.getOutputStream().write(reply.toString().getBytes(StandardCharsets.US_ASCII));socket.getOutputStream().write(data);socket.getOutputStream().flush();
        }catch(Exception ignored){}});t.setDaemon(true);t.start();}
        LanEndpoint endpoint(){return LanEndpoint.parse("http://127.0.0.1:"+listener.getLocalPort());}void add(int status,String body){responses.add(new Response(status,body,Map.of()));}
        Request request()throws Exception{Request r=requests.poll(5,TimeUnit.SECONDS);assertNotNull("No HTTP request",r);return r;}
        public void close()throws Exception{closed=true;listener.close();}
        static String line(InputStream input)throws IOException{ByteArrayOutputStream bytes=new ByteArrayOutputStream();int c;while((c=input.read())!=-1){if(c=='\n')return bytes.toString(StandardCharsets.US_ASCII).replace("\r","");bytes.write(c);}throw new IOException("EOF");}
    }
}
