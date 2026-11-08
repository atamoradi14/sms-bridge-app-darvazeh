# FieldSense Android device agent (v2)

Capacitor Android app that stays with the Iranian SIM and relays SMS through the FieldSense ASP.NET API.

**minSdk 24 (Android 7)** — large touch targets, no `java.time`, foreground poller, Huawei battery notes.

## What it does

1. Listens for inbound SMS → `POST /api/device/inbound-sms` with `X-Device-Token`
2. Foreground service heartbeats + polls `GET /api/device/outbound/pending`
3. Sends queued SMS with `SmsManager`, then reports `POST /api/device/outbound/{id}/result`

Email OTP / inbox email forward is handled by the **server** (Azure ACS), not Gmail SMTP on the phone.

## Setup

1. Run the API + Vite web app.
2. Sign in on the web → **Device** → generate a device token.
3. On the Iran phone APK:
   - Paste **API base URL** (reachable from Iran — public HTTPS URL, not LAN IP behind NAT)
   - Paste **device token**
   - Turn **Relay on** → Save
4. Grant SMS (receive/read/send), notifications, and ignore battery optimizations.
5. Huawei/Xiaomi: App launch → manage manually → Autostart + allow background.

## Build

```bash
cd android-companion
npm install
npm run sync
# open Android Studio and build/install, or:
cd android && ./gradlew assembleDebug
```

Debug APK path after build: `android/app/build/outputs/apk/debug/app-debug.apk`
