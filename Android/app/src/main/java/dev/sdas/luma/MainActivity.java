package dev.sdas.luma;

import android.Manifest;
import android.annotation.SuppressLint;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.pm.ActivityInfo;
import android.content.pm.PackageManager;
import android.content.res.Configuration;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.text.InputFilter;
import android.text.InputType;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.ViewConfiguration;
import android.view.SurfaceView;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import android.widget.BaseAdapter;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ListView;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.SeekBar;
import android.widget.TextView;
import dev.sdas.luma.net.LanApi;
import dev.sdas.luma.net.LanEndpoint;
import dev.sdas.luma.net.StreamRelay;
import dev.sdas.luma.net.ScopedWifiConnections;
import dev.sdas.luma.net.ProgressSync;
import org.videolan.libvlc.LibVLC;
import org.videolan.libvlc.Media;
import org.videolan.libvlc.MediaPlayer;
import org.videolan.libvlc.interfaces.IMedia;
import org.videolan.libvlc.interfaces.IVLCVout;
import org.videolan.libvlc.util.VLCVideoLayout;
import java.net.HttpURLConnection;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;

public final class MainActivity extends Activity {
    private static final int BG=0xff10131b,PANEL=0xff1b202d,LINE=0xff30384a,TEXT=0xfff6f5f1,MUTED=0xffa9b2c6,ACCENT=0xffffa16f;
    // Fontconfig work is non-interruptible. Serialize it across activity
    // lifetimes, before any player can create a subtitle renderer.
    private static final ThreadPoolExecutor FONT_PREPARATION=new ThreadPoolExecutor(1,1,0,TimeUnit.SECONDS,new ArrayBlockingQueue<>(4),r->{Thread t=new Thread(r,"luma-playback-preparation");t.setDaemon(true);return t;});
    private final Handler main=new Handler(Looper.getMainLooper());
    private final ThreadPoolExecutor work=new ThreadPoolExecutor(2,2,0,TimeUnit.SECONDS,new ArrayBlockingQueue<>(48),r->{Thread t=new Thread(r,"luma-api");t.setDaemon(true);return t;});
    private final ThreadPoolExecutor retiredPlayers=new ThreadPoolExecutor(1,1,0,TimeUnit.SECONDS,new ArrayBlockingQueue<>(8),r->{Thread t=new Thread(r,"luma-player-cleanup");t.setDaemon(true);return t;});
    private final Map<String,Discovery.Desktop> desktops=new LinkedHashMap<>();
    private final Map<String,Long> localResume=new java.util.HashMap<>();
    private final java.util.Set<String> completedOnPhone=new java.util.HashSet<>();
    private ProgressSync progressSync;private int progressAcceptedCount;private long lastProgressOfferAt,lastProgressOfferedPosition=-1;private String lastProgressItemId,progressSyncState="";private boolean lastProgressCompleted;
    private List<LanApi.Item> items=Collections.emptyList();
    private LanApi api;private LanApi.Session session;private LanEndpoint selectedEndpoint;private volatile ScopedWifiConnections<Network> wifiConnections;
    private Discovery discovery;private LibVLC engine;private MediaPlayer player;private StreamRelay relay;
    private FrameLayout root,home,playback,videoContainer;private VLCVideoLayout video;
    private LinearLayout desktopRows,homeContent,playerControls,playerHeader;private TextView status,playlistTitle,playlistInfo,playerTitle,playerStatus,clock;
    private View videoTapTarget;private boolean chromeVisible=true;private PlayerIcon.Button fullButton,playButton,previousButton,nextButton;
    private LinearLayout centerTransport,playlistPanel;private FrameLayout playlistOverlay;private ListView playbackList;private PlaylistAdapter playlistAdapter;
    private boolean playlistOpen,drawerRestoreChrome;private TextView deviceProgress,gestureSeekText,volumeHudText,brightnessHudText,progressShareStatus;private Button resumeComputerButton;private TextView playbackPlaylistSummary;
    private LinearLayout volumeHud,brightnessHud;private GestureLevelView volumeLevel,brightnessLevel;private View seekHud;
    private PlaybackGestureMath.Axis gestureAxis=PlaybackGestureMath.Axis.NONE;private float gestureDownX,gestureDownY;private int gesturePointer=-1,gestureStartLevel,gestureMediaGeneration;private boolean gestureVolume,gestureSeekAllowed;
    private long gestureStartTime,gestureLength,gesturePreview;private float originalWindowBrightness=-1;
    private final Runnable hideChrome=this::hideChromeIfPlaying;
    private final Runnable hideGestureHud=()->{for(View view:new View[]{volumeHud,brightnessHud,seekHud})if(view!=null)view.animate().alpha(0).setDuration(160).withEndAction(()->view.setVisibility(View.GONE)).start();};
    private EditText manual;private Button connect;private ListView list;private SeekBar seek,volume;private AlertDialog pairDialog;
    private volatile boolean foreground,windowFocused;
    private boolean polling,connecting,dragging,full,wasPlaying,autoResumeApplied,destroyed;
    private int generation,mediaGeneration;private long initialResume;private LanApi.Item current;private String lastError;
    private boolean permissionAsked,nativeDiagnostics;
    private final java.util.Set<String> softwareItems=new java.util.HashSet<>();
    private boolean softwarePlayback,videoWasDisplayed,hadVideoTracks,videoOutputWasCreated,userPaused,debugSkipSurfaceOnce,nativePlayingSeen;private int videoOutputs;private long nativeStartedAt,playRequestedAt,lastOpeningDiagnostic;
    private Media.Stats lastStats;private SeekBar.OnSeekBarChangeListener seekListener;private IVLCVout.Callback surfaceCallback;
    private boolean nativeSurfacesReady,surfaceRebindPending,surfaceRestartPending,surfaceRestartStarted,surfaceRecoveryNeedsResume;private int videoGraphSequence,retiringGraphs,surfaceRebinds,surfaceRebindAttempts,surfaceCreatedEvents,surfaceDestroyedEvents;private long graphStartedAt,lastRetirementMs,surfaceRecoveryTime;
    private boolean enginePreparing,enginePrepared,fontCacheVerified;private long preparationStartedAt,preparationDurationMs;private LanApi.Item pendingPreparationItem;private int pendingPreparationConnection,pendingPreparationMedia;
    private int nativeTimeEvents,nativeBufferingEvents;private long lastNativeTimeEvent=-1;private float nativeBufferingPercent=-1;
    private final Runnable pulse=new Runnable(){public void run(){if(!foreground||destroyed)return;updatePlayer();main.postDelayed(this,500);}};
    private final Runnable poll=new Runnable(){public void run(){if(!foreground||destroyed)return;refresh(false);main.postDelayed(this,5000);}};
    private final Runnable settleFullscreenBars=()->{if(full&&foreground&&!destroyed){applySystemBars();root.requestApplyInsets();}};

    @Override public void onCreate(Bundle saved){
        super.onCreate(saved);getWindow().setStatusBarColor(BG);getWindow().setNavigationBarColor(BG);
        root=new FrameLayout(this);root.setBackgroundColor(BG);setContentView(root);
        root.setOnApplyWindowInsetsListener((view,insets)->{view.setPadding(full?0:insets.getSystemWindowInsetLeft(),full?0:insets.getSystemWindowInsetTop(),full?0:insets.getSystemWindowInsetRight(),full?0:insets.getSystemWindowInsetBottom());return insets;});
        root.addOnLayoutChangeListener((view,left,top,right,bottom,oldLeft,oldTop,oldRight,oldBottom)->{if(full&&(right-left!=oldRight-oldLeft||bottom-top!=oldBottom-oldTop))scheduleFullscreenBars();});
        originalWindowBrightness=getWindow().getAttributes().screenBrightness;buildHome();buildPlayer();
        if(Build.VERSION.SDK_INT>=33)getOnBackInvokedDispatcher().registerOnBackInvokedCallback(android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT,this::navigateBack);
        discovery=new Discovery(this,main,new Discovery.Listener(){
            public void found(Discovery.Desktop desktop){desktops.put(desktop.name,desktop);renderDesktops();}
            public void lost(String name){desktops.remove(name);renderDesktops();}
            public void status(String value){if(session==null&&!connecting)status.setText(value);}
        });
        String previous=getPreferences(MODE_PRIVATE).getString("desktopUrl","");manual.setText(previous);
    }
    private void buildHome(){
        home=new FrameLayout(this);root.addView(home,new FrameLayout.LayoutParams(-1,-1));
        ScrollView scroll=new ScrollView(this);scroll.setFillViewport(true);home.addView(scroll);
        homeContent=column();homeContent.setPadding(dp(22),dp(20),dp(22),dp(28));scroll.addView(homeContent);
        LinearLayout header=row();TextView brand=text("▶  LUMA",22,TEXT);brand.setTypeface(null,Typeface.BOLD);header.addView(brand,new LinearLayout.LayoutParams(0,-2,1));
        Button about=button("About",this::about,false);header.addView(about);homeContent.addView(header);
        TextView tag=text("DESKTOP TO ANDROID",11,ACCENT);tag.setLetterSpacing(.13f);tag.setPadding(0,dp(25),0,dp(10));homeContent.addView(tag);
        TextView headline=text("A little closer\nto your stories.",34,TEXT);headline.setTypeface(null,Typeface.BOLD);homeContent.addView(headline);
        addText(homeContent,"Stream your desktop playlist over the same Wi-Fi. Your media stays on your computer; nothing is saved as a phone download.",15,MUTED,14);
        LinearLayout disclosure=card();addText(disclosure,"Your trusted Wi-Fi only",15,TEXT,0);
        addText(disclosure,"Pair with your own desktop. Local HTTP is unencrypted, so use a home network you trust. The host must stay awake with sharing enabled.",13,MUTED,7);homeContent.addView(disclosure,margin(0,20,0,22));
        LinearLayout nearby=row();TextView nearbyTitle=text("Nearby desktops",19,TEXT);nearbyTitle.setTypeface(null,Typeface.BOLD);nearby.addView(nearbyTitle,new LinearLayout.LayoutParams(0,-2,1));nearby.addView(button("Find",()->{desktops.clear();renderDesktops();discovery.stop();permissionAsked=false;ensurePermission();},false));homeContent.addView(nearby);
        status=text("Start local sharing in Luma on your desktop.",13,MUTED);status.setId(R.id.connection_status);status.setPadding(0,dp(8),0,dp(12));homeContent.addView(status);
        desktopRows=column();homeContent.addView(desktopRows);renderDesktops();
        LinearLayout manualCard=card();addText(manualCard,"Connect with an address",15,TEXT,0);
        manual=new EditText(this);manual.setId(R.id.manual_address);manual.setSingleLine();manual.setTextSize(14);manual.setTextColor(TEXT);manual.setHintTextColor(MUTED);manual.setHint("http://192.168.1.20:12345");manual.setInputType(InputType.TYPE_CLASS_TEXT|InputType.TYPE_TEXT_VARIATION_URI);manualCard.addView(manual,new LinearLayout.LayoutParams(-1,dp(54)));
        connect=button("Connect to desktop",()->connectManual(),true);connect.setId(R.id.connect);manualCard.addView(connect,new LinearLayout.LayoutParams(-1,dp(48)));homeContent.addView(manualCard,margin(0,12,0,24));
        LinearLayout libraryHeader=row();playlistTitle=text("Your desktop playlist",19,TEXT);playlistTitle.setTypeface(null,Typeface.BOLD);libraryHeader.addView(playlistTitle,new LinearLayout.LayoutParams(0,-2,1));libraryHeader.addView(button("Refresh",()->refresh(true),false));homeContent.addView(libraryHeader);
        playlistInfo=text("Pair with a desktop to see its current playlist.",12,MUTED);playlistInfo.setPadding(0,dp(7),0,dp(10));homeContent.addView(playlistInfo);
        list=new ListView(this);list.setId(R.id.media_list);list.setDividerHeight(dp(7));list.setDivider(new android.graphics.drawable.ColorDrawable(BG));list.setBackgroundColor(BG);playlistAdapter=new PlaylistAdapter();list.setAdapter(playlistAdapter);
        list.setOnItemClickListener((parent,view,position,id)->startMedia(items.get(position)));
        list.setOnTouchListener((view,event)->{view.getParent().requestDisallowInterceptTouchEvent(true);return false;});
        homeContent.addView(list,new LinearLayout.LayoutParams(-1,dp(330)));
        addText(homeContent,"FREE BY SDAS  ·  ANDROID PREVIEW",11,MUTED,18);
    }
    private void buildPlayer(){
        playback=new FrameLayout(this);playback.setBackgroundColor(Color.BLACK);root.addView(playback,new FrameLayout.LayoutParams(-1,-1));playback.setVisibility(View.GONE);
        videoContainer=new FrameLayout(this);playback.addView(videoContainer,new FrameLayout.LayoutParams(-1,-1));
        video=new VLCVideoLayout(this);videoContainer.addView(video,new FrameLayout.LayoutParams(-1,-1));
        videoTapTarget=new View(this);videoTapTarget.setBackgroundColor(Color.TRANSPARENT);videoTapTarget.setOnClickListener(v->{setChromeVisible(!chromeVisible);scheduleChromeHide();});videoTapTarget.setOnTouchListener(this::playbackTouch);videoContainer.addView(videoTapTarget,new FrameLayout.LayoutParams(-1,-1));
        playerHeader=row();playerHeader.setClickable(true);playerHeader.setPadding(dp(14),dp(12),dp(14),dp(16));playerHeader.setBackground(new GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM,new int[]{0xe60b0d14,0x000b0d14}));
        PlayerIcon.Button back=icon(PlayerIcon.Kind.BACK,"Back to desktops",this::leavePlayer,false,48);back.setId(R.id.player_back);playerHeader.addView(back,new LinearLayout.LayoutParams(dp(48),dp(48)));
        LinearLayout heading=column();heading.setPadding(dp(10),0,dp(8),0);playerTitle=text("",17,TEXT);playerTitle.setTypeface(null,Typeface.BOLD);playerTitle.setSingleLine();playerTitle.setEllipsize(TextUtils.TruncateAt.END);heading.addView(playerTitle);
        TextView origin=text("YOUR DESKTOP · LIVE STREAM",10,0xffbfc6d5);origin.setLetterSpacing(.1f);origin.setPadding(0,dp(5),0,0);heading.addView(origin);playerHeader.addView(heading,new LinearLayout.LayoutParams(0,-2,1));
        PlayerIcon.Button queue=icon(PlayerIcon.Kind.PLAYLIST,"Open playlist",()->setPlaylistOpen(true),false,48);queue.setId(R.id.player_playlist);playerHeader.addView(queue,new LinearLayout.LayoutParams(dp(48),dp(48)));
        fullButton=icon(PlayerIcon.Kind.FULLSCREEN,"Full screen",()->setFull(!full),false,48);playerHeader.addView(fullButton,new LinearLayout.LayoutParams(dp(48),dp(48)));
        playback.addView(playerHeader,new FrameLayout.LayoutParams(-1,-2,Gravity.TOP));
        centerTransport=row();centerTransport.setClickable(true);centerTransport.setGravity(Gravity.CENTER);centerTransport.setPadding(dp(8),dp(8),dp(8),dp(8));
        previousButton=icon(PlayerIcon.Kind.PREVIOUS,"Previous video",()->playAdjacent(-1),false,48);previousButton.setId(R.id.player_previous);centerTransport.addView(previousButton,new LinearLayout.LayoutParams(dp(48),dp(48)));
        centerTransport.addView(icon(PlayerIcon.Kind.REWIND,"Seek back ten seconds",()->jump(-10000),false,48),new LinearLayout.LayoutParams(dp(48),dp(48)));
        playButton=icon(PlayerIcon.Kind.PAUSE,"Pause",this::togglePlay,true,72);playButton.setId(R.id.player_play);LinearLayout.LayoutParams primary=new LinearLayout.LayoutParams(dp(72),dp(72));primary.setMargins(dp(14),0,dp(14),0);centerTransport.addView(playButton,primary);
        centerTransport.addView(icon(PlayerIcon.Kind.FORWARD,"Seek forward ten seconds",()->jump(10000),false,48),new LinearLayout.LayoutParams(dp(48),dp(48)));
        nextButton=icon(PlayerIcon.Kind.NEXT,"Next video",()->playAdjacent(1),false,48);nextButton.setId(R.id.player_next);centerTransport.addView(nextButton,new LinearLayout.LayoutParams(dp(48),dp(48)));
        playback.addView(centerTransport,new FrameLayout.LayoutParams(-2,-2,Gravity.CENTER));
        playerControls=column();playerControls.setClickable(true);playerControls.setPadding(dp(22),dp(22),dp(22),dp(14));playerControls.setBackground(new GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM,new int[]{0x000b0d14,0xf20b0d14}));
        LinearLayout statusLine=row();playerStatus=text("Opening stream…",11,0xffc1c8d8);playerStatus.setId(R.id.player_status);playerStatus.setSingleLine();playerStatus.setEllipsize(TextUtils.TruncateAt.END);statusLine.addView(playerStatus,new LinearLayout.LayoutParams(0,-2,1));
        resumeComputerButton=button("Resume from computer",this::resumeFromComputer,false);resumeComputerButton.setTextSize(11);resumeComputerButton.setBackground(background(0x481f2634,24));resumeComputerButton.setMinHeight(dp(48));statusLine.addView(resumeComputerButton,new LinearLayout.LayoutParams(-2,dp(48)));playerControls.addView(statusLine);
        seek=new SeekBar(this);seek.setId(R.id.player_seek);seek.setContentDescription("Playback position");seek.setMax(1000);seek.setProgressTintList(ColorStateList.valueOf(ACCENT));seek.setProgressBackgroundTintList(ColorStateList.valueOf(0x55ffffff));seek.setThumbTintList(ColorStateList.valueOf(TEXT));playerControls.addView(seek,new LinearLayout.LayoutParams(-1,dp(36)));
        seekListener=new SeekBar.OnSeekBarChangeListener(){
            public void onProgressChanged(SeekBar bar,int value,boolean user){if(user&&player!=null&&player.getLength()>0)clock.setText(time((long)(player.getLength()*value/1000.0))+" / "+time(player.getLength()));}
            public void onStartTrackingTouch(SeekBar bar){dragging=true;main.removeCallbacks(hideChrome);}
            public void onStopTrackingTouch(SeekBar bar){dragging=false;if(player!=null&&player.isSeekable()&&player.getLength()>0){autoResumeApplied=true;player.setTime((long)(player.getLength()*(bar.getProgress()/1000.0)),false);checkpointProgressSoon();}scheduleChromeHide();}
        };seek.setOnSeekBarChangeListener(seekListener);
        LinearLayout timing=row();clock=text("0:00 / 0:00",13,TEXT);clock.setTypeface(null,Typeface.BOLD);timing.addView(clock,new LinearLayout.LayoutParams(0,-2,1));deviceProgress=text("Computer · 0:00\nThis phone · 0:00",11,0xffbfc6d5);deviceProgress.setGravity(Gravity.END);deviceProgress.setMaxLines(2);timing.addView(deviceProgress);playerControls.addView(timing);
        progressShareStatus=text("",10,MUTED);progressShareStatus.setGravity(Gravity.END);progressShareStatus.setSingleLine();progressShareStatus.setEllipsize(TextUtils.TruncateAt.END);progressShareStatus.setPadding(0,dp(3),0,dp(2));playerControls.addView(progressShareStatus,new LinearLayout.LayoutParams(-1,-2));
        TextView gestures=text("Swipe left/right to seek · left edge brightness · right edge volume",10,0xff8d97ad);gestures.setGravity(Gravity.CENTER);gestures.setPadding(0,dp(12),0,0);playerControls.addView(gestures);
        playback.addView(playerControls,new FrameLayout.LayoutParams(-1,-2,Gravity.BOTTOM));
        // A model-only seek bar preserves app-local volume across player graphs;
        // there is no persistent horizontal volume control over the video.
        volume=new SeekBar(this);volume.setMax(100);volume.setProgress(80);volume.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener(){public void onStartTrackingTouch(SeekBar b){}public void onStopTrackingTouch(SeekBar b){}public void onProgressChanged(SeekBar b,int value,boolean user){if(player!=null)player.setVolume(value);}});
        buildGestureHuds();buildPlaybackPlaylist();updateNavigation();
    }
    private PlayerIcon.Button icon(PlayerIcon.Kind kind,String label,Runnable action,boolean prominent,int size){PlayerIcon.Button button=new PlayerIcon.Button(this,kind,label,prominent);button.setOnClickListener(v->{action.run();scheduleChromeHide();});return button;}
    private void setPlayAction(String action){if(action.equals("Play"))sharePlaybackProgress(true);playButton.setAction(action.equals("Play")?PlayerIcon.Kind.PLAY:PlayerIcon.Kind.PAUSE,action);if(action.equals("Play")&&(userPaused||lastError!=null)){main.removeCallbacks(hideChrome);if(full&&!playlistOpen)setChromeVisible(true);}else scheduleChromeHide();}
    private void hideChromeIfPlaying(){if(full&&!userPaused&&!playlistOpen&&lastError==null)setChromeVisible(false);}
    private void scheduleChromeHide(){main.removeCallbacks(hideChrome);if(full&&foreground&&current!=null&&!userPaused&&lastError==null&&!playlistOpen&&!((android.view.accessibility.AccessibilityManager)getSystemService(ACCESSIBILITY_SERVICE)).isTouchExplorationEnabled())main.postDelayed(hideChrome,3200);}
    private void buildGestureHuds(){
        volumeHud=levelHud(PlayerIcon.Kind.VOLUME);volumeHudText=(TextView)volumeHud.getChildAt(2);volumeLevel=(GestureLevelView)volumeHud.getChildAt(1);FrameLayout.LayoutParams right=new FrameLayout.LayoutParams(dp(62),dp(172),Gravity.END|Gravity.CENTER_VERTICAL);right.setMargins(dp(18),0,dp(18),0);playback.addView(volumeHud,right);
        brightnessHud=levelHud(PlayerIcon.Kind.BRIGHTNESS);brightnessHudText=(TextView)brightnessHud.getChildAt(2);brightnessLevel=(GestureLevelView)brightnessHud.getChildAt(1);FrameLayout.LayoutParams left=new FrameLayout.LayoutParams(dp(62),dp(172),Gravity.START|Gravity.CENTER_VERTICAL);left.setMargins(dp(18),0,dp(18),0);playback.addView(brightnessHud,left);
        LinearLayout preview=column();preview.setGravity(Gravity.CENTER);preview.setPadding(dp(22),dp(14),dp(22),dp(14));preview.setBackground(background(0xe8171c27,24));gestureSeekText=text("",19,TEXT);gestureSeekText.setGravity(Gravity.CENTER);gestureSeekText.setTypeface(null,Typeface.BOLD);preview.addView(gestureSeekText);seekHud=preview;seekHud.setVisibility(View.GONE);playback.addView(seekHud,new FrameLayout.LayoutParams(-2,-2,Gravity.CENTER));
    }
    private LinearLayout levelHud(PlayerIcon.Kind kind){LinearLayout panel=column();panel.setGravity(Gravity.CENTER);panel.setPadding(dp(12),dp(14),dp(12),dp(10));panel.setBackground(background(0xe8171c27,28));View image=new View(this);image.setBackground(new PlayerIcon(kind,TEXT));panel.addView(image,new LinearLayout.LayoutParams(dp(22),dp(22)));GestureLevelView level=new GestureLevelView(this);LinearLayout.LayoutParams lp=new LinearLayout.LayoutParams(dp(16),0,1);lp.setMargins(0,dp(10),0,dp(8));panel.addView(level,lp);TextView percent=text("",11,TEXT);percent.setGravity(Gravity.CENTER);panel.addView(percent);panel.setVisibility(View.GONE);return panel;}
    private void buildPlaybackPlaylist(){
        playlistOverlay=new FrameLayout(this);playlistOverlay.setVisibility(View.GONE);View dim=new View(this);dim.setBackgroundColor(0x66000000);dim.setOnClickListener(v->setPlaylistOpen(false));playlistOverlay.addView(dim,new FrameLayout.LayoutParams(-1,-1));
        playlistPanel=column();playlistPanel.setClickable(true);playlistPanel.setPadding(dp(18),dp(16),dp(18),dp(12));playlistPanel.setBackground(background(0xf51a1e2a,22));
        LinearLayout title=row();TextView label=text("Playlist",23,TEXT);label.setTypeface(null,Typeface.BOLD);title.addView(label,new LinearLayout.LayoutParams(0,-2,1));title.addView(icon(PlayerIcon.Kind.CLOSE,"Close playlist",()->setPlaylistOpen(false),false,48),new LinearLayout.LayoutParams(dp(48),dp(48)));playlistPanel.addView(title);
        playbackPlaylistSummary=text("Your desktop playlist · playback stays open",11,MUTED);playbackPlaylistSummary.setPadding(0,dp(4),0,dp(16));playlistPanel.addView(playbackPlaylistSummary);
        playbackList=new ListView(this);playbackList.setId(R.id.playback_playlist);playbackList.setBackgroundColor(Color.TRANSPARENT);playbackList.setDividerHeight(dp(8));playbackList.setDivider(new android.graphics.drawable.ColorDrawable(Color.TRANSPARENT));playbackList.setAdapter(playlistAdapter);playbackList.setOnItemClickListener((parent,view,position,id)->{LanApi.Item item=items.get(position);setPlaylistOpen(false);if(current==null||!current.id.equals(item.id))startMedia(item);});playlistPanel.addView(playbackList,new LinearLayout.LayoutParams(-1,0,1));
        playlistOverlay.addView(playlistPanel,new FrameLayout.LayoutParams(dp(340),-1,Gravity.START));playback.addView(playlistOverlay,new FrameLayout.LayoutParams(-1,-1));playback.addOnLayoutChangeListener((v,l,t,r,b,ol,ot,or,ob)->layoutPlaylist());
    }
    private void layoutPlaylist(){if(playlistPanel==null||playback.getWidth()<=0)return;FrameLayout.LayoutParams p=(FrameLayout.LayoutParams)playlistPanel.getLayoutParams();int max=dp(getResources().getConfiguration().orientation==Configuration.ORIENTATION_LANDSCAPE?360:380);int width=Math.min(max,Math.round(playback.getWidth()*.86f));if(p.width!=width){p.width=width;playlistPanel.setLayoutParams(p);}}
    private void setPlaylistOpen(boolean open){
        if(playlistOpen==open)return;playlistOpen=open;main.removeCallbacks(hideChrome);playlistOverlay.animate().cancel();playlistPanel.animate().cancel();
        if(open){drawerRestoreChrome=chromeVisible;setChromeVisible(false);playlistAdapter.notifyDataSetChanged();layoutPlaylist();playlistOverlay.setAlpha(1);playlistOverlay.setVisibility(View.VISIBLE);playlistPanel.setTranslationX(-Math.max(dp(340),playlistPanel.getWidth()));playlistPanel.animate().translationX(0).setDuration(220).start();}
        else{playlistPanel.animate().translationX(-Math.max(dp(340),playlistPanel.getWidth())).setDuration(180).withEndAction(()->{if(!playlistOpen)playlistOverlay.setVisibility(View.GONE);}).start();setChromeVisible(drawerRestoreChrome);scheduleChromeHide();}
    }
    private int currentIndex(){if(current!=null)for(int i=0;i<items.size();i++)if(items.get(i).id.equals(current.id))return i;return -1;}
    private void updateNavigation(){int index=currentIndex();if(previousButton!=null)previousButton.setEnabled(index>0);if(nextButton!=null)nextButton.setEnabled(index>=0&&index+1<items.size());}
    private void playAdjacent(int direction){int target=currentIndex()+direction;if(current!=null&&target>=0&&target<items.size())startMedia(items.get(target));}
    private LanApi.Item latestItem(LanApi.Item item){for(LanApi.Item latest:items)if(latest.id.equals(item.id))return latest;return item;}
    private double computerProgress(LanApi.Item item){return latestItem(item).computerProgressSeconds;}
    private double phoneProgress(LanApi.Item item){Long local=localResume.get(item.id);return local==null?latestItem(item).mobileProgressSeconds:local/1000.0;}
    private String progressText(double seconds,double duration,boolean completed){if(completed)return "Finished";if(!Double.isFinite(seconds))return "Not started";String position=time((long)(Math.max(0,seconds)*1000));return duration>0?position+" · "+Math.min(100,Math.max(0,Math.round(seconds*100/duration)))+"%":position;}
    private void refreshProgressDetails(){if(current==null||deviceProgress==null)return;LanApi.Item latest=latestItem(current);double computer=computerProgress(current),phone=phoneProgress(current);double duration=player!=null&&player.getLength()>0?player.getLength()/1000.0:latest.mobileDurationSeconds>0?latest.mobileDurationSeconds:latest.durationSeconds;boolean phoneComplete=player!=null&&player.getPlayerState()==Media.State.Ended||!localResume.containsKey(current.id)&&latest.mobileCompleted;deviceProgress.setText("Computer · "+progressText(computer,duration,latest.computerCompleted)+"\nThis phone · "+progressText(phone,duration,phoneComplete));deviceProgress.setContentDescription(deviceProgress.getText()+". "+progressSyncState);progressShareStatus.setText(progressSyncState);resumeComputerButton.setEnabled(player==null||!nativePlayingSeen||player.isSeekable());resumeComputerButton.setVisibility(computer>0&&!latest.computerCompleted?View.VISIBLE:View.GONE);resumeComputerButton.setText("Resume from computer · "+time((long)(computer*1000)));}
    private long initialResumeFor(LanApi.Item item){
        Long local=localResume.get(item.id);if(local!=null)return completedOnPhone.contains(item.id)?0:Math.max(0,local);
        LanApi.Item latest=latestItem(item);
        if(latest.hasMobileProgress()&&latest.mobileUpdatedAtUnixSeconds>=latest.computerUpdatedAtUnixSeconds)return latest.mobileCompleted?0:Math.round(latest.mobileProgressSeconds*1000);
        return latest.computerCompleted?0:Math.round(latest.computerProgressSeconds*1000);
    }
    private void beginProgressSync(LanApi client,LanApi.Session paired){
        endProgressSync();lastProgressOfferAt=0;lastProgressOfferedPosition=-1;lastProgressItemId=null;lastProgressCompleted=false;progressAcceptedCount=0;
        if(!paired.host.supportsProgress){progressSyncState="Update the desktop app to share phone progress.";return;}
        progressSyncState="Progress linked · updates about every 5s";int version=generation;
        progressSync=new ProgressSync(client,paired,(id,accepted,code)->main.post(()->{
            if(stale(version)||session!=paired)return;
            if(accepted){progressAcceptedCount++;progressSyncState="Progress sharing connected";}
            else progressSyncState=code==401?"Reconnect to share progress":code==429?"Progress sharing paused briefly":"Waiting to share progress with computer";
            if(current!=null&&current.id.equals(id))refreshProgressDetails();
        }));
    }
    private void checkpointProgressSoon(){int clip=mediaGeneration;main.postDelayed(()->{if(!destroyed&&clip==mediaGeneration)sharePlaybackProgress(true);},400);}
    private void endProgressSync(){ProgressSync old=progressSync;progressSync=null;if(old!=null)old.close();}
    private void sharePlaybackProgress(boolean force){
        if(progressSync==null||current==null||player==null||!nativePlayingSeen||!autoResumeApplied||lastError!=null||surfaceRebindPending||surfaceRestartPending||surfaceRecoveryNeedsResume)return;
        int state=player.getPlayerState();boolean complete=state==Media.State.Ended,playing=player.isPlaying();if(!force&&!playing&&!complete)return;
        long length=Math.max(0,player.getLength()),position=complete&&length>0?length:player.getTime();if(position<0||position>31536000000L||length>31536000000L)return;
        long now=android.os.SystemClock.uptimeMillis();boolean same=current.id.equals(lastProgressItemId),jump=same&&lastProgressOfferedPosition>=0&&Math.abs(position-lastProgressOfferedPosition)>8000;
        if(same&&complete==lastProgressCompleted){if(!force&&!jump&&now-lastProgressOfferAt<5000)return;if(position==lastProgressOfferedPosition)return;}
        lastProgressItemId=current.id;lastProgressOfferAt=now;lastProgressOfferedPosition=position;lastProgressCompleted=complete;
        progressSync.offer(current.id,position,length,complete);
    }
    private void resumeFromComputer(){
        if(current==null)return;long target=Math.max(0,Math.round(computerProgress(current)*1000));completedOnPhone.remove(current.id);localResume.put(current.id,target);initialResume=target;autoResumeApplied=false;userPaused=false;
        int state=player==null?Media.State.Stopped:player.getPlayerState();
        if(player==null||state==Media.State.Ended||state==Media.State.Stopped||state==Media.State.Error){startMedia(current);return;}
        setPlayAction("Pause");playerStatus.setText("Resuming from computer…");
        // The explicit target survives Opening/font preparation. Use the same
        // first-frame/deferred seek guard as ordinary resume when not ready yet.
        if(nativePlayingSeen&&nativeSurfacesReady&&validVideoSurface(video)&&player.isSeekable()&&player.getLength()>0){autoResumeApplied=true;player.setTime(Math.min(target,Math.max(0,player.getLength()-250)),false);checkpointProgressSoon();}
        if(nativeSurfacesReady&&validVideoSurface(video)&&playRequestedAt>0)player.play();
    }
    private int brightnessPercent(){float level=getWindow().getAttributes().screenBrightness;if(level<0)level=Settings.System.getInt(getContentResolver(),Settings.System.SCREEN_BRIGHTNESS,128)/255f;return Math.max(2,Math.min(100,Math.round(level*100)));}
    private void setBrightnessPercent(int percent){WindowManager.LayoutParams attributes=getWindow().getAttributes();attributes.screenBrightness=Math.max(.02f,Math.min(1,percent/100f));getWindow().setAttributes(attributes);}
    private void restoreBrightness(){WindowManager.LayoutParams attributes=getWindow().getAttributes();attributes.screenBrightness=originalWindowBrightness;getWindow().setAttributes(attributes);}
    private void showGestureLevel(boolean sound,int level){main.removeCallbacks(hideGestureHud);seekHud.setVisibility(View.GONE);(sound?brightnessHud:volumeHud).setVisibility(View.GONE);LinearLayout hud=sound?volumeHud:brightnessHud;hud.animate().cancel();hud.setAlpha(1);hud.setVisibility(View.VISIBLE);(sound?volumeLevel:brightnessLevel).setLevel(level);(sound?volumeHudText:brightnessHudText).setText(level+"%");hud.setContentDescription((sound?"Volume ":"Brightness ")+level+" percent");}
    @SuppressLint("ClickableViewAccessibility")
    private boolean playbackTouch(View target,MotionEvent event){
        int action=event.getActionMasked();if(action==MotionEvent.ACTION_DOWN){if(playlistOpen||current==null)return false;float edge=dp(16);if(event.getX()<edge||event.getX()>target.getWidth()-edge)return false;gesturePointer=event.getPointerId(0);gestureDownX=event.getX();gestureDownY=event.getY();gestureAxis=PlaybackGestureMath.Axis.NONE;gestureMediaGeneration=mediaGeneration;gestureVolume=gestureDownX>=target.getWidth()/2f;gestureStartLevel=gestureVolume?volume.getProgress():brightnessPercent();gestureStartTime=player==null?0:Math.max(0,player.getTime());gestureLength=player==null?0:player.getLength();gestureSeekAllowed=player!=null&&player.isSeekable()&&gestureLength>0;gesturePreview=gestureStartTime;main.removeCallbacks(hideChrome);return true;}
        if(gesturePointer<0)return false;
        if(event.getPointerCount()!=1||action==MotionEvent.ACTION_CANCEL||gestureMediaGeneration!=mediaGeneration){gesturePointer=-1;gestureAxis=PlaybackGestureMath.Axis.NONE;main.postDelayed(hideGestureHud,100);scheduleChromeHide();return true;}
        float dx=event.getX()-gestureDownX,dy=event.getY()-gestureDownY;
        if(action==MotionEvent.ACTION_MOVE||action==MotionEvent.ACTION_UP){if(gestureAxis==PlaybackGestureMath.Axis.NONE)gestureAxis=PlaybackGestureMath.axis(dx,dy,ViewConfiguration.get(this).getScaledTouchSlop());
            if(gestureAxis==PlaybackGestureMath.Axis.HORIZONTAL){volumeHud.setVisibility(View.GONE);brightnessHud.setVisibility(View.GONE);if(gestureSeekAllowed){gesturePreview=PlaybackGestureMath.seek(gestureStartTime,dx,target.getWidth(),gestureLength);long offset=gesturePreview-gestureStartTime;gestureSeekText.setText((offset>=0?"+":"−")+time(Math.abs(offset))+"\n"+time(gesturePreview)+" / "+time(gestureLength));if(gestureLength>0)seek.setProgress((int)(gesturePreview*1000/gestureLength));}else gestureSeekText.setText("Seeking unavailable");seekHud.animate().cancel();seekHud.setAlpha(1);seekHud.setVisibility(View.VISIBLE);}
            else if(gestureAxis==PlaybackGestureMath.Axis.VERTICAL){int level=PlaybackGestureMath.level(gestureStartLevel,dy,target.getHeight());if(gestureVolume)volume.setProgress(level);else setBrightnessPercent(level);showGestureLevel(gestureVolume,gestureVolume?level:Math.max(2,level));}if(action==MotionEvent.ACTION_MOVE)return true;}
        if(action==MotionEvent.ACTION_UP){PlaybackGestureMath.Axis completed=gestureAxis;gesturePointer=-1;gestureAxis=PlaybackGestureMath.Axis.NONE;if(completed==PlaybackGestureMath.Axis.HORIZONTAL&&gestureSeekAllowed&&player!=null){autoResumeApplied=true;player.setTime(gesturePreview,false);checkpointProgressSoon();}else if(completed==PlaybackGestureMath.Axis.NONE)target.performClick();main.postDelayed(hideGestureHud,650);scheduleChromeHide();return true;}
        return true;
    }
    private void renderDesktops(){
        if(desktopRows==null)return;desktopRows.removeAllViews();
        if(desktops.isEmpty()){TextView empty=text("No desktop yet? Turn on sharing, or paste its local address below.",13,MUTED);empty.setPadding(0,dp(5),0,dp(10));desktopRows.addView(empty);return;}
        for(Discovery.Desktop desktop:desktops.values()){
            Button row=button(desktop.name+"\n"+desktop.endpoint.origin,()->connectDesktop(desktop.endpoint,desktop.network),false);row.setGravity(Gravity.START|Gravity.CENTER_VERTICAL);row.setAllCaps(false);desktopRows.addView(row,margin(0,0,0,8));
        }
    }
    private Network wifiNetwork(){
        ConnectivityManager connectivity=(ConnectivityManager)getSystemService(CONNECTIVITY_SERVICE);if(connectivity==null)return null;
        try{Network active=connectivity.getActiveNetwork();if(usableWifi(active))return active;for(Network network:connectivity.getAllNetworks())if(usableWifi(network))return network;}catch(RuntimeException ignored){}return null;
    }
    private boolean usableWifi(Network network){
        if(network==null)return false;try{ConnectivityManager connectivity=(ConnectivityManager)getSystemService(CONNECTIVITY_SERVICE);if(connectivity==null)return false;NetworkCapabilities caps=connectivity.getNetworkCapabilities(network);if(caps==null||!caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)||caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)||caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR))return false;for(Network current:connectivity.getAllNetworks())if(network.equals(current))return true;}catch(RuntimeException ignored){}return false;
    }
    private ScopedWifiConnections<Network> forNetwork(LanEndpoint endpoint,Network preferred){return new ScopedWifiConnections<>(endpoint,preferred,new ScopedWifiConnections.Routes<Network>(){
        public boolean usable(Network network){return usableWifi(network);}
        public Network chooseWifi(){return wifiNetwork();}
        public HttpURLConnection open(Network network,java.net.URL url)throws java.io.IOException{
            if(!usableWifi(network))throw new ScopedWifiConnections.RouteException(ScopedWifiConnections.Reason.NO_WIFI);
            android.util.Log.i("LumaPlayback","wifi-route-open: publicInfo="+(url.getPath().equals("/v1/info")&&url.getQuery()==null)+", "+networkDiagnostics(network));
            return (HttpURLConnection)network.openConnection(url,java.net.Proxy.NO_PROXY);
        }
    });}
    private Network currentWifiRoute(){ScopedWifiConnections<Network> current=wifiConnections;return current==null?null:current.selectedRoute();}
    private String networkDiagnostics(Network bound){
        try{
            ConnectivityManager connectivity=(ConnectivityManager)getSystemService(CONNECTIVITY_SERVICE);
            if(connectivity==null)return "networkState=UNAVAILABLE, foreground="+foreground+", windowFocus="+windowFocused;
            Network active=connectivity.getActiveNetwork(),route=bound;
            NetworkCapabilities caps=route==null?null:connectivity.getNetworkCapabilities(route),activeCaps=active==null?null:connectivity.getNetworkCapabilities(active);boolean listed=false;int availableWifi=0;
            for(Network candidate:connectivity.getAllNetworks()){if(route!=null&&route.equals(candidate))listed=true;NetworkCapabilities candidateCaps=connectivity.getNetworkCapabilities(candidate);if(candidateCaps!=null&&candidateCaps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)&&!candidateCaps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)&&!candidateCaps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR))availableWifi++;}
            return "networkBound="+(bound!=null)+", networkListed="+listed+", networkCaps="+(caps!=null)+", networkWifi="+(caps!=null&&caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI))+", availableWifiCount="+availableWifi+", activeCaps="+(activeCaps!=null)+", activeWifi="+(activeCaps!=null&&activeCaps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI))+", activeCellular="+(activeCaps!=null&&activeCaps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR))+", activeVpn="+(activeCaps!=null&&activeCaps.hasTransport(NetworkCapabilities.TRANSPORT_VPN))+", activeMatchesRoute="+(route!=null&&route.equals(active))+", localPermission="+hasPermission()+", foreground="+foreground+", windowFocus="+windowFocused;
        }catch(RuntimeException ignored){return "networkState=UNAVAILABLE, foreground="+foreground+", windowFocus="+windowFocused;}
    }
    private void connectManual(){if(!hasPermission()){permissionAsked=false;ensurePermission();return;}try{connectDesktop(LanEndpoint.parse(manual.getText().toString().trim()),wifiNetwork());}catch(IllegalArgumentException error){status.setText(error.getMessage());}}
    private void connectDesktop(LanEndpoint endpoint,Network network){
        if(connecting)return;if(!hasPermission()){ensurePermission();return;}disconnect(false);selectedEndpoint=endpoint;wifiConnections=forNetwork(endpoint,network);api=new LanApi(wifiConnections);connecting=true;connect.setEnabled(false);status.setText("Connecting to desktop…");int version=generation;
        LanApi client=api;ScopedWifiConnections<Network> scope=wifiConnections;
        execute(()->{try{LanApi.Info info=client.info(endpoint);scope.pinExpectedHost(info.id);main.post(()->{if(stale(version))return;connecting=false;connect.setEnabled(true);pair(endpoint,info);});}catch(Exception error){main.post(()->{if(stale(version))return;connecting=false;connect.setEnabled(true);status.setText(safe(error));});}});
    }
    private void pair(LanEndpoint endpoint,LanApi.Info info){
        if(!foreground)return;EditText code=new EditText(this);code.setId(R.id.pair_code);code.setTextSize(25);code.setGravity(Gravity.CENTER);code.setInputType(InputType.TYPE_CLASS_NUMBER);code.setFilters(new InputFilter[]{new InputFilter.LengthFilter(6)});code.setHint("000000");
        LinearLayout content=column();content.setPadding(dp(22),dp(12),dp(22),dp(6));addText(content,"Enter the current six-digit code shown in Luma on your desktop. This connection is for your trusted Wi-Fi only.",14,MUTED,0);content.addView(code,new LinearLayout.LayoutParams(-1,dp(65)));
        pairDialog=new AlertDialog.Builder(this).setTitle("Pair with "+info.name).setView(content).setNegativeButton("Cancel",(d,w)->cancelPair()).setPositiveButton("Pair",null).create();
        pairDialog.setOnCancelListener(d->cancelPair());LanApi client=api;
        pairDialog.setOnShowListener(d->{Button confirm=pairDialog.getButton(AlertDialog.BUTTON_POSITIVE);confirm.setId(R.id.pair_confirm);confirm.setOnClickListener(v->{String value=code.getText().toString();if(!value.matches("[0-9]{6}")){code.setError("Enter all six digits");return;}confirm.setEnabled(false);status.setText("Pairing…");int version=generation;
            execute(()->{try{LanApi.Session paired=client.pair(endpoint,info,value);main.post(()->{if(stale(version))return;session=paired;beginProgressSync(client,paired);getPreferences(MODE_PRIVATE).edit().putString("desktopUrl",endpoint.origin).apply();manual.setText(endpoint.origin);pairDialog.dismiss();pairDialog=null;playlistTitle.setText(info.name);status.setText("Connected · playlist updates automatically");refresh(true);});}catch(Exception error){main.post(()->{if(stale(version))return;confirm.setEnabled(true);status.setText(safe(error));if(error instanceof LanApi.ApiException&&((LanApi.ApiException)error).status==401)code.setError("Pairing could not finish. Reconnect or check the current desktop code.");else code.setError(null);});}});
        });});pairDialog.show();
    }
    private void cancelPair(){generation++;ScopedWifiConnections<Network> scope=wifiConnections;if(scope!=null)scope.close();pairDialog=null;connecting=false;connect.setEnabled(true);status.setText("Pairing cancelled. Your media remains private.");}
    private void refresh(boolean explicit){
        if(session==null){if(explicit)status.setText("Connect and pair with your desktop first.");return;}if(polling)return;polling=true;LanApi.Session active=session;int version=generation;if(explicit)status.setText("Refreshing playlist…");
        LanApi client=api;
        execute(()->{try{LanApi.Info info=client.info(active.endpoint);if(!info.id.equals(active.host.id))throw new LanApi.ApiException(401,"Sharing restarted. Pair again with the new desktop code.");LanApi.Playlist snapshot=client.playlistState(active);List<LanApi.Item> playlist=snapshot.items;main.post(()->{if(stale(version))return;polling=false;items=playlist;playlistAdapter.notifyDataSetChanged();updateNavigation();refreshProgressDetails();playlistInfo.setText(snapshot.summary());playbackPlaylistSummary.setText(snapshot.summary()+" · playback stays open");status.setText("Connected · "+snapshot.summary());
            if(current!=null&&playlist.stream().noneMatch(item->item.id.equals(current.id))){lastError="This item was removed from the desktop playlist.";stopPlayback();leavePlayer();status.setText(lastError);}
        });}catch(Exception error){main.post(()->{if(stale(version))return;polling=false;status.setText(safe(error));if(error instanceof LanApi.ApiException&&((LanApi.ApiException)error).status==401){disconnect(false);status.setText("Sharing restarted or pairing expired. Connect again to pair.");}});}});
    }
    private void startMedia(LanApi.Item item){
        if(session==null){status.setText("Pair again before streaming.");return;}if(retiringGraphs>=3){playerStatus.setText("Finishing the previous stream. Please retry in a moment.");return;}stopPlayback();graphStartedAt=android.os.SystemClock.uptimeMillis();softwarePlayback=softwareItems.contains(item.id);videoWasDisplayed=false;hadVideoTracks=false;videoOutputWasCreated=false;userPaused=false;videoOutputs=0;nativeStartedAt=0;playRequestedAt=0;nativePlayingSeen=false;nativeSurfacesReady=false;surfaceRebindPending=false;surfaceRestartPending=false;surfaceRestartStarted=false;surfaceRecoveryNeedsResume=false;surfaceRebinds=0;surfaceRebindAttempts=0;surfaceCreatedEvents=0;surfaceDestroyedEvents=0;lastOpeningDiagnostic=0;current=item;lastError=null;lastStats=null;initialResume=initialResumeFor(item);autoResumeApplied=false;nativeTimeEvents=0;lastNativeTimeEvent=-1;nativeBufferingEvents=0;nativeBufferingPercent=-1;home.setVisibility(View.GONE);playback.setVisibility(View.VISIBLE);if(playlistOpen)setPlaylistOpen(false);playerTitle.setText(item.title);updateNavigation();refreshProgressDetails();setPlayAction("Pause");playerStatus.setText("Opening LAN stream…");getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        try{
            if(engine==null)engine=new LibVLC(this,new ArrayList<>(java.util.Arrays.asList(BuildConfig.DEBUG&&nativeDiagnostics?"--verbose=2":"--quiet","--no-lua","--no-video-title-show","--no-osd","--no-metadata-network-access","--stats")));
            if(!enginePrepared){preparePlayback(item);return;}
        }catch(Exception|LinkageError error){lastError="Playback could not start on this device. Return to the playlist and reconnect.";playerStatus.setText(lastError);stopPlayback();return;}
        openPreparedMedia(item);
    }
    private void preparePlayback(LanApi.Item item){
        pendingPreparationItem=item;pendingPreparationConnection=generation;pendingPreparationMedia=mediaGeneration;graphStartedAt=0;playerStatus.setText("Preparing playback for first use…");
        if(enginePreparing)return;
        enginePreparing=true;preparationStartedAt=android.os.SystemClock.uptimeMillis();LibVLC preparingEngine=engine;preparingEngine.retain();
        android.util.Log.i("LumaPlayback","preparation-started: cacheFiles="+fontCacheFiles()+", graph="+videoGraphSequence);
        try{FONT_PREPARATION.execute(()->{
            boolean verified=false,failed=false;long began=android.os.SystemClock.uptimeMillis();
            try{verified=preparingEngine.buildFontCache();}catch(RuntimeException|LinkageError error){failed=true;}
            final boolean cacheValid=verified,preparationFailed=failed;final long nativeDuration=android.os.SystemClock.uptimeMillis()-began;
            main.post(()->{
                try{
                    enginePreparing=false;enginePrepared=!preparationFailed;fontCacheVerified=cacheValid;preparationDurationMs=android.os.SystemClock.uptimeMillis()-preparationStartedAt;
                    android.util.Log.i("LumaPlayback","preparation-completed: elapsedMs="+preparationDurationMs+", nativeMs="+nativeDuration+", cacheVerified="+cacheValid+", failed="+preparationFailed+", cacheFiles="+fontCacheFiles());
                    LanApi.Item pending=pendingPreparationItem;pendingPreparationItem=null;
                    if(destroyed||!foreground||engine!=preparingEngine||session==null||pending==null||pending!=current||generation!=pendingPreparationConnection||mediaGeneration!=pendingPreparationMedia)return;
                    if(preparationFailed){lastError="Could not prepare playback on this device. Return to the playlist and retry.";playerStatus.setText(lastError);return;}
                    // A false cache-validity result can mean an optional font
                    // directory is absent. Completion is distinct from disk
                    // validation; never loop or call this a decoder failure.
                    if(userPaused){setPlayAction("Play");playerStatus.setText("Paused");return;}
                    openPreparedMedia(pending);
                }finally{preparingEngine.release();releaseEngineWhenIdle();}
            });
        });}catch(java.util.concurrent.RejectedExecutionException error){enginePreparing=false;pendingPreparationItem=null;preparingEngine.release();lastError="Playback preparation is busy. Please retry in a moment.";playerStatus.setText(lastError);releaseEngineWhenIdle();}
    }
    private int fontCacheFiles(){java.io.File directory=new java.io.File(getDir("vlc",MODE_PRIVATE),".cache/fontconfig");java.io.File[] files=directory.listFiles();if(files==null)return 0;int count=0;for(java.io.File file:files)if(file.isFile()&&file.getName().contains(".cache-"))count++;return count;}
    private void openPreparedMedia(LanApi.Item item){
        if(!enginePrepared||enginePreparing||destroyed||!foreground||current!=item||session==null||userPaused)return;
        graphStartedAt=android.os.SystemClock.uptimeMillis();playerStatus.setText("Opening LAN stream…");
        try{
            int version=generation,clip=mediaGeneration;LanApi.Session active=session;
            LanApi.Connections mediaConnections=api.connections();
            relay=new StreamRelay(active,item,mediaConnections,failure->main.post(()->{if(stale(version)||clip!=mediaGeneration||current!=item)return;lastError=failure==StreamRelay.Failure.AUTH?"Pairing expired. Return to the playlist and pair again.":failure==StreamRelay.Failure.REMOVED?"This item was removed from the desktop.":failure==StreamRelay.Failure.UNSAFE_RESPONSE?"The desktop returned an unsafe stream response.":failure==StreamRelay.Failure.RECONNECT?"Wi-Fi or the desktop sharing session changed. Reconnect to the desktop's Wi-Fi and retry, or return to the playlist to pair again.":"Desktop offline. Keep sharing enabled and retry.";playerStatus.setText(lastError);userPaused=true;if(player!=null)player.pause();}));
            // Each native player owns its own surface tree. A retired helper
            // must never receive destroy/layout callbacks for the next player.
            videoContainer.removeView(video);video=new VLCVideoLayout(this);videoContainer.addView(video,0,new FrameLayout.LayoutParams(-1,-1));videoGraphSequence++;
            player=new MediaPlayer(engine);MediaPlayer activePlayer=player;player.setVolume(volume.getProgress());
            player.setEventListener(event->main.post(()->{
                if(destroyed||player!=activePlayer||current!=item)return;
                if(event.type==MediaPlayer.Event.Playing){nativePlayingSeen=true;if(nativeSurfacesReady&&activePlayer.isPlaying()){surfaceRecoveryNeedsResume=false;surfaceRestartStarted=false;}if(userPaused){activePlayer.pause();setPlayAction("Play");if(lastError==null)playerStatus.setText("Paused");return;}nativeStartedAt=android.os.SystemClock.uptimeMillis();hadVideoTracks|=hasKnownVideoTrack();applyResume();playerStatus.setText((softwarePlayback?"Software playback · ":"Streaming · ")+active.host.name);setPlayAction("Pause");logPlayback("playing");}
                else if(event.type==MediaPlayer.Event.Vout){videoOutputs=event.getVoutCount();videoOutputWasCreated|=videoOutputs>0;if(videoOutputs>0&&nativeSurfacesReady)surfaceRebindAttempts=0;logPlayback("video-output");}
                else if(event.type==MediaPlayer.Event.Paused){if(!activePlayer.isPlaying()){setPlayAction("Play");if(lastError==null)playerStatus.setText("Paused");}}
                else if(event.type==MediaPlayer.Event.TimeChanged){nativeTimeEvents++;lastNativeTimeEvent=event.getTimeChanged();}
                else if(event.type==MediaPlayer.Event.Buffering){nativeBufferingPercent=event.getBuffering();nativeBufferingEvents++;if(!userPaused&&lastError==null&&event.getBuffering()<100)playerStatus.setText("Buffering "+Math.round(event.getBuffering())+"% · keep the desktop awake");}
                else if(event.type==MediaPlayer.Event.EncounteredError){logPlayback("native-input-error");if(surfaceRebindPending||surfaceRestartPending)return;if(retrySoftware())return;lastError=lastError==null?"Could not play this stream. Check that the desktop is awake and sharing, then retry.":lastError;playerStatus.setText(lastError);setPlayAction("Play");}
                else if(event.type==MediaPlayer.Event.EndReached){lastStats=stats();if(surfaceRebindPending||surfaceRestartPending||surfaceRecoveryNeedsResume)return;if(!videoWasDisplayed&&retrySoftware())return;logPlayback("ended");playerStatus.setText("Finished · choose another item from your playlist");setPlayAction("Play");}
            }));
            attachWhenReady(activePlayer,video,item,relay.playbackUrl(),android.os.SystemClock.uptimeMillis());
        }catch(Exception|LinkageError error){lastError="Playback could not start on this device. Return to the playlist and reconnect.";playerStatus.setText(lastError);stopPlayback();}
    }
    private void attachWhenReady(MediaPlayer activePlayer,VLCVideoLayout activeVideo,LanApi.Item item,String localUrl,long waitingSince){
        activeVideo.postOnAnimation(()->{
            if(destroyed||player!=activePlayer||video!=activeVideo||current!=item||!foreground)return;
            if(!activeVideo.isAttachedToWindow()||!activeVideo.isShown()||activeVideo.getWidth()<=0||activeVideo.getHeight()<=0){
                if(android.os.SystemClock.uptimeMillis()-waitingSince<5000){attachWhenReady(activePlayer,activeVideo,item,localUrl,waitingSince);return;}
                lastError="The video view could not become ready. Return to the playlist and retry.";playerStatus.setText(lastError);logPlayback("surface-timeout");stopPlayback();return;
            }
            try{
                Media media=new Media(engine,Uri.parse(localUrl));media.setHWDecoderEnabled(!softwarePlayback,false);
                media.addOption(":network-caching=1200");media.addOption(":http-reconnect");
                if(PlaybackPolicy.useAvformat(item.mimeType))media.addOption(":demux=avformat");
                activePlayer.setMedia(media);media.release();
                if(BuildConfig.DEBUG&&debugSkipSurfaceOnce&&!softwarePlayback){debugSkipSurfaceOnce=false;logPlayback("test-missing-native-output");playRequestedAt=android.os.SystemClock.uptimeMillis();activePlayer.play();return;}
                surfaceCallback=new IVLCVout.Callback(){
                    public void onSurfacesCreated(IVLCVout vout){if(player!=activePlayer||video!=activeVideo||destroyed)return;surfaceCreatedEvents++;nativeSurfacesReady=validVideoSurface(activeVideo);surfaceRebindPending=false;logPlayback("native-surfaces-created");main.post(()->{startAfterSurfaces(activePlayer,activeVideo,item,waitingSince);resumeAfterSurfaceRecovery(activePlayer,activeVideo);});}
                    public void onSurfacesDestroyed(IVLCVout vout){if(player!=activePlayer||video!=activeVideo||destroyed)return;surfaceDestroyedEvents++;surfaceRecoveryTime=Math.max(0,activePlayer.getTime());surfaceRecoveryNeedsResume=playRequestedAt>0;nativeSurfacesReady=false;logPlayback("native-surfaces-destroyed");if(foreground&&!surfaceRebindPending){surfaceRebindPending=true;main.post(()->reattachLostSurfaces(activePlayer,activeVideo,item,android.os.SystemClock.uptimeMillis()));}}
                };
                activePlayer.getVLCVout().addCallback(surfaceCallback);activePlayer.attachViews(activeVideo,null,true,false);logPlayback("views-attached-waiting-for-surface");
                startAfterSurfaces(activePlayer,activeVideo,item,waitingSince);
            }catch(RuntimeException error){lastError="Playback could not initialize its video view. Return to the playlist and retry.";playerStatus.setText(lastError);logPlayback("surface-error");stopPlayback();}
        });
    }
    private static boolean validVideoSurface(VLCVideoLayout layout){View value=layout.findViewById(org.videolan.R.id.surface_video);return value instanceof SurfaceView&&((SurfaceView)value).getHolder().getSurface().isValid();}
    private void reattachLostSurfaces(MediaPlayer activePlayer,VLCVideoLayout activeVideo,LanApi.Item item,long waitingSince){
        if(destroyed||player!=activePlayer||video!=activeVideo||current!=item||!foreground||!surfaceRebindPending)return;
        if(activePlayer.getVLCVout().areViewsAttached()){surfaceRebindPending=false;return;}
        if(!videoSurfaceReady()||!validVideoSurface(activeVideo)){
            if(android.os.SystemClock.uptimeMillis()-waitingSince<5000){main.postDelayed(()->reattachLostSurfaces(activePlayer,activeVideo,item,waitingSince),50);return;}
            lastError="The video surface was lost. Retry playback to create a new view.";playerStatus.setText(lastError);stopPlayback();return;
        }
        if(surfaceRebindAttempts>=2){lastError="This device repeatedly lost its video surface. Retry playback.";playerStatus.setText(lastError);stopPlayback();return;}
        surfaceRebinds++;surfaceRebindAttempts++;surfaceRebindPending=false;
        try{activePlayer.detachViews();activePlayer.attachViews(activeVideo,null,true,false);logPlayback("native-surfaces-reattached");}
        catch(RuntimeException error){lastError="The video surface could not be restored. Retry playback.";playerStatus.setText(lastError);stopPlayback();}
    }
    private void resumeAfterSurfaceRecovery(MediaPlayer activePlayer,VLCVideoLayout activeVideo){
        if(destroyed||player!=activePlayer||video!=activeVideo||!foreground||userPaused||!nativeSurfacesReady||surfaceRebinds==0||surfaceRestartPending||surfaceRestartStarted||lastError!=null)return;
        // Disabling the old video track may finish asynchronously even after a
        // Playing/Paused observation. Stop that input before restarting the same
        // player on its restored valid surface, preserving the user's position.
        surfaceRestartPending=true;activePlayer.retain();final long began=android.os.SystemClock.uptimeMillis();playerStatus.setText("Restoring video…");logPlayback("surface-restart-requested");
        retiredPlayers.execute(()->{try{activePlayer.stop();}catch(RuntimeException ignored){}finally{main.post(()->{
            try{if(destroyed||player!=activePlayer||video!=activeVideo)return;surfaceRestartPending=false;if(!foreground||userPaused||!nativeSurfacesReady||!validVideoSurface(activeVideo))return;
                initialResume=surfaceRecoveryTime;autoResumeApplied=false;nativePlayingSeen=false;nativeStartedAt=0;videoWasDisplayed=false;videoOutputWasCreated=false;videoOutputs=0;lastStats=null;surfaceRestartStarted=true;playRequestedAt=android.os.SystemClock.uptimeMillis();logPlayback("resume-after-surface-recovery-stopMs="+(playRequestedAt-began));activePlayer.play();
            }finally{activePlayer.release();}
        });}});
    }
    private void startAfterSurfaces(MediaPlayer activePlayer,VLCVideoLayout activeVideo,LanApi.Item item,long waitingSince){
        if(destroyed||player!=activePlayer||video!=activeVideo||current!=item||!foreground||playRequestedAt>0)return;
        if(nativeSurfacesReady&&validVideoSurface(activeVideo)&&activePlayer.getVLCVout().areViewsAttached()){
            if(userPaused){logPlayback("valid-surface-awaiting-play-intent");return;}
            playRequestedAt=android.os.SystemClock.uptimeMillis();logPlayback("native-surface-valid-start");activePlayer.play();return;
        }
        if(android.os.SystemClock.uptimeMillis()-waitingSince>=5000){lastError="The device could not create a valid video surface. Return to the playlist and retry.";playerStatus.setText(lastError);logPlayback("native-surface-timeout");stopPlayback();return;}
        main.postDelayed(()->startAfterSurfaces(activePlayer,activeVideo,item,waitingSince),50);
    }
    private boolean hasKnownVideoTrack(){
        if(player==null)return false;MediaPlayer.TrackDescription[] tracks=player.getVideoTracks();if(tracks==null)return false;
        for(MediaPlayer.TrackDescription track:tracks)if(track.id>=0)return true;return false;
    }
    private boolean videoSurfaceReady(){return video.isAttachedToWindow()&&video.isShown()&&video.getWidth()>0&&video.getHeight()>0;}
    private boolean retrySoftware(){
        if(player==null||current==null||session==null||softwarePlayback||lastError!=null||!foreground||userPaused||videoWasDisplayed||videoOutputWasCreated||!(hadVideoTracks||hasKnownVideoTrack()))return false;
        LanApi.Item item=current;logPlayback("retry-software-once");softwareItems.add(item.id);localResume.put(item.id,initialResume);startMedia(item);return true;
    }
    private String playbackDiagnostics(){
        Media.Stats value=stats();return "preparing="+enginePreparing+", prepared="+enginePrepared+", cacheVerified="+fontCacheVerified+", preparationMs="+(enginePreparing?android.os.SystemClock.uptimeMillis()-preparationStartedAt:preparationDurationMs)+", graph="+videoGraphSequence+", graphAgeMs="+(graphStartedAt==0?0:android.os.SystemClock.uptimeMillis()-graphStartedAt)+", retiring="+retiringGraphs+", lastRetirementMs="+lastRetirementMs+", software="+softwarePlayback+", nativeState="+(player==null?-1:player.getPlayerState())+", nativeTimeMs="+(player==null?-1:player.getTime())+", position="+(player==null?-1:player.getPosition())+", lengthMs="+(player==null?-1:player.getLength())+", timeEvents="+nativeTimeEvents+", lastTimeEventMs="+lastNativeTimeEvent+", bufferingPercent="+nativeBufferingPercent+", bufferingEvents="+nativeBufferingEvents+", openingAgeMs="+(playRequestedAt==0?0:android.os.SystemClock.uptimeMillis()-playRequestedAt)+", playingEventSeen="+nativePlayingSeen+", resumeApplied="+autoResumeApplied+", forwardedBytes="+(relay==null?0:relay.forwardedBytes())+", relay={"+(relay==null?"none":relay.diagnostics())+"}, surfaceReady="+videoSurfaceReady()+", nativeSurfaceReady="+nativeSurfacesReady+", holderValid="+validVideoSurface(video)+", surfaceCreated="+surfaceCreatedEvents+", surfaceDestroyed="+surfaceDestroyedEvents+", rebinds="+surfaceRebinds+", rebindPending="+surfaceRebindPending+", restartPending="+surfaceRestartPending+", recoveryNeedsResume="+surfaceRecoveryNeedsResume+", size="+video.getWidth()+"x"+video.getHeight()+", videoTracks="+(player==null?-1:player.getVideoTracksCount())+", outputs="+videoOutputs+", outputCreated="+videoOutputWasCreated+", decodedVideo="+(value==null?0:value.decodedVideo)+", displayed="+(value==null?0:value.displayedPictures)+", decodedAudio="+(value==null?0:value.decodedAudio)+", playing="+(player!=null&&player.isPlaying());
    }
    private void logPlayback(String reason){ScopedWifiConnections<Network> scope=wifiConnections;android.util.Log.i("LumaPlayback",reason+": "+playbackDiagnostics()+", routeVerifications="+(scope==null?0:scope.verificationCount())+", "+networkDiagnostics(currentWifiRoute()));}
    private void updatePlayer(){
        if(player==null||current==null)return;applyResume();long length=player.getLength(),time=Math.max(0,player.getTime());
        clock.setText(time(time)+" / "+time(length));if(!dragging&&gestureAxis!=PlaybackGestureMath.Axis.HORIZONTAL&&length>0)seek.setProgress((int)Math.min(1000,time*1000/length));seek.setEnabled(player.isSeekable());
        if(player.getPlayerState()==Media.State.Ended&&length>0){time=length;localResume.put(current.id,length);completedOnPhone.add(current.id);}else if(time>=0&&nativePlayingSeen&&autoResumeApplied){localResume.put(current.id,time);if(player.isPlaying())completedOnPhone.remove(current.id);}sharePlaybackProgress(false);refreshProgressDetails();if(playlistOpen)playlistAdapter.notifyDataSetChanged();Media.Stats value=stats();if(value!=null){lastStats=value;if(value.displayedPictures>0)videoWasDisplayed=true;}
        hadVideoTracks|=hasKnownVideoTrack();
        long now=android.os.SystemClock.uptimeMillis(),openingAge=playRequestedAt==0?0:now-playRequestedAt;
        if(!nativePlayingSeen&&playRequestedAt>0&&now-lastOpeningDiagnostic>2000){lastOpeningDiagnostic=now;logPlayback("waiting-for-playing");}
        // Let the relay's10s read timeout report network errors first. Only a
        // proven video input with delivered bytes and no output gets one retry.
        if(playRequestedAt>0&&!nativePlayingSeen&&!userPaused&&hadVideoTracks&&!videoOutputWasCreated&&!videoWasDisplayed&&videoSurfaceReady()&&lastError==null&&relay!=null&&relay.forwardedBytes()>0&&openingAge>12000){
            if(retrySoftware())return;
            if(softwarePlayback&&openingAge>15000){lastError="The video decoder could not start on this device. Retry or try another device.";playerStatus.setText(lastError);logPlayback("opening-unavailable");userPaused=true;player.pause();}
        }
        if(nativeStartedAt>0&&player.isPlaying()&&!videoWasDisplayed&&videoOutputs==0&&(hadVideoTracks||hasKnownVideoTrack())&&android.os.SystemClock.uptimeMillis()-nativeStartedAt>3500){
            if(retrySoftware())return;
            if(softwarePlayback&&lastError==null){lastError="This device could not create video output, including software playback. Retry or try another device.";playerStatus.setText(lastError);logPlayback("video-unavailable");userPaused=true;player.pause();}
        }
    }
    private void applyResume(){
        if(player==null||!nativePlayingSeen||userPaused||autoResumeApplied)return;
        if(initialResume<=0){autoResumeApplied=true;return;}
        // Starting a seek while the first decoder/output graph is still being
        // created can strand slow MediaCodec implementations in Opening.
        if(android.os.SystemClock.uptimeMillis()-nativeStartedAt<250)return;
        if((hadVideoTracks||hasKnownVideoTrack())&&!videoWasDisplayed)return;
        if(player.isSeekable()&&player.getLength()>0){logPlayback("resume-before-seek");autoResumeApplied=true;player.setTime(Math.min(initialResume,Math.max(0,player.getLength()-1000)),false);logPlayback("resume-after-playing");}
    }
    private Media.Stats stats(){if(player==null)return lastStats;IMedia media=player.getMedia();if(media==null)return lastStats;try{Media.Stats value=media.getStats();if(value!=null&&(value.decodedVideo>0||value.decodedAudio>0)){lastStats=value;if(value.displayedPictures>0)videoWasDisplayed=true;}return lastStats==null?value:lastStats;}finally{media.release();}}
    private void togglePlay(){
        if(player==null&&enginePreparing&&current!=null){userPaused=!userPaused;setPlayAction(userPaused?"Play":"Pause");playerStatus.setText(userPaused?"Paused":"Preparing playback for first use…");return;}
        if(player==null){if(current!=null)startMedia(current);return;}
        if(lastError!=null){startMedia(current);return;}
        if(surfaceRebindPending||surfaceRestartPending||surfaceRecoveryNeedsResume){userPaused=!userPaused;setPlayAction(userPaused?"Play":"Pause");playerStatus.setText(userPaused?"Paused":"Restoring video…");if(userPaused){if(player.isPlaying())player.pause();}else if(!surfaceRebindPending&&!surfaceRestartPending)resumeAfterSurfaceRecovery(player,video);return;}
        int nativeState=player.getPlayerState();
        if(nativeState==Media.State.Ended||nativeState==Media.State.Stopped){localResume.put(current.id,0L);startMedia(current);return;}
        if(!userPaused){userPaused=true;setPlayAction("Play");playerStatus.setText("Paused");if(nativePlayingSeen||player.isPlaying())player.pause();}
        else{userPaused=false;setPlayAction("Pause");playerStatus.setText("Resuming stream…");if(nativeSurfacesReady&&validVideoSurface(video)){if(!nativePlayingSeen)playRequestedAt=android.os.SystemClock.uptimeMillis();player.play();}}
    }
    private void jump(long amount){if(player!=null&&player.isSeekable()){autoResumeApplied=true;player.setTime(Math.max(0,Math.min(player.getLength(),player.getTime()+amount)),false);checkpointProgressSoon();}}
    private void stopPlayback(){
        sharePlaybackProgress(true);
        mediaGeneration++;
        pendingPreparationItem=null;
        final MediaPlayer old=player;player=null;final StreamRelay oldRelay=relay;relay=null;final IVLCVout.Callback oldCallback=surfaceCallback;surfaceCallback=null;nativeSurfacesReady=false;
        final int graph=videoGraphSequence;final long began=android.os.SystemClock.uptimeMillis();
        if(oldRelay!=null)oldRelay.cancelLocalConnections();
        if(old!=null){
            old.setEventListener(null);if(oldCallback!=null)old.getVLCVout().removeCallback(oldCallback);
            try{if(old.isPlaying())old.pause();}catch(RuntimeException ignored){}
            try{old.detachViews();}catch(RuntimeException ignored){}
            videoContainer.removeView(video);retiringGraphs++;
            android.util.Log.i("LumaPlayback","retire-detached: graph="+graph+", elapsedMs="+(android.os.SystemClock.uptimeMillis()-began));
            retiredPlayers.execute(()->{
                try{old.stop();}catch(RuntimeException ignored){}
                android.util.Log.i("LumaPlayback","retire-native-stopped: graph="+graph+", elapsedMs="+(android.os.SystemClock.uptimeMillis()-began));
                try{if(oldRelay!=null)oldRelay.close();}catch(RuntimeException ignored){}
                android.util.Log.i("LumaPlayback","retire-upstream-closed: graph="+graph+", elapsedMs="+(android.os.SystemClock.uptimeMillis()-began));
                main.post(()->{try{old.release();}finally{retiringGraphs--;lastRetirementMs=android.os.SystemClock.uptimeMillis()-began;android.util.Log.i("LumaPlayback","retire-released: graph="+graph+", elapsedMs="+lastRetirementMs);releaseEngineWhenIdle();}});
            });
        }else if(oldRelay!=null){retiredPlayers.execute(oldRelay::close);}
        getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
    }
    private void releaseEngineWhenIdle(){if(destroyed&&!enginePreparing&&retiringGraphs==0&&engine!=null){engine.release();engine=null;}}
    private static Media.Stats statsOf(MediaPlayer player){IMedia media=player.getMedia();if(media==null)return null;try{return media.getStats();}finally{media.release();}}
    private void leavePlayer(){updatePlayer();stopPlayback();if(playlistOpen)setPlaylistOpen(false);current=null;playback.setVisibility(View.GONE);home.setVisibility(View.VISIBLE);setFull(false);restoreBrightness();}
    private void setChromeVisible(boolean value){chromeVisible=value;playerHeader.setVisibility(value?View.VISIBLE:View.GONE);playerControls.setVisibility(value?View.VISIBLE:View.GONE);centerTransport.setVisibility(value?View.VISIBLE:View.GONE);videoTapTarget.setContentDescription(value?"Hide playback controls":"Show playback controls");videoTapTarget.setImportantForAccessibility(full?View.IMPORTANT_FOR_ACCESSIBILITY_YES:View.IMPORTANT_FOR_ACCESSIBILITY_NO);}
    private void applySystemBars(){if(Build.VERSION.SDK_INT>=30){WindowInsetsController controller=getWindow().getInsetsController();if(controller!=null){if(full){controller.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);controller.hide(WindowInsets.Type.systemBars());}else controller.show(WindowInsets.Type.systemBars());}}else getWindow().getDecorView().setSystemUiVisibility(full?View.SYSTEM_UI_FLAG_FULLSCREEN|View.SYSTEM_UI_FLAG_HIDE_NAVIGATION|View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY:0);}
    // Rotation/focus transitions can restore bars after the initial hide request.
    // Reapply once after geometry settles, rather than suppressing user swipes.
    private void scheduleFullscreenBars(){main.removeCallbacks(settleFullscreenBars);if(full)main.postDelayed(settleFullscreenBars,500);}
    private void setFull(boolean value){full=value;fullButton.setAction(value?PlayerIcon.Kind.EXIT_FULLSCREEN:PlayerIcon.Kind.FULLSCREEN,value?"Exit full screen":"Full screen");setChromeVisible(!value);applySystemBars();root.requestApplyInsets();scheduleFullscreenBars();setRequestedOrientation(value?ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE:ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED);}
    private void disconnect(boolean clearUrl){endProgressSync();generation++;ScopedWifiConnections<Network> scope=wifiConnections;wifiConnections=null;if(scope!=null)scope.close();polling=false;connecting=false;session=null;api=null;localResume.clear();completedOnPhone.clear();softwareItems.clear();items=Collections.emptyList();if(playlistInfo!=null)playlistInfo.setText("Pair with a desktop to see its current playlist.");if(list!=null)((BaseAdapter)list.getAdapter()).notifyDataSetChanged();if(pairDialog!=null){pairDialog.dismiss();pairDialog=null;}if(current!=null)leavePlayer();if(clearUrl)getPreferences(MODE_PRIVATE).edit().remove("desktopUrl").apply();}
    private void execute(Runnable action){try{work.execute(action);}catch(java.util.concurrent.RejectedExecutionException error){status.setText("Please wait for the current desktop request to finish.");polling=false;connecting=false;connect.setEnabled(true);}}
    private boolean stale(int version){return destroyed||version!=generation;}
    private String permission(){return Build.VERSION.SDK_INT>=37?Manifest.permission.ACCESS_LOCAL_NETWORK:Build.VERSION.SDK_INT==36?Manifest.permission.NEARBY_WIFI_DEVICES:null;}
    private boolean hasPermission(){String permission=permission();return permission==null||checkSelfPermission(permission)==PackageManager.PERMISSION_GRANTED;}
    private void ensurePermission(){if(hasPermission()){discovery.start();return;}if(permissionAsked)return;permissionAsked=true;requestPermissions(new String[]{permission()},100);}
    @Override public void onRequestPermissionsResult(int request,String[] permissions,int[] results){super.onRequestPermissionsResult(request,permissions,results);if(request==100){if(hasPermission())discovery.start();else{status.setText("Local-network access is needed to discover and stream. Tap Find to try again, or allow it in Settings.");}}}
    @Override protected void onResume(){super.onResume();foreground=true;ensurePermission();main.post(pulse);main.post(poll);if(wasPlaying&&current!=null){wasPlaying=false;userPaused=false;if(player!=null){if(nativeSurfacesReady&&validVideoSurface(video))player.play();}else startMedia(current);}else if(current!=null&&player==null){setPlayAction("Play");playerStatus.setText("Paused · tap Play to resume streaming");}}
    @Override protected void onPause(){sharePlaybackProgress(true);foreground=false;main.removeCallbacks(pulse);main.removeCallbacks(poll);main.removeCallbacks(settleFullscreenBars);main.removeCallbacks(hideChrome);main.removeCallbacks(hideGestureHud);discovery.stop();if(player!=null){int observed=player.getPlayerState();wasPlaying=!userPaused&&lastError==null&&observed!=Media.State.Ended&&observed!=Media.State.Error&&observed!=Media.State.Stopped;userPaused=true;if(nativePlayingSeen||player.isPlaying())player.pause();}else if(enginePreparing&&current!=null){wasPlaying=!userPaused;userPaused=true;}super.onPause();}
    @Override protected void onStop(){if(player!=null&&current!=null){updatePlayer();stopPlayback();}super.onStop();}
    @Override protected void onDestroy(){endProgressSync();destroyed=true;generation++;ScopedWifiConnections<Network> scope=wifiConnections;wifiConnections=null;if(scope!=null)scope.close();discovery.stop();stopPlayback();releaseEngineWhenIdle();retiredPlayers.shutdown();session=null;work.shutdownNow();super.onDestroy();}
    @Override public void onConfigurationChanged(Configuration config){super.onConfigurationChanged(config);layoutPlaylist();scheduleFullscreenBars();MediaPlayer active=player;if(active!=null)main.post(()->{if(player==active)active.updateVideoSurfaces();});}
    @Override public void onWindowFocusChanged(boolean hasFocus){super.onWindowFocusChanged(hasFocus);windowFocused=hasFocus;if(hasFocus)scheduleFullscreenBars();}
    // Modern back gestures use the native API33 dispatcher registered in onCreate.
    @SuppressLint("GestureBackNavigation")
    @Override public void onBackPressed(){navigateBack();}
    private void navigateBack(){if(playlistOpen)setPlaylistOpen(false);else if(full)setFull(false);else if(current!=null)leavePlayer();else finish();}
    private void about(){new AlertDialog.Builder(this).setTitle("Luma · by sdas").setMessage("Free Android preview. Stream only from your trusted local desktop. No cloud relay, analytics, or saved media download.\n\nPlayback: "+(BuildConfig.LUMA_CUSTOM_RUNTIME?"locally built libVLC with filtered Android3.7.7 bindings":"official VideoLAN Android3.7.7 bindings")+". Third-party terms apply. "+(BuildConfig.LUMA_APPROVED_RUNTIME?"This build uses the repository's reviewed ARM64 component. License notices are included in the app assets; matching library and application sources accompany distribution.":"This development runtime is pending the repository's component/source audit.")+"\n\n"+(engine==null?"Engine starts when you play.":"libVLC "+LibVLC.version()+"\n"+LibVLC.changeset())).setPositiveButton("Done",null).setNeutralButton("Disconnect",(d,w)->{disconnect(true);status.setText("Disconnected. Pair again to stream.");}).setNegativeButton("Network settings",(d,w)->startActivity(new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,Uri.parse("package:"+getPackageName())))).show();}
    private static String safe(Exception error){return error instanceof LanApi.ApiException?error.getMessage():"Could not connect. Check the local address and keep desktop sharing enabled.";}
    private int dp(int value){return Math.round(value*getResources().getDisplayMetrics().density);}
    private TextView text(String value,int size,int color){TextView v=new TextView(this);v.setText(value);v.setTextSize(size);v.setTextColor(color);return v;}
    private LinearLayout column(){LinearLayout v=new LinearLayout(this);v.setOrientation(LinearLayout.VERTICAL);return v;}
    private LinearLayout row(){LinearLayout v=new LinearLayout(this);v.setOrientation(LinearLayout.HORIZONTAL);v.setGravity(Gravity.CENTER_VERTICAL);return v;}
    private LinearLayout card(){LinearLayout v=column();v.setPadding(dp(16),dp(16),dp(16),dp(16));v.setBackground(background(PANEL,16));return v;}
    private GradientDrawable background(int color,int radius){GradientDrawable d=new GradientDrawable();d.setColor(color);d.setCornerRadius(dp(radius));d.setStroke(dp(1),LINE);return d;}
    private Button button(String label,Runnable action,boolean primary){Button b=new Button(this);b.setText(label);b.setAllCaps(false);b.setTextSize(13);b.setTextColor(primary?BG:TEXT);b.setBackground(background(primary?ACCENT:PANEL,12));b.setPadding(dp(14),dp(6),dp(14),dp(6));b.setMinHeight(dp(44));b.setOnClickListener(v->action.run());return b;}
    private LinearLayout.LayoutParams margin(int l,int t,int r,int b){LinearLayout.LayoutParams p=new LinearLayout.LayoutParams(-1,-2);p.setMargins(dp(l),dp(t),dp(r),dp(b));return p;}
    private void addText(LinearLayout parent,String value,int size,int color,int top){TextView v=text(value,size,color);v.setLineSpacing(dp(3),1);parent.addView(v,margin(0,top,0,0));}
    private static String time(long milliseconds){long seconds=Math.max(0,milliseconds/1000);return seconds>=3600?String.format(java.util.Locale.ROOT,"%d:%02d:%02d",seconds/3600,seconds/60%60,seconds%60):String.format(java.util.Locale.ROOT,"%d:%02d",seconds/60,seconds%60);}
    private final class PlaylistAdapter extends BaseAdapter {
        public int getCount(){return items.size();}public Object getItem(int position){return items.get(position);}public long getItemId(int position){return position;}
        public View getView(int position,View convert,ViewGroup parent){
            LanApi.Item item=items.get(position);PlaylistRow row=convert!=null&&convert.getTag() instanceof PlaylistRow?(PlaylistRow)convert.getTag():new PlaylistRow();
            boolean active=current!=null&&current.id.equals(item.id);row.title.setText(item.title);row.now.setVisibility(active?View.VISIBLE:View.GONE);row.root.setBackground(background(active?0xff293143:0xff202634,16));
            double duration=item.durationSeconds,phoneDuration=active&&player!=null&&player.getLength()>0?player.getLength()/1000.0:item.mobileDurationSeconds>0?item.mobileDurationSeconds:duration,computer=computerProgress(item),phone=phoneProgress(item);
            row.computer.setText("Computer · "+progressText(computer,duration,item.computerCompleted));row.phone.setText("Phone · "+progressText(phone,phoneDuration,active&&player!=null&&player.getPlayerState()==Media.State.Ended||item.mobileCompleted&&!localResume.containsKey(item.id)));
            row.computerBar.setProgress(item.computerCompleted?1000:duration>0?(int)Math.min(1000,Math.max(0,computer*1000/duration)):0);row.phoneBar.setProgress((active&&player!=null&&player.getPlayerState()==Media.State.Ended||item.mobileCompleted&&!localResume.containsKey(item.id))?1000:phoneDuration>0&&Double.isFinite(phone)?(int)Math.min(1000,Math.max(0,phone*1000/phoneDuration)):0);
            return row.root;
        }
    }
    private final class PlaylistRow {
        final LinearLayout root=card();final TextView title=text("",15,TEXT),now=text("NOW PLAYING",9,ACCENT),computer=text("",11,0xffaacbf5),phone=text("",11,MUTED);final ProgressBar computerBar=progressBar(0xff91bff6),phoneBar=progressBar(ACCENT);
        PlaylistRow(){root.setTag(this);now.setLetterSpacing(.1f);now.setPadding(0,0,0,dp(7));root.addView(now);title.setTypeface(null,Typeface.BOLD);title.setMaxLines(2);title.setEllipsize(TextUtils.TruncateAt.END);root.addView(title);computer.setPadding(0,dp(12),0,dp(5));root.addView(computer);root.addView(computerBar,new LinearLayout.LayoutParams(-1,dp(3)));phone.setPadding(0,dp(9),0,dp(5));root.addView(phone);root.addView(phoneBar,new LinearLayout.LayoutParams(-1,dp(3)));}
    }
    private ProgressBar progressBar(int color){ProgressBar bar=new ProgressBar(this,null,android.R.attr.progressBarStyleHorizontal);bar.setIndeterminate(false);bar.setMax(1000);bar.setProgressTintList(ColorStateList.valueOf(color));bar.setProgressBackgroundTintList(ColorStateList.valueOf(0x22ffffff));return bar;}
    // Debug-only observable hooks exercise actual UI/network/player paths from instrumentation.
    public void debugConnect(String url){if(!BuildConfig.DEBUG)throw new IllegalStateException();manual.setText(url);connect.performClick();}
    public void debugPair(String code){if(!BuildConfig.DEBUG||pairDialog==null)throw new IllegalStateException();EditText input=pairDialog.findViewById(R.id.pair_code);input.setText(code);pairDialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick();}
    public boolean debugPairVisible(){return BuildConfig.DEBUG&&pairDialog!=null&&pairDialog.isShowing();}
    public boolean debugPaired(){return BuildConfig.DEBUG&&session!=null;}
    public int debugPlaylistSize(){return BuildConfig.DEBUG?items.size():0;}
    public void debugPlay(String id){if(!BuildConfig.DEBUG)throw new IllegalStateException();for(int i=0;i<items.size();i++)if(items.get(i).id.equals(id)){list.performItemClick(list.getAdapter().getView(i,null,list),i,i);return;}throw new IllegalArgumentException("Test media is unavailable");}
    public long debugTime(){return player==null?-1:player.getTime();}
    public boolean debugPlaying(){return player!=null&&player.isPlaying();}
    public boolean debugSeekable(){return player!=null&&player.isSeekable();}
    public void debugToggle(){if(!BuildConfig.DEBUG)throw new IllegalStateException();playButton.performClick();}
    public void debugSeek(long milliseconds){if(!BuildConfig.DEBUG||player==null||player.getLength()<=0)throw new IllegalStateException();seekListener.onStartTrackingTouch(seek);seek.setProgress((int)Math.min(1000,milliseconds*1000/player.getLength()));seekListener.onStopTrackingTouch(seek);}
    public Media.Stats debugStats(){return BuildConfig.DEBUG?stats():null;}
    public String debugEngineVersion(){return engine==null?"not started":LibVLC.version()+" changeset="+LibVLC.changeset();}
    public boolean debugPausedUi(){return BuildConfig.DEBUG&&player!=null&&player.getPlayerState()==Media.State.Paused&&!debugPlaying()&&android.text.TextUtils.equals("Play",playButton.getContentDescription())&&playerStatus.getText().toString().equals("Paused");}
    public void debugFailInitialOutputOnce(){if(!BuildConfig.DEBUG||player!=null)throw new IllegalStateException();debugSkipSurfaceOnce=true;}
    public boolean debugSoftwarePlayback(){return BuildConfig.DEBUG&&softwarePlayback;}
    public boolean debugPauseRequested(){return BuildConfig.DEBUG&&userPaused;}
    public boolean debugPlaybackPrepared(){return BuildConfig.DEBUG&&enginePrepared&&!enginePreparing;}
    public boolean debugNativePlayRequested(){return BuildConfig.DEBUG&&playRequestedAt>0;}
    public int debugFontCacheFiles(){return BuildConfig.DEBUG?fontCacheFiles():-1;}
    public boolean debugFontCacheVerified(){return BuildConfig.DEBUG&&fontCacheVerified;}
    public long debugPreparationDurationMs(){return BuildConfig.DEBUG?preparationDurationMs:0;}
    public void debugNativeDiagnostics(boolean value){if(!BuildConfig.DEBUG||engine!=null)throw new IllegalStateException();nativeDiagnostics=value;}
    public boolean debugPauseIntentUi(){return BuildConfig.DEBUG&&userPaused&&android.text.TextUtils.equals("Play",playButton.getContentDescription())&&playerStatus.getText().toString().equals("Paused");}
    public boolean debugNativeSurfaceReady(){return BuildConfig.DEBUG&&nativeSurfacesReady&&validVideoSurface(video)&&player!=null&&player.getVLCVout().areViewsAttached();}
    public int debugVideoGraphSequence(){return BuildConfig.DEBUG?videoGraphSequence:0;}
    public int debugSurfaceDestroyedEvents(){return BuildConfig.DEBUG?surfaceDestroyedEvents:0;}
    public void debugRecreateSurface(){if(!BuildConfig.DEBUG||player==null)throw new IllegalStateException();VLCVideoLayout activeVideo=video;View surface=activeVideo.findViewById(org.videolan.R.id.surface_video);surface.setVisibility(View.INVISIBLE);main.postDelayed(()->{if(video==activeVideo&&player!=null)surface.setVisibility(View.VISIBLE);},250);}
    public boolean debugFullscreenInsetsClear(){return BuildConfig.DEBUG&&full&&root.getPaddingLeft()==0&&root.getPaddingTop()==0&&root.getPaddingRight()==0&&root.getPaddingBottom()==0&&(Build.VERSION.SDK_INT<30||root.getRootWindowInsets()!=null&&!root.getRootWindowInsets().isVisible(WindowInsets.Type.systemBars()));}
    public boolean debugResumeApplied(){return BuildConfig.DEBUG&&autoResumeApplied;}
    public void debugInvalidateWifiRoute(){if(!BuildConfig.DEBUG||wifiConnections==null)throw new IllegalStateException();wifiConnections.invalidateForTesting();}
    public long debugWifiRouteReplacements(){return BuildConfig.DEBUG&&wifiConnections!=null?wifiConnections.replacementCount():0;}
    public String debugError(){return lastError;}
    public String debugDiagnostics(){return BuildConfig.DEBUG?playbackDiagnostics():"";}
    public void debugLeave(){if(!BuildConfig.DEBUG)throw new IllegalStateException();leavePlayer();}
    public void debugFull(boolean value){if(!BuildConfig.DEBUG)throw new IllegalStateException();setFull(value);}
    public boolean debugIsFull(){return BuildConfig.DEBUG&&full;}
    public boolean debugChromeVisible(){return BuildConfig.DEBUG&&playerHeader.getVisibility()==View.VISIBLE&&playerControls.getVisibility()==View.VISIBLE;}
    public void debugTapVideo(){if(!BuildConfig.DEBUG)throw new IllegalStateException();videoTapTarget.performClick();}
    public boolean debugExitFullLabel(){return BuildConfig.DEBUG&&android.text.TextUtils.equals("Exit full screen",fullButton.getContentDescription());}
    public int debugPlayerIdentity(){return BuildConfig.DEBUG?System.identityHashCode(player):0;}
    public int debugVideoViewIdentity(){return BuildConfig.DEBUG?System.identityHashCode(video):0;}
    public int debugVideoOutputs(){return BuildConfig.DEBUG?videoOutputs:0;}
    public boolean debugPlayUsesIcon(){return BuildConfig.DEBUG&&playButton.getText().length()==0&&playButton.getContentDescription()!=null;}
    public void debugPlaylist(boolean open){if(!BuildConfig.DEBUG)throw new IllegalStateException();setPlaylistOpen(open);}
    public boolean debugPlaylistOpen(){return BuildConfig.DEBUG&&playlistOpen&&playlistOverlay.getVisibility()==View.VISIBLE;}
    public int debugProgressAccepted(){return BuildConfig.DEBUG?progressAcceptedCount:0;}
    public boolean debugProgressSupported(){return BuildConfig.DEBUG&&session!=null&&session.host.supportsProgress;}
    public void debugShareProgressNow(){if(!BuildConfig.DEBUG)throw new IllegalStateException();sharePlaybackProgress(true);}
    public void debugResumeComputer(){if(!BuildConfig.DEBUG)throw new IllegalStateException();resumeFromComputer();}
    public String debugProgressLabels(){return BuildConfig.DEBUG?deviceProgress.getText().toString():"";}
    public void debugPrevious(){if(!BuildConfig.DEBUG)throw new IllegalStateException();previousButton.performClick();}
    public void debugNext(){if(!BuildConfig.DEBUG)throw new IllegalStateException();nextButton.performClick();}
    public boolean debugPreviousEnabled(){return BuildConfig.DEBUG&&previousButton.isEnabled();}
    public boolean debugNextEnabled(){return BuildConfig.DEBUG&&nextButton.isEnabled();}
    public String debugCurrentId(){return current==null?null:current.id;}
    public int debugBrightness(){return brightnessPercent();}
    public boolean debugVolumeHud(){return BuildConfig.DEBUG&&volumeHud.getVisibility()==View.VISIBLE;}
    public boolean debugBrightnessHud(){return BuildConfig.DEBUG&&brightnessHud.getVisibility()==View.VISIBLE;}
    public void debugGesture(float fromX,float fromY,float toX,float toY,boolean cancel){
        if(!BuildConfig.DEBUG)throw new IllegalStateException();long began=android.os.SystemClock.uptimeMillis();int width=videoTapTarget.getWidth(),height=videoTapTarget.getHeight();
        MotionEvent down=MotionEvent.obtain(began,began,MotionEvent.ACTION_DOWN,fromX*width,fromY*height,0);videoTapTarget.dispatchTouchEvent(down);down.recycle();
        MotionEvent move=MotionEvent.obtain(began,began+200,MotionEvent.ACTION_MOVE,toX*width,toY*height,0);videoTapTarget.dispatchTouchEvent(move);move.recycle();
        MotionEvent up=MotionEvent.obtain(began,began+250,cancel?MotionEvent.ACTION_CANCEL:MotionEvent.ACTION_UP,toX*width,toY*height,0);videoTapTarget.dispatchTouchEvent(up);up.recycle();
    }
    public void debugVolume(int value){if(!BuildConfig.DEBUG)throw new IllegalStateException();volume.setProgress(value);}
    public int debugVolume(){return player==null?-1:player.getVolume();}
}
