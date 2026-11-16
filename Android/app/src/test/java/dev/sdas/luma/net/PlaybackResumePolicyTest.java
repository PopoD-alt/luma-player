package dev.sdas.luma.net;

import dev.sdas.luma.PlaybackResumePolicy;
import org.junit.Test;
import java.util.Arrays;
import java.util.Collections;
import static org.junit.Assert.*;

public class PlaybackResumePolicyTest {
    private LanApi.Item item(String id,double resume,double computer,double computerAt,boolean computerDone,double phone,double phoneAt,boolean phoneDone){
        return new LanApi.Item(id,id,"video/mp4","/v1/media/"+id,100,resume,computer,computerAt,computerDone,phone,100,phoneAt,phoneDone);
    }
    @Test public void newestServerTimestampWins(){
        LanApi.Item phone=item("a",3,20,10,false,42,11,false);assertEquals(42000,PlaybackResumePolicy.initialPositionMillis(phone));assertEquals(PlaybackResumePolicy.Source.PHONE,PlaybackResumePolicy.latest(phone).source);
        LanApi.Item computer=item("b",3,61,12,false,42,11,false);assertEquals(61000,PlaybackResumePolicy.initialPositionMillis(computer));
    }
    @Test public void computerWinsEqualTimestamps(){assertEquals(21000,PlaybackResumePolicy.initialPositionMillis(item("a",1,21,7,false,55,7,false)));}
    @Test public void phoneAtBeginningIsKnownProgress(){assertEquals(0,PlaybackResumePolicy.initialPositionMillis(item("a",30,30,7,false,0,8,false)));assertEquals(PlaybackResumePolicy.Source.PHONE,PlaybackResumePolicy.latest(item("a",30,30,7,false,0,8,false)).source);}
    @Test public void newestCompletedRestartsRatherThanResumingOlderPosition(){assertEquals(0,PlaybackResumePolicy.initialPositionMillis(item("a",30,30,7,false,100,8,true)));assertEquals(0,PlaybackResumePolicy.initialPositionMillis(item("a",30,100,9,true,30,8,false)));}
    @Test public void unknownPhoneNeverWinsEvenWithTimestamp(){assertEquals(30000,PlaybackResumePolicy.initialPositionMillis(item("a",3,30,7,false,Double.NaN,8,false)));}
    @Test public void noTimestampUsesLegacyComputerResume(){LanApi.Item value=item("a",17,90,0,false,71,0,false);assertEquals(17000,PlaybackResumePolicy.initialPositionMillis(value));assertEquals(PlaybackResumePolicy.Source.LEGACY,PlaybackResumePolicy.latest(value).source);assertFalse(PlaybackResumePolicy.latest(value).hasTimestamp);}
    @Test public void timestampedPhoneCanBeatLegacyComputer(){assertEquals(23000,PlaybackResumePolicy.initialPositionMillis(item("a",17,90,0,false,23,6,false)));}
    @Test public void invalidTimestampDoesNotWin(){assertEquals(17000,PlaybackResumePolicy.initialPositionMillis(item("a",17,80,Double.NaN,false,50,Double.POSITIVE_INFINITY,false)));}
    @Test public void initialPositionIsClampedToDuration(){assertEquals(100000,PlaybackResumePolicy.initialPositionMillis(item("a",17,200,9,false,23,6,false)));}
    @Test public void mostRecentIncludesCompletedAndUsesStableQueueTies(){LanApi.Item a=item("a",1,2,4,false,3,5,false),b=item("b",1,100,6,true,3,5,false),c=item("c",1,2,6,false,3,5,false);assertSame(b,PlaybackResumePolicy.mostRecent(Arrays.asList(a,b,c)));assertNull(PlaybackResumePolicy.mostRecent(Collections.emptyList()));}
    @Test public void allLegacyShowsFirstQueueItemWithoutClaimingRecency(){LanApi.Item a=item("a",1,2,0,false,3,0,false),b=item("b",9,9,0,false,3,0,false);assertSame(a,PlaybackResumePolicy.mostRecent(Arrays.asList(a,b)));}
    @Test public void upNextKeepsSuccessorsAndEarlierItemsInDesktopOrder(){LanApi.Item a=item("a",1,2,1,false,3,0,false),b=item("b",1,2,1,false,3,0,false),c=item("c",1,2,1,false,3,0,false),d=item("d",1,2,1,false,3,0,false);assertEquals(Arrays.asList(c,d,a),PlaybackResumePolicy.queueAfter(Arrays.asList(a,b,c,d),"b"));assertEquals(2,PlaybackResumePolicy.successorCount(Arrays.asList(a,b,c,d),"b"));assertEquals(Arrays.asList(a,b,c,d),PlaybackResumePolicy.queueAfter(Arrays.asList(a,b,c,d),"gone"));}
    @Test public void pausedPlayAcceptsOnlyNewerComputerCheckpoint(){
        LanApi.Item value=item("a",2,61,12,false,42,11,false);
        assertEquals(Long.valueOf(61000),PlaybackResumePolicy.pausedComputerPositionMillis(value,10,false));
        assertNull(PlaybackResumePolicy.pausedComputerPositionMillis(value,12,false));
        assertNull(PlaybackResumePolicy.pausedComputerPositionMillis(value,13,false));
    }
    @Test public void manualPausedSeekAndNewerPhoneKeepLocalIntent(){
        assertNull(PlaybackResumePolicy.pausedComputerPositionMillis(item("a",2,61,12,false,42,11,false),10,true));
        assertNull(PlaybackResumePolicy.pausedComputerPositionMillis(item("a",2,61,12,false,42,13,false),10,false));
        assertNull(PlaybackResumePolicy.pausedComputerPositionMillis(item("a",2,61,0,false,42,0,false),0,false));
    }
    @Test public void pausedHandoffHonorsKnownZeroAndCompletedRestart(){
        assertEquals(Long.valueOf(0),PlaybackResumePolicy.pausedComputerPositionMillis(item("a",2,0,12,false,42,11,false),10,false));
        assertEquals(Long.valueOf(0),PlaybackResumePolicy.pausedComputerPositionMillis(item("a",2,100,12,true,42,11,false),10,false));
    }
    @Test public void justPlayedItemStaysFeaturedUntilHostCatchesUp(){
        LanApi.Item older=item("older",2,2,9,false,Double.NaN,0,false),local=item("local",0,0,0,false,Double.NaN,0,false);
        assertEquals("local",PlaybackResumePolicy.homeFeatured(Arrays.asList(older,local),"local",9,true).id);
        assertEquals("older",PlaybackResumePolicy.homeFeatured(Arrays.asList(older,local),"local",9,false).id);
    }
    @Test public void newerHostActivityReplacesPendingLocalCard(){
        LanApi.Item remote=item("remote",2,2,11,false,Double.NaN,0,false),local=item("local",0,0,0,false,Double.NaN,0,false);
        assertEquals("remote",PlaybackResumePolicy.homeFeatured(Arrays.asList(remote,local),"local",9,true).id);
    }
    @Test public void removedLocalItemDoesNotRemainFeatured(){
        LanApi.Item existing=item("existing",2,2,9,false,Double.NaN,0,false);
        assertEquals("existing",PlaybackResumePolicy.homeFeatured(Collections.singletonList(existing),"removed",9,true).id);
    }
}
