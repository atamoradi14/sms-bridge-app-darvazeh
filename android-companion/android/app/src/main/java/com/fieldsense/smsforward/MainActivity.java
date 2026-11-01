package com.fieldsense.smsforward;

import android.Manifest;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.webkit.JavascriptInterface;
import android.webkit.WebView;

import androidx.core.app.ActivityCompat;
import androidx.core.content.ContextCompat;

import com.getcapacitor.BridgeActivity;

public class MainActivity extends BridgeActivity {
    private static final int REQ_SMS = 4217;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestSmsPermissionIfNeeded();

        WebView webView = this.bridge != null ? this.bridge.getWebView() : null;
        if (webView != null) {
            webView.addJavascriptInterface(new JsBridge(), "FieldSenseSms");
        }
    }

    private void requestSmsPermissionIfNeeded() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) return;
        boolean receive = ContextCompat.checkSelfPermission(this, Manifest.permission.RECEIVE_SMS)
                == PackageManager.PERMISSION_GRANTED;
        boolean read = ContextCompat.checkSelfPermission(this, Manifest.permission.READ_SMS)
                == PackageManager.PERMISSION_GRANTED;
        if (receive && read) return;
        ActivityCompat.requestPermissions(
                this,
                new String[]{Manifest.permission.RECEIVE_SMS, Manifest.permission.READ_SMS},
                REQ_SMS
        );
    }

    private class JsBridge {
        @JavascriptInterface
        public void saveSettings(String json) {
            SmsForwardBridge.saveSettingsJson(getApplicationContext(), json);
        }
    }
}
