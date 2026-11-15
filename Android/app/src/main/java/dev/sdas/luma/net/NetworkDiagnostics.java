package dev.sdas.luma.net;

import java.net.SocketTimeoutException;
import java.util.Locale;

/** Maps platform failures to fixed labels; exception messages never leave this class. */
public final class NetworkDiagnostics {
    private NetworkDiagnostics() {}
    public enum Reason { UNKNOWN, PERMISSION_DENIED, CONNECTION_REFUSED, CONNECTION_RESET, CONNECTION_ABORTED, NETWORK_UNREACHABLE, HOST_UNREACHABLE, NETWORK_DOWN, NETWORK_UNAVAILABLE, NOT_CONNECTED, BROKEN_PIPE, SOCKET_CLOSED, TIMEOUT, CANCELLED, RESPONSE_EOF }
    public static final class Failure {
        public final Reason reason;
        public final int errno;
        Failure(Reason reason,int errno){this.reason=reason;this.errno=errno;}
    }
    public static Failure classify(Throwable failure) {
        Reason reason=Reason.UNKNOWN;int errno=0;
        for(int depth=0;failure!=null&&depth<8;depth++,failure=failure.getCause()) {
            if(failure.getClass().getName().equals("android.system.ErrnoException")) {
                try {int value=failure.getClass().getField("errno").getInt(failure);if(value>0&&value<=4095){errno=value;Reason known=fromErrno(value);if(known!=Reason.UNKNOWN)return new Failure(known,errno);}}
                catch(ReflectiveOperationException|RuntimeException ignored){}
            }
            if(failure instanceof SocketTimeoutException)reason=Reason.TIMEOUT;
            String message=failure.getMessage();
            if(message==null)continue;
            String lower=message.substring(0,Math.min(message.length(),2048)).toLowerCase(Locale.ROOT);
            Reason known=fromMessage(lower);if(known!=Reason.UNKNOWN)reason=known;
        }
        return new Failure(reason,errno);
    }
    private static Reason fromErrno(int value) {
        switch(value) {
            case 1:case 13:return Reason.PERMISSION_DENIED;
            case 111:return Reason.CONNECTION_REFUSED;
            case 104:return Reason.CONNECTION_RESET;
            case 103:return Reason.CONNECTION_ABORTED;
            case 101:return Reason.NETWORK_UNREACHABLE;
            case 113:return Reason.HOST_UNREACHABLE;
            case 100:return Reason.NETWORK_DOWN;
            case 64:return Reason.NETWORK_UNAVAILABLE;
            case 107:return Reason.NOT_CONNECTED;
            case 32:return Reason.BROKEN_PIPE;
            case 110:return Reason.TIMEOUT;
            case 125:return Reason.CANCELLED;
            default:return Reason.UNKNOWN;
        }
    }
    private static Reason fromMessage(String value) {
        if(value.contains("permission denied")||value.contains("operation not permitted")||value.contains("eacces")||value.contains("eperm"))return Reason.PERMISSION_DENIED;
        if(value.contains("connection refused")||value.contains("econnrefused"))return Reason.CONNECTION_REFUSED;
        if(value.contains("connection reset")||value.contains("econnreset"))return Reason.CONNECTION_RESET;
        if(value.contains("connection abort")||value.contains("econnaborted"))return Reason.CONNECTION_ABORTED;
        if(value.contains("network is unreachable")||value.contains("enetunreach"))return Reason.NETWORK_UNREACHABLE;
        if(value.contains("no route to host")||value.contains("ehostunreach"))return Reason.HOST_UNREACHABLE;
        if(value.contains("network is down")||value.contains("enetdown"))return Reason.NETWORK_DOWN;
        if(value.contains("not on the network")||value.contains("enonet"))return Reason.NETWORK_UNAVAILABLE;
        if(value.contains("not connected")||value.contains("enotconn"))return Reason.NOT_CONNECTED;
        if(value.contains("broken pipe")||value.contains("epipe"))return Reason.BROKEN_PIPE;
        if(value.contains("socket closed")||value.contains("socket is closed"))return Reason.SOCKET_CLOSED;
        if(value.contains("timed out")||value.contains("etimedout"))return Reason.TIMEOUT;
        if(value.contains("canceled")||value.contains("cancelled")||value.contains("ecanceled"))return Reason.CANCELLED;
        if(value.contains("unexpected end of stream")||value.contains("unexpected end of file"))return Reason.RESPONSE_EOF;
        return Reason.UNKNOWN;
    }
}
