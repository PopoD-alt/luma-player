package dev.sdas.luma;

import java.util.Locale;

/** Container selection for the desktop's streamed media, independent of codec choice. */
public final class PlaybackPolicy {
    private PlaybackPolicy() {}

    public static boolean useAvformat(String mimeType) {
        if (mimeType == null) return false;
        String base = mimeType.split(";", 2)[0].trim().toLowerCase(Locale.ROOT);
        // VLC's MP4 slow-seek/HTTP preload can queue a short single-track movie
        // in one demux call, leaving its reported native time at 250 ms. The
        // retained avformat demuxer advances native time and precise seeks.
        return base.equals("video/mp4") || base.equals("audio/mp4") || base.equals("video/quicktime");
    }
}
