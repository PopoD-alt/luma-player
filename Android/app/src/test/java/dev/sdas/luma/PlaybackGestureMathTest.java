package dev.sdas.luma;

import org.junit.Test;
import static org.junit.Assert.*;

public class PlaybackGestureMathTest {
    @Test public void tapsAndDiagonalSlopDoNotAccidentallySeek(){
        assertEquals(PlaybackGestureMath.Axis.NONE,PlaybackGestureMath.axis(4,5,10));
        assertEquals(PlaybackGestureMath.Axis.NONE,PlaybackGestureMath.axis(13,12,10));
        assertEquals(PlaybackGestureMath.Axis.HORIZONTAL,PlaybackGestureMath.axis(40,12,10));
        assertEquals(PlaybackGestureMath.Axis.VERTICAL,PlaybackGestureMath.axis(8,-40,10));
    }
    @Test public void seekIsBoundedAndDoesNotScaleToAnEntireLongMovie(){
        assertEquals(90000,PlaybackGestureMath.seek(60000,250,1000,7_200_000));
        assertEquals(0,PlaybackGestureMath.seek(1000,-1000,1000,12000));
        assertEquals(12000,PlaybackGestureMath.seek(11000,1000,1000,12000));
        assertEquals(6000,PlaybackGestureMath.seek(3000,250,1000,12000));
    }
    @Test public void invalidGeometryDoesNotCreateAnUnexpectedJump(){
        assertEquals(1000,PlaybackGestureMath.seek(1000,100,0,12000));
        assertEquals(1000,PlaybackGestureMath.seek(1000,Float.NaN,1000,12000));
        assertEquals(30,PlaybackGestureMath.level(30,Float.NaN,1000));
    }
    @Test public void VerticalLevelsMatchSwipeDirectionAndClamp(){
        assertEquals(80,PlaybackGestureMath.level(50,-200,1000));
        assertEquals(20,PlaybackGestureMath.level(50,200,1000));
        assertEquals(100,PlaybackGestureMath.level(90,-1000,1000));
        assertEquals(0,PlaybackGestureMath.level(10,1000,1000));
    }
}
