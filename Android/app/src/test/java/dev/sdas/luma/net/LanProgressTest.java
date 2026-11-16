package dev.sdas.luma.net;

import org.json.JSONObject;
import org.junit.Test;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import static org.junit.Assert.*;

public class LanProgressTest {
    private static LanApi.Session session(){return new LanApi.Session(LanEndpoint.parse("http://127.0.0.1:1234"),new LanApi.Info("session","Desktop",true),LanProtocolTest.TOKEN);}
    @Test public void olderHostsKeepDesktopResumeAndDoNotReceiveProgressPosts()throws Exception{
        try(LanProtocolTest.Server server=new LanProtocolTest.Server()){
            LanApi api=new LanApi();server.add(200,"{\"version\":1,\"name\":\"Desktop\",\"id\":\"session\",\"pairingRequired\":true}");
            LanApi.Info info=api.info(server.endpoint());server.request();assertFalse(info.supportsProgress);
            LanApi.Session old=new LanApi.Session(server.endpoint(),info,LanProtocolTest.TOKEN);
            LanApi.Item item=LanApi.parsePlaylist(LanProtocolTest.playlist(LanProtocolTest.TOKEN),old).get(0);
            assertEquals(2,item.computerProgressSeconds,0);assertFalse(item.hasMobileProgress());assertFalse(item.computerCompleted);
            try{api.reportProgress(old,"episode-1",3,12,false);fail();}catch(LanApi.ApiException expected){assertEquals(0,expected.status);}
            assertNull(server.requests.poll(100,TimeUnit.MILLISECONDS));
        }
    }
    @Test public void actualProgressPostUsesBearerAndFourMetadataFieldsOnly()throws Exception{
        try(LanProtocolTest.Server server=new LanProtocolTest.Server()){
            LanApi api=new LanApi();LanApi.Session session=new LanApi.Session(server.endpoint(),new LanApi.Info("session","Desktop",true),LanProtocolTest.TOKEN);
            server.add(200,"{\"version\":1,\"positionSeconds\":11.5,\"durationSeconds\":12,\"completed\":false,\"updatedAtUnixSeconds\":1794787200}");
            LanApi.ProgressReceipt receipt=api.reportProgress(session,"episode-1",50,12,false);
            LanProtocolTest.Request request=server.request();assertEquals("POST /v1/progress HTTP/1.1",request.line);assertEquals("Bearer "+LanProtocolTest.TOKEN,request.headers.get("authorization"));
            JSONObject body=new JSONObject(request.body);assertEquals(4,body.length());assertEquals("episode-1",body.getString("itemId"));assertEquals(12,body.getDouble("positionSeconds"),0);assertEquals(12,body.getDouble("durationSeconds"),0);assertFalse(body.getBoolean("completed"));assertFalse(request.body.contains(LanProtocolTest.TOKEN));
            assertEquals(11.5,receipt.positionSeconds,0);assertEquals(1794787200,receipt.updatedAtUnixSeconds,0);
        }
    }
    @Test public void separateDeviceProgressAndCompletionSurvivePlaylistParsing()throws Exception{
        JSONObject response=LanProtocolTest.playlist(LanProtocolTest.TOKEN),entry=response.getJSONArray("items").getJSONObject(0);
        entry.put("computerProgressSeconds",10).put("computerUpdatedAtUnixSeconds",1794787200).put("computerCompleted",false)
             .put("mobileProgressSeconds",4).put("mobileDurationSeconds",12).put("mobileUpdatedAtUnixSeconds",1794787300).put("mobileCompleted",true);
        LanApi.Item item=LanApi.parsePlaylist(response,session()).get(0);
        assertEquals("Legacy desktop resume stays distinct",2,item.resumeSeconds,0);assertEquals(10,item.computerProgressSeconds,0);assertEquals(4,item.mobileProgressSeconds,0);assertEquals(12,item.mobileDurationSeconds,0);assertTrue(item.mobileCompleted);assertFalse(item.computerCompleted);
        entry.put("computerProgressSeconds",90).put("mobileProgressSeconds",80).put("mobileDurationSeconds",100);
        item=LanApi.parsePlaylist(response,session()).get(0);assertEquals(12,item.computerProgressSeconds,0);assertEquals(12,item.mobileProgressSeconds,0);assertEquals(12,item.mobileDurationSeconds,0);
        entry.put("durationSeconds",0).put("resumeSeconds",0).put("mobileProgressSeconds",50).put("mobileDurationSeconds",60);
        item=LanApi.parsePlaylist(response,session()).get(0);assertEquals(0,item.durationSeconds,0);assertEquals(50,item.mobileProgressSeconds,0);assertEquals(60,item.mobileDurationSeconds,0);
    }
    @Test public void optionalProgressIsStrictlyTypedBoundedAndBackwardCompatible()throws Exception{
        for(String field:new String[]{"computerProgressSeconds","mobileProgressSeconds","mobileDurationSeconds","computerUpdatedAtUnixSeconds","mobileUpdatedAtUnixSeconds"}){
            JSONObject response=LanProtocolTest.playlist(LanProtocolTest.TOKEN),entry=response.getJSONArray("items").getJSONObject(0);
            entry.put(field,-1);LanProtocolTest.rejectApi(()->LanApi.parsePlaylist(response,session()));
            entry.put(field,"2");LanProtocolTest.rejectApi(()->LanApi.parsePlaylist(response,session()));
            entry.put(field,field.contains("Updated")?253402300800.0:31536001);LanProtocolTest.rejectApi(()->LanApi.parsePlaylist(response,session()));
            entry.put(field,JSONObject.NULL);assertEquals(1,LanApi.parsePlaylist(response,session()).size());
        }
        for(String field:new String[]{"computerCompleted","mobileCompleted"}){
            JSONObject response=LanProtocolTest.playlist(LanProtocolTest.TOKEN);response.getJSONArray("items").getJSONObject(0).put(field,"true");LanProtocolTest.rejectApi(()->LanApi.parsePlaylist(response,session()));
        }
        for(double value:new double[]{Double.NaN,Double.POSITIVE_INFINITY,-1,31536001}){
            try{new LanApi().reportProgress(session(),"episode-1",value,12,false);fail();}catch(LanApi.ApiException expected){}
        }
        for(String id:new String[]{"../private","a?token=x","/v1/media/episode"})try{new LanApi().reportProgress(session(),id,1,12,false);fail();}catch(LanApi.ApiException expected){}
    }
    @Test public void latestBackwardSeekWinsAndRapidEventsAreCoalesced(){
        ProgressOutbox queue=new ProgressOutbox();queue.offer(new ProgressOutbox.Snapshot("one",100,120,false));queue.offer(new ProgressOutbox.Snapshot("one",20,120,false));
        ProgressOutbox.Snapshot first=queue.take(0);assertEquals(20,first.positionSeconds,0);
        queue.offer(new ProgressOutbox.Snapshot("one",18,120,false));queue.offer(new ProgressOutbox.Snapshot("one",10,120,false));assertNull(queue.take(9000));queue.finished(100,200);
        assertNull(queue.take(2499));assertEquals(1,queue.delay(2499));assertEquals(10,queue.take(2500).positionSeconds,0);
    }
    @Test public void recentItemFinalPositionsAreBoundedAndThrottleRevocationAreHonored(){
        ProgressOutbox queue=new ProgressOutbox();for(int i=0;i<12;i++)queue.offer(new ProgressOutbox.Snapshot("item"+i,i,20,false));assertEquals(8,queue.size());assertEquals("item4",queue.take(0).itemId);
        queue.finished(100,429);assertNull(queue.take(60099));assertEquals("item5",queue.take(60100).itemId);queue.finished(60101,401);assertEquals(0,queue.size());queue.offer(new ProgressOutbox.Snapshot("new",1,20,false));assertNull(queue.take(100000));
        ProgressOutbox other=new ProgressOutbox();other.offer(new ProgressOutbox.Snapshot("one",0,0,false));other.close();assertNull(other.take(0));
        ProgressOutbox recent=new ProgressOutbox();for(int i=0;i<8;i++)recent.offer(new ProgressOutbox.Snapshot("item"+i,i,20,false));recent.offer(new ProgressOutbox.Snapshot("item0",15,20,false));recent.offer(new ProgressOutbox.Snapshot("item8",8,20,false));assertEquals("Oldest checkpoint, not freshly updated item0, is evicted","item2",recent.take(0).itemId);
    }
    @Test public void backgroundPublisherReportsActualAcknowledgementWithoutBlockingCaller()throws Exception{
        try(LanProtocolTest.Server server=new LanProtocolTest.Server()){
            LanApi.Session session=new LanApi.Session(server.endpoint(),new LanApi.Info("session","Desktop",true),LanProtocolTest.TOKEN);
            server.add(200,"{\"version\":1}");CountDownLatch accepted=new CountDownLatch(1);
            try(ProgressSync publisher=new ProgressSync(new LanApi(),session,(id,ok,status)->{if(id.equals("episode-1")&&ok)accepted.countDown();})){
                publisher.offer("episode-1",4500,12000,false);assertTrue(accepted.await(2,TimeUnit.SECONDS));assertEquals(4.5,new JSONObject(server.request().body).getDouble("positionSeconds"),0);
            }
        }
    }
}
