package dev.sdas.luma.net;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.Collections;
import java.util.HashMap;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;

/**
 * Bounded, loopback-only streaming bridge. Desktop tokens never enter libVLC's URL/logs.
 * Redirects are never followed; byte ranges are forwarded; only a64KiB memory buffer
 * is used. No media file, whole-file cache, URL supplied by a remote caller or upload.
 */
public final class StreamRelay implements AutoCloseable {
    public enum Failure { AUTH, REMOVED, OFFLINE, UNSAFE_RESPONSE }
    public interface Listener { void failed(Failure failure); }
    private final ServerSocket server;
    private final URL upstream;
    private final LanApi.Connections connections;
    private final Listener listener;
    private final String path;
    private final Set<Socket> sockets=Collections.newSetFromMap(new ConcurrentHashMap<>());
    private final Set<HttpURLConnection> requests=Collections.newSetFromMap(new ConcurrentHashMap<>());
    private final ThreadPoolExecutor workers=new ThreadPoolExecutor(3,3,0,TimeUnit.SECONDS,new ArrayBlockingQueue<>(6),r->{Thread t=new Thread(r,"luma-stream");t.setDaemon(true);return t;});
    private volatile boolean closed;

    public StreamRelay(LanApi.Session session,LanApi.Item item,LanApi.Connections connections,Listener listener) throws IOException {
        this.upstream=session.endpoint.media(item.streamPath,item.id,session.token);
        this.connections=connections;this.listener=listener;
        byte[] nonce=new byte[24];new SecureRandom().nextBytes(nonce);StringBuilder secret=new StringBuilder();
        for(byte value:nonce)secret.append(String.format(java.util.Locale.ROOT,"%02x",value&255));path="/media/"+secret;
        server=new ServerSocket(0,6,InetAddress.getByName("127.0.0.1"));
        Thread accept=new Thread(this::accept,"luma-stream-accept");accept.setDaemon(true);accept.start();
    }
    public String playbackUrl(){return "http://127.0.0.1:"+server.getLocalPort()+path;}

    private void accept(){
        while(!closed)try{
            Socket socket=server.accept();socket.setSoTimeout(10000);sockets.add(socket);
            try{workers.execute(()->serve(socket));}catch(RejectedExecutionException e){sockets.remove(socket);socket.close();}
        }catch(IOException error){if(!closed)fail(Failure.OFFLINE);break;}
    }
    private void serve(Socket socket){
        HttpURLConnection remote=null;
        try(Socket client=socket){
            if(!client.getInetAddress().isLoopbackAddress())return;
            BufferedInputStream input=new BufferedInputStream(client.getInputStream());BufferedOutputStream output=new BufferedOutputStream(client.getOutputStream());
            String request=line(input,2048);String[] first=request.split(" ");
            if(first.length!=3||!first[1].equals(path)||!(first[2].equals("HTTP/1.1")||first[2].equals("HTTP/1.0"))){error(output,404);return;}
            boolean head=first[0].equals("HEAD");if(!head&&!first[0].equals("GET")){error(output,405);return;}
            Map<String,String> headers=new HashMap<>();int total=request.length(),count=0;
            while(true){String value=line(input,4096);total+=value.length();if(total>16384||++count>32){error(output,431);return;}if(value.isEmpty())break;
                int colon=value.indexOf(':');if(colon<1){error(output,400);return;}String key=value.substring(0,colon).trim().toLowerCase(java.util.Locale.ROOT);
                if(headers.put(key,value.substring(colon+1).trim())!=null){error(output,400);return;}}
            String range=headers.get("range");if(range!=null&&(range.length()>100||!range.matches("bytes=(?:[0-9]+-[0-9]*|-[0-9]+)"))){error(output,416);return;}
            remote=connections.open(upstream);requests.add(remote);remote.setInstanceFollowRedirects(false);remote.setUseCaches(false);
            remote.setConnectTimeout(5000);remote.setReadTimeout(10000);remote.setRequestMethod(head?"HEAD":"GET");
            remote.setRequestProperty("Connection","close");remote.setRequestProperty("User-Agent","LumaAndroid/0.1");
            if(range!=null)remote.setRequestProperty("Range",range);
            int status=remote.getResponseCode();
            if(status==401){error(output,401);fail(Failure.AUTH);return;}
            if(status==404){error(output,404);fail(Failure.REMOVED);return;}
            if(status>=300&&status<400){error(output,502);fail(Failure.UNSAFE_RESPONSE);return;}
            if(status!=200&&status!=206&&status!=416){error(output,502);fail(Failure.OFFLINE);return;}
            long length=remote.getContentLengthLong();String contentRange=remote.getHeaderField("Content-Range");
            if(length<0||!validRange(status,contentRange,length)){error(output,502);fail(Failure.UNSAFE_RESPONSE);return;}
            String mime=remote.getContentType();if(mime==null||!mime.matches("[A-Za-z0-9!#$&^_.+-]+/[A-Za-z0-9!#$&^_.+-]+(?:;[ A-Za-z0-9=._-]+)?"))mime="application/octet-stream";
            StringBuilder response=new StringBuilder("HTTP/1.1 ").append(status).append(status==206?" Partial Content":status==416?" Range Not Satisfiable":" OK").append("\r\nContent-Length: ").append(length)
                .append("\r\nContent-Type: ").append(mime).append("\r\nConnection: close\r\nCache-Control: no-store\r\nAccept-Ranges: bytes\r\n");
            if(contentRange!=null)response.append("Content-Range: ").append(contentRange).append("\r\n");
            response.append("\r\n");output.write(response.toString().getBytes(StandardCharsets.US_ASCII));output.flush();
            if(head||length==0||status==416)return;
            try(InputStream media=remote.getInputStream()){
                byte[] buffer=new byte[65536];long remaining=length;
                while(!closed&&remaining>0){int read=media.read(buffer,0,(int)Math.min(buffer.length,remaining));if(read<0)throw new IOException("Incomplete LAN stream");output.write(buffer,0,read);output.flush();remaining-=read;}
            }
        }catch(SocketTimeoutException e){if(!closed)fail(Failure.OFFLINE);}
        catch(IOException ignored){/* A VLC seek closes its old range; do not turn that into a false host-offline alert. */}
        finally{if(remote!=null){requests.remove(remote);remote.disconnect();}sockets.remove(socket);}
    }
    private static boolean validRange(int status,String value,long length){
        if(status==200)return value==null;
        if(value==null||value.length()>128)return false;
        try{
            if(status==416)return value.matches("bytes \\*/[0-9]+");
            if(!value.matches("bytes [0-9]+-[0-9]+/[0-9]+"))return false;
            String[] parts=value.substring(6).split("[-/]");long first=Long.parseLong(parts[0]),last=Long.parseLong(parts[1]),total=Long.parseLong(parts[2]);
            return first>=0&&last>=first&&last<total&&last-first+1==length;
        }catch(NumberFormatException e){return false;}
    }
    private static String line(InputStream input,int limit)throws IOException{
        java.io.ByteArrayOutputStream bytes=new java.io.ByteArrayOutputStream();int c;
        while((c=input.read())!=-1){if(c=='\n'){byte[] value=bytes.toByteArray();int n=value.length;if(n>0&&value[n-1]=='\r')n--;return new String(value,0,n,StandardCharsets.US_ASCII);}if(bytes.size()>=limit)throw new IOException("Header limit");bytes.write(c);}
        throw new IOException("Connection closed");
    }
    private static void error(OutputStream output,int code)throws IOException{
        output.write(("HTTP/1.1 "+code+" Request unavailable\r\nContent-Length: 0\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n").getBytes(StandardCharsets.US_ASCII));output.flush();
    }
    private void fail(Failure failure){if(!closed&&listener!=null)listener.failed(failure);}
    @Override public void close(){closed=true;try{server.close();}catch(IOException ignored){}for(Socket s:sockets)try{s.close();}catch(IOException ignored){}for(HttpURLConnection c:requests)c.disconnect();workers.shutdownNow();}
}
