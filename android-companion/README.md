# FieldSense Android SMS companion

Browsers **cannot** read the SMS inbox. This Capacitor Android app listens for `SMS_RECEIVED` and then:

1. **Auto-sends email via Gmail SMTP** using a Google **App Password** (required — opening the Gmail app from the background is blocked on modern Android and does not send by itself)
2. Shows a notification (tap opens a Gmail draft as fallback)
3. Optionally `POST`s `{ notification, message, sms }` to your API with auth header/token

## Configure

1. Install the APK (`versionName 1.1.0+`) on your Android phone  
2. Grant **SMS** + **Notifications** when prompted  
3. Create a Gmail [App Password](https://myaccount.google.com/apppasswords) (2-Step Verification must be on)  
4. In the companion UI:  
   - Forward-to email  
   - Gmail address + App Password  
   - Optionally API endpoint + auth token  
   - Turn **Forward on** → **Save settings**  
5. Send yourself a test SMS — you should get a notification and an email in the forward-to inbox  
6. Tap **Refresh last SMS** if nothing arrives (shows receiver status)

## Build

Requires Android Studio / SDK (WSL or macOS/Linux):

```bash
cd android-companion
npm install
npm run sync
npm run open
# Android Studio → Run on device
# or: cd android && ./gradlew assembleDebug
```

APK output (debug): `android/app/build/outputs/apk/debug/app-debug.apk`

## Compatibility

| | Support |
| --- | --- |
| Minimum Android | **7.0 (API 24)** — required by Capacitor 8 |
| Typical old Huawei (EMUI 8/9/10, Android 8–10) | Supported |
| Android 5–6 Huawei | Not supported by this Capacitor build |
| Huawei without Google Play / Gmail app | OK — email uses **SMTP**, not the Gmail app |
| Google Play Services | Not required |

### Huawei / EMUI checklist (SMS often “silently fails” otherwise)

1. Install APK → allow **SMS** (+ notifications if shown)
2. Allow **Ignore battery optimizations** when prompted
3. **Phone Manager / Optimizer → App launch** → FieldSense SMS → manage manually → enable Auto-launch + Secondary launch + Run in background
4. Keep the app installed; don’t Force stop it
5. Use Gmail **App Password** SMTP (works without Huawei having Gmail installed)

A prebuilt debug APK from CI/agent builds is published as artifact  
`FieldSense-SMS-debug.apk` (package `com.fieldsense.smsforward`).

Install on a phone (USB / Files):

```bash
adb install -r FieldSense-SMS-debug.apk
```

Then open **FieldSense SMS**, grant SMS permission, set email (+ optional API), turn **Forward on**, tap **Save**.

## API payload example

```json
{
  "type": "sms.received",
  "notification": { "title": "SMS from +6421…", "message": "…" },
  "message": { "from": "+6421…", "body": "…", "receivedAt": "…", "simulated": false },
  "sms": { "from": "+6421…", "body": "…", "receivedAt": "…", "simulated": false }
}
```

Auth: sends your token on the configured header (default `Authorization: Bearer <token>`).
