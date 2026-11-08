package com.fieldsense.smsforward;

import android.Manifest;
import android.annotation.SuppressLint;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.PowerManager;
import android.provider.Settings;
import android.webkit.JavascriptInterface;
import android.webkit.WebView;

import androidx.core.app.ActivityCompat;
import androidx.core.content.ContextCompat;

import com.getcapacitor.BridgeActivity;

import org.json.JSONObject;

public class MainActivity extends BridgeActivity {
    private static final int REQ_PERMS = 4217;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestRuntimePermissions();
        maybePromptBatteryOptimizations();
        attachJsBridge();
        maybeStartAgent();
    }

    /** Critical on Huawei/Xiaomi/older OEMs that kill SMS receivers in Doze/battery save. */
    @SuppressLint("BatteryLife")
    private void maybePromptBatteryOptimizations() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) return;
        try {
            PowerManager pm = (PowerManager) getSystemService(POWER_SERVICE);
            if (pm == null || pm.isIgnoringBatteryOptimizations(getPackageName())) return;
            Intent intent = new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS);
            intent.setData(Uri.parse("package:" + getPackageName()));
            startActivity(intent);
        } catch (Exception ignored) {
        }
    }

    @Override
    public void onResume() {
        super.onResume();
        attachJsBridge();
        maybeStartAgent();
    }

    private void maybeStartAgent() {
        try {
            JSONObject settings = SmsForwardBridge.readSettings(getApplicationContext());
            if (settings != null && settings.optBoolean("enabled", false)) {
                DeviceAgentService.start(getApplicationContext());
            }
        } catch (Exception ignored) {
        }
    }

    private void attachJsBridge() {
        if (this.bridge == null) return;
        WebView webView = this.bridge.getWebView();
        if (webView != null) {
            webView.addJavascriptInterface(new JsBridge(), "FieldSenseSms");
        }
    }

    private void requestRuntimePermissions() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) return;
        java.util.ArrayList<String> needed = new java.util.ArrayList<>();
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECEIVE_SMS)
                != PackageManager.PERMISSION_GRANTED) {
            needed.add(Manifest.permission.RECEIVE_SMS);
        }
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.READ_SMS)
                != PackageManager.PERMISSION_GRANTED) {
            needed.add(Manifest.permission.READ_SMS);
        }
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.SEND_SMS)
                != PackageManager.PERMISSION_GRANTED) {
            needed.add(Manifest.permission.SEND_SMS);
        }
        if (Build.VERSION.SDK_INT >= 33
                && ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED) {
            needed.add(Manifest.permission.POST_NOTIFICATIONS);
        }
        if (!needed.isEmpty()) {
            ActivityCompat.requestPermissions(this, needed.toArray(new String[0]), REQ_PERMS);
        }
    }

    private class JsBridge {
        @JavascriptInterface
        public void saveSettings(String json) {
            SmsForwardBridge.saveSettingsJson(getApplicationContext(), json);
        }

        @JavascriptInterface
        public String getLastEvent() {
            return SmsForwardBridge.getLastEventJson(getApplicationContext());
        }

        @JavascriptInterface
        public String testHeartbeat() {
            return SmsForwardBridge.testHeartbeat(getApplicationContext());
        }
    }
}
