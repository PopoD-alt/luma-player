package dev.sdas.luma.net;

import java.util.LinkedHashMap;

/** Bounded latest-position queue; backward seeks replace older positions too. */
public final class ProgressOutbox {
    public static final long MIN_SEND_INTERVAL_MS=2500;
    public static final int MAX_PENDING_ITEMS=8;
    public static final class Snapshot {
        public final String itemId;public final double positionSeconds,durationSeconds;public final boolean completed;
        public Snapshot(String itemId,double position,double duration,boolean completed){
            if(itemId==null||!itemId.matches("[A-Za-z0-9_-]{1,200}")||!valid(position)||!valid(duration))throw new IllegalArgumentException("Invalid playback progress.");
            this.itemId=itemId;this.durationSeconds=duration;this.positionSeconds=duration>0?Math.min(position,duration):position;this.completed=completed;
        }
        private static boolean valid(double value){return Double.isFinite(value)&&value>=0&&value<=31536000;}
    }
    private final LinkedHashMap<String,Snapshot> pending=new LinkedHashMap<>();
    private long nextAllowed=Long.MIN_VALUE;private boolean sending,closed;
    public synchronized void offer(Snapshot value){
        if(closed)return;
        pending.remove(value.itemId);pending.put(value.itemId,value);
        while(pending.size()>MAX_PENDING_ITEMS)pending.remove(pending.keySet().iterator().next());
    }
    public synchronized Snapshot take(long now){
        if(closed||sending||pending.isEmpty()||now<nextAllowed)return null;
        String id=pending.keySet().iterator().next();Snapshot value=pending.remove(id);sending=true;nextAllowed=now+MIN_SEND_INTERVAL_MS;return value;
    }
    public synchronized void finished(long now,int status){
        sending=false;
        // A server throttle backs off the whole session. Transport failures are
        // not requeued: a future heartbeat offers only fresh playback state.
        if(status==429)nextAllowed=Math.max(nextAllowed,now+60000);
        if(status==401){closed=true;pending.clear();}
    }
    public synchronized long delay(long now){return closed||sending||pending.isEmpty()?-1:now>=nextAllowed?0:nextAllowed-now;}
    public synchronized int size(){return pending.size();}
    public synchronized void close(){closed=true;pending.clear();}
}
