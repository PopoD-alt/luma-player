package dev.sdas.luma;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.RectF;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.util.LruCache;
import android.view.PixelCopy;
import android.view.SurfaceView;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.Arrays;
import java.util.Comparator;
import java.util.Set;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;

/**
 * Small, private posters copied from this app's already-playing video surface.
 * Never opens a stream, sends a request, or saves a media file. A callback bitmap
 * is shared with the memory cache: callers must not modify or recycle it.
 */
public final class PlayerArtworkStore implements AutoCloseable {
    public interface Callback { void ready(Bitmap bitmap); }
    private static final int WIDTH=320,HEIGHT=180,MAX_FILES=24;
    private static final long MAX_BYTES=4*1024*1024,CAPTURE_INTERVAL_MS=30000;
    private final Handler main=new Handler(Looper.getMainLooper());
    private final File directory;
    private final LruCache<String,Bitmap> memory=new LruCache<>(12);
    private final LruCache<String,Long> lastCapture=new LruCache<>(MAX_FILES);
    private final Set<String> capturing=ConcurrentHashMap.newKeySet();
    private final ThreadPoolExecutor io=new ThreadPoolExecutor(1,1,0,TimeUnit.SECONDS,new ArrayBlockingQueue<>(32),r->{Thread thread=new Thread(r,"luma-artwork");thread.setDaemon(true);return thread;});
    private volatile boolean closed;

    public PlayerArtworkStore(Context context){directory=new File(context.getApplicationContext().getCacheDir(),"player-artwork-v1");execute(this::prune);}

    public void load(String hostId,String itemId,Callback callback){
        String key=key(hostId,itemId);if(key==null||closed){deliver(callback,null);return;}
        Bitmap cached=memory.get(key);if(cached!=null){deliver(callback,cached);return;}
        if(!execute(()->{
            Bitmap bitmap=memory.get(key);File file=file(key);
            try{
                if(bitmap==null&&file.isFile()){
                    BitmapFactory.Options bounds=new BitmapFactory.Options();bounds.inJustDecodeBounds=true;BitmapFactory.decodeFile(file.getPath(),bounds);
                    if(bounds.outWidth==WIDTH&&bounds.outHeight==HEIGHT&&file.length()<=MAX_BYTES){
                        BitmapFactory.Options options=new BitmapFactory.Options();options.inPreferredConfig=Bitmap.Config.RGB_565;
                        bitmap=BitmapFactory.decodeFile(file.getPath(),options);
                    }
                    if(bitmap!=null){memory.put(key,bitmap);file.setLastModified(System.currentTimeMillis());}else file.delete();
                }
            }catch(RuntimeException|OutOfMemoryError ignored){bitmap=null;}
            deliver(callback,bitmap);
        }))deliver(callback,null);
    }

    /** Call on the UI thread, after the native video output has displayed frames. */
    public boolean capture(String hostId,String itemId,SurfaceView surface,Callback callback){
        String key=key(hostId,itemId);Long previous=key==null?null:lastCapture.get(key);
        if(closed||key==null||surface==null||surface.getWidth()<=0||surface.getHeight()<=0||!surface.getHolder().getSurface().isValid()||previous!=null&&SystemClock.uptimeMillis()-previous<CAPTURE_INTERVAL_MS||!capturing.add(key))return false;
        float scale=Math.min(WIDTH/(float)surface.getWidth(),HEIGHT/(float)surface.getHeight());Bitmap copy;
        try{copy=Bitmap.createBitmap(Math.max(1,Math.round(surface.getWidth()*scale)),Math.max(1,Math.round(surface.getHeight()*scale)),Bitmap.Config.ARGB_8888);}catch(RuntimeException|OutOfMemoryError error){capturing.remove(key);return false;}
        try{
            PixelCopy.request(surface,copy,result->{
                if(result!=PixelCopy.SUCCESS||closed){copy.recycle();capturing.remove(key);deliver(callback,null);return;}
                if(!execute(()->{
                    Bitmap poster=null;
                    try{
                        if(!hasVisibleFrame(copy))return;
                        poster=Bitmap.createBitmap(WIDTH,HEIGHT,Bitmap.Config.ARGB_8888);Canvas canvas=new Canvas(poster);canvas.drawColor(Color.BLACK);
                        float fit=Math.max(WIDTH/(float)copy.getWidth(),HEIGHT/(float)copy.getHeight()),w=copy.getWidth()*fit,h=copy.getHeight()*fit;
                        canvas.drawBitmap(copy,null,new RectF((WIDTH-w)/2,(HEIGHT-h)/2,(WIDTH+w)/2,(HEIGHT+h)/2),new Paint(Paint.ANTI_ALIAS_FLAG|Paint.FILTER_BITMAP_FLAG));
                        if(write(key,poster)){memory.put(key,poster);lastCapture.put(key,SystemClock.uptimeMillis());deliver(callback,poster);poster=null;}
                    }catch(RuntimeException|OutOfMemoryError ignored){/* Artwork is optional; playback keeps its native surface. */}
                    finally{copy.recycle();if(poster!=null)poster.recycle();capturing.remove(key);}
                })){copy.recycle();capturing.remove(key);deliver(callback,null);}
            },main);
            return true;
        }catch(IllegalArgumentException error){copy.recycle();capturing.remove(key);return false;}
    }

    private boolean hasVisibleFrame(Bitmap bitmap){
        // Initial surface buffers can be entirely black before the first frame.
        // Leave them uncached so the next periodic attempt can capture the video.
        for(int y=1;y<6;y++)for(int x=1;x<8;x++){int color=bitmap.getPixel(Math.min(bitmap.getWidth()-1,x*bitmap.getWidth()/8),Math.min(bitmap.getHeight()-1,y*bitmap.getHeight()/6));if(Color.red(color)+Color.green(color)+Color.blue(color)>36)return true;}
        return false;
    }
    private boolean write(String key,Bitmap bitmap){
        if(closed||!directory.isDirectory()&&!directory.mkdirs())return false;File destination=file(key),temporary=new File(directory,key+".tmp");
        try{
            try(FileOutputStream output=new FileOutputStream(temporary)){if(!bitmap.compress(Bitmap.CompressFormat.JPEG,82,output))throw new IOException("No poster was encoded");}
            if(!temporary.renameTo(destination))return false;destination.setLastModified(System.currentTimeMillis());prune();return true;
        }catch(IOException|SecurityException ignored){return false;}finally{temporary.delete();}
    }
    private void prune(){
        File[] files=directory.listFiles();if(files==null)return;
        for(File file:files)if(file.getName().endsWith(".tmp"))file.delete();
        files=directory.listFiles(file->file.getName().matches("[a-f0-9]{64}\\.jpg")&&file.isFile());if(files==null)return;
        Arrays.sort(files,Comparator.comparingLong(File::lastModified));long bytes=0;for(File file:files)bytes+=file.length();int count=files.length;
        for(File file:files){if(count<=MAX_FILES&&bytes<=MAX_BYTES)break;long length=file.length();if(file.delete()){bytes-=length;count--;}}
    }
    private File file(String key){return new File(directory,key+".jpg");}
    private String key(String hostId,String itemId){
        if(hostId==null||itemId==null||hostId.isEmpty()||itemId.isEmpty()||hostId.length()>256||itemId.length()>256)return null;
        try{byte[] bytes=MessageDigest.getInstance("SHA-256").digest((hostId+'\0'+itemId).getBytes(StandardCharsets.UTF_8));StringBuilder value=new StringBuilder(64);for(byte b:bytes)value.append(Character.forDigit((b>>4)&15,16)).append(Character.forDigit(b&15,16));return value.toString();}catch(NoSuchAlgorithmException impossible){throw new IllegalStateException(impossible);}
    }
    private boolean execute(Runnable runnable){if(closed)return false;try{io.execute(runnable);return true;}catch(RejectedExecutionException ignored){return false;}}
    private void deliver(Callback callback,Bitmap bitmap){if(callback!=null&&!closed)main.post(()->{if(!closed)callback.ready(bitmap);});}
    @Override public void close(){closed=true;io.shutdownNow();memory.evictAll();lastCapture.evictAll();}
}
