package com.fieldsense.smsforward;

import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.net.Uri;
import android.os.Build;
import android.util.Log;

import androidx.core.app.NotificationCompat;
import androidx.core.app.NotificationManagerCompat;

import org.json.JSONObject;

import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.Properties;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

import javax.mail.Authenticator;
import javax.mail.Message;
import javax.mail.PasswordAuthentication;
import javax.mail.Session;
import javax.mail.Transport;
import javax.mail.internet.InternetAddress;
import javax.mail.internet.MimeMessage;

/**
 * Forwards incoming SMS via SMTP (real auto-send) + optional API push.
 * Opening the Gmail app from a background receiver is blocked on modern Android,
 * so compose-only is a fallback when the user taps a notification.
 */
public final class SmsForwardBridge {
    private static final String TAG = "FieldSenseSms";
    private static final String PREFS = "fieldsense_sms";
    private static final String CAP_PREFS = "CapacitorStorage";
    private static final String KEY = "fieldsense.sms-forward.v1";
    private static final String CHANNEL_ID = "sms_forward";
    private static final int NOTIF_ID = 4201;
    private static final ExecutorService IO = Executors.newSingleThreadExecutor();

    private SmsForwardBridge() {}

    public static void handleIncomingSms(Context context, String from, String body) {
        Context app = context.getApplicationContext();
        JSONObject settings = readSettings(app);
        if (settings == null || !settings.optBoolean("enabled", false)) {
            Log.i(TAG, "SMS ignored — forward disabled or missing settings");
            showNotification(app, "SMS not forwarded", "Turn on Forward and Save settings in FieldSense SMS.", null);
            return;
        }

        String receivedAt = java.time.Instant.now().toString();
        String preview = body == null ? "" : body;
        if (preview.length() > 80) preview = preview.substring(0, 80) + "…";

        writeLastEvent(app, from, body, receivedAt, "received");
        showNotification(
                app,
                "SMS from " + safe(from),
                preview.isEmpty() ? "Forwarding…" : preview,
                buildGmailIntent(app, settings, from, body, receivedAt)
        );

        IO.execute(() -> {
            boolean smtpOk = false;
            String smtpDetail = "skipped";
            if (settings.optBoolean("smtpEnabled", true)) {
                try {
                    sendSmtp(settings, from, body, receivedAt);
                    smtpOk = true;
                    smtpDetail = "sent";
                    Log.i(TAG, "SMTP send OK");
                } catch (Exception e) {
                    smtpDetail = e.getMessage() == null ? "SMTP failed" : e.getMessage();
                    Log.e(TAG, "SMTP send failed", e);
                }
            }

            String apiDetail = "skipped";
            if (settings.optBoolean("apiEnabled", false)) {
                try {
                    int code = pushApi(settings, from, body, receivedAt);
                    apiDetail = "HTTP " + code;
                } catch (Exception e) {
                    apiDetail = e.getMessage() == null ? "API failed" : e.getMessage();
                    Log.e(TAG, "API push failed", e);
                }
            }

            writeLastEvent(app, from, body, receivedAt, "smtp=" + smtpDetail + "; api=" + apiDetail);
            showNotification(
                    app,
                    smtpOk ? "SMS emailed" : "SMS forward issue",
                    smtpOk
                            ? ("Sent to " + settings.optString("forwardEmail", ""))
                            : ("Email not sent: " + smtpDetail + " (tap to open Gmail draft)"),
                    buildGmailIntent(app, settings, from, body, receivedAt)
            );
        });
    }

    public static void saveSettingsJson(Context context, String json) {
        SharedPreferences prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        prefs.edit().putString(KEY, json).apply();
        // Also mirror into Capacitor storage for compatibility.
        context.getSharedPreferences(CAP_PREFS, Context.MODE_PRIVATE)
                .edit()
                .putString(KEY, json)
                .apply();
    }

    public static String getLastEventJson(Context context) {
        return context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .getString("last_event", "{}");
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

    private static JSONObject readSettings(Context context) {
        try {
            SharedPreferences prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
            String raw = prefs.getString(KEY, null);
            if (raw == null || raw.isEmpty()) {
                SharedPreferences cap = context.getSharedPreferences(CAP_PREFS, Context.MODE_PRIVATE);
                raw = cap.getString(KEY, null);
                if (raw == null) raw = cap.getString("_cap_" + KEY, null);
            }
            if (raw == null) return null;
            return new JSONObject(raw);
        } catch (Exception e) {
            Log.e(TAG, "Failed reading settings", e);
            return null;
        }
    }

    private static void sendSmtp(JSONObject settings, String from, String body, String receivedAt) throws Exception {
        String to = settings.optString("forwardEmail", "").trim();
        String smtpUser = settings.optString("smtpUser", "").trim();
        String smtpPass = settings.optString("smtpAppPassword", "").trim().replace(" ", "");
        String host = settings.optString("smtpHost", "smtp.gmail.com").trim();
        int port = settings.optInt("smtpPort", 587);

        if (!to.contains("@")) throw new IllegalArgumentException("Set forward-to email");
        if (!smtpUser.contains("@")) throw new IllegalArgumentException("Set Gmail address (SMTP user)");
        if (smtpPass.length() < 8) {
            throw new IllegalArgumentException("Set a Gmail App Password (not your normal password)");
        }

        String subject = "SMS from " + safe(from);
        String text = "From: " + safe(from)
                + "\nReceived: " + receivedAt
                + "\nSource: device SMS (FieldSense auto-forward)\n\n"
                + (body == null ? "" : body);

        Properties props = new Properties();
        props.put("mail.smtp.auth", "true");
        props.put("mail.smtp.starttls.enable", "true");
        props.put("mail.smtp.host", host);
        props.put("mail.smtp.port", String.valueOf(port));
        props.put("mail.smtp.ssl.trust", host);

        final String user = smtpUser;
        final String pass = smtpPass;
        Session session = Session.getInstance(props, new Authenticator() {
            @Override
            protected PasswordAuthentication getPasswordAuthentication() {
                return new PasswordAuthentication(user, pass);
            }
        });

        MimeMessage message = new MimeMessage(session);
        message.setFrom(new InternetAddress(smtpUser));
        message.setRecipients(Message.RecipientType.TO, InternetAddress.parse(to));
        message.setSubject(subject, "UTF-8");
        message.setText(text, "UTF-8");
        Transport.send(message);
    }

    private static Intent buildGmailIntent(Context context, JSONObject settings, String from, String body, String receivedAt) {
        String email = settings.optString("forwardEmail", "");
        String subject = "SMS from " + safe(from);
        String text = "From: " + safe(from) + "\nReceived: " + receivedAt + "\nSource: device SMS\n\n" + (body == null ? "" : body);

        Intent gmail = new Intent(Intent.ACTION_SEND);
        gmail.setType("message/rfc822");
        gmail.putExtra(Intent.EXTRA_EMAIL, new String[]{email});
        gmail.putExtra(Intent.EXTRA_SUBJECT, subject);
        gmail.putExtra(Intent.EXTRA_TEXT, text);
        gmail.setPackage("com.google.android.gm");
        gmail.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        try {
            context.getPackageManager().getPackageInfo("com.google.android.gm", 0);
            return gmail;
        } catch (Exception e) {
            Intent mailto = new Intent(Intent.ACTION_SENDTO);
            mailto.setData(Uri.parse("mailto:" + Uri.encode(email)));
            mailto.putExtra(Intent.EXTRA_SUBJECT, subject);
            mailto.putExtra(Intent.EXTRA_TEXT, text);
            mailto.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            return Intent.createChooser(mailto, "Send SMS via email");
        }
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
            PendingIntent pi = PendingIntent.getActivity(
                    context,
                    0,
                    contentIntent,
                    PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE
            );
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
                "SMS forward",
                NotificationManager.IMPORTANCE_HIGH
        );
        channel.setDescription("Alerts when an SMS is forwarded to email/API");
        NotificationManager nm = context.getSystemService(NotificationManager.class);
        if (nm != null) nm.createNotificationChannel(channel);
    }

    private static int pushApi(JSONObject settings, String from, String body, String receivedAt) throws Exception {
        String endpoint = settings.optString("apiEndpoint", "").trim();
        if (endpoint.isEmpty()) throw new IllegalArgumentException("API endpoint empty");

        JSONObject sms = new JSONObject();
        sms.put("from", from);
        sms.put("body", body);
        sms.put("receivedAt", receivedAt);
        sms.put("simulated", false);

        JSONObject notification = new JSONObject();
        notification.put("title", "SMS from " + safe(from));
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
        conn.disconnect();
        if (code < 200 || code >= 300) throw new IllegalStateException("HTTP " + code);
        return code;
    }

    private static String safe(String value) {
        return value == null || value.isEmpty() ? "unknown" : value;
    }
}
