package dev.sdas.luma;

import android.system.ErrnoException;
import android.system.OsConstants;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import dev.sdas.luma.net.NetworkDiagnostics;
import java.net.SocketException;
import org.junit.Test;
import org.junit.runner.RunWith;
import static org.junit.Assert.*;

/** Verifies the actual Android errno carrier, rather than a JVM Android stub. */
@RunWith(AndroidJUnit4.class)
public class NetworkDiagnosticsDeviceTest {
    @Test public void platformErrnoSurvivesSafeClassification() {
        SocketException error=new SocketException("Private URL and credentials must not be returned");
        error.initCause(new ErrnoException("connect",OsConstants.EACCES));
        NetworkDiagnostics.Failure value=NetworkDiagnostics.classify(error);
        assertEquals(OsConstants.EACCES,value.errno);
        assertEquals(NetworkDiagnostics.Reason.PERMISSION_DENIED,value.reason);
    }
}
