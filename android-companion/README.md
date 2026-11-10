# FieldSense Android device agent (v2)

Capacitor Android app that stays with the Iranian SIM and relays SMS through the FieldSense ASP.NET API.

**minSdk 24 (Android 7)** — large touch targets, no `java.time`, foreground poller, Huawei battery notes.

## What it does

1. Sign in with the **same email OTP** as the web app (`client=phone`) → server auto-links this installation and returns a device token (stored on the phone; user never copies it)
2. Listens for inbound SMS → `POST /api/device/inbound-sms` with `X-Device-Token`
3. Foreground service heartbeats + polls `GET /api/device/outbound/pending`
4. Sends queued SMS with `SmsManager`, then reports `POST /api/device/outbound/{id}/result`

Email OTP / inbox email forward is handled by the **server** (Azure ACS), not Gmail SMTP on the phone.

## Setup

1. Run the API + Vite web app.
2. On the Iran phone APK:
   - Enter **API base URL** (reachable from Iran — public HTTPS URL, not LAN IP behind NAT)
   - Sign in with the **same email** as the web app and the OTP code
   - Relay starts after login; adjust and Save if needed
3. On the web **Device** tab, the phone appears as linked (online/offline). No token paste step.
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
