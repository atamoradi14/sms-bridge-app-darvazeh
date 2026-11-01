# FieldSense Android SMS companion

Browsers **cannot** read the SMS inbox. This Capacitor Android app listens for `SMS_RECEIVED` and then:

1. Opens the **Gmail** app (compose) to the forward-to email you configured  
2. Optionally `POST`s `{ notification, message, sms }` to your API with auth header/token

## Configure

1. Install the APK on your Android phone  
2. Grant **SMS** permission when prompted  
3. In the companion UI (or the main FieldSense web app → **SMS → Gmail + API**):  
   - Set forward email  
   - Optionally enable API endpoint + auth token  
   - Turn **Forward on** and tap **Save settings**

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
