package dev.sdas.luma;

import android.Manifest;
import android.os.Build;
import android.os.Bundle;
import androidx.test.core.app.ActivityScenario;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.videolan.libvlc.Media;
import java.util.concurrent.atomic.AtomicReference;
import java.util.function.Function;
import static org.junit.Assert.*;

/** End-to-end test against the production desktop sharing server, not a UI mock. */
@RunWith(AndroidJUnit4.class)
public class LanPlaybackTest {
    @Test public void pairBrowseStreamDecodePauseSeekVolumeAndFullscreen()throws Exception{
        Bundle args=InstrumentationRegistry.getArguments();String url=required(args,"serverUrl"),code=required(args,"pairCode"),demo=required(args,"demoId"),hevc=required(args,"hevcId");
        String permission=Build.VERSION.SDK_INT>=37?Manifest.permission.ACCESS_LOCAL_NETWORK:Build.VERSION.SDK_INT==36?Manifest.permission.NEARBY_WIFI_DEVICES:null;
        if(permission!=null)InstrumentationRegistry.getInstrumentation().getUiAutomation().grantRuntimePermission(InstrumentationRegistry.getInstrumentation().getTargetContext().getPackageName(),permission);
        try(ActivityScenario<MainActivity> app=ActivityScenario.launch(MainActivity.class)){
            app.onActivity(a->a.debugConnect(url));await(app,MainActivity::debugPairVisible,15000,"Actual desktop info/pair dialog");
            app.onActivity(a->a.debugPair(code));await(app,a->a.debugPaired()&&a.debugPlaylistSize()>=2,15000,"Actual six-digit pairing and production playlist");
            boolean exerciseRecovery="true".equals(args.getString("exerciseVideoRecovery"));if(exerciseRecovery)app.onActivity(MainActivity::debugFailInitialOutputOnce);
            app.onActivity(a->{a.debugPlay(demo);if(!exerciseRecovery){a.debugToggle();assertTrue("Opening pause intent lost",a.debugPauseRequested());assertFalse("Opening pause applied an early resume seek",a.debugResumeApplied());}});
            if(!exerciseRecovery){await(app,MainActivity::debugPauseIntentUi,5000,"Opening pause button/status intent");app.onActivity(MainActivity::debugToggle);}
            await(app,a->{Media.Stats s=a.debugStats();return s!=null&&s.decodedVideo>0&&s.displayedPictures>0;},25000,"Actual AVC streaming decode/rendered-frame statistics");
            boolean avcSoftwareRetry=value(app,MainActivity::debugSoftwarePlayback);if(exerciseRecovery)assertTrue("Missing hardware output did not recover through one software retry",avcSoftwareRetry);
            await(app,a->a.debugSeekable()&&a.debugTime()>200,12000,"Range-seekable HTTP media");
            app.onActivity(MainActivity::debugToggle);await(app,MainActivity::debugPausedUi,5000,"Native pause and matching button/status UI");long paused=value(app,MainActivity::debugTime);saveReviewScreenshot("luma-android-review.png");Thread.sleep(350);assertTrue("Paused clock advanced",Math.abs(value(app,MainActivity::debugTime)-paused)<350);
            app.onActivity(a->a.debugSeek(3000));app.onActivity(MainActivity::debugToggle);await(app,a->a.debugPlaying()&&a.debugTime()>=2500,10000,"Native byte-range seek/resume");
            app.onActivity(a->a.debugVolume(37));assertEquals(37,(int)value(app,MainActivity::debugVolume));
            int playerIdentity=value(app,MainActivity::debugPlayerIdentity),viewIdentity=value(app,MainActivity::debugVideoViewIdentity),displayedBefore=value(app,a->a.debugStats().displayedPictures);
            app.onActivity(a->a.debugFull(true));await(app,a->a.debugIsFull()&&!a.debugChromeVisible()&&a.getResources().getConfiguration().orientation==android.content.res.Configuration.ORIENTATION_LANDSCAPE&&a.getWindow().getDecorView().getWidth()>a.getWindow().getDecorView().getHeight()&&(Build.VERSION.SDK_INT<30||a.getWindow().getDecorView().getRootWindowInsets()!=null&&!a.getWindow().getDecorView().getRootWindowInsets().isVisible(android.view.WindowInsets.Type.systemBars()))&&a.debugPlayerIdentity()==playerIdentity&&a.debugVideoViewIdentity()==viewIdentity&&a.debugVideoOutputs()>0&&a.debugStats()!=null&&a.debugStats().displayedPictures>displayedBefore,8000,"Video-only fullscreen retains player/view and advances displayed frames");
            saveReviewScreenshot("luma-android-fullscreen.png");
            app.onActivity(MainActivity::debugTapVideo);await(app,a->a.debugChromeVisible()&&a.debugExitFullLabel(),5000,"Tap shows playback controls and explicit fullscreen exit");
            app.onActivity(MainActivity::debugTapVideo);await(app,a->!a.debugChromeVisible(),5000,"Tap hides fullscreen chrome");
            app.onActivity(a->a.debugFull(false));await(app,a->!a.debugIsFull()&&a.debugChromeVisible()&&a.debugPlayerIdentity()==playerIdentity&&a.debugVideoViewIdentity()==viewIdentity,8000,"Fullscreen exit restores controls without replacing video output");
            app.onActivity(MainActivity::debugLeave);app.onActivity(a->a.debugPlay(hevc));
            await(app,a->{Media.Stats s=a.debugStats();return s!=null&&s.decodedVideo>=5&&s.displayedPictures>0&&s.decodedAudio>0;},30000,"Actual HEVC Main10/AAC5.1 native decode and displayed frames");
            Media.Stats stats=value(app,MainActivity::debugStats);
            Bundle result=new Bundle();result.putString("softwareRecoveryExercised",Boolean.toString(exerciseRecovery));result.putString("avcUsedSoftwareRetry",Boolean.toString(avcSoftwareRetry));result.putString("lumaEngine",value(app,MainActivity::debugEngineVersion));result.putString("playbackDiagnostics",value(app,MainActivity::debugDiagnostics));result.putInt("hevcDecodedVideo",stats.decodedVideo);result.putInt("hevcDisplayedPictures",stats.displayedPictures);result.putInt("aacDecodedAudio",stats.decodedAudio);result.putString("scope","Production desktop pair/list/byte-range stream; real libVLC decoded/displayed frames. Emulator audio decoding is not physical speaker proof; Wi-Fi discovery still needs target-device verification.");
            InstrumentationRegistry.getInstrumentation().sendStatus(0,result);
            assertFalse("Android persisted a media download",hasMedia(InstrumentationRegistry.getInstrumentation().getTargetContext().getFilesDir()));
            assertFalse("Android cached a saved movie",hasMedia(InstrumentationRegistry.getInstrumentation().getTargetContext().getCacheDir()));
        }
    }
    private static void saveReviewScreenshot(String name)throws Exception{
        // Text properties become observable before the next rendered frame.
        InstrumentationRegistry.getInstrumentation().waitForIdleSync();Thread.sleep(name.contains("fullscreen")?700:150);
        android.graphics.Bitmap bitmap=InstrumentationRegistry.getInstrumentation().getUiAutomation().takeScreenshot();assertNotNull("Review screenshot unavailable",bitmap);
        java.io.File file=new java.io.File(InstrumentationRegistry.getInstrumentation().getTargetContext().getCacheDir(),name);
        try(java.io.FileOutputStream output=new java.io.FileOutputStream(file)){assertTrue("Review PNG encoding failed",bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG,100,output));}finally{bitmap.recycle();}
    }
    private static boolean hasMedia(java.io.File root){java.io.File[] files=root.listFiles();if(files==null)return false;for(java.io.File file:files)if(file.isDirectory()?hasMedia(file):file.getName().matches("(?i).+\\.(mp4|mkv|m4a|avi|mov|flac|wav)$"))return true;return false;}
    private static String required(Bundle args,String name){String value=args.getString(name);if(value==null||value.isEmpty())throw new IllegalArgumentException("Missing synthetic desktop-test parameter: "+name);return value;}
    private static <T>T value(ActivityScenario<MainActivity> app,Function<MainActivity,T> get){AtomicReference<T> result=new AtomicReference<>();app.onActivity(a->result.set(get.apply(a)));return result.get();}
    private static void await(ActivityScenario<MainActivity> app,Function<MainActivity,Boolean> predicate,long timeout,String label)throws Exception{
        long deadline=System.currentTimeMillis()+timeout;while(System.currentTimeMillis()<deadline){if(value(app,predicate))return;String error=value(app,MainActivity::debugError);if(error!=null)fail(label+": "+error);Thread.sleep(100);}fail("Timed out: "+label+"; "+value(app,MainActivity::debugDiagnostics));
    }
}
