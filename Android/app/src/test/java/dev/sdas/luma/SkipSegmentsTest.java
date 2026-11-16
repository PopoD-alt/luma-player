package dev.sdas.luma;

import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import org.json.JSONArray;
import org.junit.Test;
import static org.junit.Assert.*;

public class SkipSegmentsTest {
    private static SkipSegments.Range range(String kind,double start,double end){return new SkipSegments.Range(kind,start,end,"manual",null);}
    @Test public void namedChaptersPreserveColdOpenAndPostCredits(){
        List<SkipSegments.Range> ranges=SkipSegments.chapters(Arrays.asList(
            new SkipSegments.Chapter("Cold open",0,1),new SkipSegments.Chapter("Opening",1,2),
            new SkipSegments.Chapter("Story",3,5),new SkipSegments.Chapter("Credits",8,2),
            new SkipSegments.Chapter("Post-credits",10,2)),12);
        assertEquals(2,ranges.size());assertEquals(1,ranges.get(0).startSeconds,0);assertEquals(3,ranges.get(0).endSeconds,0);
        assertEquals("credits",ranges.get(1).kind);assertEquals(10,ranges.get(1).endSeconds,0);
        assertNull(SkipSegments.active(ranges,.5));assertNull(SkipSegments.active(ranges,10.5));
    }
    @Test public void genericAndStoryLabelsNeverBecomeSkipRanges(){
        for(String label:new String[]{"Chapter 1","Prologue","Epilogue","Post-credits","Cold open","Opening scene","Next episode","Recap","The End","Opening up to a friend"})assertNull(label,SkipSegments.kind(label));
        assertEquals("intro",SkipSegments.kind("OP 1"));assertEquals("credits",SkipSegments.kind("Ending song"));
        assertTrue(SkipSegments.chapters(Collections.singletonList(new SkipSegments.Chapter("Opening",0,12)),12).isEmpty());
    }
    @Test public void invalidAndOverlappingRangesAreRejected(){
        assertTrue(SkipSegments.validated(Arrays.asList(range("intro",-1,3),range("intro",3,3),range("intro",0,Double.NaN),range("credits",8,13)),12).isEmpty());
        assertTrue(SkipSegments.validated(Arrays.asList(range("intro",1,4),range("credits",3,6)),12).isEmpty());
        assertEquals(2,SkipSegments.validated(Arrays.asList(range("intro",1,3),range("credits",3,5)),12).size());
    }
    @Test public void explicitRangesOverrideNativeRangesConservatively(){
        List<SkipSegments.Range> manual=Collections.singletonList(range("intro",2,4));
        List<SkipSegments.Range> chapters=Arrays.asList(new SkipSegments.Range("intro",1,3,"chapter",null),new SkipSegments.Range("credits",8,10,"chapter",null));
        List<SkipSegments.Range> result=SkipSegments.resolve(manual,chapters,12);
        assertEquals(2,result.size());assertEquals(2,result.get(0).startSeconds,0);assertEquals(10,result.get(1).endSeconds,0);
    }
    @Test public void automaticSkipIsOptInPlayingOnlyAndOncePerPlaythrough(){
        List<SkipSegments.Range> ranges=Collections.singletonList(range("intro",1,3));SkipSegments.Session s=new SkipSegments.Session();
        assertNull(s.automatic(ranges,1.5,false,true));assertNull(s.automatic(ranges,1.5,true,false));
        assertNotNull(s.automatic(ranges,1.5,true,true));assertNull(s.automatic(ranges,1.6,true,true));
        assertNull(s.automatic(ranges,1.5,true,true));assertNotNull("Manual skip remains available after rewind",SkipSegments.active(ranges,1.5));
        s.reset();assertNotNull(s.automatic(ranges,1.5,true,true));
    }
    @Test public void seekingIntoRangeKeepsUserControl(){
        List<SkipSegments.Range> ranges=Collections.singletonList(range("credits",8,10));SkipSegments.Session s=new SkipSegments.Session();
        s.userSeek(8.5,ranges);assertNull(s.automatic(ranges,8.5,true,true));assertNotNull(SkipSegments.active(ranges,8.5));
    }
    @Test public void lateChapterDiscoveryDoesNotOverrideAnExplicitSeek(){
        SkipSegments.Session session=new SkipSegments.Session();session.userSeek(1.5,Collections.emptyList());
        List<SkipSegments.Range> later=Collections.singletonList(range("intro",1,3));
        assertNull(session.automatic(later,1.5,true,true));assertNotNull(SkipSegments.active(later,1.5));
        session.reset();assertNotNull(session.automatic(later,1.5,true,true));
    }
    @Test public void aStalledSeekOutOfTheIntroCannotBeOverriddenByAutoSkip(){
        SkipSegments.Session session=new SkipSegments.Session();session.userSeek(1.5,6,Collections.emptyList());
        List<SkipSegments.Range> later=Collections.singletonList(range("intro",1,3));
        assertNull(session.automatic(later,1.5,true,true));assertNotNull(SkipSegments.active(later,1.5));
    }
    @Test public void rapidSeeksAlsoKeepTheirOriginsWhenMetadataArrivesLater(){
        SkipSegments.Session session=new SkipSegments.Session();session.userSeek(1.5,6,Collections.emptyList());session.userSeek(6,9,Collections.emptyList());
        List<SkipSegments.Range> later=Arrays.asList(range("intro",1,3),range("credits",8,10));
        assertNull(session.automatic(later,1.5,true,true));assertNull(session.automatic(later,9,true,true));
    }
    @Test public void persistedMetadataRoundTripsAndRejectsNumericStrings()throws Exception{
        List<SkipSegments.Range> original=Arrays.asList(range("intro",1,3),range("credits",8,10));
        List<SkipSegments.Range> parsed=SkipSegments.parse(SkipSegments.json(original),12);assertEquals(2,parsed.size());assertEquals(original.get(1).key(),parsed.get(1).key());
        assertTrue(SkipSegments.parse(new JSONArray("[{\"kind\":\"intro\",\"startSeconds\":\"1\",\"endSeconds\":3,\"source\":\"manual\"}]"),12).isEmpty());
        assertTrue(SkipSegments.parse(new JSONArray("[{\"kind\":\"intro\",\"startSeconds\":true,\"endSeconds\":3,\"source\":\"manual\"}]"),12).isEmpty());
    }
}
