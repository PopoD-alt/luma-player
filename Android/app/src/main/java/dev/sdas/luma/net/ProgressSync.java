package dev.sdas.luma.net;

import java.io.IOException;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.ScheduledThreadPoolExecutor;
import java.util.concurrent.TimeUnit;

/** One background request at a time; UI events never wait for network I/O. */
public final class ProgressSync implements AutoCloseable {
    public interface Listener { void result(String itemId,boolean accepted,int status); }
    private final LanApi api;private final LanApi.Session session;private final Listener listener;
    private final ProgressOutbox outbox=new ProgressOutbox();
    private final ScheduledThreadPoolExecutor executor=new ScheduledThreadPoolExecutor(1,r->{Thread t=new Thread(r,"luma-progress");t.setDaemon(true);return t;});
    private final Object scheduleLock=new Object();private ScheduledFuture<?> scheduled;private volatile boolean closed;
    public ProgressSync(LanApi api,LanApi.Session session,Listener listener){
        this.api=api;this.session=session;this.listener=listener;executor.setRemoveOnCancelPolicy(true);
    }
    public void offer(String itemId,long positionMillis,long durationMillis,boolean completed){
        if(closed||!session.host.supportsProgress)return;
        if(positionMillis>31536000000L||durationMillis>31536000000L)return;
        outbox.offer(new ProgressOutbox.Snapshot(itemId,Math.max(0,positionMillis)/1000.0,Math.max(0,durationMillis)/1000.0,completed));schedule();
    }
    private static long now(){return TimeUnit.NANOSECONDS.toMillis(System.nanoTime());}
    private void schedule(){
        synchronized(scheduleLock){
            if(closed||scheduled!=null)return;long delay=outbox.delay(now());if(delay<0)return;
            try{scheduled=executor.schedule(this::send,delay,TimeUnit.MILLISECONDS);}catch(java.util.concurrent.RejectedExecutionException ignored){}
        }
    }
    private void send(){
        synchronized(scheduleLock){scheduled=null;if(closed)return;}
        ProgressOutbox.Snapshot value=outbox.take(now());if(value==null){schedule();return;}
        boolean accepted=false;int status=0;
        try{if(!closed){api.reportProgress(session,value.itemId,value.positionSeconds,value.durationSeconds,value.completed);accepted=true;}}
        catch(LanApi.ApiException error){status=error.status;}
        catch(IOException ignored){}
        finally{
            outbox.finished(now(),status);
            try{if(!closed&&listener!=null)listener.result(value.itemId,accepted,status);}finally{schedule();}
        }
    }
    /** Invalidates pending work without joining a blocked HTTP request on UI. */
    @Override public void close(){
        closed=true;outbox.close();synchronized(scheduleLock){if(scheduled!=null)scheduled.cancel(false);scheduled=null;}executor.shutdownNow();
    }
}
