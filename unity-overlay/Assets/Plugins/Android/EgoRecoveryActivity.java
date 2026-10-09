package org.local.picoegotracker;

import android.app.Activity;
import android.content.Intent;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;

// Runs in :recovery so it survives Unity's process exit and can relaunch the
// app while still foreground. It never starts a recording.
public class EgoRecoveryActivity extends Activity {
    private static final String TAG = "EgoCaptureRecovery";

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        Log.w(TAG, "Recovery activity visible; waiting for Unity process to exit");
        new Handler(Looper.getMainLooper()).postDelayed(() -> {
            try {
                Intent launch = new Intent(this, EgoCaptureActivity.class);
                launch.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK);
                startActivity(launch);
                Log.w(TAG, "Relaunched EgoCaptureActivity from foreground recovery task");
            } catch (Exception failure) {
                Log.e(TAG, "Foreground camera relaunch failed", failure);
            }
            finish();
        }, 1800L);
    }
}
