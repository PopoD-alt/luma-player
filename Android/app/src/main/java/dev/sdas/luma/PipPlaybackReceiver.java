package dev.sdas.luma;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/** Explicit, non-exported receiver for immutable System UI PiP transport actions. */
public final class PipPlaybackReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context, Intent intent) {
        MainActivity.dispatchPipControl(intent);
    }
}
