package dev.sdas.luma.net;

import org.json.JSONArray;
import org.json.JSONObject;
import org.junit.Test;
import static org.junit.Assert.*;

public class LanSkipMetadataTest {
    private LanApi.Session session(){return new LanApi.Session(LanEndpoint.parse("http://192.168.1.2:1234"),new LanApi.Info("desktop","Desktop"),LanProtocolTest.TOKEN);}
    @Test public void optionalSkipMetadataKeepsOlderHostsCompatible()throws Exception{
        JSONObject response=LanProtocolTest.playlist(LanProtocolTest.TOKEN);
        assertTrue(LanApi.parsePlaylist(response,session()).get(0).skipRanges.isEmpty());
        response.getJSONArray("items").getJSONObject(0).put("skipRanges",new JSONArray("[{\"kind\":\"intro\",\"startSeconds\":1,\"endSeconds\":3,\"source\":\"manual\",\"label\":\"Saved opening\"}]"));
        LanApi.Item item=LanApi.parsePlaylist(response,session()).get(0);assertEquals(1,item.skipRanges.size());assertEquals(3,item.skipRanges.get(0).endSeconds,0);assertEquals("Saved opening",item.skipRanges.get(0).label);
    }
    @Test public void unsafeSkipMetadataCannotTriggerASeek()throws Exception{
        JSONObject response=LanProtocolTest.playlist(LanProtocolTest.TOKEN),item=response.getJSONArray("items").getJSONObject(0);
        item.put("skipRanges",new JSONArray("[{\"kind\":\"intro\",\"startSeconds\":0,\"endSeconds\":999,\"source\":\"manual\"}]"));assertTrue(LanApi.parsePlaylist(response,session()).get(0).skipRanges.isEmpty());
        item.put("skipRanges","skip everything");try{LanApi.parsePlaylist(response,session());fail();}catch(LanApi.ApiException expected){}
    }
}
