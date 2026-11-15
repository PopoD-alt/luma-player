package dev.sdas.luma;

import android.content.Context;
import android.net.Network;
import android.net.nsd.NsdManager;
import android.net.nsd.NsdServiceInfo;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Handler;
import dev.sdas.luma.net.LanEndpoint;
import java.net.InetAddress;
import java.util.ArrayDeque;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

final class Discovery {
    static final String TYPE="_luma-player._tcp.";
    static final class Desktop {
        final String name;final LanEndpoint endpoint;final Network network;
        Desktop(String name,LanEndpoint endpoint,Network network){this.name=name;this.endpoint=endpoint;this.network=network;}
    }
    interface Listener {void found(Desktop desktop);void lost(String name);void status(String value);}
    private final Context context;private final Handler main;private final Listener listener;private final NsdManager nsd;
    private final ArrayDeque<NsdServiceInfo> pending=new ArrayDeque<>();private final Set<String> names=new HashSet<>();
    private NsdManager.DiscoveryListener discovery;private NsdManager.ResolveListener resolver;private WifiManager.MulticastLock multicast;
    private boolean active,resolving;private int generation;
    Discovery(Context context,Handler main,Listener listener){this.context=context.getApplicationContext();this.main=main;this.listener=listener;nsd=(NsdManager)this.context.getSystemService(Context.NSD_SERVICE);}
    void start(){
        if(active||nsd==null)return;active=true;int version=++generation;
        if(Build.VERSION.SDK_INT<=33){try{WifiManager wifi=(WifiManager)context.getSystemService(Context.WIFI_SERVICE);if(wifi!=null){multicast=wifi.createMulticastLock("luma-discovery");multicast.setReferenceCounted(false);multicast.acquire();}}catch(RuntimeException ignored){}}
        discovery=new NsdManager.DiscoveryListener(){
            public void onDiscoveryStarted(String type){post(version,()->listener.status("Looking for Luma desktops on your Wi-Fi…"));}
            public void onDiscoveryStopped(String type){}
            public void onStartDiscoveryFailed(String type,int code){post(version,()->listener.status("Discovery unavailable. Enter the desktop's local address below."));}
            public void onStopDiscoveryFailed(String type,int code){}
            public void onServiceFound(NsdServiceInfo info){post(version,()->{if(names.size()>=48||!info.getServiceType().replaceAll("\\.$","").equals(TYPE.substring(0,TYPE.length()-1))||!names.add(info.getServiceName()))return;pending.add(info);resolve(version);});}
            public void onServiceLost(NsdServiceInfo info){post(version,()->{names.remove(info.getServiceName());listener.lost(info.getServiceName());});}
        };
        try{nsd.discoverServices(TYPE,NsdManager.PROTOCOL_DNS_SD,discovery);}catch(SecurityException error){listener.status("Allow local-network access to discover desktops, or enable it in app settings.");}catch(RuntimeException error){listener.status("Discovery unavailable. Manual connection still works.");}
    }
    private void resolve(int version){
        if(!active||resolving||pending.isEmpty())return;resolving=true;NsdServiceInfo next=pending.removeFirst();
        resolver=new NsdManager.ResolveListener(){
            public void onResolveFailed(NsdServiceInfo info,int code){post(version,()->{resolving=false;names.remove(info.getServiceName());resolve(version);});}
            public void onServiceResolved(NsdServiceInfo info){post(version,()->{
                resolving=false;
                try{
                    byte[] protocol=info.getAttributes().get("v");if(protocol!=null&&!new String(protocol,java.nio.charset.StandardCharsets.US_ASCII).equals("1"))return;
                    List<InetAddress> addresses=Build.VERSION.SDK_INT>=34?info.getHostAddresses():Collections.singletonList(info.getHost());
                    InetAddress selected=null;for(InetAddress a:addresses)if(a!=null&&LanEndpoint.isLocal(a)&&(selected==null||a.getAddress().length==4))selected=a;
                    if(selected!=null)listener.found(new Desktop(info.getServiceName(),LanEndpoint.discovered(selected,info.getPort()),Build.VERSION.SDK_INT>=33?info.getNetwork():null));
                }catch(IllegalArgumentException ignored){}finally{resolve(version);}
            });}
        };
        try{nsd.resolveService(next,resolver);}catch(RuntimeException error){resolving=false;resolve(version);}
    }
    private void post(int version,Runnable run){main.post(()->{if(active&&version==generation)run.run();});}
    void stop(){
        if(!active)return;active=false;generation++;pending.clear();names.clear();resolving=false;
        if(discovery!=null)try{nsd.stopServiceDiscovery(discovery);}catch(RuntimeException ignored){}
        if(Build.VERSION.SDK_INT>=34&&resolver!=null)try{nsd.stopServiceResolution(resolver);}catch(RuntimeException ignored){}
        discovery=null;resolver=null;if(multicast!=null&&multicast.isHeld())multicast.release();multicast=null;
    }
}
