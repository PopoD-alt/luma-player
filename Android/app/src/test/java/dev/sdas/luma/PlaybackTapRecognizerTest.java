package dev.sdas.luma;

import org.junit.Test;
import static org.junit.Assert.*;
import static dev.sdas.luma.PlaybackTapRecognizer.Action.*;

public class PlaybackTapRecognizerTest {
    private static PlaybackTapRecognizer recognizer() {
        return new PlaybackTapRecognizer(300, 500, 10, 100);
    }

    private static void tap(PlaybackTapRecognizer recognizer, long time, float x, float y) {
        assertEquals(NONE, recognizer.down(time, x, y, 1000, 600));
        assertEquals(NONE, recognizer.up(time + 40, x, y));
    }

    @Test public void singleWaitsForConfirmationAndIsConsumedOnlyOnce() {
        PlaybackTapRecognizer taps = recognizer();
        tap(taps, 1000, 250, 300);
        assertEquals(1340, taps.nextSingleDeadlineMillis());
        assertEquals(NONE, taps.confirmSingle(1339));
        assertEquals(SINGLE, taps.confirmSingle(1340));
        assertEquals(NONE, taps.confirmSingle(2000));
        assertEquals(PlaybackTapRecognizer.NO_DEADLINE, taps.nextSingleDeadlineMillis());
    }

    @Test public void leftAndRightDoubleTapsWaitForSecondUpAndSuppressSingle() {
        for (float x : new float[]{250, 750}) {
            PlaybackTapRecognizer taps = recognizer();
            tap(taps, 1000, x, 300);
            assertEquals(NONE, taps.down(1200, x + 5, 305, 1000, 600));
            assertEquals(PlaybackTapRecognizer.NO_DEADLINE, taps.nextSingleDeadlineMillis());
            assertEquals(NONE, taps.confirmSingle(1340));
            assertEquals(x < 500 ? DOUBLE_LEFT : DOUBLE_RIGHT, taps.up(1380, x + 5, 305));
            assertEquals(NONE, taps.confirmSingle(2000));
        }
    }

    @Test public void fourTapsProduceTwoIndependentDoubleTaps() {
        PlaybackTapRecognizer taps = recognizer();
        for (long start : new long[]{1000, 1400}) {
            tap(taps, start, 750, 300);
            assertEquals(NONE, taps.down(start + 150, 750, 300, 1000, 600));
            assertEquals(DOUBLE_RIGHT, taps.up(start + 190, 750, 300));
        }
        assertEquals(NONE, taps.confirmSingle(2500));
    }

    @Test public void oppositeHalvesAndDistantTapsStaySingles() {
        PlaybackTapRecognizer taps = recognizer();
        tap(taps, 1000, 498, 300);
        assertEquals(SINGLE, taps.down(1200, 502, 300, 1000, 600));
        assertEquals(NONE, taps.up(1240, 502, 300));
        assertEquals(SINGLE, taps.confirmSingle(1540));
        tap(taps, 2000, 100, 300);
        assertEquals(SINGLE, taps.down(2200, 201, 300, 1000, 600));
        assertEquals(NONE, taps.up(2240, 201, 300));
        assertEquals(SINGLE, taps.confirmSingle(2540));
    }

    @Test public void timeoutBoundaryStartsANewSingle() {
        PlaybackTapRecognizer taps = recognizer();
        tap(taps, 1000, 750, 300);
        assertEquals(SINGLE, taps.down(1340, 750, 300, 1000, 600));
        assertEquals(NONE, taps.up(1380, 750, 300));
        assertEquals(SINGLE, taps.confirmSingle(1680));
    }

    @Test public void horizontalVerticalAndReturningSwipesDoNotBecomeTaps() {
        for (float[] point : new float[][]{{780, 300}, {750, 330}, {758, 308}}) {
            PlaybackTapRecognizer taps = recognizer();
            tap(taps, 1000, 750, 300);
            assertEquals(NONE, taps.down(1200, 750, 300, 1000, 600));
            taps.move(1220, point[0], point[1]);
            taps.move(1230, 750, 300);
            assertEquals(NONE, taps.up(1240, 750, 300));
            assertEquals(NONE, taps.confirmSingle(2000));
        }
    }

    @Test public void releaseOutsideSlopWithoutMoveEventDoesNotTap() {
        PlaybackTapRecognizer taps = recognizer();
        tap(taps, 1000, 750, 300);
        taps.down(1200, 750, 300, 1000, 600);
        assertEquals(NONE, taps.up(1240, 770, 300));
        assertEquals(NONE, taps.confirmSingle(2000));
    }

    @Test public void heldContactCannotTriggerASingleOrDouble() {
        PlaybackTapRecognizer taps = recognizer();
        taps.down(1000, 750, 300, 1000, 600);
        assertEquals(NONE, taps.up(1500, 750, 300));
        assertEquals(NONE, taps.confirmSingle(2000));
        tap(taps, 3000, 750, 300);
        taps.down(3200, 750, 300, 1000, 600);
        assertEquals(NONE, taps.confirmSingle(3600));
        assertEquals(NONE, taps.up(3700, 750, 300));
        assertEquals(NONE, taps.confirmSingle(4000));
    }

    @Test public void cancellationAndMultitouchDiscardPendingCallbacks() {
        for (boolean secondContact : new boolean[]{false, true}) {
            PlaybackTapRecognizer taps = recognizer();
            tap(taps, 1000, 750, 300);
            if (secondContact) taps.down(1200, 750, 300, 1000, 600);
            taps.cancel();
            assertEquals(NONE, taps.up(1240, 750, 300));
            assertEquals(NONE, taps.confirmSingle(2000));
            assertEquals(PlaybackTapRecognizer.NO_DEADLINE, taps.nextSingleDeadlineMillis());
        }
    }

    @Test public void invalidOrChangedGeometryCannotFormADoubleTap() {
        PlaybackTapRecognizer taps = recognizer();
        taps.down(1000, Float.NaN, 300, 1000, 600);
        assertEquals(NONE, taps.up(1040, 750, 300));
        assertEquals(NONE, taps.confirmSingle(2000));
        tap(taps, 3000, 750, 300);
        assertEquals(SINGLE, taps.down(3200, 750, 300, 1001, 600));
        assertEquals(NONE, taps.up(3240, 750, 300));
        assertEquals(SINGLE, taps.confirmSingle(3540));
    }

    @Test public void backdatedAndDuplicateEventsCannotManufactureADoubleTap() {
        PlaybackTapRecognizer taps = recognizer();
        tap(taps, 1000, 750, 300);
        assertEquals(NONE, taps.down(1039, 750, 300, 1000, 600));
        assertEquals(NONE, taps.up(1050, 750, 300));
        assertEquals(NONE, taps.confirmSingle(2000));
        taps.down(3000, 750, 300, 1000, 600);
        taps.move(3020, 750, 300);
        assertEquals(NONE, taps.up(3010, 750, 300));
        taps.down(4000, 750, 300, 1000, 600);
        assertEquals(NONE, taps.down(4010, 750, 300, 1000, 600));
        assertEquals(NONE, taps.up(4040, 750, 300));
    }

    @Test public void centerLineBelongsToRightAndTouchSlopBoundaryIsAllowed() {
        PlaybackTapRecognizer taps = recognizer();
        tap(taps, 1000, 500, 300);
        taps.down(1200, 500, 300, 1000, 600);
        taps.move(1220, 506, 308);
        assertEquals(DOUBLE_RIGHT, taps.up(1240, 506, 308));
    }
}
