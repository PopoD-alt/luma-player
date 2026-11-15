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
            app.onActivity(a->a.debugNativeDiagnostics("true".equals(args.getString("nativeDiagnostics"))));
            app.onActivity(a->a.debugConnect(url));await(app,MainActivity::debugPairVisible,15000,"Actual desktop info/pair dialog");
            app.onActivity(a->a.debugPair(code));await(app,a->a.debugPaired()&&a.debugPlaylistSize()>=2,15000,"Actual six-digit pairing and production playlist");
            long expectedResumeMs=desktopResumeMillis(url,code,demo);
            boolean initiallyEmptyFontCache=value(app,MainActivity::debugFontCacheFiles)==0;
            boolean exerciseRecovery="true".equals(args.getString("exerciseVideoRecovery"));if(exerciseRecovery)app.onActivity(MainActivity::debugFailInitialOutputOnce);
            app.onActivity(a->{a.debugPlay(demo);if(!exerciseRecovery){a.debugToggle();assertTrue("Opening pause intent lost",a.debugPauseRequested());assertFalse("Opening pause applied an early resume seek",a.debugResumeApplied());}});
            await(app,a->{if(!a.debugPlaybackPrepared()){assertFalse("Native playback started underneath unfinished font preparation",a.debugNativePlayRequested());assertEquals("Player graph created before preparation completed",0,a.debugVideoGraphSequence());}return a.debugPlaybackPrepared();},120000,"Native playback preparation before any decoder or renderer starts");
            if(!exerciseRecovery){await(app,MainActivity::debugPauseIntentUi,5000,"Opening pause button/status intent");app.onActivity(MainActivity::debugToggle);}
            await(app,a->{Media.Stats s=a.debugStats();return a.debugNativeSurfaceReady()&&s!=null&&s.decodedVideo>0&&s.displayedPictures>0;},25000,"Actual AVC streaming decode/rendered-frame statistics with callback-confirmed valid surface");
            boolean avcSoftwareRetry=value(app,MainActivity::debugSoftwarePlayback);if(exerciseRecovery)assertTrue("Missing hardware output did not recover through one software retry",avcSoftwareRetry);
            if(exerciseRecovery)assertTrue("Software recovery reused the retired player's surface tree",value(app,MainActivity::debugVideoGraphSequence)>=2);
            await(app,a->a.debugSeekable()&&a.debugTime()>200,12000,"Range-seekable HTTP media");
            awaitSettledResume(app,expectedResumeMs,"initial-resume");
            ClockSnapshot beforeRapid=snapshot(app);android.util.Log.i("LumaPlayback","test-rapid-before: "+beforeRapid);
            app.onActivity(a->{a.debugToggle();a.debugToggle();assertFalse("Queued native pause overwrote newer Play intent",a.debugPauseRequested());});
            await(app,a->a.debugPlaying()&&!a.debugPauseRequested()&&a.debugTime()>beforeRapid.time+200&&a.debugStats()!=null&&a.debugStats().displayedPictures>beforeRapid.displayed,6000,"Rapid Pause then Play retains newer intent and advances native clock/display; before="+beforeRapid);
            android.util.Log.i("LumaPlayback","test-rapid-after: "+snapshot(app));
            int surfacePlayer=value(app,MainActivity::debugPlayerIdentity),surfaceView=value(app,MainActivity::debugVideoViewIdentity),beforeSurfaceFrames=value(app,a->a.debugStats().displayedPictures);long surfaceResume=value(app,MainActivity::debugTime);
            app.onActivity(MainActivity::debugRecreateSurface);await(app,a->a.debugSurfaceDestroyedEvents()>0&&!a.debugNativeSurfaceReady(),2000,"Actual SurfaceView destruction callback");
            app.onActivity(a->{a.debugToggle();assertTrue("Pause during surface repair lost its intent",a.debugPauseRequested());});
            await(app,a->a.debugNativeSurfaceReady()&&a.debugPauseIntentUi()&&a.debugPlayerIdentity()==surfacePlayer&&a.debugVideoViewIdentity()==surfaceView,5000,"Surface restoration preserves Pause intent and graph");
            app.onActivity(MainActivity::debugToggle);awaitSettledResume(app,surfaceResume,"surface-recovery");
            await(app,a->a.debugNativeSurfaceReady()&&a.debugPlaying()&&a.debugPlayerIdentity()==surfacePlayer&&a.debugVideoViewIdentity()==surfaceView&&a.debugStats()!=null&&a.debugStats().displayedPictures>beforeSurfaceFrames,7000,"Actual SurfaceView destruction/recreation restores same graph and displayed frames");
            app.onActivity(MainActivity::debugToggle);await(app,MainActivity::debugPausedUi,5000,"Native pause and matching button/status UI");long paused=value(app,MainActivity::debugTime);saveReviewScreenshot("luma-android-review.png");Thread.sleep(350);assertTrue("Paused clock advanced",Math.abs(value(app,MainActivity::debugTime)-paused)<350);
            app.onActivity(a->a.debugSeek(3000));app.onActivity(MainActivity::debugToggle);await(app,a->a.debugPlaying()&&a.debugTime()>=2500,10000,"Native byte-range seek/resume");
            app.onActivity(a->a.debugVolume(37));assertEquals(37,(int)value(app,MainActivity::debugVolume));
            int playerIdentity=value(app,MainActivity::debugPlayerIdentity),viewIdentity=value(app,MainActivity::debugVideoViewIdentity),displayedBefore=value(app,a->a.debugStats().displayedPictures);
            app.onActivity(a->a.debugFull(true));await(app,a->a.debugIsFull()&&!a.debugChromeVisible()&&a.getResources().getConfiguration().orientation==android.content.res.Configuration.ORIENTATION_LANDSCAPE&&a.getWindow().getDecorView().getWidth()>a.getWindow().getDecorView().getHeight()&&(Build.VERSION.SDK_INT<30||a.getWindow().getDecorView().getRootWindowInsets()!=null&&!a.getWindow().getDecorView().getRootWindowInsets().isVisible(android.view.WindowInsets.Type.systemBars()))&&a.debugPlayerIdentity()==playerIdentity&&a.debugVideoViewIdentity()==viewIdentity&&a.debugVideoOutputs()>0&&a.debugStats()!=null&&a.debugStats().displayedPictures>displayedBefore,8000,"Video-only fullscreen retains player/view and advances displayed frames");
            Thread.sleep(1200);dismissSystemFullscreenHint();await(app,MainActivity::debugFullscreenInsetsClear,4000,"Settled fullscreen hides system bars without reserved padding");
            saveReviewScreenshot("luma-android-fullscreen.png");assertTrue("Fullscreen bars/padding returned after capture",value(app,MainActivity::debugFullscreenInsetsClear));
            app.onActivity(MainActivity::debugTapVideo);await(app,a->a.debugChromeVisible()&&a.debugExitFullLabel(),5000,"Tap shows playback controls and explicit fullscreen exit");
            app.onActivity(MainActivity::debugTapVideo);await(app,a->!a.debugChromeVisible(),5000,"Tap hides fullscreen chrome");
            app.onActivity(a->a.debugFull(false));await(app,a->!a.debugIsFull()&&a.debugChromeVisible()&&a.debugPlayerIdentity()==playerIdentity&&a.debugVideoViewIdentity()==viewIdentity,8000,"Fullscreen exit restores controls without replacing video output");
            app.onActivity(MainActivity::debugLeave);app.onActivity(a->a.debugPlay(hevc));
            await(app,a->{Media.Stats s=a.debugStats();return a.debugNativeSurfaceReady()&&a.debugPlayerIdentity()!=playerIdentity&&a.debugVideoViewIdentity()!=viewIdentity&&s!=null&&s.decodedVideo>=5&&s.displayedPictures>0&&s.decodedAudio>0;},30000,"Actual HEVC Main10/AAC5.1 native decode on a fresh player/surface tree");
            Media.Stats stats=value(app,MainActivity::debugStats);
            int stressedTransitions="true".equals(args.getString("stressTransitions"))?stressTransitions(app,demo,hevc):0;
            Bundle result=new Bundle();result.putString("fontCacheInitiallyEmpty",Boolean.toString(initiallyEmptyFontCache));result.putString("fontCacheVerified",Boolean.toString(value(app,MainActivity::debugFontCacheVerified)));result.putLong("preparationMs",value(app,MainActivity::debugPreparationDurationMs));result.putString("softwareRecoveryExercised",Boolean.toString(exerciseRecovery));result.putString("avcUsedSoftwareRetry",Boolean.toString(avcSoftwareRetry));result.putString("lumaEngine",value(app,MainActivity::debugEngineVersion));result.putString("playbackDiagnostics",value(app,MainActivity::debugDiagnostics));result.putInt("hevcDecodedVideo",stats.decodedVideo);result.putInt("hevcDisplayedPictures",stats.displayedPictures);result.putInt("aacDecodedAudio",stats.decodedAudio);result.putString("scope","Production desktop pair/list/byte-range stream; real libVLC decoded/displayed frames. Emulator audio decoding is not physical speaker proof; Wi-Fi discovery still needs target-device verification.");
            InstrumentationRegistry.getInstrumentation().sendStatus(0,result);
            if(stressedTransitions>0){Bundle stress=new Bundle();stress.putInt("stressTransitionsCompleted",stressedTransitions);stress.putString("stressScope","Same paired session, bound Network, Activity and native engine; ten alternating fresh AVC/HEVC graphs, actual decoded/displayed frames and progressing native clock after precise seek; AVC fullscreen transitions; maximum120seconds.");InstrumentationRegistry.getInstrumentation().sendStatus(0,stress);}
            assertFalse("Android persisted a media download",hasMedia(InstrumentationRegistry.getInstrumentation().getTargetContext().getFilesDir()));
            assertFalse("Android cached a saved movie",hasMedia(InstrumentationRegistry.getInstrumentation().getTargetContext().getCacheDir()));
        }
    }
    private static long desktopResumeMillis(String url,String code,String itemId)throws Exception{
        // Read the fixture's actual protocol metadata independently; its Mac and
        // portable hosts intentionally publish different resume positions.
        dev.sdas.luma.net.LanApi api=new dev.sdas.luma.net.LanApi();dev.sdas.luma.net.LanEndpoint endpoint=dev.sdas.luma.net.LanEndpoint.parse(url);
        dev.sdas.luma.net.LanApi.Session session=api.pair(endpoint,api.info(endpoint),code);
        for(dev.sdas.luma.net.LanApi.Item item:api.playlist(session))if(item.id.equals(itemId))return Math.round(item.resumeSeconds*1000);
        throw new AssertionError("Demo metadata absent from the production fixture playlist");
    }
    private static final class ClockSnapshot{
        final long time;final int displayed;final boolean resumed,paused,playing;final String phase;
        ClockSnapshot(MainActivity activity){time=activity.debugTime();Media.Stats stats=activity.debugStats();displayed=stats==null?0:stats.displayedPictures;resumed=activity.debugResumeApplied();paused=activity.debugPauseRequested();playing=activity.debugPlaying();phase=activity.debugDiagnostics();}
        public String toString(){return "timeMs="+time+", resumeApplied="+resumed+", pauseRequested="+paused+", playing="+playing+", displayed="+displayed+"; "+phase;}
    }
    private static ClockSnapshot snapshot(ActivityScenario<MainActivity> app){return value(app,ClockSnapshot::new);}
    private static void awaitSettledResume(ActivityScenario<MainActivity> app,long expected,String phase)throws Exception{awaitSettledResume(app,expected,phase,12000);}
    private static void awaitSettledResume(ActivityScenario<MainActivity> app,long expected,String phase,long timeout)throws Exception{
        long deadline=System.currentTimeMillis()+timeout,anchor=-1,last=-1;int anchorFrames=0,stableSamples=0;ClockSnapshot observed=snapshot(app);
        android.util.Log.i("LumaPlayback","test-"+phase+"-wait: expectedMs="+expected+"; before="+observed);
        while(System.currentTimeMillis()<deadline){observed=snapshot(app);long time=observed.time;
            if(observed.resumed&&observed.playing&&!observed.paused){
                if(anchor<0){
                    // The target window qualifies the initial observation. Once
                    // anchored, real playback is expected to advance beyond it.
                    if(time>=Math.max(0,expected-350)&&time<=expected+1800){anchor=time;last=time;anchorFrames=observed.displayed;stableSamples=0;}
                }else if(time<last||time-last>1000){anchor=-1;last=-1;stableSamples=0;}
                else{stableSamples++;last=time;if(stableSamples>=3&&time-anchor>=300&&observed.displayed>anchorFrames){android.util.Log.i("LumaPlayback","test-"+phase+"-settled: expectedMs="+expected+", anchorMs="+anchor+"; "+observed);return;}}
            }else{anchor=-1;last=-1;stableSamples=0;}
            String error=value(app,MainActivity::debugError);if(error!=null)fail(phase+": "+error);Thread.sleep(100);
        }
        fail(phase+" did not settle before transport checks; expectedMs="+expected+"; after="+observed);
    }
    private static int stressTransitions(ActivityScenario<MainActivity> app,String demo,String hevc)throws Exception{
        long deadline=System.currentTimeMillis()+120000;
        for(int iteration=1;iteration<=10;iteration++){
            boolean videoWithAudio=iteration%2==0;String item=videoWithAudio?hevc:demo;String phase="stress-transition-"+iteration;
            int previousPlayer=value(app,MainActivity::debugPlayerIdentity),previousView=value(app,MainActivity::debugVideoViewIdentity);
            android.util.Log.i("LumaPlayback",phase+"-open: family="+(videoWithAudio?"HEVC_AAC":"AVC")+"; before="+snapshot(app));
            app.onActivity(a->{a.debugLeave();a.debugPlay(item);});
            await(app,a->{Media.Stats s=a.debugStats();return a.debugNativeSurfaceReady()&&a.debugPlaying()&&a.debugResumeApplied()&&a.debugPlayerIdentity()!=previousPlayer&&a.debugVideoViewIdentity()!=previousView&&s!=null&&s.decodedVideo>=5&&s.displayedPictures>0&&(!videoWithAudio||s.decodedAudio>0);},stressRemaining(deadline,15000),phase+" actual fresh graph decoded/displayed frames");
            app.onActivity(a->a.debugSeek(500));
            awaitSettledResume(app,500,phase+"-precise-seek",stressRemaining(deadline,12000));
            if(!videoWithAudio){
                int before=value(app,a->a.debugStats().displayedPictures),identity=value(app,MainActivity::debugPlayerIdentity);
                app.onActivity(a->a.debugFull(true));
                await(app,a->a.debugIsFull()&&!a.debugChromeVisible()&&a.debugNativeSurfaceReady()&&a.debugPlayerIdentity()==identity&&a.getResources().getConfiguration().orientation==android.content.res.Configuration.ORIENTATION_LANDSCAPE&&a.debugStats()!=null&&a.debugStats().displayedPictures>before,stressRemaining(deadline,8000),phase+" fullscreen actual output");
                app.onActivity(a->a.debugFull(false));
                await(app,a->!a.debugIsFull()&&a.debugChromeVisible()&&a.debugPlayerIdentity()==identity,stressRemaining(deadline,8000),phase+" fullscreen exit");
            }
            android.util.Log.i("LumaPlayback",phase+"-complete: "+snapshot(app));
            stressRemaining(deadline,1);
        }
        return 10;
    }
    private static long stressRemaining(long deadline,long maximum){long left=deadline-System.currentTimeMillis();assertTrue("Same-session transition stress exceeded120seconds",left>0);return Math.min(left,maximum);}
    private static void dismissSystemFullscreenHint()throws Exception{
        // A clean Android image may show its own first-use immersive tutorial.
        // Acknowledge only that SystemUI prompt; leave application/OS policy intact.
        android.app.UiAutomation automation=InstrumentationRegistry.getInstrumentation().getUiAutomation();
        android.accessibilityservice.AccessibilityServiceInfo service=automation.getServiceInfo();int original=service.flags;
        service.flags|=android.accessibilityservice.AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS;automation.setServiceInfo(service);
        try{long deadline=System.currentTimeMillis()+1500;while(System.currentTimeMillis()<deadline){
            for(android.view.accessibility.AccessibilityWindowInfo window:automation.getWindows()){
                android.view.accessibility.AccessibilityNodeInfo root=window.getRoot();if(root==null||!android.text.TextUtils.equals("com.android.systemui",root.getPackageName())||root.findAccessibilityNodeInfosByText("Viewing full screen").isEmpty())continue;
                for(android.view.accessibility.AccessibilityNodeInfo node:root.findAccessibilityNodeInfosByText("Got it"))if(android.text.TextUtils.equals("Got it",node.getText())&&node.performAction(android.view.accessibility.AccessibilityNodeInfo.ACTION_CLICK)){Thread.sleep(250);return;}
            }Thread.sleep(100);
        }}finally{service.flags=original;automation.setServiceInfo(service);}
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
        long deadline=System.currentTimeMillis()+timeout;while(System.currentTimeMillis()<deadline){if(value(app,predicate))return;String error=value(app,MainActivity::debugError);if(error!=null)fail(label+": "+error+"; after="+snapshot(app));Thread.sleep(100);}fail("Timed out: "+label+"; after="+snapshot(app));
    }
}
