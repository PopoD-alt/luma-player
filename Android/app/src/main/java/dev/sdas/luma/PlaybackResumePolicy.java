package dev.sdas.luma;

import dev.sdas.luma.net.LanApi;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/** Shared progress uses the desktop server's clock; the phone clock never breaks ties. */
public final class PlaybackResumePolicy {
    private PlaybackResumePolicy() {}
    public enum Source { COMPUTER, PHONE, LEGACY }
    public static final class Snapshot {
        public final Source source;
        public final double updatedAtSeconds,positionSeconds,durationSeconds;
        public final boolean completed,hasTimestamp;
        private Snapshot(Source source,double timestamp,double position,double duration,boolean completed) {
            this.source=source;updatedAtSeconds=timestamp;hasTimestamp=validTimestamp(timestamp);
            durationSeconds=validPosition(duration)?duration:0;
            positionSeconds=Math.min(validPosition(position)?position:0,durationSeconds>0?durationSeconds:Double.MAX_VALUE);
            this.completed=completed;
        }
        public long initialPositionMillis(){return completed?0:Math.round(positionSeconds*1000);}
    }
    private static boolean validTimestamp(double value){return Double.isFinite(value)&&value>0&&value<=253402300799.0;}
    private static boolean validPosition(double value){return Double.isFinite(value)&&value>=0&&value<=31536000;}
    public static Snapshot latest(LanApi.Item item) {
        boolean computer=validTimestamp(item.computerUpdatedAtUnixSeconds)&&validPosition(item.computerProgressSeconds);
        boolean phone=validTimestamp(item.mobileUpdatedAtUnixSeconds)&&validPosition(item.mobileProgressSeconds);
        if(phone&&(!computer||item.mobileUpdatedAtUnixSeconds>item.computerUpdatedAtUnixSeconds))
            return new Snapshot(Source.PHONE,item.mobileUpdatedAtUnixSeconds,item.mobileProgressSeconds,item.durationSeconds>0?item.durationSeconds:item.mobileDurationSeconds,item.mobileCompleted);
        if(computer)return new Snapshot(Source.COMPUTER,item.computerUpdatedAtUnixSeconds,item.computerProgressSeconds,item.durationSeconds,item.computerCompleted);
        // Older hosts already publish a restart-safe resumeSeconds value. An
        // untimestamped phone snapshot cannot supersede that desktop bookmark.
        return new Snapshot(Source.LEGACY,0,item.resumeSeconds,item.durationSeconds,item.computerCompleted);
    }
    public static long initialPositionMillis(LanApi.Item item){return latest(item).initialPositionMillis();}
    /** A user's Play may adopt new remote progress; arriving metadata never calls this itself. */
    public static Long pausedComputerPositionMillis(LanApi.Item item,double computerTimestampAtPause,boolean manualSeekPinned) {
        Snapshot value=latest(item);
        if(manualSeekPinned||!value.hasTimestamp||value.source!=Source.COMPUTER
                ||value.updatedAtSeconds<=Math.max(0,computerTimestampAtPause))return null;
        return value.initialPositionMillis();
    }
    /** Stable desktop queue order resolves equal timestamps and legacy hosts. */
    public static LanApi.Item mostRecent(List<LanApi.Item> items) {
        LanApi.Item best=null;double latest=-1;
        for(LanApi.Item item:items){Snapshot value=latest(item);if(best==null||value.updatedAtSeconds>latest){best=item;latest=value.updatedAtSeconds;}}
        return best;
    }
    /** Keep a just-played local item visible until a fresher host snapshot can arbitrate it. */
    public static LanApi.Item homeFeatured(List<LanApi.Item> items,String localId,double observedHostTimestamp,boolean localWasPlayed) {
        LanApi.Item saved=mostRecent(items);
        if(localWasPlayed&&(saved==null||latest(saved).updatedAtSeconds<=Math.max(0,observedHostTimestamp)))
            for(LanApi.Item item:items)if(item.id.equals(localId))return item;
        return saved;
    }
    /** Successors first, followed by the earlier queue. The hero owns focus itself. */
    public static List<LanApi.Item> queueAfter(List<LanApi.Item> items,String focusId) {
        int focus=-1;for(int i=0;i<items.size();i++)if(items.get(i).id.equals(focusId)){focus=i;break;}
        if(focus<0)return Collections.unmodifiableList(new ArrayList<>(items));
        List<LanApi.Item> queue=new ArrayList<>(Math.max(0,items.size()-1));
        for(int i=focus+1;i<items.size();i++)queue.add(items.get(i));
        for(int i=0;i<focus;i++)queue.add(items.get(i));
        return Collections.unmodifiableList(queue);
    }
    public static int successorCount(List<LanApi.Item> items,String focusId) {
        for(int i=0;i<items.size();i++)if(items.get(i).id.equals(focusId))return items.size()-i-1;
        return items.size();
    }
}
