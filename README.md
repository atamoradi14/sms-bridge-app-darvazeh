# FieldSense

A Next.js demo that shows how a **webapp can read and control phone sensors** on Android and iOS through browser APIs — motion, orientation, geolocation, ambient light, and vibration.

Open it on a phone over HTTPS (or use **Desktop simulate** on a laptop).

## Run locally

```bash
npm install
npm run dev
```

App: [http://127.0.0.1:43123](http://127.0.0.1:43123)

## How webapps reach Android / iOS sensors

Browsers do **not** expose every native sensor. They expose a curated, permissioned set of Web APIs. That is usually enough for tilt games, compass UIs, fitness-style motion cues, maps, and light-aware themes.

### 1. Secure context first

Most sensor APIs only work on **HTTPS** or **localhost**. Serving over plain `http://192.168.x.x` on a LAN phone will fail.

### 2. Core browser APIs

| Capability | API | Notes |
| --- | --- | --- |
| Accelerometer / gyro rates | `DeviceMotionEvent` | Wide support. iOS needs a user-gesture permission prompt. |
| Device attitude | `DeviceOrientationEvent` | Alpha / beta / gamma. Same iOS permission gate. |
| GPS | `navigator.geolocation` | Explicit permission on every platform. |
| Ambient light | `AmbientLightSensor` | Mostly Chromium. |
| Haptics (output) | `navigator.vibrate()` | Android-friendly; limited/no support on iOS Safari. |
| Camera / mic | `navigator.mediaDevices.getUserMedia` | Sensors in a broad sense; not wired in this demo. |

Example — request iOS motion permission, then listen:

```ts
async function startMotion(onData: (e: DeviceMotionEvent) => void) {
  const request = (
    DeviceMotionEvent as unknown as {
      requestPermission?: () => Promise<"granted" | "denied">;
    }
  ).requestPermission;

  if (typeof request === "function") {
    const state = await request();
    if (state !== "granted") throw new Error("Motion denied");
  }

  window.addEventListener("devicemotion", onData);
}
```

### 3. Platform differences

- **Android Chrome**: motion/orientation often stream after enabling listeners; geolocation still prompts; vibration works; Ambient Light may work in Chromium.
- **iOS Safari**: `DeviceOrientationEvent.requestPermission()` / `DeviceMotionEvent.requestPermission()` must run from a tap; vibration is generally unavailable; Ambient Light is unsupported.

### 4. When you need deeper hardware

If you need Bluetooth LE peripherals, barometer, continuous background sensing, or Apple Health / Google Fit bridges, wrap the web UI with:

- **Capacitor** or **Cordova** (web codebase + native plugins), or
- **React Native / Flutter** with a WebView bridge

Those native shells call platform SDKs and pass readings into JS.

## What this repo demonstrates

- Permission flows for motion, orientation, and geolocation
- Live readouts + a simple orientation visualization
- Desktop simulation mode for development without a phone
- Vibration as a web → device actuator

## Stack

Next.js (App Router), TypeScript, Tailwind CSS, shadcn/ui.
