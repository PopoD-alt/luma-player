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
            app.onActivity(a->a.debugAutoSkip(false));
            app.onActivity(a->a.debugNativeDiagnostics("true".equals(args.getString("nativeDiagnostics"))));
            app.onActivity(a->a.debugConnect(url));await(app,MainActivity::debugPairVisible,15000,"Actual desktop info/pair dialog");
            app.onActivity(a->a.debugPair(code));await(app,a->a.debugPaired()&&a.debugPlaylistSize()>=2,15000,"Actual six-digit pairing and production playlist");
            DesktopObserver observer=new DesktopObserver(url,code);dev.sdas.luma.net.LanApi.Item initialCheckpoint=observer.item(demo);long expectedLatestResumeMs=PlaybackResumePolicy.initialPositionMillis(initialCheckpoint),expectedResumeMs=Math.round(initialCheckpoint.computerProgressSeconds*1000);
            boolean initiallyEmptyFontCache=value(app,MainActivity::debugFontCacheFiles)==0;
            boolean exerciseRecovery="true".equals(args.getString("exerciseVideoRecovery"));if(exerciseRecovery)app.onActivity(MainActivity::debugFailInitialOutputOnce);
            app.onActivity(a->{a.debugPlay(demo);if(!exerciseRecovery){a.debugToggle();assertTrue("Opening pause intent lost",a.debugPauseRequested());assertFalse("Opening pause applied an early resume seek",a.debugResumeApplied());a.debugResumeComputer();assertFalse("Resume from computer did not keep Play intent during preparation",a.debugPauseRequested());assertFalse("Resume from computer started native playback underneath unfinished preparation",a.debugNativePlayRequested());a.debugToggle();assertTrue("Pause after queued computer resume lost its intent",a.debugPauseRequested());}});
            await(app,a->{if(!a.debugPlaybackPrepared()){assertFalse("Native playback started underneath unfinished font preparation",a.debugNativePlayRequested());assertEquals("Player graph created before preparation completed",0,a.debugVideoGraphSequence());}return a.debugPlaybackPrepared();},120000,"Native playback preparation before any decoder or renderer starts");
            if(!exerciseRecovery){await(app,MainActivity::debugPauseIntentUi,5000,"Opening pause button/status intent");app.onActivity(MainActivity::debugToggle);}
            await(app,a->{Media.Stats s=a.debugStats();return a.debugNativeSurfaceReady()&&s!=null&&s.decodedVideo>0&&s.displayedPictures>0;},25000,"Actual AVC streaming decode/rendered-frame statistics with callback-confirmed valid surface");
            boolean avcSoftwareRetry=value(app,MainActivity::debugSoftwarePlayback);if(exerciseRecovery)assertTrue("Missing hardware output did not recover through one software retry",avcSoftwareRetry);
            if(exerciseRecovery)assertTrue("Software recovery reused the retired player's surface tree",value(app,MainActivity::debugVideoGraphSequence)>=2);
            await(app,a->a.debugSeekable()&&a.debugTime()>200,12000,"Range-seekable HTTP media");
            awaitSettledResume(app,exerciseRecovery?expectedLatestResumeMs:expectedResumeMs,"initial-resume");
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
            assertTrue("Play/pause must be a vector icon with an accessibility action, not visible text",value(app,MainActivity::debugPlayUsesIcon));
            app.onActivity(MainActivity::debugToggle);await(app,MainActivity::debugPausedUi,5000,"Pause for gesture cancellation validation");long beforeCancelledSwipe=value(app,MainActivity::debugTime);
            app.onActivity(a->a.debugGesture(.45f,.5f,.70f,.5f,true));Thread.sleep(200);assertTrue("Cancelled seek gesture changed the native paused position",Math.abs(value(app,MainActivity::debugTime)-beforeCancelledSwipe)<350);
            app.onActivity(a->a.debugGesture(.45f,.5f,.57f,.5f,false));await(app,a->a.debugTime()>beforeCancelledSwipe+800,5000,"Horizontal video swipe commits native forward seek");
            assertTrue("The current desktop fixture does not advertise shared playback progress",value(app,MainActivity::debugProgressSupported));
            app.onActivity(a->a.debugSeek(5500));await(app,a->a.debugTime()>=5300&&a.debugTime()<=5750,5000,"Paused phone progress checkpoint target");long phoneCheckpoint=value(app,MainActivity::debugTime);app.onActivity(MainActivity::debugShareProgressNow);
            dev.sdas.luma.net.LanApi.Item shared=awaitSharedPhoneProgress(app,observer,demo,phoneCheckpoint,expectedResumeMs);
            assertTrue("Desktop playlist does not expose a phone update timestamp",shared.mobileUpdatedAtUnixSeconds>0);assertFalse("A paused middle position was incorrectly marked finished",shared.mobileCompleted);assertTrue("Player labels do not distinguish Computer and This phone",value(app,MainActivity::debugProgressLabels).contains("Computer")&&value(app,MainActivity::debugProgressLabels).contains("This phone"));
            int resumePlayer=value(app,MainActivity::debugPlayerIdentity),resumeView=value(app,MainActivity::debugVideoViewIdentity);app.onActivity(MainActivity::debugResumeComputer);awaitSettledResume(app,expectedResumeMs,"explicit-computer-resume");assertEquals("Resume from computer replaced the native player",resumePlayer,(int)value(app,MainActivity::debugPlayerIdentity));assertEquals("Resume from computer replaced the surface tree",resumeView,(int)value(app,MainActivity::debugVideoViewIdentity));
            app.onActivity(MainActivity::debugToggle);await(app,MainActivity::debugPausedUi,5000,"Pause after explicit computer resume");app.onActivity(a->a.debugSeek(2000));await(app,a->a.debugTime()>=1800&&a.debugTime()<=2300,5000,"Reset representative short fixture position before remaining UI phases");
            app.onActivity(a->a.debugGesture(.82f,.6f,.82f,.4f,false));assertTrue("Right-side vertical gesture did not change app volume",value(app,MainActivity::debugVolume)>37);assertTrue("Volume gesture has no transient vertical HUD",value(app,MainActivity::debugVolumeHud));
            int brightnessBefore=value(app,MainActivity::debugBrightness);app.onActivity(a->a.debugGesture(.18f,.35f,.18f,.65f,false));assertTrue("Left-side gesture did not reduce window brightness",value(app,MainActivity::debugBrightness)<brightnessBefore||brightnessBefore==2);assertTrue("Brightness gesture has no transient HUD",value(app,MainActivity::debugBrightnessHud));assertFalse("Inactive volume HUD remained visible",value(app,MainActivity::debugVolumeHud));
            app.onActivity(a->a.debugGesture(.18f,.65f,.18f,.35f,false));app.onActivity(MainActivity::debugToggle);await(app,MainActivity::debugPlaying,5000,"Resume after actual gesture controls");
            int beforeDrawerPlayer=value(app,MainActivity::debugPlayerIdentity),beforeDrawerView=value(app,MainActivity::debugVideoViewIdentity);long beforeDrawerTime=value(app,MainActivity::debugTime);
            app.onActivity(a->a.debugPlaylist(true));await(app,a->a.debugPlaylistOpen()&&a.debugPlaying()&&a.debugTime()>beforeDrawerTime+200,5000,"In-player left playlist keeps native playback advancing");assertEquals(beforeDrawerPlayer,(int)value(app,MainActivity::debugPlayerIdentity));assertEquals(beforeDrawerView,(int)value(app,MainActivity::debugVideoViewIdentity));saveReviewScreenshot("luma-android-playlist.png");app.onActivity(a->a.debugPlaylist(false));
            int playerIdentity=value(app,MainActivity::debugPlayerIdentity),viewIdentity=value(app,MainActivity::debugVideoViewIdentity),displayedBefore=value(app,a->a.debugStats().displayedPictures);
            app.onActivity(a->a.debugFull(true));await(app,a->a.debugIsFull()&&!a.debugChromeVisible()&&a.getResources().getConfiguration().orientation==android.content.res.Configuration.ORIENTATION_LANDSCAPE&&a.getWindow().getDecorView().getWidth()>a.getWindow().getDecorView().getHeight()&&(Build.VERSION.SDK_INT<30||a.getWindow().getDecorView().getRootWindowInsets()!=null&&!a.getWindow().getDecorView().getRootWindowInsets().isVisible(android.view.WindowInsets.Type.systemBars()))&&a.debugPlayerIdentity()==playerIdentity&&a.debugVideoViewIdentity()==viewIdentity&&a.debugVideoOutputs()>0&&a.debugStats()!=null&&a.debugStats().displayedPictures>displayedBefore,8000,"Video-only fullscreen retains player/view and advances displayed frames");
            Thread.sleep(1200);dismissSystemFullscreenHint();await(app,MainActivity::debugFullscreenInsetsClear,4000,"Settled fullscreen hides system bars without reserved padding");
            saveReviewScreenshot("luma-android-fullscreen.png");assertTrue("Fullscreen bars/padding returned after capture",value(app,MainActivity::debugFullscreenInsetsClear));
            app.onActivity(MainActivity::debugTapVideo);await(app,a->a.debugChromeVisible()&&a.debugExitFullLabel(),5000,"Tap shows playback controls and explicit fullscreen exit");
            app.onActivity(MainActivity::debugToggle);await(app,MainActivity::debugPausedUi,5000,"Icon pause works in modern fullscreen chrome");await(app,MainActivity::debugTransportUnobscured,5000,"Floating transport fits between header and footer without overlap");saveReviewScreenshot("luma-android-modern-controls.png");app.onActivity(MainActivity::debugToggle);await(app,MainActivity::debugPlaying,5000,"Icon play resumes fullscreen playback");
            long fullscreenDrawerTime=value(app,MainActivity::debugTime);app.onActivity(a->a.debugPlaylist(true));await(app,a->a.debugPlaylistOpen()&&a.debugTime()>fullscreenDrawerTime+200&&a.debugPlayerIdentity()==playerIdentity&&a.debugVideoViewIdentity()==viewIdentity,5000,"Landscape playlist keeps the existing native player and video advancing");saveReviewScreenshot("luma-android-playlist.png");app.onActivity(a->a.debugPlaylist(false));
            app.onActivity(MainActivity::debugTapVideo);await(app,a->!a.debugChromeVisible(),5000,"Tap hides fullscreen chrome");
            app.onActivity(a->a.debugFull(false));await(app,a->!a.debugIsFull()&&a.debugChromeVisible()&&a.debugPlayerIdentity()==playerIdentity&&a.debugVideoViewIdentity()==viewIdentity,8000,"Fullscreen exit restores controls without replacing video output");
            long routeReplacements=value(app,MainActivity::debugWifiRouteReplacements);
            // Exercise the real public-info identity gate on the current Wi-Fi
            // without toggling system Wi-Fi or creating an artificial HTTP path.
            app.onActivity(MainActivity::debugInvalidateWifiRoute);
            app.onActivity(MainActivity::debugLeave);app.onActivity(a->a.debugPlay(hevc));
            await(app,a->{Media.Stats s=a.debugStats();return a.debugNativeSurfaceReady()&&a.debugPlayerIdentity()!=playerIdentity&&a.debugVideoViewIdentity()!=viewIdentity&&s!=null&&s.decodedVideo>=5&&s.displayedPictures>0&&s.decodedAudio>0;},30000,"Actual HEVC Main10/AAC5.1 native decode on a fresh player/surface tree");
            Media.Stats stats=value(app,MainActivity::debugStats);
            assertTrue("Invalidated cached Wi-Fi route was used without successful public-info verification",value(app,MainActivity::debugWifiRouteReplacements)>routeReplacements);
            app.onActivity(a->a.debugSeek(400));awaitSettledResume(app,400,"verified-route-HEVC");
            app.onActivity(MainActivity::debugPrevious);await(app,a->demo.equals(a.debugCurrentId())&&a.debugStats()!=null&&a.debugStats().displayedPictures>0,15000,"Previous icon opens the actual preceding playlist item");app.onActivity(MainActivity::debugNext);await(app,a->hevc.equals(a.debugCurrentId())&&a.debugStats()!=null&&a.debugStats().displayedPictures>0,15000,"Next icon opens the actual following playlist item");
            int stressedTransitions="true".equals(args.getString("stressTransitions"))?stressTransitions(app,demo,hevc):0;
            Bundle result=new Bundle();result.putString("fontCacheInitiallyEmpty",Boolean.toString(initiallyEmptyFontCache));result.putString("fontCacheVerified",Boolean.toString(value(app,MainActivity::debugFontCacheVerified)));result.putLong("preparationMs",value(app,MainActivity::debugPreparationDurationMs));result.putString("softwareRecoveryExercised",Boolean.toString(exerciseRecovery));result.putString("avcUsedSoftwareRetry",Boolean.toString(avcSoftwareRetry));result.putString("lumaEngine",value(app,MainActivity::debugEngineVersion));result.putString("playbackDiagnostics",value(app,MainActivity::debugDiagnostics));result.putInt("hevcDecodedVideo",stats.decodedVideo);result.putInt("hevcDisplayedPictures",stats.displayedPictures);result.putInt("aacDecodedAudio",stats.decodedAudio);result.putString("scope","Production desktop pair/list/byte-range stream; real libVLC decoded/displayed frames. Emulator audio decoding is not physical speaker proof; Wi-Fi discovery still needs target-device verification.");
            result.putString("wifiRouteRevalidationExercised","true");result.putLong("verifiedWifiReplacements",value(app,MainActivity::debugWifiRouteReplacements));InstrumentationRegistry.getInstrumentation().sendStatus(0,result);
            if(stressedTransitions>0){Bundle stress=new Bundle();stress.putInt("stressTransitionsCompleted",stressedTransitions);stress.putString("stressScope","Same paired session, bound Network, Activity and native engine; ten alternating fresh AVC/HEVC graphs, actual decoded/displayed frames and progressing native clock after precise seek; AVC fullscreen transitions; maximum120seconds.");InstrumentationRegistry.getInstrumentation().sendStatus(0,stress);}
            assertFalse("Android persisted a media download",hasMedia(InstrumentationRegistry.getInstrumentation().getTargetContext().getFilesDir()));
            assertFalse("Android cached a saved movie",hasMedia(InstrumentationRegistry.getInstrumentation().getTargetContext().getCacheDir()));
        }
    }
    @Test public void autoSkipNamedChaptersAndSavedDesktopTimings()throws Exception{
        Bundle args=InstrumentationRegistry.getArguments();String url=required(args,"serverUrl"),code=required(args,"pairCode"),skip=required(args,"skipId"),demo=required(args,"demoId");
        String permission=Build.VERSION.SDK_INT>=37?Manifest.permission.ACCESS_LOCAL_NETWORK:Build.VERSION.SDK_INT==36?Manifest.permission.NEARBY_WIFI_DEVICES:null;
        if(permission!=null)InstrumentationRegistry.getInstrumentation().getUiAutomation().grantRuntimePermission(InstrumentationRegistry.getInstrumentation().getTargetContext().getPackageName(),permission);
        try(ActivityScenario<MainActivity> app=ActivityScenario.launch(MainActivity.class)){
            try{
                app.onActivity(a->{a.debugAutoSkip(false);a.debugConnect(url);});await(app,MainActivity::debugPairVisible,15000,"Auto-skip production pairing dialog");app.onActivity(a->a.debugPair(code));await(app,a->a.debugPaired()&&a.debugPlaylistSize()>=3,15000,"Auto-skip authored chapter fixture shared");
                app.onActivity(a->a.debugPlay(skip));await(app,MainActivity::debugPlaybackPrepared,120000,"Auto-skip native preparation");
                await(app,a->{Media.Stats st=a.debugStats();return a.debugResumeApplied()&&a.debugChapterSkipRanges()==2&&st!=null&&st.displayedPictures>0&&st.decodedVideo>0;},30000,"Actual native labelled chapters and displayed video");
                int identity=value(app,MainActivity::debugPlayerIdentity),surface=value(app,MainActivity::debugVideoViewIdentity);
                app.onActivity(MainActivity::debugToggle);await(app,MainActivity::debugPausedUi,5000,"Pause before checking opt-in skip");
                app.onActivity(a->a.debugSeek(1500));await(app,a->a.debugTime()>=1300&&a.debugTime()<=1800&&a.debugSkipVisible(),5000,"Opening identified without automatic skip while disabled");assertEquals(0,(int)value(app,MainActivity::debugSkipSeeks));
                app.onActivity(a->a.debugAutoSkip(true));Thread.sleep(600);assertTrue("Enabling auto-skip while paused moved the native clock",value(app,MainActivity::debugTime)<2000);assertEquals(0,(int)value(app,MainActivity::debugSkipSeeks));
                int displayed=value(app,a->a.debugStats().displayedPictures);app.onActivity(MainActivity::debugToggle);
                await(app,a->a.debugSkipSeeks()==1&&a.debugTime()>=3200&&a.debugTime()<5000&&a.debugStats().displayedPictures>displayed,8000,"Real native auto-skip opening 1 to 3 seconds");
                assertEquals(identity,(int)value(app,MainActivity::debugPlayerIdentity));assertEquals(surface,(int)value(app,MainActivity::debugVideoViewIdentity));
                app.onActivity(MainActivity::debugToggle);await(app,MainActivity::debugPausedUi,5000,"Pause before explicit replay of opening");app.onActivity(a->a.debugSeek(1500));await(app,a->a.debugTime()>=1300&&a.debugTime()<1900,5000,"Seek back into opening");app.onActivity(MainActivity::debugToggle);
                await(app,a->a.debugTime()>=1750&&a.debugTime()<2800,5000,"User rewind plays opening without immediately skipping again");assertEquals(1,(int)value(app,MainActivity::debugSkipSeeks));
                app.onActivity(MainActivity::debugToggle);await(app,MainActivity::debugPausedUi,5000,"Pause explicit replay");app.onActivity(MainActivity::debugSkip);await(app,a->a.debugTime()>=2900&&a.debugTime()<3600&&a.debugPausedUi(),5000,"Manual Skip intro preserves paused intent");assertEquals(2,(int)value(app,MainActivity::debugSkipSeeks));
                app.onActivity(a->a.debugSeek(7300));await(app,a->a.debugTime()>=7100&&a.debugTime()<7800,5000,"Approach credits after story");int beforeCreditsFrames=value(app,a->a.debugStats().displayedPictures);app.onActivity(MainActivity::debugToggle);
                await(app,a->a.debugSkipSeeks()==3&&a.debugTime()>=10200&&a.debugTime()<11500&&a.debugStats().displayedPictures>beforeCreditsFrames,6000,"Credits skip ends at post-credits boundary, not EOF");
                assertEquals(identity,(int)value(app,MainActivity::debugPlayerIdentity));assertEquals(surface,(int)value(app,MainActivity::debugVideoViewIdentity));
                app.onActivity(MainActivity::debugToggle);await(app,MainActivity::debugPausedUi,5000,"Pause in preserved post-credits scene");app.onActivity(a->a.debugSeek(8500));await(app,MainActivity::debugSkipVisible,5000,"Manual Skip credits remains available after replay");
                app.onActivity(a->a.debugFull(true));await(app,a->a.debugIsFull()&&a.getResources().getConfiguration().orientation==android.content.res.Configuration.ORIENTATION_LANDSCAPE,5000,"Auto-skip fullscreen review");Thread.sleep(1200);dismissSystemFullscreenHint();app.onActivity(a->{if(!a.debugChromeVisible())a.debugTapVideo();});await(app,MainActivity::debugChromeVisible,5000,"Auto-skip header visible");saveReviewScreenshot("luma-android-auto-skip.png");app.onActivity(a->a.debugFull(false));
                app.onActivity(a->{a.debugAutoSkip(false);a.debugPlay(demo);});await(app,a->{Media.Stats st=a.debugStats();return a.debugResumeApplied()&&st!=null&&st.displayedPictures>0&&a.debugSkipRanges()==1;},25000,"Desktop saved range received on original MP4");
                app.onActivity(MainActivity::debugToggle);await(app,MainActivity::debugPausedUi,5000,"Pause original MP4");app.onActivity(a->a.debugSeek(4100));await(app,a->a.debugActiveSkipStart()==4,5000,"Host saved opening 4 to 5 seconds exposed");app.onActivity(a->a.debugAutoSkip(true));app.onActivity(MainActivity::debugToggle);await(app,a->a.debugSkipSeeks()==1&&a.debugTime()>=4900&&a.debugTime()<6500,6000,"Actual native skip from shared desktop range");
                app.onActivity(MainActivity::debugToggle);await(app,MainActivity::debugPausedUi,5000,"Pause to save phone override");app.onActivity(a->a.debugSaveSkip("intro",6,7));app.onActivity(a->a.debugPlay(demo));await(app,a->{Media.Stats st=a.debugStats();return a.debugResumeApplied()&&st!=null&&st.displayedPictures>0;},25000,"Reload phone-saved range from preferences");app.onActivity(MainActivity::debugToggle);await(app,MainActivity::debugPausedUi,5000,"Pause after saved-range reload");app.onActivity(a->a.debugSeek(6100));await(app,a->a.debugActiveSkipStart()==6,5000,"Persisted phone override takes priority over desktop timing");
                System.out.println("PASS: actual native chapter intro/credits skipping, post-credits preservation, opt-in/paused/user-rewind guards, same player/surface, shared desktop range and saved phone override");
            }finally{app.onActivity(a->{a.debugAutoSkip(false);if(a.debugCurrentId()!=null)a.debugUseDesktopSkipTimings();});}
        }
    }

    @Test public void doubleTapSeeksTenSecondsWithoutStealingOtherGestures()throws Exception{
        Bundle args=InstrumentationRegistry.getArguments();String url=required(args,"serverUrl"),code=required(args,"pairCode"),demo=required(args,"demoId");
        grantLocalNetworkPermission();
        try(ActivityScenario<MainActivity> app=ActivityScenario.launch(MainActivity.class)){
            app.onActivity(a->{a.debugAutoSkip(false);a.debugConnect(url);});await(app,MainActivity::debugPairVisible,15000,"Gesture test pairing dialog");app.onActivity(a->a.debugPair(code));await(app,a->a.debugPaired()&&a.debugPlaylistSize()>=2,15000,"Gesture test production playlist");
            app.onActivity(a->a.debugPlay(demo));await(app,MainActivity::debugPlaybackPrepared,120000,"Gesture native preparation");await(app,a->a.debugSeekable()&&a.debugResumeApplied()&&a.debugPlaying()&&a.debugStats()!=null&&a.debugStats().displayedPictures>0,25000,"Gesture actual streamed native video");
            app.onActivity(MainActivity::debugToggle);await(app,MainActivity::debugPausedUi,5000,"Paused native clock for exact ten-second taps");
            int identity=value(app,MainActivity::debugPlayerIdentity),surface=value(app,MainActivity::debugVideoViewIdentity);
            app.onActivity(a->{a.debugSeek(1000);if(a.debugChromeVisible())a.debugTapVideo();});await(app,a->a.debugTime()>=800&&a.debugTime()<=1250,5000,"One-second tap starting position");
            doubleTap(app,.82f,.5f);await(app,a->a.debugTime()>=10600&&a.debugTime()<=11400&&a.debugPausedUi(),5000,"Actual right-side double tap seeks forward ten seconds while paused");
            Thread.sleep(450);assertFalse("Double tap also fired the delayed single-tap chrome action",value(app,MainActivity::debugChromeVisible));
            doubleTap(app,.18f,.5f);await(app,a->a.debugTime()>=600&&a.debugTime()<=1400&&a.debugPausedUi(),5000,"Actual left-side double tap rewinds ten seconds while paused");
            doubleTap(app,.18f,.5f);await(app,a->a.debugTime()>=0&&a.debugTime()<350&&a.debugPausedUi(),5000,"Left double tap clamps at the beginning");
            assertEquals("Double tap replaced the native player",identity,(int)value(app,MainActivity::debugPlayerIdentity));assertEquals("Double tap replaced the video surface tree",surface,(int)value(app,MainActivity::debugVideoViewIdentity));
            app.onActivity(a->a.debugSeek(5000));await(app,a->a.debugTime()>=4700&&a.debugTime()<=5250,5000,"Middle position for gesture rejection");long middle=value(app,MainActivity::debugTime);
            singleTap(app,.75f,.5f);Thread.sleep(100);assertFalse("Single tap fired before the double-tap decision window closed",value(app,MainActivity::debugChromeVisible));await(app,MainActivity::debugChromeVisible,1500,"A confirmed single tap still reveals playback controls");
            app.onActivity(MainActivity::debugTapVideo);assertFalse(value(app,MainActivity::debugChromeVisible));
            singleTap(app,.18f,.5f);Thread.sleep(70);singleTap(app,.82f,.5f);Thread.sleep(450);assertTrue("Taps on opposite halves were incorrectly combined into a seek",Math.abs(value(app,MainActivity::debugTime)-middle)<350);
            app.onActivity(a->{if(a.debugChromeVisible())a.debugTapVideo();});long held=android.os.SystemClock.uptimeMillis();touch(app,android.view.MotionEvent.ACTION_DOWN,.82f,.5f,held,held);Thread.sleep(650);touch(app,android.view.MotionEvent.ACTION_UP,.82f,.5f,held,android.os.SystemClock.uptimeMillis());Thread.sleep(400);assertTrue("A hold was incorrectly recognized as a seek",Math.abs(value(app,MainActivity::debugTime)-middle)<350);assertFalse("A hold fired a single-tap chrome action",value(app,MainActivity::debugChromeVisible));
            long cancelled=android.os.SystemClock.uptimeMillis();touch(app,android.view.MotionEvent.ACTION_DOWN,.82f,.5f,cancelled,cancelled);Thread.sleep(35);touch(app,android.view.MotionEvent.ACTION_CANCEL,.82f,.5f,cancelled,android.os.SystemClock.uptimeMillis());singleTap(app,.82f,.5f);Thread.sleep(450);assertTrue("Cancelled touch participated in a double tap",Math.abs(value(app,MainActivity::debugTime)-middle)<350);
            app.onActivity(a->a.debugGesture(.45f,.5f,.70f,.5f,true));Thread.sleep(350);singleTap(app,.70f,.5f);Thread.sleep(450);assertTrue("Cancelled horizontal swipe participated in a double tap or committed a seek",Math.abs(value(app,MainActivity::debugTime)-middle)<350);
            assertTrue("Tap/hold/cancel gestures unexpectedly resumed a paused video",value(app,MainActivity::debugPausedUi));assertEquals(identity,(int)value(app,MainActivity::debugPlayerIdentity));assertEquals(surface,(int)value(app,MainActivity::debugVideoViewIdentity));
            System.out.println("PASS: real MotionEvent left/right ten-second seeks, beginning clamp, delayed single tap, opposite-half/hold/cancel/swipe guards and unchanged native player/surface");
        }
    }
    @Test public void pictureInPictureKeepsStreamAndPreservesPauseAndFullscreen()throws Exception{
        Bundle args=InstrumentationRegistry.getArguments();String url=required(args,"serverUrl"),code=required(args,"pairCode"),demo=required(args,"demoId");grantLocalNetworkPermission();
        assertTrue("The owned Android emulator must support real picture in picture",InstrumentationRegistry.getInstrumentation().getTargetContext().getPackageManager().hasSystemFeature(android.content.pm.PackageManager.FEATURE_PICTURE_IN_PICTURE));
        try(ActivityScenario<MainActivity> app=ActivityScenario.launch(MainActivity.class)){
            app.onActivity(a->{a.debugAutoSkip(false);a.debugAutoPip(true);a.debugConnect(url);});await(app,MainActivity::debugPairVisible,15000,"PiP production pairing dialog");app.onActivity(a->a.debugPair(code));await(app,a->a.debugPaired()&&a.debugPlaylistSize()>=2,15000,"PiP production playlist");
            app.onActivity(a->a.debugPlay(demo));await(app,MainActivity::debugPlaybackPrepared,120000,"PiP native preparation");await(app,a->a.debugSeekable()&&a.debugResumeApplied()&&a.debugPlaying()&&a.debugStats()!=null&&a.debugStats().displayedPictures>0,25000,"PiP real native streaming video");
            app.onActivity(a->a.debugFull(true));await(app,a->a.debugIsFull()&&a.getResources().getConfiguration().orientation==android.content.res.Configuration.ORIENTATION_LANDSCAPE,8000,"PiP initial landscape fullscreen");Thread.sleep(1200);dismissSystemFullscreenHint();app.onActivity(a->a.debugSeek(500));awaitSettledResume(app,500,"before-PiP");
            int identity=value(app,MainActivity::debugPlayerIdentity),surface=value(app,MainActivity::debugVideoViewIdentity),relay=value(app,MainActivity::debugRelayIdentity);ClockSnapshot before=snapshot(app);
            pressHome();await(app,a->a.debugInPip()&&!a.debugForeground()&&a.debugPlaying()&&!a.debugChromeVisible()&&a.debugPlayerIdentity()==identity&&a.debugVideoViewIdentity()==surface&&a.debugRelayIdentity()==relay&&a.debugRelayActive()&&a.debugForwardedBytes()>0,8000,"Actual Home enters OS picture in picture with paused Activity and unchanged live native graph/HTTP relay");
            awaitStablePipBounds(app);await(app,a->a.debugTime()>before.time+300&&a.debugStats()!=null&&a.debugStats().displayedPictures>before.displayed,5000,"Native clock and displayed frames advance while Activity is paused in PiP");saveReviewScreenshot("luma-android-pip.png");
            clickPipSystemAction(app,"Pause");await(app,a->a.debugInPip()&&a.debugPauseRequested()&&a.debugPausedUi(),5000,"Actual SystemUI PiP Pause action pauses native playback");long paused=value(app,MainActivity::debugTime);Thread.sleep(450);assertTrue("Paused PiP native clock advanced",Math.abs(value(app,MainActivity::debugTime)-paused)<350);
            returnToPlayer();await(app,a->!a.debugInPip()&&a.debugForeground()&&a.debugIsFull()&&a.getResources().getConfiguration().orientation==android.content.res.Configuration.ORIENTATION_LANDSCAPE&&a.debugPausedUi(),8000,"Return from PiP restores fullscreen and preserves explicit Pause");assertEquals(identity,(int)value(app,MainActivity::debugPlayerIdentity));assertEquals(surface,(int)value(app,MainActivity::debugVideoViewIdentity));assertEquals(relay,(int)value(app,MainActivity::debugRelayIdentity));
            app.onActivity(a->{a.debugSeek(500);a.debugToggle();});awaitSettledResume(app,500,"before-PiP-close");pressHome();await(app,a->a.debugInPip()&&a.debugPlaying()&&!a.debugForeground(),8000,"Second real Home PiP transition");MainActivity closing=value(app,a->a);
            clickPipSystemAction(app,"Close","Close window","Dismiss","Dismiss window","Close picture-in-picture","Close picture in picture");awaitObserved(closing,a->!a.debugRelayActive()&&a.debugPlayerIdentity()==0&&a.debugRetiringGraphs()==0,12000,"Closing actual PiP retires the native graph and closes its HTTP stream");
            System.out.println("PASS: actual Home/OS PiP, paused-Activity native clock/display frames, unchanged player/surface/HTTP relay, real PiP Pause, fullscreen restoration and actual PiP Close stream retirement");
        }
        // A denied/unsupported PiP path must retain the existing background
        // lifecycle: release the relay/decoder rather than stream invisibly.
        try(ActivityScenario<MainActivity> app=ActivityScenario.launch(MainActivity.class)){
            app.onActivity(a->{a.debugAutoSkip(false);a.debugAutoPip(false);a.debugConnect(url);});await(app,MainActivity::debugPairVisible,15000,"Ordinary-Home pairing dialog");app.onActivity(a->a.debugPair(code));await(app,a->a.debugPaired()&&a.debugPlaylistSize()>=2,15000,"Ordinary-Home playlist");app.onActivity(a->a.debugPlay(demo));await(app,a->a.debugResumeApplied()&&a.debugPlaying()&&a.debugStats()!=null&&a.debugStats().displayedPictures>0,25000,"Ordinary-Home native playback");MainActivity background=value(app,a->a);
            pressHome();awaitObserved(background,a->!a.debugForeground()&&!a.debugInPip()&&a.debugPlayerIdentity()==0&&!a.debugRelayActive()&&a.debugRetiringGraphs()==0,12000,"Home with PiP disabled releases decoder and HTTP relay");returnToPlayer();await(app,MainActivity::debugForeground,8000,"Ordinary-Home return");app.onActivity(a->a.debugAutoPip(true));
        }
    }
    @Test public void dashboardUsesNewestSharedCheckpointAndCachesWithoutAutoplay()throws Exception{
        Bundle args=InstrumentationRegistry.getArguments();String url=required(args,"serverUrl"),code=required(args,"pairCode"),demo=required(args,"demoId"),hevc=required(args,"hevcId");grantLocalNetworkPermission();String cachedTitle;
        DesktopObserver observer=new DesktopObserver(url,code);
        try(ActivityScenario<MainActivity> app=ActivityScenario.launch(MainActivity.class)){
            assertTrue("Connection form leaked onto the initial home dashboard",value(app,MainActivity::debugConnectionsControlsHidden));assertFalse(value(app,MainActivity::debugConnectionsOpen));assertEquals("Opening the dashboard started a native player",0,(int)value(app,MainActivity::debugPlayerIdentity));
            app.onActivity(MainActivity::debugOpenConnections);assertTrue("Hamburger did not open the connections drawer",value(app,MainActivity::debugConnectionsOpen));app.onActivity(a->{a.debugAutoSkip(false);a.debugConnect(url);});await(app,MainActivity::debugPairVisible,15000,"Dashboard pairing dialog");app.onActivity(a->a.debugPair(code));await(app,a->a.debugPaired()&&a.debugPlaylistSize()>=3&&a.debugConnectionsControlsHidden(),15000,"Successful pairing returns to dashboard with connection form hidden");
            java.util.List<dev.sdas.luma.net.LanApi.Item> initial=observer.api.playlist(observer.session);String newest=mostRecentProtocolItem(initial);await(app,a->newest.equals(a.debugHomeFeaturedId()),10000,"Initial hero is the newest server checkpoint");assertNull("Pairing unexpectedly selected a video for playback",value(app,MainActivity::debugCurrentId));assertEquals(0,(int)value(app,MainActivity::debugPlayerIdentity));
            dev.sdas.luma.net.LanApi.Item originalDemo=observer.item(demo);dev.sdas.luma.net.LanApi.ProgressReceipt checkpoint=observer.api.reportProgress(observer.session,demo,2.3,12,false);assertEquals(2.3,checkpoint.positionSeconds,.01);assertTrue("Server did not timestamp the independent phone checkpoint",checkpoint.updatedAtUnixSeconds>originalDemo.computerUpdatedAtUnixSeconds);
            app.onActivity(MainActivity::debugRefresh);await(app,a->demo.equals(a.debugHomeFeaturedId())&&a.debugHomeDetails().contains("phone")&&a.debugHomeDetails().contains("0:02"),10000,"Home refresh displays independent phone checkpoint");assertNull("Remote progress refresh silently selected a playback item",value(app,MainActivity::debugCurrentId));assertEquals("Remote progress refresh silently created a native decoder",0,(int)value(app,MainActivity::debugPlayerIdentity));assertFalse("Remote progress refresh requested native playback",value(app,MainActivity::debugNativePlayRequested));
            app.onActivity(MainActivity::debugHomeContinue);await(app,MainActivity::debugPlaybackPrepared,120000,"Continue native preparation");awaitSettledResume(app,2300,"dashboard-newest-phone-checkpoint");assertEquals(demo,value(app,MainActivity::debugCurrentId));assertTrue("Continue did not render actual streamed video",value(app,a->a.debugStats()!=null&&a.debugStats().displayedPictures>0));
            app.onActivity(MainActivity::debugToggle);await(app,MainActivity::debugPausedUi,5000,"Pause before leaving the dashboard handoff video");long finalDemo=value(app,MainActivity::debugTime);app.onActivity(MainActivity::debugShareProgressNow);awaitExactSharedPhoneProgress(app,observer,demo,finalDemo,Math.round(originalDemo.computerProgressSeconds*1000),checkpoint.updatedAtUnixSeconds);app.onActivity(MainActivity::debugLeave);await(app,a->a.debugCurrentId()==null&&a.debugPlayerIdentity()==0,5000,"Back to dashboard without an active decoder");
            // The old 650ms position tolerance could accept the independent
            // 2.3s checkpoint while a newer paused point was still queued. Let
            // the real coalescing interval finish before creating a newer item.
            Thread.sleep(dev.sdas.luma.net.ProgressOutbox.MIN_SEND_INTERVAL_MS+300);dev.sdas.luma.net.LanApi.Item settledDemo=awaitExactSharedPhoneProgress(app,observer,demo,finalDemo,Math.round(originalDemo.computerProgressSeconds*1000),checkpoint.updatedAtUnixSeconds);await(app,a->demo.equals(a.debugHomeFeaturedId())&&a.debugCurrentId()==null&&a.debugPlayerIdentity()==0,5000,"Played Demo hero is visible for dashboard review");saveReviewScreenshot("luma-android-home.png");
            dev.sdas.luma.net.LanApi.ProgressReceipt latest=observer.api.reportProgress(observer.session,hevc,.5,2,false);assertTrue("New independent checkpoint did not supersede the drained paused Demo report",latest.updatedAtUnixSeconds>settledDemo.mobileUpdatedAtUnixSeconds);java.util.List<dev.sdas.luma.net.LanApi.Item> refreshed=observer.api.playlist(observer.session);cachedTitle=observer.item(hevc).title;String expectedQueue=queueIdsAfter(refreshed,hevc);
            app.onActivity(MainActivity::debugRefresh);await(app,a->hevc.equals(a.debugHomeFeaturedId())&&expectedQueue.equals(a.debugHomeQueueIds())&&a.debugHomeDetails().contains("phone"),10000,"Newest HEVC checkpoint becomes hero and Up next keeps desktop successor/wrap order");assertNull(value(app,MainActivity::debugCurrentId));assertEquals("Newest home item autoplayed",0,(int)value(app,MainActivity::debugPlayerIdentity));assertTrue(value(app,MainActivity::debugConnectionsControlsHidden));
        }
        // A fresh Activity reads only cached display metadata; it must pair
        // again before playing. The instrumentation process itself stays alive.
        try(ActivityScenario<MainActivity> app=ActivityScenario.launch(MainActivity.class)){
            assertEquals("Fresh launch lost the last shared title",cachedTitle,value(app,MainActivity::debugHomeTitle));assertTrue("Fresh launch does not identify cached progress",value(app,MainActivity::debugHomeDetails).contains("Cached"));assertFalse("Cached display metadata restored a pairing token",value(app,MainActivity::debugPaired));assertNull(value(app,MainActivity::debugCurrentId));assertEquals("Cached hero autoplayed on launch",0,(int)value(app,MainActivity::debugPlayerIdentity));assertTrue(value(app,MainActivity::debugConnectionsControlsHidden));assertFalse(value(app,MainActivity::debugConnectionsOpen));app.onActivity(MainActivity::debugHomeContinue);assertTrue("Cached Continue did not require opening connections",value(app,MainActivity::debugConnectionsOpen));assertEquals(0,(int)value(app,MainActivity::debugPlayerIdentity));
        }
        System.out.println("PASS: hamburger connections, newest real server checkpoint/ordered Up next, explicit Continue resumes native2.3seconds with frames, and cached fresh-Activity home without pairing or autoplay");
    }
    private static String mostRecentProtocolItem(java.util.List<dev.sdas.luma.net.LanApi.Item> items){String result="";double latest=-1;for(dev.sdas.luma.net.LanApi.Item item:items){double updated=Math.max(item.computerUpdatedAtUnixSeconds,item.mobileUpdatedAtUnixSeconds);if(updated>latest){latest=updated;result=item.id;}}return result;}
    private static String queueIdsAfter(java.util.List<dev.sdas.luma.net.LanApi.Item> items,String featured){int focus=-1;for(int i=0;i<items.size();i++)if(featured.equals(items.get(i).id))focus=i;assertTrue("Featured fixture absent from server queue",focus>=0);StringBuilder expected=new StringBuilder();for(int offset=1;offset<items.size();offset++){if(expected.length()>0)expected.append(',');expected.append(items.get((focus+offset)%items.size()).id);}return expected.toString();}
    private static void grantLocalNetworkPermission(){String permission=Build.VERSION.SDK_INT>=37?Manifest.permission.ACCESS_LOCAL_NETWORK:Build.VERSION.SDK_INT==36?Manifest.permission.NEARBY_WIFI_DEVICES:null;if(permission!=null)InstrumentationRegistry.getInstrumentation().getUiAutomation().grantRuntimePermission(InstrumentationRegistry.getInstrumentation().getTargetContext().getPackageName(),permission);}
    private static void touch(ActivityScenario<MainActivity> app,int action,float x,float y,long down,long event){app.onActivity(a->a.debugVideoTouch(action,x,y,down,event));}
    private static void singleTap(ActivityScenario<MainActivity> app,float x,float y)throws Exception{long down=android.os.SystemClock.uptimeMillis();touch(app,android.view.MotionEvent.ACTION_DOWN,x,y,down,down);Thread.sleep(35);touch(app,android.view.MotionEvent.ACTION_UP,x,y,down,android.os.SystemClock.uptimeMillis());}
    private static void doubleTap(ActivityScenario<MainActivity> app,float x,float y)throws Exception{singleTap(app,x,y);Thread.sleep(70);singleTap(app,x,y);}
    private static void pressHome()throws Exception{try(android.os.ParcelFileDescriptor.AutoCloseInputStream input=new android.os.ParcelFileDescriptor.AutoCloseInputStream(InstrumentationRegistry.getInstrumentation().getUiAutomation().executeShellCommand("input keyevent KEYCODE_HOME"))){byte[] bytes=new byte[512];while(input.read(bytes)>=0){}}}
    private static void returnToPlayer(){android.content.Context context=InstrumentationRegistry.getInstrumentation().getTargetContext();android.content.Intent intent=context.getPackageManager().getLaunchIntentForPackage(context.getPackageName());assertNotNull("Installed player launch intent unavailable",intent);intent.addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK|android.content.Intent.FLAG_ACTIVITY_REORDER_TO_FRONT|android.content.Intent.FLAG_ACTIVITY_SINGLE_TOP);context.startActivity(intent);}
    private static <T>T observed(MainActivity activity,Function<MainActivity,T> get){AtomicReference<T> result=new AtomicReference<>();InstrumentationRegistry.getInstrumentation().runOnMainSync(()->result.set(get.apply(activity)));return result.get();}
    private static void awaitObserved(MainActivity activity,Function<MainActivity,Boolean> predicate,long timeout,String label)throws Exception{long deadline=System.currentTimeMillis()+timeout;while(System.currentTimeMillis()<deadline){if(observed(activity,predicate))return;Thread.sleep(100);}fail("Timed out: "+label+"; after="+observed(activity,MainActivity::debugDiagnostics));}
    private static android.graphics.Rect awaitStablePipBounds(ActivityScenario<MainActivity> app)throws Exception{
        android.graphics.Rect previous=null;long began=android.os.SystemClock.uptimeMillis(),stableSince=began;android.graphics.Point screen=new android.graphics.Point();
        while(android.os.SystemClock.uptimeMillis()-began<7000){
            android.view.WindowManager windows=(android.view.WindowManager)InstrumentationRegistry.getInstrumentation().getContext().getSystemService(android.content.Context.WINDOW_SERVICE);windows.getDefaultDisplay().getRealSize(screen);
            android.graphics.Rect bounds=value(app,a->{android.view.View decor=a.getWindow().getDecorView();int[] location=new int[2];decor.getLocationOnScreen(location);return new android.graphics.Rect(location[0],location[1],location[0]+decor.getWidth(),location[1]+decor.getHeight());});long now=android.os.SystemClock.uptimeMillis();
            boolean visible=value(app,MainActivity::debugInPip)&&bounds.width()>48&&bounds.height()>48&&bounds.left>=0&&bounds.top>=0&&bounds.right<=screen.x&&bounds.bottom<=screen.y&&(long)bounds.width()*bounds.height()<(long)screen.x*screen.y/2;
            if(!visible||!bounds.equals(previous)){previous=bounds;stableSince=now;}else if(now-stableSince>=600){Thread.sleep(700);android.util.Log.i("LumaPlayback","test-PiP-settled-bounds: "+bounds.toShortString()+"; screen="+screen.x+"x"+screen.y);return bounds;}Thread.sleep(100);
        }
        fail("OS PiP window did not settle into visible display bounds: window="+previous+"; screen="+screen.x+"x"+screen.y);return null;
    }
    private static void clickPipSystemAction(ActivityScenario<MainActivity> app,String... labels)throws Exception{
        android.app.UiAutomation automation=InstrumentationRegistry.getInstrumentation().getUiAutomation();android.accessibilityservice.AccessibilityServiceInfo service=automation.getServiceInfo();int flags=service.flags;service.flags|=android.accessibilityservice.AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS;automation.setServiceInfo(service);
        try{
            android.graphics.Rect bounds=awaitStablePipBounds(app);long deadline=android.os.SystemClock.uptimeMillis()+4000,lastTap=0;
            while(android.os.SystemClock.uptimeMillis()<deadline){
                for(android.view.accessibility.AccessibilityWindowInfo window:automation.getWindows()){android.view.accessibility.AccessibilityNodeInfo root=window.getRoot();if(isSystemUi(root)&&clickLabel(root,labels))return;}
                long now=android.os.SystemClock.uptimeMillis();if(lastTap==0||now-lastTap>=1400){
                    // Search existing controls before tapping: another menu tap
                    // would hide an already visible PiP menu. Wait for the real
                    // rotated window geometry rather than its entry callback.
                    android.view.MotionEvent event=android.view.MotionEvent.obtain(now,now,android.view.MotionEvent.ACTION_DOWN,bounds.exactCenterX(),bounds.exactCenterY(),0);event.setSource(android.view.InputDevice.SOURCE_TOUCHSCREEN);assertTrue("PiP menu down event rejected",automation.injectInputEvent(event,true));event.recycle();Thread.sleep(40);event=android.view.MotionEvent.obtain(now,android.os.SystemClock.uptimeMillis(),android.view.MotionEvent.ACTION_UP,bounds.exactCenterX(),bounds.exactCenterY(),0);event.setSource(android.view.InputDevice.SOURCE_TOUCHSCREEN);assertTrue("PiP menu up event rejected",automation.injectInputEvent(event,true));event.recycle();lastTap=now;
                }Thread.sleep(100);
            }
            String summary=systemUiLabels(automation);android.util.Log.i("LumaPlayback","test-PiP-menu-labels: "+summary);fail("SystemUI PiP action unavailable: "+java.util.Arrays.toString(labels)+"; "+summary);
        }finally{service.flags=flags;automation.setServiceInfo(service);}
    }
    private static boolean isSystemUi(android.view.accessibility.AccessibilityNodeInfo root){return root!=null&&(android.text.TextUtils.equals("com.android.systemui",root.getPackageName())||android.text.TextUtils.equals("com.android.wm.shell",root.getPackageName()));}
    private static String systemUiLabels(android.app.UiAutomation automation){StringBuilder out=new StringBuilder();for(android.view.accessibility.AccessibilityWindowInfo window:automation.getWindows()){android.view.accessibility.AccessibilityNodeInfo root=window.getRoot();if(isSystemUi(root))collectSystemUiLabels(root,out,new int[]{0});if(out.length()>=1800)break;}return out.length()==0?"No accessible SystemUI PiP menu labels":out.toString();}
    private static void collectSystemUiLabels(android.view.accessibility.AccessibilityNodeInfo node,StringBuilder out,int[] count){if(node==null||count[0]++>=80||out.length()>=1800)return;String description=safeUiLabel(node.getContentDescription()),text=safeUiLabel(node.getText()),id=safeUiLabel(node.getViewIdResourceName());if(!description.isEmpty()||!text.isEmpty()||!id.isEmpty()){out.append('[').append(id).append('|').append(description).append('|').append(text).append(";click=").append(node.isClickable()).append(']');}for(int i=0;i<node.getChildCount()&&out.length()<1800;i++)collectSystemUiLabels(node.getChild(i),out,count);}
    private static String safeUiLabel(CharSequence label){if(label==null)return "";String value=label.toString().replaceAll("[\\p{Cntrl}]"," ").replaceAll("https?://[^ ]+","[url]").replaceAll("(?i)(token|code|secret)[=: ]+[^ ]+","[redacted]").replaceAll("[0-9]{4,}","#");return value.substring(0,Math.min(100,value.length()));}
    private static boolean clickLabel(android.view.accessibility.AccessibilityNodeInfo node,String[] labels){
        for(String label:labels)if(android.text.TextUtils.equals(label,node.getContentDescription())||android.text.TextUtils.equals(label,node.getText())){android.view.accessibility.AccessibilityNodeInfo candidate=node;for(int parent=0;candidate!=null&&parent<3;parent++,candidate=candidate.getParent())if(candidate.isEnabled()&&candidate.isClickable()&&candidate.performAction(android.view.accessibility.AccessibilityNodeInfo.ACTION_CLICK))return true;}
        for(int index=0;index<node.getChildCount();index++){android.view.accessibility.AccessibilityNodeInfo child=node.getChild(index);if(child!=null&&clickLabel(child,labels))return true;}return false;
    }

    private static final class DesktopObserver {
        final dev.sdas.luma.net.LanApi api=new dev.sdas.luma.net.LanApi();final dev.sdas.luma.net.LanApi.Session session;
        DesktopObserver(String url,String code)throws Exception{dev.sdas.luma.net.LanEndpoint endpoint=dev.sdas.luma.net.LanEndpoint.parse(url);session=api.pair(endpoint,api.info(endpoint),code);}
        dev.sdas.luma.net.LanApi.Item item(String id)throws Exception{for(dev.sdas.luma.net.LanApi.Item item:api.playlist(session))if(item.id.equals(id))return item;throw new AssertionError("Authored fixture disappeared from the desktop playlist");}
    }
    private static dev.sdas.luma.net.LanApi.Item awaitExactSharedPhoneProgress(ActivityScenario<MainActivity> app,DesktopObserver observer,String id,long phoneMs,long computerMs,double afterTimestamp)throws Exception{
        long deadline=System.currentTimeMillis()+12000;dev.sdas.luma.net.LanApi.Item received=null;
        while(System.currentTimeMillis()<deadline){received=observer.item(id);assertEquals("Paused phone report changed computer progress",computerMs/1000.0,received.computerProgressSeconds,.05);
            if(value(app,MainActivity::debugProgressAccepted)>0&&received.hasMobileProgress()&&Math.abs(received.mobileProgressSeconds*1000-phoneMs)<30&&received.mobileUpdatedAtUnixSeconds>afterTimestamp){android.util.Log.i("LumaPlayback","test-home-exact-paused-report: phoneMs="+phoneMs+", serverMs="+Math.round(received.mobileProgressSeconds*1000));return received;}
            String error=value(app,MainActivity::debugError);if(error!=null)fail("Exact paused phone progress: "+error);Thread.sleep(100);
        }
        fail("Desktop did not acknowledge the exact paused dashboard checkpoint: expectedMs="+phoneMs+", serverMs="+(received==null||!received.hasMobileProgress()?-1:Math.round(received.mobileProgressSeconds*1000))+", accepted="+value(app,MainActivity::debugProgressAccepted));return null;
    }
    private static dev.sdas.luma.net.LanApi.Item awaitSharedPhoneProgress(ActivityScenario<MainActivity> app,DesktopObserver observer,String id,long phoneMs,long computerMs)throws Exception{
        long deadline=System.currentTimeMillis()+12000;dev.sdas.luma.net.LanApi.Item observed=null;
        while(System.currentTimeMillis()<deadline){observed=observer.item(id);assertEquals("Phone progress overwrote the computer playback position",computerMs/1000.0,observed.computerProgressSeconds,.05);
            if(value(app,MainActivity::debugProgressAccepted)>0&&observed.hasMobileProgress()&&Math.abs(observed.mobileProgressSeconds*1000-phoneMs)<650){android.util.Log.i("LumaPlayback","test-shared-phone-progress: phoneMs="+phoneMs+", desktopPhoneMs="+Math.round(observed.mobileProgressSeconds*1000)+", computerMs="+Math.round(observed.computerProgressSeconds*1000));return observed;}
            String error=value(app,MainActivity::debugError);if(error!=null)fail("Shared phone progress: "+error);Thread.sleep(200);
        }
        fail("Desktop did not acknowledge the paused phone position; accepted="+value(app,MainActivity::debugProgressAccepted)+", phoneMs="+phoneMs+", observedMs="+(observed==null||!observed.hasMobileProgress()?-1:Math.round(observed.mobileProgressSeconds*1000)));return null;
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
