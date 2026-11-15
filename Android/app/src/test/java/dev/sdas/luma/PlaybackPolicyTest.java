package dev.sdas.luma;

import org.junit.Test;
import static org.junit.Assert.*;

public class PlaybackPolicyTest {
    @Test public void onlyKnownIsoBmffMimeFamiliesUseAvformat() {
        for (String type : new String[]{"video/mp4", "audio/mp4", "video/quicktime", " VIDEO/MP4 ; codecs=avc1"})
            assertTrue(type, PlaybackPolicy.useAvformat(type));
        for (String type : new String[]{"video/x-matroska", "audio/x-matroska", "video/webm", "video/mp2t", "audio/aac", "audio/mpeg", "audio/flac", "audio/ogg", "application/octet-stream", "video/mp4-extra", "video/mp4\n:demux=other", ""})
            assertFalse(type, PlaybackPolicy.useAvformat(type));
        assertFalse(PlaybackPolicy.useAvformat(null));
    }
}
