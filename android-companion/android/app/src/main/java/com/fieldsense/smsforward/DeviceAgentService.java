package com.fieldsense.smsforward;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.util.Log;

import androidx.core.app.NotificationCompat;

import org.json.JSONObject;

/**
 * Lightweight foreground poller so older OEMs are less aggressive about killing the agent.
 */
public class DeviceAgentService extends Service {
    public static final String BRAND = "دروازه";
    public static final String STATUS_OPEN = "دروازه ها برای پیامک باز است";
    public static final String STATUS_CLOSED = "دروازه ها برای پیامک بسته است";

    private static final String TAG = "FieldSenseAgent";
    private static final String CHANNEL_ID = "darvazeh_agent_v3";
    private static final int NOTIF_ID = 4210;
    private static final long INTERVAL_MS = 12_000L;

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Runnable tick = new Runnable() {
        @Override
        public void run() {
            try {
                SmsForwardBridge.pollOnce(getApplicationContext(), ok ->
                        handler.post(() -> publishStatus(ok ? STATUS_OPEN : STATUS_CLOSED, true)));
            } catch (Exception e) {
                Log.w(TAG, "tick failed", e);
                publishStatus(STATUS_CLOSED, true);
            }
            handler.postDelayed(this, INTERVAL_MS);
        }
    };

    public static void start(Context context) {
        Intent i = new Intent(context, DeviceAgentService.class);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            context.startForegroundService(i);
        } else {
            context.startService(i);
        }
    }

    public static void stop(Context context) {
        showClosedNotification(context.getApplicationContext());
        context.stopService(new Intent(context, DeviceAgentService.class));
    }

    /** Update the persistent status notification from outside the service. */
    public static void showClosedNotification(Context context) {
        ensureChannelStatic(context);
        NotificationManager nm = context.getSystemService(NotificationManager.class);
        if (nm == null) return;
        nm.notify(NOTIF_ID, buildNotificationStatic(context, STATUS_CLOSED, false));
    }

    @Override
    public void onCreate() {
        super.onCreate();
        ensureChannel();
        startForeground(NOTIF_ID, buildNotification(STATUS_OPEN, true));
        handler.post(tick);
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        JSONObject settings = SmsForwardBridge.readSettings(this);
        if (settings == null || !settings.optBoolean("enabled", false)) {
            publishStatus(STATUS_CLOSED, false);
            stopSelf();
            return START_NOT_STICKY;
        }
        publishStatus(STATUS_OPEN, true);
        return START_STICKY;
    }

    @Override
    public void onDestroy() {
        handler.removeCallbacks(tick);
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    private void publishStatus(String text, boolean ongoing) {
        NotificationManager nm = getSystemService(NotificationManager.class);
        if (nm == null) return;
        nm.notify(NOTIF_ID, buildNotification(text, ongoing));
    }

    private Notification buildNotification(String text, boolean ongoing) {
        return buildNotificationStatic(this, text, ongoing);
    }

    private static Notification buildNotificationStatic(Context context, String text, boolean ongoing) {
        Intent open = context.getPackageManager().getLaunchIntentForPackage(context.getPackageName());
        if (open == null) open = new Intent(context, MainActivity.class);
        int flags = PendingIntent.FLAG_UPDATE_CURRENT;
        if (Build.VERSION.SDK_INT >= 31) flags |= PendingIntent.FLAG_IMMUTABLE;
        PendingIntent pi = PendingIntent.getActivity(context, 0, open, flags);

        return new NotificationCompat.Builder(context, CHANNEL_ID)
                .setSmallIcon(android.R.drawable.stat_notify_sync)
                .setContentTitle(BRAND)
                .setContentText(text)
                .setStyle(new NotificationCompat.BigTextStyle().bigText(text))
                .setContentIntent(pi)
                .setOngoing(ongoing)
                .setOnlyAlertOnce(true)
                .setPriority(NotificationCompat.PRIORITY_LOW)
                .build();
    }

    private void ensureChannel() {
        ensureChannelStatic(this);
    }

    private static void ensureChannelStatic(Context context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return;
        NotificationChannel channel = new NotificationChannel(
                CHANNEL_ID,
                BRAND,
                NotificationManager.IMPORTANCE_LOW
        );
        channel.setDescription(BRAND + " — وضعیت پیامک");
        NotificationManager nm = context.getSystemService(NotificationManager.class);
        if (nm != null) nm.createNotificationChannel(channel);
    }
}
