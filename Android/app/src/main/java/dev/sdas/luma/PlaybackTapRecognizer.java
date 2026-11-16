package dev.sdas.luma;

/**
 * Tap recognition independent of Android's event dispatch. The host supplies MotionEvent event
 * times, forwards every movement, and calls cancel for pointer changes or lifecycle transitions.
 * Singles are deferred so the first tap in a double tap never toggles playback controls.
 */
public final class PlaybackTapRecognizer {
    public enum Action { NONE, SINGLE, DOUBLE_LEFT, DOUBLE_RIGHT }
    public static final long NO_DEADLINE = -1;

    private final long doubleTapTimeoutMillis, maxTapDurationMillis;
    private final double touchSlopSquared, doubleTapSlopSquared;
    private boolean down, pending, pairing;
    private long downTime, lastEventTime, firstUpTime;
    private float downX, downY, width, height, firstX, firstY, firstWidth, firstHeight;
    private boolean right, firstRight;

    public PlaybackTapRecognizer(long doubleTapTimeoutMillis, long maxTapDurationMillis,
                                 float touchSlop, float doubleTapSlop) {
        if (doubleTapTimeoutMillis <= 0 || maxTapDurationMillis <= 0
                || !Float.isFinite(touchSlop) || !Float.isFinite(doubleTapSlop)
                || touchSlop < 0 || doubleTapSlop < 0) {
            throw new IllegalArgumentException("Invalid tap configuration");
        }
        this.doubleTapTimeoutMillis = doubleTapTimeoutMillis;
        this.maxTapDurationMillis = maxTapDurationMillis;
        touchSlopSquared = (double) touchSlop * touchSlop;
        doubleTapSlopSquared = (double) doubleTapSlop * doubleTapSlop;
    }

    /** May confirm the preceding single immediately when this tap cannot form a pair. */
    public Action down(long eventTimeMillis, float x, float y, float viewWidth, float viewHeight) {
        if (down || eventTimeMillis < 0 || (pending && eventTimeMillis < firstUpTime)) {
            cancel();
            return Action.NONE;
        }
        Action result = confirmSingle(eventTimeMillis);
        if (!inside(x, y, viewWidth, viewHeight)) {
            cancel();
            return result;
        }
        boolean newRight = x >= viewWidth / 2f;
        pairing = pending && firstRight == newRight
                && firstWidth == viewWidth && firstHeight == viewHeight
                && distanceSquared(firstX, firstY, x, y) <= doubleTapSlopSquared;
        if (pending && !pairing) {
            pending = false;
            result = Action.SINGLE;
        }
        down = true;
        downTime = lastEventTime = eventTimeMillis;
        downX = x;
        downY = y;
        width = viewWidth;
        height = viewHeight;
        right = newRight;
        return result;
    }

    /** Movement beyond touch slop invalidates the tap even if the finger later returns. */
    public void move(long eventTimeMillis, float x, float y) {
        if (down && !validContact(eventTimeMillis, x, y)) cancel();
        else if (down) lastEventTime = eventTimeMillis;
    }

    /** A double tap is emitted only after a valid second UP, never on its DOWN. */
    public Action up(long eventTimeMillis, float x, float y) {
        if (!down) return Action.NONE;
        if (!validContact(eventTimeMillis, x, y)) {
            cancel();
            return Action.NONE;
        }
        down = false;
        if (pairing && pending && (x >= width / 2f) == right) {
            Action result = right ? Action.DOUBLE_RIGHT : Action.DOUBLE_LEFT;
            cancel();
            return result;
        }
        pairing = false;
        pending = true;
        firstUpTime = eventTimeMillis;
        firstX = downX;
        firstY = downY;
        firstWidth = width;
        firstHeight = height;
        firstRight = right;
        return Action.NONE;
    }

    /** Call from the host's delayed single-tap callback, using the same uptime clock as events. */
    public Action confirmSingle(long nowMillis) {
        if (!pending || pairing || nowMillis < firstUpTime
                || nowMillis - firstUpTime < doubleTapTimeoutMillis) return Action.NONE;
        pending = false;
        return Action.SINGLE;
    }

    /** Schedule one callback at this uptime; a potential second tap temporarily defers it. */
    public long nextSingleDeadlineMillis() {
        if (!pending || pairing) return NO_DEADLINE;
        return firstUpTime > Long.MAX_VALUE - doubleTapTimeoutMillis
                ? Long.MAX_VALUE : firstUpTime + doubleTapTimeoutMillis;
    }

    /** Discards both a contact and any unconfirmed single (cancel, multitouch, swipe, new media). */
    public void cancel() {
        down = pending = pairing = false;
    }

    private boolean validContact(long eventTimeMillis, float x, float y) {
        return eventTimeMillis >= lastEventTime && eventTimeMillis >= downTime
                && eventTimeMillis - downTime < maxTapDurationMillis
                && inside(x, y, width, height)
                && distanceSquared(downX, downY, x, y) <= touchSlopSquared;
    }

    private static boolean inside(float x, float y, float width, float height) {
        return Float.isFinite(x) && Float.isFinite(y) && Float.isFinite(width)
                && Float.isFinite(height) && width > 0 && height > 0
                && x >= 0 && y >= 0 && x < width && y < height;
    }

    private static double distanceSquared(float x1, float y1, float x2, float y2) {
        double dx = (double) x2 - x1, dy = (double) y2 - y1;
        return dx * dx + dy * dy;
    }
}
