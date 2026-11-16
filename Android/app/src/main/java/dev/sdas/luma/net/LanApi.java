package dev.sdas.luma.net;

import dev.sdas.luma.SkipSegments;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.Proxy;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

public final class LanApi {
    public interface Connections { HttpURLConnection open(URL url) throws IOException; }
    public static final Connections DIRECT = url -> (HttpURLConnection)url.openConnection(Proxy.NO_PROXY);
    public static final class ApiException extends IOException {
        public final int status;
        public ApiException(int status, String message) { super(message); this.status = status; }
    }
    public static final class Info {
        public final String id, name;
        public final boolean supportsProgress;
        Info(String id, String name) { this(id,name,false); }
        Info(String id, String name,boolean supportsProgress) { this.id=id; this.name=name;this.supportsProgress=supportsProgress; }
    }
    public static final class Session {
        public final LanEndpoint endpoint;
        public final Info host;
        public final String token;
        public Session(LanEndpoint endpoint, Info host, String token) {
            if (!LanEndpoint.validToken(token)) throw new IllegalArgumentException("Invalid pairing response.");
            this.endpoint=endpoint;this.host=host;this.token=token;
        }
    }
    public static final class Item {
        public final String id, title, mimeType, streamPath;
        public final double durationSeconds, resumeSeconds;
        public final double computerProgressSeconds,computerUpdatedAtUnixSeconds,mobileProgressSeconds,mobileDurationSeconds,mobileUpdatedAtUnixSeconds;
        public final boolean computerCompleted,mobileCompleted;
        public final List<SkipSegments.Range> skipRanges;
        Item(String id,String title,String mimeType,String streamPath,double duration,double resume) {
            this(id,title,mimeType,streamPath,duration,resume,resume,0,false,Double.NaN,0,0,false);
        }
        Item(String id,String title,String mimeType,String streamPath,double duration,double resume,double computerProgress,double computerUpdated,boolean computerCompleted,double mobileProgress,double mobileDuration,double mobileUpdated,boolean mobileCompleted) {
            this(id,title,mimeType,streamPath,duration,resume,computerProgress,computerUpdated,computerCompleted,mobileProgress,mobileDuration,mobileUpdated,mobileCompleted,Collections.emptyList());
        }
        Item(String id,String title,String mimeType,String streamPath,double duration,double resume,double computerProgress,double computerUpdated,boolean computerCompleted,double mobileProgress,double mobileDuration,double mobileUpdated,boolean mobileCompleted,List<SkipSegments.Range> skipRanges) {
            this.skipRanges=Collections.unmodifiableList(new ArrayList<>(skipRanges));
            this.id=id;this.title=title;this.mimeType=mimeType;this.streamPath=streamPath;this.durationSeconds=duration;this.resumeSeconds=resume;
            this.computerProgressSeconds=computerProgress;this.computerUpdatedAtUnixSeconds=computerUpdated;this.computerCompleted=computerCompleted;
            this.mobileProgressSeconds=mobileProgress;this.mobileDurationSeconds=mobileDuration;this.mobileUpdatedAtUnixSeconds=mobileUpdated;this.mobileCompleted=mobileCompleted;
        }
        public boolean hasMobileProgress(){return Double.isFinite(mobileProgressSeconds);}
    }
    public static final class ProgressReceipt {
        public final double positionSeconds,durationSeconds,updatedAtUnixSeconds;
        public final boolean completed;
        ProgressReceipt(double position,double duration,boolean completed,double updated){this.positionSeconds=position;this.durationSeconds=duration;this.completed=completed;this.updatedAtUnixSeconds=updated;}
    }
    public static final class Playlist {
        public final List<Item> items;
        public final boolean truncated;
        public final int totalItems;
        private Playlist(List<Item> items, boolean truncated, int totalItems) {
            this.items=items;this.truncated=truncated;this.totalItems=totalItems;
        }
        public String summary() {
            if(truncated)return "Showing first "+items.size()+" of "+totalItems+" items · live desktop playlist";
            return items.isEmpty()?"The desktop playlist is empty":items.size()+" items · live desktop playlist";
        }
    }
    private final Connections connections;
    public LanApi() { this(DIRECT); }
    public LanApi(Connections connections) { this.connections=connections; }
    public Connections connections() { return connections; }

    public Info info(LanEndpoint endpoint) throws IOException {
        try {
            JSONObject json=request(endpoint.api("/v1/info"),"GET",null,null);
            if (json.getInt("version") != 1 || !json.getBoolean("pairingRequired")) throw malformed();
            return new Info(bounded(json.getString("id"),128),bounded(json.getString("name"),256),optionalBoolean(json,"progressReporting",false));
        } catch (JSONException | IllegalArgumentException e) { throw malformed(); }
    }
    public Session pair(LanEndpoint endpoint, Info info, String code) throws IOException {
        if (code == null || !code.matches("[0-9]{6}")) throw new ApiException(0,"Enter the desktop's six-digit pairing code.");
        try {
            JSONObject response=request(endpoint.api("/v1/pair"),"POST",null,new JSONObject().put("code",code).toString());
            return new Session(endpoint,info,response.getString("token"));
        } catch (JSONException | IllegalArgumentException e) { throw malformed(); }
    }
    public List<Item> playlist(Session session) throws IOException {
        return playlistState(session).items;
    }
    public Playlist playlistState(Session session) throws IOException {
        return parsePlaylistState(request(session.endpoint.api("/v1/playlist"),"GET",session.token,null),session);
    }
    /** Metadata only: this never sends a file path or controls the desktop player. */
    public ProgressReceipt reportProgress(Session session,String itemId,double position,double duration,boolean completed)throws IOException{
        if(!session.host.supportsProgress)throw new ApiException(0,"This desktop version does not support shared progress yet.");
        if(itemId==null||!itemId.matches("[A-Za-z0-9_-]{1,200}")||!validTime(position)||!validTime(duration))throw malformed();
        if(duration>0)position=Math.min(position,duration);
        try{
            JSONObject body=new JSONObject().put("itemId",itemId).put("positionSeconds",position).put("durationSeconds",duration).put("completed",completed);
            if(body.toString().getBytes(StandardCharsets.UTF_8).length>4096)throw malformed();
            JSONObject response=request(session.endpoint.api("/v1/progress"),"POST",session.token,body.toString());
            if(response.getInt("version")!=1)throw malformed();
            return new ProgressReceipt(optionalNumber(response,"positionSeconds",position,31536000),optionalNumber(response,"durationSeconds",duration,31536000),optionalBoolean(response,"completed",completed),optionalNumber(response,"updatedAtUnixSeconds",0,253402300799.0));
        }catch(JSONException error){throw malformed();}
    }
    public static List<Item> parsePlaylist(JSONObject json, Session session) throws ApiException {
        return parsePlaylistState(json,session).items;
    }
    public static Playlist parsePlaylistState(JSONObject json, Session session) throws ApiException {
        try {
            if (json.getInt("version") != 1) throw malformed();
            bounded(json.getString("name"),256);
            JSONArray items=json.getJSONArray("items"); if (items.length()>1000) throw malformed();
            // Older v1 hosts can omit both fields; new hosts always make truncation explicit.
            boolean truncated=false;int total=items.length();
            if(json.has("truncated")!=json.has("totalItems"))throw malformed();
            if(json.has("truncated")) {
                Object flag=json.get("truncated"), count=json.get("totalItems");
                if(!(flag instanceof Boolean)||!(count instanceof Number))throw malformed();
                double value=((Number)count).doubleValue();
                if(!Double.isFinite(value)||value<0||value>Integer.MAX_VALUE||value!=Math.floor(value))throw malformed();
                truncated=(Boolean)flag;total=(int)value;
                if(total<items.length()||truncated!=(total>items.length()))throw malformed();
            }
            List<Item> result=new ArrayList<>();java.util.HashSet<String> ids=new java.util.HashSet<>();
            for (int i=0;i<items.length();i++) {
                JSONObject item=items.getJSONObject(i);String id=bounded(item.getString("id"),200), title=bounded(item.getString("title"),512);
                if (!ids.add(id)) throw malformed();
                String path=bounded(item.getString("streamPath"),1024);session.endpoint.media(path,id,session.token);
                double duration=item.getDouble("durationSeconds"),resume=item.getDouble("resumeSeconds");
                if (!Double.isFinite(duration)||!Double.isFinite(resume)||duration<0||resume<0||duration>31536000||resume>31536000) throw malformed();
                if (duration>0) resume=Math.min(resume,duration);
                String mime=bounded(item.getString("mimeType"),128);
                double computer=optionalNumber(item,"computerProgressSeconds",resume,31536000),computerUpdated=optionalNumber(item,"computerUpdatedAtUnixSeconds",0,253402300799.0);
                double mobile=optionalNumber(item,"mobileProgressSeconds",Double.NaN,31536000),mobileDuration=optionalNumber(item,"mobileDurationSeconds",0,31536000),mobileUpdated=optionalNumber(item,"mobileUpdatedAtUnixSeconds",0,253402300799.0);
                if(duration>0)computer=Math.min(computer,duration);
                if(duration>0)mobileDuration=duration;
                if(Double.isFinite(mobile)){double known=duration>0?duration:mobileDuration;if(known>0)mobile=Math.min(mobile,known);}
                if(item.has("skipRanges")&&!item.isNull("skipRanges")&&!(item.get("skipRanges") instanceof JSONArray))throw malformed();
                result.add(new Item(id,title,mime,path,duration,resume,computer,computerUpdated,optionalBoolean(item,"computerCompleted",false),mobile,mobileDuration,mobileUpdated,optionalBoolean(item,"mobileCompleted",false),SkipSegments.parse(item.optJSONArray("skipRanges"),duration)));
            }
            return new Playlist(Collections.unmodifiableList(result),truncated,total);
        } catch (JSONException | IllegalArgumentException e) { throw malformed(); }
    }
    private JSONObject request(URL url,String method,String token,String body) throws IOException {
        HttpURLConnection connection=null;
        try {
            connection=connections.open(url);connection.setInstanceFollowRedirects(false);connection.setUseCaches(false);
            connection.setConnectTimeout(5000);connection.setReadTimeout(8000);connection.setRequestMethod(method);
            connection.setRequestProperty("Accept","application/json");connection.setRequestProperty("Connection","close");
            connection.setRequestProperty("User-Agent","LumaAndroid/0.1");
            if (token!=null) { if(!LanEndpoint.validToken(token))throw malformed();connection.setRequestProperty("Authorization","Bearer "+token); }
            if(body!=null) {
                byte[] data=body.getBytes(StandardCharsets.UTF_8);connection.setDoOutput(true);connection.setFixedLengthStreamingMode(data.length);
                connection.setRequestProperty("Content-Type","application/json; charset=utf-8");
                try(java.io.OutputStream output=connection.getOutputStream()){output.write(data);}
            }
            int status=connection.getResponseCode();
            if(status==401)throw new ApiException(status,"Pairing expired or the code was incorrect. Pair again with the desktop.");
            if(status==429)throw new ApiException(status,"Too many pairing attempts. Wait a minute and use the current desktop code.");
            if(status>=300&&status<400)throw new ApiException(status,"Desktop redirects are blocked. Use its displayed local address.");
            if(status!=200)throw new ApiException(status,status==404?"Sharing or this media is no longer available.":"The desktop could not complete the request.");
            int limit=4*1024*1024;long declared=connection.getContentLengthLong();if(declared>limit)throw malformed();
            ByteArrayOutputStream data=new ByteArrayOutputStream();
            try(InputStream input=connection.getInputStream()) {
                byte[] buffer=new byte[8192];int count;
                while((count=input.read(buffer))!=-1){if(data.size()+count>limit)throw malformed();data.write(buffer,0,count);}
            }
            String text=StandardCharsets.UTF_8.newDecoder().onMalformedInput(java.nio.charset.CodingErrorAction.REPORT).onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT).decode(java.nio.ByteBuffer.wrap(data.toByteArray())).toString();
            return new JSONObject(text);
        } catch(JSONException e){throw malformed();}
        catch(ScopedWifiConnections.RouteException e){throw new ApiException(e.reason==ScopedWifiConnections.Reason.HOST_CHANGED||e.reason==ScopedWifiConnections.Reason.UNPINNED?401:0,e.getMessage());}
        catch(ApiException e){throw e;}
        catch(IOException e){throw new ApiException(0,"Desktop offline or unreachable. Keep both devices on the same Wi-Fi and sharing enabled.");}
        finally {if(connection!=null)connection.disconnect();}
    }
    private static String bounded(String value,int limit) throws ApiException {
        if(value==null||value.isEmpty()||value.length()>limit||value.indexOf('\0')>=0)throw malformed();return value;
    }
    private static boolean validTime(double value){return Double.isFinite(value)&&value>=0&&value<=31536000;}
    private static double optionalNumber(JSONObject object,String key,double fallback,double maximum)throws ApiException{
        if(!object.has(key)||object.isNull(key))return fallback;
        Object value=object.opt(key);if(!(value instanceof Number))throw malformed();double number=((Number)value).doubleValue();
        if(!Double.isFinite(number)||number<0||number>maximum)throw malformed();return number;
    }
    private static boolean optionalBoolean(JSONObject object,String key,boolean fallback)throws ApiException{
        if(!object.has(key)||object.isNull(key))return fallback;Object value=object.opt(key);if(!(value instanceof Boolean))throw malformed();return (Boolean)value;
    }
    private static ApiException malformed(){return new ApiException(0,"The desktop returned an unsupported or unsafe sharing response.");}
}
