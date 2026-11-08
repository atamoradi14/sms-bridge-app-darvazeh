package com.fieldsense.smsforward;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

import org.json.JSONObject;

public class BootReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        if (intent == null || intent.getAction() == null) return;
        try {
            JSONObject settings = SmsForwardBridge.readSettings(context.getApplicationContext());
            if (settings != null && settings.optBoolean("enabled", false)) {
                DeviceAgentService.start(context.getApplicationContext());
            }
        } catch (Exception ignored) {
        }
    }
}
