package com.fieldsense.smsforward;

import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Build;
import android.telephony.SmsManager;
import android.util.Log;

import androidx.core.app.NotificationCompat;
import androidx.core.app.NotificationManagerCompat;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.TimeZone;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Device agent for FieldSense API:
 * - uploads inbound SMS
 * - heartbeats
 * - polls outbound jobs and sends SMS via Android SmsManager
 * Designed for API 24+ (Android 7) including older Huawei/Xiaomi builds.
 */
public final class SmsForwardBridge {
    private static final String TAG = "FieldSenseSms";
    private static final String PREFS = "fieldsense_sms";
    private static final String CAP_PREFS = "CapacitorStorage";
    private static final String KEY = "fieldsense.device-agent.v2";
    private static final String LEGACY_KEY = "fieldsense.sms-forward.v1";
    private static final String CHANNEL_ID = "sms_relay";
    private static final int NOTIF_ID = 4201;
    private static final ExecutorService IO = Executors.newSingleThreadExecutor();

    private SmsForwardBridge() {}

    public static void handleIncomingSms(Context context, String from, String body) {
        Context app = context.getApplicationContext();
        JSONObject settings = readSettings(app);
        if (settings == null || !settings.optBoolean("enabled", false)) {
            Log.i(TAG, "SMS ignored — relay disabled");
            showNotification(app, "SMS not relayed", "Turn on Relay and Save in FieldSense.", null);
            return;
        }

        String receivedAt = nowIsoUtc();
        String preview = body == null ? "" : body;
        if (preview.length() > 80) preview = preview.substring(0, 80) + "…";

        writeLastEvent(app, from, body, receivedAt, "received");
        showNotification(app, "SMS from " + safe(from), preview.isEmpty() ? "Uploading…" : preview, openAppIntent(app));

        IO.execute(() -> {
            try {
                int code = postInbound(settings, from, body, receivedAt);
                writeLastEvent(app, from, body, receivedAt, "uploaded HTTP " + code);
                showNotification(app, "SMS uploaded", "Inbox + optional email forward via server", openAppIntent(app));
            } catch (Exception e) {
                String detail = e.getMessage() == null ? "upload failed" : e.getMessage();
                Log.e(TAG, "Inbound upload failed", e);
                writeLastEvent(app, from, body, receivedAt, "error: " + detail);
                showNotification(app, "SMS upload failed", detail, openAppIntent(app));
            }
        });
    }

    public static void saveSettingsJson(Context context, String json) {
        SharedPreferences prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        prefs.edit().putString(KEY, json).apply();
        context.getSharedPreferences(CAP_PREFS, Context.MODE_PRIVATE)
                .edit()
                .putString(KEY, json)
                .apply();
        try {
            JSONObject settings = new JSONObject(json);
            if (settings.optBoolean("enabled", false)) {
                DeviceAgentService.start(context.getApplicationContext());
            } else {
                DeviceAgentService.stop(context.getApplicationContext());
            }
        } catch (Exception ignored) {
        }
    }

    public static String getLastEventJson(Context context) {
        return context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .getString("last_event", "{}");
    }

    public static String testHeartbeat(Context context) {
        JSONObject settings = readSettings(context);
        if (settings == null) return "No settings saved";
        try {
            heartbeat(settings);
            writeLastEvent(context, "server", "heartbeat", nowIsoUtc(), "heartbeat ok");
            DeviceAgentService.start(context.getApplicationContext());
            return "Heartbeat OK — relay service started";
        } catch (Exception e) {
            String msg = e.getMessage() == null ? "failed" : e.getMessage();
            writeLastEvent(context, "server", "heartbeat", nowIsoUtc(), "heartbeat error: " + msg);
            return "Heartbeat failed: " + msg;
        }
    }

    public static void pollOnce(Context context) {
        JSONObject settings = readSettings(context);
        if (settings == null || !settings.optBoolean("enabled", false)) return;
        IO.execute(() -> {
            try {
                heartbeat(settings);
                JSONArray jobs = fetchPending(settings);
                for (int i = 0; i < jobs.length(); i++) {
                    JSONObject job = jobs.getJSONObject(i);
                    String jobId = job.optString("id", "");
                    String to = job.optString("toNumber", "");
                    String body = job.optString("body", "");
                    try {
                        sendSms(context, to, body);
                        reportResult(settings, jobId, "sent", null);
                        writeLastEvent(context, to, body, nowIsoUtc(), "outbound sent job=" + jobId);
                        showNotification(context, "SMS sent", "To " + to, openAppIntent(context));
                    } catch (Exception sendErr) {
                        String err = sendErr.getMessage() == null ? "send failed" : sendErr.getMessage();
                        reportResult(settings, jobId, "failed", err);
                        writeLastEvent(context, to, body, nowIsoUtc(), "outbound failed: " + err);
                        showNotification(context, "SMS send failed", err, openAppIntent(context));
                    }
                }
            } catch (Exception e) {
                Log.w(TAG, "pollOnce failed", e);
                writeLastEvent(context, "server", "poll", nowIsoUtc(),
                        "poll error: " + (e.getMessage() == null ? "failed" : e.getMessage()));
            }
        });
    }

    private static void sendSms(Context context, String to, String body) {
        if (to == null || to.trim().isEmpty()) throw new IllegalArgumentException("Empty destination");
        if (body == null) body = "";
        SmsManager sms = SmsManager.getDefault();
        if (body.length() > 160) {
            sms.sendMultipartTextMessage(to, null, sms.divideMessage(body), null, null);
        } else {
            sms.sendTextMessage(to, null, body, null, null);
        }
    }

    private static void heartbeat(JSONObject settings) throws Exception {
        request(settings, "POST", "/api/device/heartbeat", null);
    }

    private static int postInbound(JSONObject settings, String from, String body, String receivedAt) throws Exception {
        JSONObject payload = new JSONObject();
        payload.put("from", from);
        payload.put("body", body);
        payload.put("receivedAt", receivedAt);
        HttpURLConnection conn = request(settings, "POST", "/api/device/inbound-sms", payload.toString());
        int code = conn.getResponseCode();
        conn.disconnect();
        if (code < 200 || code >= 300) throw new IllegalStateException("HTTP " + code);
        return code;
    }

    private static JSONArray fetchPending(JSONObject settings) throws Exception {
        HttpURLConnection conn = request(settings, "GET", "/api/device/outbound/pending", null);
        int code = conn.getResponseCode();
        String raw = readBody(conn, code);
        conn.disconnect();
        if (code < 200 || code >= 300) throw new IllegalStateException("HTTP " + code + " " + raw);
        return new JSONArray(raw);
    }

    private static void reportResult(JSONObject settings, String jobId, String status, String error) throws Exception {
        JSONObject payload = new JSONObject();
        payload.put("status", status);
        if (error != null) payload.put("error", error);
        HttpURLConnection conn = request(settings, "POST", "/api/device/outbound/" + jobId + "/result", payload.toString());
        int code = conn.getResponseCode();
        conn.disconnect();
        if (code < 200 || code >= 300) throw new IllegalStateException("result HTTP " + code);
    }

    private static HttpURLConnection request(JSONObject settings, String method, String path, String jsonBody) throws Exception {
        String base = settings.optString("apiBase", "").trim().replaceAll("/$", "");
        String token = settings.optString("deviceToken", "").trim();
        if (base.isEmpty()) throw new IllegalArgumentException("Set API base URL");
        if (token.isEmpty()) throw new IllegalArgumentException("Set device token");

        URL url = new URL(base + path);
        HttpURLConnection conn = (HttpURLConnection) url.openConnection();
        conn.setConnectTimeout(20000);
        conn.setReadTimeout(20000);
        conn.setRequestMethod(method);
        conn.setRequestProperty("Accept", "application/json");
        conn.setRequestProperty("X-Device-Token", token);
        if (jsonBody != null) {
            byte[] bytes = jsonBody.getBytes(StandardCharsets.UTF_8);
            conn.setDoOutput(true);
            conn.setRequestProperty("Content-Type", "application/json");
            conn.setFixedLengthStreamingMode(bytes.length);
            try (OutputStream os = conn.getOutputStream()) {
                os.write(bytes);
            }
        }
        return conn;
    }

    private static String readBody(HttpURLConnection conn, int code) throws Exception {
        InputStream stream = code >= 400 ? conn.getErrorStream() : conn.getInputStream();
        if (stream == null) return "";
        StringBuilder sb = new StringBuilder();
        try (BufferedReader br = new BufferedReader(new InputStreamReader(stream, StandardCharsets.UTF_8))) {
            String line;
            while ((line = br.readLine()) != null) sb.append(line);
        }
        return sb.toString();
    }

    private static void writeLastEvent(Context context, String from, String body, String receivedAt, String status) {
        try {
            JSONObject o = new JSONObject();
            o.put("from", from);
            o.put("body", body);
            o.put("receivedAt", receivedAt);
            o.put("status", status);
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                    .edit()
                    .putString("last_event", o.toString())
                    .apply();
        } catch (Exception ignored) {
        }
    }

    public static JSONObject readSettings(Context context) {
        try {
            SharedPreferences prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
            String raw = prefs.getString(KEY, null);
            if (raw == null || raw.isEmpty()) {
                SharedPreferences cap = context.getSharedPreferences(CAP_PREFS, Context.MODE_PRIVATE);
                raw = cap.getString(KEY, null);
                if (raw == null) raw = cap.getString("_cap_" + KEY, null);
            }
            // Migrate legacy SMTP-only settings if present and new key missing.
            if (raw == null) {
                String legacy = prefs.getString(LEGACY_KEY, null);
                if (legacy == null) {
                    SharedPreferences cap = context.getSharedPreferences(CAP_PREFS, Context.MODE_PRIVATE);
                    legacy = cap.getString(LEGACY_KEY, null);
                }
                if (legacy != null) {
                    JSONObject old = new JSONObject(legacy);
                    JSONObject migrated = new JSONObject();
                    migrated.put("enabled", old.optBoolean("enabled", false));
                    String endpoint = old.optString("apiEndpoint", "");
                    if (endpoint.contains("/api/")) {
                        migrated.put("apiBase", endpoint.replaceAll("/api/.*$", ""));
                    } else {
                        migrated.put("apiBase", endpoint);
                    }
                    migrated.put("deviceToken", old.optString("apiAuthToken", ""));
                    raw = migrated.toString();
                }
            }
            if (raw == null) return null;
            return new JSONObject(raw);
        } catch (Exception e) {
            Log.e(TAG, "Failed reading settings", e);
            return null;
        }
    }

    private static Intent openAppIntent(Context context) {
        Intent i = context.getPackageManager().getLaunchIntentForPackage(context.getPackageName());
        if (i == null) i = new Intent(context, MainActivity.class);
        i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        return i;
    }

    private static void showNotification(Context context, String title, String text, Intent contentIntent) {
        ensureChannel(context);
        NotificationCompat.Builder builder = new NotificationCompat.Builder(context, CHANNEL_ID)
                .setSmallIcon(android.R.drawable.ic_dialog_email)
                .setContentTitle(title)
                .setContentText(text)
                .setStyle(new NotificationCompat.BigTextStyle().bigText(text))
                .setPriority(NotificationCompat.PRIORITY_HIGH)
                .setAutoCancel(true);

        if (contentIntent != null) {
            int flags = PendingIntent.FLAG_UPDATE_CURRENT;
            if (Build.VERSION.SDK_INT >= 31) {
                flags |= PendingIntent.FLAG_IMMUTABLE;
            }
            PendingIntent pi = PendingIntent.getActivity(context, 0, contentIntent, flags);
            builder.setContentIntent(pi);
        }

        try {
            NotificationManagerCompat.from(context).notify(NOTIF_ID, builder.build());
        } catch (SecurityException e) {
            Log.w(TAG, "Notification permission missing", e);
        }
    }

    private static void ensureChannel(Context context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return;
        NotificationChannel channel = new NotificationChannel(
                CHANNEL_ID,
                "SMS relay",
                NotificationManager.IMPORTANCE_HIGH
        );
        channel.setDescription("Inbound/outbound SMS relay status");
        NotificationManager nm = context.getSystemService(NotificationManager.class);
        if (nm != null) nm.createNotificationChannel(channel);
    }

    private static String safe(String value) {
        return value == null || value.isEmpty() ? "unknown" : value;
    }

    /** API 24/25 safe — avoid java.time (API 26+) without desugaring. */
    private static String nowIsoUtc() {
        SimpleDateFormat fmt = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.US);
        fmt.setTimeZone(TimeZone.getTimeZone("UTC"));
        return fmt.format(new Date());
    }
}
