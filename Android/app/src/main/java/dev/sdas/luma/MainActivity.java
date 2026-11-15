package dev.sdas.luma;

import android.Manifest;
import android.annotation.SuppressLint;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.pm.ActivityInfo;
import android.content.pm.PackageManager;
import android.content.res.Configuration;
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
    private List<LanApi.Item> items=Collections.emptyList();
    private LanApi api=new LanApi();private LanApi.Session session;private LanEndpoint selectedEndpoint;private Network selectedNetwork;
    private Discovery discovery;private LibVLC engine;private MediaPlayer player;private StreamRelay relay;
    private FrameLayout root,home,playback,videoContainer;private VLCVideoLayout video;
    private LinearLayout desktopRows,homeContent,playerControls,playerHeader;private TextView status,playlistTitle,playlistInfo,playerTitle,playerStatus,clock;
    private View videoTapTarget;private boolean chromeVisible=true;private Button fullButton;
    private EditText manual;private Button connect,playButton;private ListView list;private SeekBar seek,volume;private AlertDialog pairDialog;
    private boolean foreground,polling,connecting,dragging,full,wasPlaying,autoResumeApplied,destroyed;
    private int generation,mediaGeneration;private long initialResume;private LanApi.Item current;private String lastError;
    private boolean permissionAsked,nativeDiagnostics;
    private final java.util.Set<String> softwareItems=new java.util.HashSet<>();
    private boolean softwarePlayback,videoWasDisplayed,hadVideoTracks,videoOutputWasCreated,userPaused,debugSkipSurfaceOnce,nativePlayingSeen;private int videoOutputs;private long nativeStartedAt,playRequestedAt,lastOpeningDiagnostic;
    private Media.Stats lastStats;private SeekBar.OnSeekBarChangeListener seekListener;private IVLCVout.Callback surfaceCallback;
    private boolean nativeSurfacesReady,surfaceRebindPending,surfaceRestartPending,surfaceRestartStarted,surfaceRecoveryNeedsResume;private int videoGraphSequence,retiringGraphs,surfaceRebinds,surfaceRebindAttempts,surfaceCreatedEvents,surfaceDestroyedEvents;private long graphStartedAt,lastRetirementMs,surfaceRecoveryTime;
    private boolean enginePreparing,enginePrepared,fontCacheVerified;private long preparationStartedAt,preparationDurationMs;private LanApi.Item pendingPreparationItem;private int pendingPreparationConnection,pendingPreparationMedia;
    private final Runnable pulse=new Runnable(){public void run(){if(!foreground||destroyed)return;updatePlayer();main.postDelayed(this,500);}};
    private final Runnable poll=new Runnable(){public void run(){if(!foreground||destroyed)return;refresh(false);main.postDelayed(this,5000);}};
    private final Runnable settleFullscreenBars=()->{if(full&&foreground&&!destroyed){applySystemBars();root.requestApplyInsets();}};

    @Override public void onCreate(Bundle saved){
        super.onCreate(saved);getWindow().setStatusBarColor(BG);getWindow().setNavigationBarColor(BG);
        root=new FrameLayout(this);root.setBackgroundColor(BG);setContentView(root);
        root.setOnApplyWindowInsetsListener((view,insets)->{view.setPadding(full?0:insets.getSystemWindowInsetLeft(),full?0:insets.getSystemWindowInsetTop(),full?0:insets.getSystemWindowInsetRight(),full?0:insets.getSystemWindowInsetBottom());return insets;});
        root.addOnLayoutChangeListener((view,left,top,right,bottom,oldLeft,oldTop,oldRight,oldBottom)->{if(full&&(right-left!=oldRight-oldLeft||bottom-top!=oldBottom-oldTop))scheduleFullscreenBars();});
        buildHome();buildPlayer();
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
        list=new ListView(this);list.setId(R.id.media_list);list.setDividerHeight(dp(7));list.setDivider(new android.graphics.drawable.ColorDrawable(BG));list.setBackgroundColor(BG);list.setAdapter(new PlaylistAdapter());
        list.setOnItemClickListener((parent,view,position,id)->startMedia(items.get(position)));
        list.setOnTouchListener((view,event)->{view.getParent().requestDisallowInterceptTouchEvent(true);return false;});
        homeContent.addView(list,new LinearLayout.LayoutParams(-1,dp(330)));
        addText(homeContent,"FREE BY SDAS  ·  ANDROID PREVIEW",11,MUTED,18);
    }
    private void buildPlayer(){
        playback=new FrameLayout(this);playback.setBackgroundColor(Color.BLACK);root.addView(playback,new FrameLayout.LayoutParams(-1,-1));playback.setVisibility(View.GONE);
        videoContainer=new FrameLayout(this);playback.addView(videoContainer,new FrameLayout.LayoutParams(-1,-1));
        video=new VLCVideoLayout(this);videoContainer.addView(video,new FrameLayout.LayoutParams(-1,-1));
        videoTapTarget=new View(this);videoTapTarget.setBackgroundColor(Color.TRANSPARENT);videoTapTarget.setOnClickListener(v->{if(full)setChromeVisible(!chromeVisible);});videoContainer.addView(videoTapTarget,new FrameLayout.LayoutParams(-1,-1));
        LinearLayout top=row();playerHeader=top;top.setPadding(dp(12),dp(12),dp(12),dp(12));top.setBackgroundColor(0xc010131b);
        Button back=button("‹  Playlist",this::leavePlayer,false);back.setId(R.id.player_back);top.addView(back);playerTitle=text("",16,TEXT);playerTitle.setSingleLine();playerTitle.setEllipsize(TextUtils.TruncateAt.END);top.addView(playerTitle,new LinearLayout.LayoutParams(0,-2,1));fullButton=button("Full screen",()->setFull(!full),false);top.addView(fullButton);
        FrameLayout.LayoutParams topParams=new FrameLayout.LayoutParams(-1,-2,Gravity.TOP);playback.addView(top,topParams);
        playerControls=column();playerControls.setPadding(dp(14),dp(10),dp(14),dp(16));playerControls.setBackgroundColor(0xe610131b);
        playerStatus=text("Opening stream…",12,MUTED);playerStatus.setId(R.id.player_status);playerControls.addView(playerStatus);
        seek=new SeekBar(this);seek.setId(R.id.player_seek);seek.setMax(1000);playerControls.addView(seek,new LinearLayout.LayoutParams(-1,dp(38)));
        seekListener=new SeekBar.OnSeekBarChangeListener(){
            public void onProgressChanged(SeekBar bar,int value,boolean user){}
            public void onStartTrackingTouch(SeekBar bar){dragging=true;}
            public void onStopTrackingTouch(SeekBar bar){dragging=false;if(player!=null&&player.isSeekable()&&player.getLength()>0){autoResumeApplied=true;player.setTime((long)(player.getLength()*(bar.getProgress()/1000.0)),false);}}
        };seek.setOnSeekBarChangeListener(seekListener);
        LinearLayout transport=row();transport.addView(button("−10",()->jump(-10000),false));playButton=button("Pause",this::togglePlay,true);playButton.setId(R.id.player_play);transport.addView(playButton);transport.addView(button("+10",()->jump(10000),false));
        clock=text("0:00 / 0:00",12,TEXT);clock.setGravity(Gravity.CENTER);transport.addView(clock,new LinearLayout.LayoutParams(0,-2,1));playerControls.addView(transport);
        LinearLayout sound=row();sound.addView(text("Volume",12,MUTED));volume=new SeekBar(this);volume.setMax(100);volume.setProgress(80);sound.addView(volume,new LinearLayout.LayoutParams(0,dp(42),1));
        volume.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener(){public void onStartTrackingTouch(SeekBar b){}public void onStopTrackingTouch(SeekBar b){}public void onProgressChanged(SeekBar b,int value,boolean user){if(player!=null)player.setVolume(value);}});
        sound.addView(button("Retry",()->{if(current!=null)startMedia(current);},false));playerControls.addView(sound);
        playback.addView(playerControls,new FrameLayout.LayoutParams(-1,-2,Gravity.BOTTOM));
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
        for(Network network:connectivity.getAllNetworks()){NetworkCapabilities caps=connectivity.getNetworkCapabilities(network);if(caps!=null&&caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI))return network;}return null;
    }
    private LanApi forNetwork(Network network){return network==null?new LanApi():new LanApi(url->(HttpURLConnection)network.openConnection(url,java.net.Proxy.NO_PROXY));}
    private void connectManual(){if(!hasPermission()){permissionAsked=false;ensurePermission();return;}try{connectDesktop(LanEndpoint.parse(manual.getText().toString().trim()),wifiNetwork());}catch(IllegalArgumentException error){status.setText(error.getMessage());}}
    private void connectDesktop(LanEndpoint endpoint,Network network){
        if(connecting)return;if(!hasPermission()){ensurePermission();return;}disconnect(false);selectedEndpoint=endpoint;selectedNetwork=network;api=forNetwork(network);connecting=true;connect.setEnabled(false);status.setText("Connecting to desktop…");int version=generation;
        LanApi client=api;
        execute(()->{try{LanApi.Info info=client.info(endpoint);main.post(()->{if(stale(version))return;connecting=false;connect.setEnabled(true);pair(endpoint,info);});}catch(Exception error){main.post(()->{if(stale(version))return;connecting=false;connect.setEnabled(true);status.setText(safe(error));});}});
    }
    private void pair(LanEndpoint endpoint,LanApi.Info info){
        if(!foreground)return;EditText code=new EditText(this);code.setId(R.id.pair_code);code.setTextSize(25);code.setGravity(Gravity.CENTER);code.setInputType(InputType.TYPE_CLASS_NUMBER);code.setFilters(new InputFilter[]{new InputFilter.LengthFilter(6)});code.setHint("000000");
        LinearLayout content=column();content.setPadding(dp(22),dp(12),dp(22),dp(6));addText(content,"Enter the current six-digit code shown in Luma on your desktop. This connection is for your trusted Wi-Fi only.",14,MUTED,0);content.addView(code,new LinearLayout.LayoutParams(-1,dp(65)));
        pairDialog=new AlertDialog.Builder(this).setTitle("Pair with "+info.name).setView(content).setNegativeButton("Cancel",(d,w)->cancelPair()).setPositiveButton("Pair",null).create();
        pairDialog.setOnCancelListener(d->cancelPair());LanApi client=api;
        pairDialog.setOnShowListener(d->{Button confirm=pairDialog.getButton(AlertDialog.BUTTON_POSITIVE);confirm.setId(R.id.pair_confirm);confirm.setOnClickListener(v->{String value=code.getText().toString();if(!value.matches("[0-9]{6}")){code.setError("Enter all six digits");return;}confirm.setEnabled(false);status.setText("Pairing…");int version=generation;
            execute(()->{try{LanApi.Session paired=client.pair(endpoint,info,value);main.post(()->{if(stale(version))return;session=paired;getPreferences(MODE_PRIVATE).edit().putString("desktopUrl",endpoint.origin).apply();manual.setText(endpoint.origin);pairDialog.dismiss();pairDialog=null;playlistTitle.setText(info.name);status.setText("Connected · playlist updates automatically");refresh(true);});}catch(Exception error){main.post(()->{if(stale(version))return;confirm.setEnabled(true);status.setText(safe(error));code.setError("Check the current desktop code and try again");});}});
        });});pairDialog.show();
    }
    private void cancelPair(){generation++;pairDialog=null;connecting=false;connect.setEnabled(true);status.setText("Pairing cancelled. Your media remains private.");}
    private void refresh(boolean explicit){
        if(session==null){if(explicit)status.setText("Connect and pair with your desktop first.");return;}if(polling)return;polling=true;LanApi.Session active=session;int version=generation;if(explicit)status.setText("Refreshing playlist…");
        LanApi client=api;
        execute(()->{try{LanApi.Info info=client.info(active.endpoint);if(!info.id.equals(active.host.id))throw new LanApi.ApiException(401,"Sharing restarted. Pair again with the new desktop code.");LanApi.Playlist snapshot=client.playlistState(active);List<LanApi.Item> playlist=snapshot.items;main.post(()->{if(stale(version))return;polling=false;items=playlist;((BaseAdapter)list.getAdapter()).notifyDataSetChanged();playlistInfo.setText(snapshot.summary());status.setText("Connected · "+snapshot.summary());
            if(current!=null&&playlist.stream().noneMatch(item->item.id.equals(current.id))){lastError="This item was removed from the desktop playlist.";stopPlayback();leavePlayer();status.setText(lastError);}
        });}catch(Exception error){main.post(()->{if(stale(version))return;polling=false;status.setText(safe(error));if(error instanceof LanApi.ApiException&&((LanApi.ApiException)error).status==401){disconnect(false);status.setText("Sharing restarted or pairing expired. Connect again to pair.");}});}});
    }
    private void startMedia(LanApi.Item item){
        if(session==null){status.setText("Pair again before streaming.");return;}if(retiringGraphs>=3){playerStatus.setText("Finishing the previous stream. Please retry in a moment.");return;}stopPlayback();graphStartedAt=android.os.SystemClock.uptimeMillis();softwarePlayback=softwareItems.contains(item.id);videoWasDisplayed=false;hadVideoTracks=false;videoOutputWasCreated=false;userPaused=false;videoOutputs=0;nativeStartedAt=0;playRequestedAt=0;nativePlayingSeen=false;nativeSurfacesReady=false;surfaceRebindPending=false;surfaceRestartPending=false;surfaceRestartStarted=false;surfaceRecoveryNeedsResume=false;surfaceRebinds=0;surfaceRebindAttempts=0;surfaceCreatedEvents=0;surfaceDestroyedEvents=0;lastOpeningDiagnostic=0;current=item;lastError=null;lastStats=null;initialResume=localResume.getOrDefault(item.id,(long)(item.resumeSeconds*1000));autoResumeApplied=false;home.setVisibility(View.GONE);playback.setVisibility(View.VISIBLE);playerTitle.setText(item.title);playButton.setText("Pause");playerStatus.setText("Opening LAN stream…");getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
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
                    if(userPaused){playButton.setText("Play");playerStatus.setText("Paused");return;}
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
            relay=new StreamRelay(active,item,api.connections(),failure->main.post(()->{if(stale(version)||clip!=mediaGeneration||current!=item)return;lastError=failure==StreamRelay.Failure.AUTH?"Pairing expired. Return to the playlist and pair again.":failure==StreamRelay.Failure.REMOVED?"This item was removed from the desktop.":failure==StreamRelay.Failure.UNSAFE_RESPONSE?"The desktop returned an unsafe stream response.":"Desktop offline. Keep sharing enabled and retry.";playerStatus.setText(lastError);userPaused=true;if(player!=null)player.pause();}));
            // Each native player owns its own surface tree. A retired helper
            // must never receive destroy/layout callbacks for the next player.
            videoContainer.removeView(video);video=new VLCVideoLayout(this);videoContainer.addView(video,0,new FrameLayout.LayoutParams(-1,-1));videoGraphSequence++;
            player=new MediaPlayer(engine);MediaPlayer activePlayer=player;player.setVolume(volume.getProgress());
            player.setEventListener(event->main.post(()->{
                if(destroyed||player!=activePlayer||current!=item)return;
                if(event.type==MediaPlayer.Event.Playing){nativePlayingSeen=true;if(nativeSurfacesReady&&activePlayer.isPlaying()){surfaceRecoveryNeedsResume=false;surfaceRestartStarted=false;}if(userPaused){activePlayer.pause();playButton.setText("Play");if(lastError==null)playerStatus.setText("Paused");return;}nativeStartedAt=android.os.SystemClock.uptimeMillis();hadVideoTracks|=hasKnownVideoTrack();applyResume();playerStatus.setText((softwarePlayback?"Software playback · ":"Streaming · ")+active.host.name);playButton.setText("Pause");logPlayback("playing");}
                else if(event.type==MediaPlayer.Event.Vout){videoOutputs=event.getVoutCount();videoOutputWasCreated|=videoOutputs>0;if(videoOutputs>0&&nativeSurfacesReady)surfaceRebindAttempts=0;logPlayback("video-output");}
                else if(event.type==MediaPlayer.Event.Paused){if(!activePlayer.isPlaying()){playButton.setText("Play");if(lastError==null)playerStatus.setText("Paused");}}
                else if(event.type==MediaPlayer.Event.Buffering&&!userPaused&&lastError==null&&event.getBuffering()<100)playerStatus.setText("Buffering "+Math.round(event.getBuffering())+"% · keep the desktop awake");
                else if(event.type==MediaPlayer.Event.EncounteredError){if(surfaceRebindPending||surfaceRestartPending)return;if(retrySoftware())return;lastError=lastError==null?"Could not play this stream. Check that the desktop is awake and sharing, then retry.":lastError;playerStatus.setText(lastError);playButton.setText("Play");}
                else if(event.type==MediaPlayer.Event.EndReached){lastStats=stats();if(surfaceRebindPending||surfaceRestartPending||surfaceRecoveryNeedsResume)return;if(!videoWasDisplayed&&retrySoftware())return;logPlayback("ended");playerStatus.setText("Finished · choose another item from your playlist");playButton.setText("Play");}
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
                media.addOption(":network-caching=1200");media.addOption(":http-reconnect");activePlayer.setMedia(media);media.release();
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
        Media.Stats value=stats();return "preparing="+enginePreparing+", prepared="+enginePrepared+", cacheVerified="+fontCacheVerified+", preparationMs="+(enginePreparing?android.os.SystemClock.uptimeMillis()-preparationStartedAt:preparationDurationMs)+", graph="+videoGraphSequence+", graphAgeMs="+(graphStartedAt==0?0:android.os.SystemClock.uptimeMillis()-graphStartedAt)+", retiring="+retiringGraphs+", lastRetirementMs="+lastRetirementMs+", software="+softwarePlayback+", nativeState="+(player==null?-1:player.getPlayerState())+", openingAgeMs="+(playRequestedAt==0?0:android.os.SystemClock.uptimeMillis()-playRequestedAt)+", playingEventSeen="+nativePlayingSeen+", resumeApplied="+autoResumeApplied+", forwardedBytes="+(relay==null?0:relay.forwardedBytes())+", surfaceReady="+videoSurfaceReady()+", nativeSurfaceReady="+nativeSurfacesReady+", holderValid="+validVideoSurface(video)+", surfaceCreated="+surfaceCreatedEvents+", surfaceDestroyed="+surfaceDestroyedEvents+", rebinds="+surfaceRebinds+", rebindPending="+surfaceRebindPending+", restartPending="+surfaceRestartPending+", recoveryNeedsResume="+surfaceRecoveryNeedsResume+", size="+video.getWidth()+"x"+video.getHeight()+", videoTracks="+(player==null?-1:player.getVideoTracksCount())+", outputs="+videoOutputs+", outputCreated="+videoOutputWasCreated+", decodedVideo="+(value==null?0:value.decodedVideo)+", displayed="+(value==null?0:value.displayedPictures)+", decodedAudio="+(value==null?0:value.decodedAudio)+", playing="+(player!=null&&player.isPlaying());
    }
    private void logPlayback(String reason){android.util.Log.i("LumaPlayback",reason+": "+playbackDiagnostics());}
    private void updatePlayer(){
        if(player==null||current==null)return;applyResume();long length=player.getLength(),time=Math.max(0,player.getTime());
        clock.setText(time(time)+" / "+time(length));if(!dragging&&length>0)seek.setProgress((int)Math.min(1000,time*1000/length));seek.setEnabled(player.isSeekable());
        if(time>0)localResume.put(current.id,time);Media.Stats value=stats();if(value!=null){lastStats=value;if(value.displayedPictures>0)videoWasDisplayed=true;}
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
        if(player.isSeekable()&&player.getLength()>0){autoResumeApplied=true;player.setTime(Math.min(initialResume,Math.max(0,player.getLength()-1000)),false);logPlayback("resume-after-playing");}
    }
    private Media.Stats stats(){if(player==null)return lastStats;IMedia media=player.getMedia();if(media==null)return lastStats;try{Media.Stats value=media.getStats();if(value!=null&&(value.decodedVideo>0||value.decodedAudio>0)){lastStats=value;if(value.displayedPictures>0)videoWasDisplayed=true;}return lastStats==null?value:lastStats;}finally{media.release();}}
    private void togglePlay(){
        if(player==null&&enginePreparing&&current!=null){userPaused=!userPaused;playButton.setText(userPaused?"Play":"Pause");playerStatus.setText(userPaused?"Paused":"Preparing playback for first use…");return;}
        if(player==null){if(current!=null)startMedia(current);return;}
        if(lastError!=null){startMedia(current);return;}
        if(surfaceRebindPending||surfaceRestartPending||surfaceRecoveryNeedsResume){userPaused=!userPaused;playButton.setText(userPaused?"Play":"Pause");playerStatus.setText(userPaused?"Paused":"Restoring video…");if(userPaused){if(player.isPlaying())player.pause();}else if(!surfaceRebindPending&&!surfaceRestartPending)resumeAfterSurfaceRecovery(player,video);return;}
        int nativeState=player.getPlayerState();
        if(nativeState==Media.State.Ended||nativeState==Media.State.Stopped){localResume.put(current.id,0L);startMedia(current);return;}
        if(!userPaused){userPaused=true;playButton.setText("Play");playerStatus.setText("Paused");if(nativePlayingSeen||player.isPlaying())player.pause();}
        else{userPaused=false;playButton.setText("Pause");playerStatus.setText("Resuming stream…");if(nativeSurfacesReady&&validVideoSurface(video)){if(!nativePlayingSeen)playRequestedAt=android.os.SystemClock.uptimeMillis();player.play();}}
    }
    private void jump(long amount){if(player!=null&&player.isSeekable()){autoResumeApplied=true;player.setTime(Math.max(0,Math.min(player.getLength(),player.getTime()+amount)),false);}}
    private void stopPlayback(){
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
    private void leavePlayer(){updatePlayer();stopPlayback();current=null;playback.setVisibility(View.GONE);home.setVisibility(View.VISIBLE);setFull(false);}
    private void setChromeVisible(boolean value){chromeVisible=value;playerHeader.setVisibility(value?View.VISIBLE:View.GONE);playerControls.setVisibility(value?View.VISIBLE:View.GONE);videoTapTarget.setContentDescription(value?"Hide playback controls":"Show playback controls");videoTapTarget.setImportantForAccessibility(full?View.IMPORTANT_FOR_ACCESSIBILITY_YES:View.IMPORTANT_FOR_ACCESSIBILITY_NO);}
    private void applySystemBars(){if(Build.VERSION.SDK_INT>=30){WindowInsetsController controller=getWindow().getInsetsController();if(controller!=null){if(full){controller.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);controller.hide(WindowInsets.Type.systemBars());}else controller.show(WindowInsets.Type.systemBars());}}else getWindow().getDecorView().setSystemUiVisibility(full?View.SYSTEM_UI_FLAG_FULLSCREEN|View.SYSTEM_UI_FLAG_HIDE_NAVIGATION|View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY:0);}
    // Rotation/focus transitions can restore bars after the initial hide request.
    // Reapply once after geometry settles, rather than suppressing user swipes.
    private void scheduleFullscreenBars(){main.removeCallbacks(settleFullscreenBars);if(full)main.postDelayed(settleFullscreenBars,500);}
    private void setFull(boolean value){full=value;fullButton.setText(value?"Exit full screen":"Full screen");setChromeVisible(!value);applySystemBars();root.requestApplyInsets();scheduleFullscreenBars();setRequestedOrientation(value?ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE:ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED);}
    private void disconnect(boolean clearUrl){generation++;polling=false;connecting=false;session=null;localResume.clear();softwareItems.clear();items=Collections.emptyList();if(playlistInfo!=null)playlistInfo.setText("Pair with a desktop to see its current playlist.");if(list!=null)((BaseAdapter)list.getAdapter()).notifyDataSetChanged();if(pairDialog!=null){pairDialog.dismiss();pairDialog=null;}if(current!=null)leavePlayer();if(clearUrl)getPreferences(MODE_PRIVATE).edit().remove("desktopUrl").apply();}
    private void execute(Runnable action){try{work.execute(action);}catch(java.util.concurrent.RejectedExecutionException error){status.setText("Please wait for the current desktop request to finish.");polling=false;connecting=false;connect.setEnabled(true);}}
    private boolean stale(int version){return destroyed||version!=generation;}
    private String permission(){return Build.VERSION.SDK_INT>=37?Manifest.permission.ACCESS_LOCAL_NETWORK:Build.VERSION.SDK_INT==36?Manifest.permission.NEARBY_WIFI_DEVICES:null;}
    private boolean hasPermission(){String permission=permission();return permission==null||checkSelfPermission(permission)==PackageManager.PERMISSION_GRANTED;}
    private void ensurePermission(){if(hasPermission()){discovery.start();return;}if(permissionAsked)return;permissionAsked=true;requestPermissions(new String[]{permission()},100);}
    @Override public void onRequestPermissionsResult(int request,String[] permissions,int[] results){super.onRequestPermissionsResult(request,permissions,results);if(request==100){if(hasPermission())discovery.start();else{status.setText("Local-network access is needed to discover and stream. Tap Find to try again, or allow it in Settings.");}}}
    @Override protected void onResume(){super.onResume();foreground=true;ensurePermission();main.post(pulse);main.post(poll);if(wasPlaying&&current!=null){wasPlaying=false;userPaused=false;if(player!=null){if(nativeSurfacesReady&&validVideoSurface(video))player.play();}else startMedia(current);}else if(current!=null&&player==null){playButton.setText("Play");playerStatus.setText("Paused · tap Play to resume streaming");}}
    @Override protected void onPause(){foreground=false;main.removeCallbacks(pulse);main.removeCallbacks(poll);main.removeCallbacks(settleFullscreenBars);discovery.stop();if(player!=null){int observed=player.getPlayerState();wasPlaying=!userPaused&&lastError==null&&observed!=Media.State.Ended&&observed!=Media.State.Error&&observed!=Media.State.Stopped;userPaused=true;if(nativePlayingSeen||player.isPlaying())player.pause();}else if(enginePreparing&&current!=null){wasPlaying=!userPaused;userPaused=true;}super.onPause();}
    @Override protected void onStop(){if(player!=null&&current!=null){updatePlayer();stopPlayback();}super.onStop();}
    @Override protected void onDestroy(){destroyed=true;generation++;discovery.stop();stopPlayback();releaseEngineWhenIdle();retiredPlayers.shutdown();session=null;work.shutdownNow();super.onDestroy();}
    @Override public void onConfigurationChanged(Configuration config){super.onConfigurationChanged(config);scheduleFullscreenBars();MediaPlayer active=player;if(active!=null)main.post(()->{if(player==active)active.updateVideoSurfaces();});}
    @Override public void onWindowFocusChanged(boolean hasFocus){super.onWindowFocusChanged(hasFocus);if(hasFocus)scheduleFullscreenBars();}
    // Modern back gestures use the native API33 dispatcher registered in onCreate.
    @SuppressLint("GestureBackNavigation")
    @Override public void onBackPressed(){navigateBack();}
    private void navigateBack(){if(full)setFull(false);else if(current!=null)leavePlayer();else finish();}
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
        public View getView(int position,View convert,ViewGroup parent){LanApi.Item item=items.get(position);LinearLayout itemView=card();TextView title=text("▶  "+item.title,16,TEXT);title.setMaxLines(2);title.setEllipsize(TextUtils.TruncateAt.END);itemView.addView(title);addText(itemView,(item.durationSeconds>0?time((long)(item.durationSeconds*1000)):"Duration available while playing")+(item.resumeSeconds>3?"  ·  Resume "+time((long)(item.resumeSeconds*1000)):""),12,MUTED,7);return itemView;}
    }
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
    public boolean debugPausedUi(){return BuildConfig.DEBUG&&player!=null&&player.getPlayerState()==Media.State.Paused&&!debugPlaying()&&playButton.getText().toString().equals("Play")&&playerStatus.getText().toString().equals("Paused");}
    public void debugFailInitialOutputOnce(){if(!BuildConfig.DEBUG||player!=null)throw new IllegalStateException();debugSkipSurfaceOnce=true;}
    public boolean debugSoftwarePlayback(){return BuildConfig.DEBUG&&softwarePlayback;}
    public boolean debugPauseRequested(){return BuildConfig.DEBUG&&userPaused;}
    public boolean debugPlaybackPrepared(){return BuildConfig.DEBUG&&enginePrepared&&!enginePreparing;}
    public boolean debugNativePlayRequested(){return BuildConfig.DEBUG&&playRequestedAt>0;}
    public int debugFontCacheFiles(){return BuildConfig.DEBUG?fontCacheFiles():-1;}
    public boolean debugFontCacheVerified(){return BuildConfig.DEBUG&&fontCacheVerified;}
    public long debugPreparationDurationMs(){return BuildConfig.DEBUG?preparationDurationMs:0;}
    public void debugNativeDiagnostics(boolean value){if(!BuildConfig.DEBUG||engine!=null)throw new IllegalStateException();nativeDiagnostics=value;}
    public boolean debugPauseIntentUi(){return BuildConfig.DEBUG&&userPaused&&playButton.getText().toString().equals("Play")&&playerStatus.getText().toString().equals("Paused");}
    public boolean debugNativeSurfaceReady(){return BuildConfig.DEBUG&&nativeSurfacesReady&&validVideoSurface(video)&&player!=null&&player.getVLCVout().areViewsAttached();}
    public int debugVideoGraphSequence(){return BuildConfig.DEBUG?videoGraphSequence:0;}
    public int debugSurfaceDestroyedEvents(){return BuildConfig.DEBUG?surfaceDestroyedEvents:0;}
    public void debugRecreateSurface(){if(!BuildConfig.DEBUG||player==null)throw new IllegalStateException();VLCVideoLayout activeVideo=video;View surface=activeVideo.findViewById(org.videolan.R.id.surface_video);surface.setVisibility(View.INVISIBLE);main.postDelayed(()->{if(video==activeVideo&&player!=null)surface.setVisibility(View.VISIBLE);},250);}
    public boolean debugFullscreenInsetsClear(){return BuildConfig.DEBUG&&full&&root.getPaddingLeft()==0&&root.getPaddingTop()==0&&root.getPaddingRight()==0&&root.getPaddingBottom()==0&&(Build.VERSION.SDK_INT<30||root.getRootWindowInsets()!=null&&!root.getRootWindowInsets().isVisible(WindowInsets.Type.systemBars()));}
    public boolean debugResumeApplied(){return BuildConfig.DEBUG&&autoResumeApplied;}
    public String debugError(){return lastError;}
    public String debugDiagnostics(){return BuildConfig.DEBUG?playbackDiagnostics():"";}
    public void debugLeave(){if(!BuildConfig.DEBUG)throw new IllegalStateException();leavePlayer();}
    public void debugFull(boolean value){if(!BuildConfig.DEBUG)throw new IllegalStateException();setFull(value);}
    public boolean debugIsFull(){return BuildConfig.DEBUG&&full;}
    public boolean debugChromeVisible(){return BuildConfig.DEBUG&&playerHeader.getVisibility()==View.VISIBLE&&playerControls.getVisibility()==View.VISIBLE;}
    public void debugTapVideo(){if(!BuildConfig.DEBUG)throw new IllegalStateException();videoTapTarget.performClick();}
    public boolean debugExitFullLabel(){return BuildConfig.DEBUG&&fullButton.getText().toString().equals("Exit full screen");}
    public int debugPlayerIdentity(){return BuildConfig.DEBUG?System.identityHashCode(player):0;}
    public int debugVideoViewIdentity(){return BuildConfig.DEBUG?System.identityHashCode(video):0;}
    public int debugVideoOutputs(){return BuildConfig.DEBUG?videoOutputs:0;}
    public void debugVolume(int value){if(!BuildConfig.DEBUG)throw new IllegalStateException();volume.setProgress(value);}
    public int debugVolume(){return player==null?-1:player.getVolume();}
}
