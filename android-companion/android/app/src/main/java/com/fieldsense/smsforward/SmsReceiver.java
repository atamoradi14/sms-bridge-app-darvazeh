package com.fieldsense.smsforward;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.os.Bundle;
import android.provider.Telephony;
import android.telephony.SmsMessage;
import android.util.Log;

public class SmsReceiver extends BroadcastReceiver {
    private static final String TAG = "FieldSenseSms";

    @Override
    public void onReceive(Context context, Intent intent) {
        if (intent == null || intent.getAction() == null) return;
        if (!Telephony.Sms.Intents.SMS_RECEIVED_ACTION.equals(intent.getAction())
                && !"android.provider.Telephony.SMS_RECEIVED".equals(intent.getAction())) {
            return;
        }

        try {
            SmsMessage[] messages;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.KITKAT) {
                messages = Telephony.Sms.Intents.getMessagesFromIntent(intent);
            } else {
                Bundle bundle = intent.getExtras();
                if (bundle == null) return;
                Object[] pdus = (Object[]) bundle.get("pdus");
                if (pdus == null) return;
                messages = new SmsMessage[pdus.length];
                for (int i = 0; i < pdus.length; i++) {
                    messages[i] = SmsMessage.createFromPdu((byte[]) pdus[i]);
                }
            }

            if (messages == null || messages.length == 0) return;

            String from = messages[0].getDisplayOriginatingAddress();
            StringBuilder body = new StringBuilder();
            for (SmsMessage message : messages) {
                if (message != null && message.getMessageBody() != null) {
                    body.append(message.getMessageBody());
                }
            }

            Log.i(TAG, "SMS received from=" + from);
            SmsForwardBridge.handleIncomingSms(context.getApplicationContext(), from, body.toString());
        } catch (Exception e) {
            Log.e(TAG, "Failed handling SMS", e);
        }
    }
}
