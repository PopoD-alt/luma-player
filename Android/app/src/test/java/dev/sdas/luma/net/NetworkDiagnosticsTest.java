package dev.sdas.luma.net;

import java.io.IOException;
import java.net.SocketException;
import java.net.SocketTimeoutException;
import org.junit.Test;
import static org.junit.Assert.*;

public class NetworkDiagnosticsTest {
    @Test public void knownPlatformSignalsBecomeFixedLabelsWithoutPrivateMessages() {
        String secret=" http://private.invalid/media?token=do-not-report-this";
        String[] signals={"EPERM (Operation not permitted)","EACCES (Permission denied)","ECONNREFUSED (Connection refused)","ECONNRESET (Connection reset)","ECONNABORTED (Software caused connection abort)","ENETUNREACH (Network is unreachable)","EHOSTUNREACH (No route to host)","ENONET (Machine is not on the network)","Socket closed","Broken pipe","unexpected end of stream"};
        NetworkDiagnostics.Reason[] expected={NetworkDiagnostics.Reason.PERMISSION_DENIED,NetworkDiagnostics.Reason.PERMISSION_DENIED,NetworkDiagnostics.Reason.CONNECTION_REFUSED,NetworkDiagnostics.Reason.CONNECTION_RESET,NetworkDiagnostics.Reason.CONNECTION_ABORTED,NetworkDiagnostics.Reason.NETWORK_UNREACHABLE,NetworkDiagnostics.Reason.HOST_UNREACHABLE,NetworkDiagnostics.Reason.NETWORK_UNAVAILABLE,NetworkDiagnostics.Reason.SOCKET_CLOSED,NetworkDiagnostics.Reason.BROKEN_PIPE,NetworkDiagnostics.Reason.RESPONSE_EOF};
        for(int i=0;i<signals.length;i++){
            NetworkDiagnostics.Failure value=NetworkDiagnostics.classify(new IOException("Outer private wrapper"+secret,new SocketException(signals[i]+secret)));
            assertEquals(expected[i],value.reason);assertEquals(0,value.errno);assertFalse(value.reason.toString().contains("private"));assertFalse(value.reason.toString().contains("token"));
        }
        assertEquals(NetworkDiagnostics.Reason.TIMEOUT,NetworkDiagnostics.classify(new SocketTimeoutException(secret)).reason);
        assertEquals(NetworkDiagnostics.Reason.UNKNOWN,NetworkDiagnostics.classify(new IOException(secret)).reason);
    }
    @Test public void cyclicAndExcessiveCausesRemainBounded() {
        IOException first=new IOException("private-a"),second=new IOException("private-b");first.initCause(second);second.initCause(first);
        assertEquals(NetworkDiagnostics.Reason.UNKNOWN,NetworkDiagnostics.classify(first).reason);
        Throwable deep=new SocketException("ECONNRESET");for(int i=0;i<20;i++)deep=new IOException("private-wrapper",deep);
        assertEquals(NetworkDiagnostics.Reason.UNKNOWN,NetworkDiagnostics.classify(deep).reason);
    }
}
