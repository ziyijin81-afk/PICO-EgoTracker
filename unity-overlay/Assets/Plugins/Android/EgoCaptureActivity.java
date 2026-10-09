package org.local.picoegotracker;

import android.content.Context;
import android.content.Intent;
import android.content.ContentResolver;
import android.content.ContentValues;
import android.media.AudioManager;
import android.net.Uri;
import android.os.Environment;
import android.os.Handler;
import android.os.Looper;
import android.os.Process;
import android.os.SystemClock;
import android.provider.MediaStore;
import android.util.Log;
import android.view.KeyEvent;
import android.view.WindowManager;
import java.io.FileInputStream;
import java.io.OutputStream;
import com.unity3d.player.UnityPlayer;
import com.unity3d.player.UnityPlayerActivity;

public class EgoCaptureActivity extends UnityPlayerActivity {
    private static final String TAG = "EgoCaptureKeys";
    private final Handler volumeHandler = new Handler(Looper.getMainLooper());
    private AudioManager audioManager;
    private int lastMusicVolume = -1;
    private boolean watchingVolume;
    private static final String RECOVERY_PREFS = "egocapture_camera_recovery";
    private static final String LAST_RESTART = "last_restart_elapsed_ms";
    private final Runnable volumeWatcher = new Runnable() {
        @Override public void run() {
            if (!watchingVolume) return;
            int current = audioManager.getStreamVolume(AudioManager.STREAM_MUSIC);
            if (lastMusicVolume >= 0 && current != lastMusicVolume) {
                sendVolumeButton(current > lastMusicVolume ? "up" : "down", "audio");
            }
            lastMusicVolume = current;
            volumeHandler.postDelayed(this, 100);
        }
    };

    @Override protected void onResume() {
        super.onResume();
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        audioManager = (AudioManager) getSystemService(Context.AUDIO_SERVICE);
        lastMusicVolume = audioManager.getStreamVolume(AudioManager.STREAM_MUSIC);
        watchingVolume = true;
        volumeHandler.removeCallbacks(volumeWatcher);
        volumeHandler.postDelayed(volumeWatcher, 100);
    }

    @Override protected void onPause() {
        watchingVolume = false;
        volumeHandler.removeCallbacks(volumeWatcher);
        getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        super.onPause();
    }

    private void sendVolumeButton(String button, String source) {
        Log.i(TAG, source + " volume " + button);
        UnityPlayer.UnitySendMessage("EgoCaptureXRRoot", "OnVolumeButton", button);
    }

    public void markCameraReady() {
        getSharedPreferences(RECOVERY_PREFS, MODE_PRIVATE).edit()
            .remove(LAST_RESTART).apply();
    }

    // A PICO native camera can remain in OPENING after XR is suspended. Its
    // CreateCameraDevice API then rejects every reopen. Start a short-lived
    // foreground recovery activity in another process before exiting Unity;
    // Android 14 blocks relaunches sent from background alarms.
    public boolean restartAfterCameraFailure() {
        long now = SystemClock.elapsedRealtime();
        long last = getSharedPreferences(RECOVERY_PREFS, MODE_PRIVATE)
            .getLong(LAST_RESTART, 0L);
        if (last > 0L && now >= last && now - last < 120000L) {
            Log.w(TAG, "Camera restart suppressed by two-minute loop guard");
            return false;
        }
        getSharedPreferences(RECOVERY_PREFS, MODE_PRIVATE).edit()
            .putLong(LAST_RESTART, now).commit();
        try {
            Intent recovery = new Intent(this, EgoRecoveryActivity.class);
            recovery.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            startActivity(recovery);
        } catch (Exception failure) {
            Log.e(TAG, "Failed to start camera recovery activity", failure);
            return false;
        }
        Log.w(TAG, "Foreground recovery activity started for stuck native camera");
        volumeHandler.postDelayed(() -> {
            finishAffinity();
            Process.killProcess(Process.myPid());
        }, 500L);
        return true;
    }

    // Keep the original recording and publish a second copy through MediaStore,
    // where the headset Gallery can discover it on Android 10 and later.
    public String publishVideoToGallery(String sourcePath, String displayName) {
        ContentResolver resolver = getContentResolver();
        ContentValues values = new ContentValues();
        values.put(MediaStore.MediaColumns.DISPLAY_NAME, displayName);
        values.put(MediaStore.MediaColumns.MIME_TYPE, "video/mp4");
        values.put(MediaStore.MediaColumns.RELATIVE_PATH,
            Environment.DIRECTORY_DCIM + "/PICOEgoTracker/");
        values.put(MediaStore.MediaColumns.IS_PENDING, 1);
        Uri destination = resolver.insert(MediaStore.Video.Media.EXTERNAL_CONTENT_URI, values);
        if (destination == null) throw new IllegalStateException("MediaStore insert failed");
        try {
            try (FileInputStream input = new FileInputStream(sourcePath);
                 OutputStream output = resolver.openOutputStream(destination)) {
                if (output == null) throw new IllegalStateException("MediaStore output is null");
                byte[] buffer = new byte[131072];
                int count;
                while ((count = input.read(buffer)) != -1) output.write(buffer, 0, count);
                output.flush();
            }
            values.clear();
            values.put(MediaStore.MediaColumns.IS_PENDING, 0);
            resolver.update(destination, values, null, null);
            Log.i(TAG, "Gallery copy saved: " + destination);
            return destination.toString();
        } catch (Exception failure) {
            resolver.delete(destination, null, null);
            throw new RuntimeException("Gallery copy failed", failure);
        }
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent event) {
        int key = event.getKeyCode();
        if (key == KeyEvent.KEYCODE_VOLUME_UP || key == KeyEvent.KEYCODE_VOLUME_DOWN) {
            if (event.getAction() == KeyEvent.ACTION_DOWN && event.getRepeatCount() == 0) {
                sendVolumeButton(key == KeyEvent.KEYCODE_VOLUME_UP ? "up" : "down", "key");
            }
            return true;
        }
        return super.dispatchKeyEvent(event);
    }
}
