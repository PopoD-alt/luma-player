package dev.sdas.luma;
import org.junit.Test;
import static org.junit.Assert.*;
public class PipPlaybackPolicyTest {
    @Test public void aspectBoundsMatchAndroidLimits(){assertEquals(16.0/9,PipPlaybackPolicy.aspect(1920,1080,1),.0001);assertEquals(2.39,PipPlaybackPolicy.aspect(8000,100,1),.0001);assertEquals(1/2.39,PipPlaybackPolicy.aspect(100,8000,1),.0001);assertEquals(16.0/9,PipPlaybackPolicy.aspect(0,0,Double.NaN),.0001);}
    @Test public void readinessCannotEnablePipForHiddenAudioOrFailedInputs(){assertTrue(PipPlaybackPolicy.eligible(true,true,true,true,true,false));assertFalse(PipPlaybackPolicy.eligible(false,true,true,true,true,false));assertFalse(PipPlaybackPolicy.eligible(true,false,true,true,true,false));assertFalse(PipPlaybackPolicy.eligible(true,true,true,false,true,false));assertFalse(PipPlaybackPolicy.eligible(true,true,true,true,false,false));assertFalse(PipPlaybackPolicy.eligible(true,true,true,true,true,true));}
}
