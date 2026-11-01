package com.fieldsense.smsforward;

import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.net.Uri;
import android.util.Log;

import org.json.JSONObject;

import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Reads FieldSense SMS settings and dispatches Gmail compose + optional API push.
 */
public final class SmsForwardBridge {
    private static final String TAG = "FieldSenseSms";
    private static final String PREFS = "CapacitorStorage";
    private static final String KEY = "fieldsense.sms-forward.v1";
    private static final ExecutorService IO = Executors.newSingleThreadExecutor();

    private SmsForwardBridge() {}

    public static void handleIncomingSms(Context context, String from, String body) {
        JSONObject settings = readSettings(context);
        if (settings == null || !settings.optBoolean("enabled", false)) {
            Log.i(TAG, "SMS ignored — forward disabled or missing settings");
            return;
        }

        String receivedAt = java.time.Instant.now().toString();

        if (settings.optBoolean("openGmailApp", true)) {
            openGmail(context, settings.optString("forwardEmail", ""), from, body, receivedAt);
        }

        if (settings.optBoolean("apiEnabled", false)) {
            IO.execute(() -> pushApi(settings, from, body, receivedAt));
        }
    }

    public static void saveSettingsJson(Context context, String json) {
        SharedPreferences prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        prefs.edit().putString(KEY, json).apply();
    }

    private static JSONObject readSettings(Context context) {
        try {
            SharedPreferences prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
            String raw = prefs.getString(KEY, null);
            if (raw == null || raw.isEmpty()) {
                // Capacitor Preferences may prefix keys depending on version; try common forms.
                raw = prefs.getString("_cap_" + KEY, null);
            }
            if (raw == null) return null;
            return new JSONObject(raw);
        } catch (Exception e) {
            Log.e(TAG, "Failed reading settings", e);
            return null;
        }
    }

    private static void openGmail(Context context, String email, String from, String body, String receivedAt) {
        if (email == null || !email.contains("@")) {
            Log.w(TAG, "No valid forward email configured");
            return;
        }
        String subject = "SMS from " + (from == null || from.isEmpty() ? "unknown" : from);
        String text = "From: " + from + "\nReceived: " + receivedAt + "\nSource: device SMS\n\n" + body;

        try {
            Intent gmail = new Intent(Intent.ACTION_SEND);
            gmail.setType("message/rfc822");
            gmail.putExtra(Intent.EXTRA_EMAIL, new String[]{email});
            gmail.putExtra(Intent.EXTRA_SUBJECT, subject);
            gmail.putExtra(Intent.EXTRA_TEXT, text);
            gmail.setPackage("com.google.android.gm");
            gmail.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            context.startActivity(gmail);
            return;
        } catch (Exception ignored) {
            // Fall through to mailto chooser.
        }

        Intent mailto = new Intent(Intent.ACTION_SENDTO);
        mailto.setData(Uri.parse("mailto:" + Uri.encode(email)));
        mailto.putExtra(Intent.EXTRA_SUBJECT, subject);
        mailto.putExtra(Intent.EXTRA_TEXT, text);
        mailto.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        Intent chooser = Intent.createChooser(mailto, "Send SMS via email");
        chooser.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        context.startActivity(chooser);
    }

    private static void pushApi(JSONObject settings, String from, String body, String receivedAt) {
        String endpoint = settings.optString("apiEndpoint", "").trim();
        if (endpoint.isEmpty()) {
            Log.w(TAG, "API enabled but endpoint empty");
            return;
        }
        try {
            JSONObject sms = new JSONObject();
            sms.put("from", from);
            sms.put("body", body);
            sms.put("receivedAt", receivedAt);
            sms.put("simulated", false);

            JSONObject notification = new JSONObject();
            notification.put("title", "SMS from " + (from == null || from.isEmpty() ? "unknown" : from));
            notification.put("message", body);

            JSONObject message = new JSONObject();
            message.put("from", from);
            message.put("body", body);
            message.put("receivedAt", receivedAt);
            message.put("simulated", false);

            JSONObject payload = new JSONObject();
            payload.put("type", "sms.received");
            payload.put("notification", notification);
            payload.put("message", message);
            payload.put("sms", sms);

            URL url = new URL(endpoint);
            HttpURLConnection conn = (HttpURLConnection) url.openConnection();
            conn.setConnectTimeout(15000);
            conn.setReadTimeout(15000);
            conn.setRequestMethod("POST");
            conn.setDoOutput(true);
            conn.setRequestProperty("Content-Type", "application/json");
            conn.setRequestProperty("Accept", "application/json");

            String header = settings.optString("apiAuthHeader", "Authorization").trim();
            String token = settings.optString("apiAuthToken", "").trim();
            if (!header.isEmpty() && !token.isEmpty()) {
                if (header.equalsIgnoreCase("Authorization") && !token.toLowerCase().startsWith("bearer ")) {
                    token = "Bearer " + token;
                }
                conn.setRequestProperty(header, token);
            }

            byte[] bytes = payload.toString().getBytes(StandardCharsets.UTF_8);
            conn.setFixedLengthStreamingMode(bytes.length);
            try (OutputStream os = conn.getOutputStream()) {
                os.write(bytes);
            }
            int code = conn.getResponseCode();
            Log.i(TAG, "API push status=" + code);
            conn.disconnect();
        } catch (Exception e) {
            Log.e(TAG, "API push failed", e);
        }
    }
}
