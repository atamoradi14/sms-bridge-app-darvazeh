# دروازه ها باز است — Android device agent

Capacitor Android app that stays with the Iranian SIM and relays SMS through the ASP.NET API.

**minSdk 24 (Android 7)** — large touch targets, no `java.time`, foreground poller, Huawei battery notes.

## What it does

1. Sign in with the **same email OTP** as the web app (`client=phone`) → server auto-links this installation
2. Listens for inbound SMS → `POST /api/device/inbound-sms` with `X-Device-Token`
3. Foreground service heartbeats + polls outbound jobs
4. Sends queued SMS with `SmsManager`

## Setup

1. Install the APK from the web **Device** tab
2. Sign in with the same email (API URL is loaded from `mobile-config.json` — no token paste)
3. Grant SMS, notifications, and ignore battery optimizations

## Build

```bash
cd android-companion
npm install
npm run sync
cd android && ./gradlew assembleDebug
```
