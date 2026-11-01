"use client";

import { useMemo, useState, useSyncExternalStore } from "react";
import { OrientationViz } from "@/components/orientation-viz";
import { SensorPanel } from "@/components/sensor-panel";
import { ValueRow } from "@/components/value-row";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import { useAmbientLight } from "@/hooks/use-ambient-light";
import { useDeviceMotion } from "@/hooks/use-device-motion";
import { useDeviceOrientation } from "@/hooks/use-device-orientation";
import { useGeolocation } from "@/hooks/use-geolocation";
import { isSecureContext, needsIOSMotionGesture } from "@/lib/sensors";

function subscribeNoop() {
  return () => {};
}

export function SensorLab() {
  const [simulate, setSimulate] = useState(false);
  const secure = useSyncExternalStore(subscribeNoop, isSecureContext, () => true);
  const iosGate = useSyncExternalStore(subscribeNoop, needsIOSMotionGesture, () => false);
  const vibrateSupported = useSyncExternalStore(
    subscribeNoop,
    () => typeof navigator !== "undefined" && "vibrate" in navigator,
    () => false,
  );
  const [motionOn, setMotionOn] = useState(false);
  const [orientationOn, setOrientationOn] = useState(false);
  const [geoOn, setGeoOn] = useState(false);
  const [lightOn, setLightOn] = useState(false);
  const [lastVibrate, setLastVibrate] = useState<string | null>(null);

  const motion = useDeviceMotion({ enabled: motionOn, simulate });
  const orientation = useDeviceOrientation({ enabled: orientationOn, simulate });
  const geo = useGeolocation({ enabled: geoOn, simulate });
  const light = useAmbientLight({ enabled: lightOn, simulate });

  const gravity = motion.data?.accelerationIncludingGravity;
  const tiltX = gravity ? Math.max(-1, Math.min(1, gravity.x / 9.81)) : 0;
  const tiltY = gravity ? Math.max(-1, Math.min(1, gravity.y / 9.81)) : 0;

  const contextNote = useMemo(() => {
    if (simulate) return "Simulation feeds synthetic sensor streams so you can explore on desktop.";
    if (!secure) {
      return "Sensors need a secure context (HTTPS or localhost). Open this app over HTTPS on your phone.";
    }
    if (iosGate) {
      return "iOS Safari requires a tap to grant motion/orientation access. Use Allow access on each panel.";
    }
    return "Android Chrome usually streams motion without an extra prompt. Geolocation still asks once.";
  }, [iosGate, secure, simulate]);

  function pulse(pattern: number | number[]) {
    if (!vibrateSupported || simulate) {
      setLastVibrate(simulate ? "Simulated haptic pulse" : "Vibration API unavailable");
      return;
    }
    const ok = navigator.vibrate(pattern);
    setLastVibrate(ok ? `Played pattern ${JSON.stringify(pattern)}` : "Vibrate call failed");
  }

  async function enableAll() {
    setMotionOn(true);
    setOrientationOn(true);
    setGeoOn(true);
    setLightOn(true);
    await Promise.all([motion.enable(), orientation.enable(), geo.enable(), light.enable()]);
  }

  return (
    <div className="relative min-h-full overflow-hidden">
      <div className="pointer-events-none absolute inset-0 bg-[radial-gradient(ellipse_at_top_left,rgba(34,197,164,0.22),transparent_42%),radial-gradient(ellipse_at_80%_10%,rgba(14,116,144,0.16),transparent_40%),linear-gradient(180deg,#f4fbf8_0%,#e8f1ee_48%,#dfe9e5_100%)]" />
      <div className="pointer-events-none absolute -left-24 top-24 h-72 w-72 rounded-full bg-[color:var(--teal)]/15 blur-3xl animate-drift" />
      <div className="pointer-events-none absolute right-[-4rem] top-[18rem] h-80 w-80 rounded-full bg-cyan-500/10 blur-3xl animate-drift-slow" />

      <main className="relative mx-auto flex w-full max-w-6xl flex-col gap-10 px-5 pb-20 pt-8 sm:px-8 sm:pt-12">
        <header className="grid gap-8 lg:grid-cols-[1.15fr_0.85fr] lg:items-end">
          <div className="space-y-5">
            <p className="font-[family-name:var(--font-display)] text-5xl leading-none tracking-tight text-[color:var(--ink)] sm:text-6xl md:text-7xl">
              FieldSense
            </p>
            <h1 className="max-w-xl text-xl leading-snug text-[color:var(--ink)] sm:text-2xl">
              Read phone motion, orientation, location, and light from a web app.
            </h1>
            <p className="max-w-xl text-base leading-relaxed text-[color:var(--ink-muted)]">
              Browser APIs give controlled access to device sensors — no native app required. Grant
              permission, stream readings, and drive simple haptics.
            </p>
            <div className="flex flex-wrap items-center gap-3">
              <Button
                type="button"
                size="lg"
                onClick={() => void enableAll()}
                className="rounded-xl bg-[color:var(--teal)] px-6 text-[color:var(--teal-ink)] hover:bg-[color:var(--teal-deep)]"
              >
                Enable sensors
              </Button>
              <label className="inline-flex items-center gap-2 rounded-xl border border-[color:var(--ink-line)] bg-[color:var(--panel)]/70 px-3 py-2 text-sm text-[color:var(--ink)]">
                <Switch checked={simulate} onCheckedChange={setSimulate} />
                Desktop simulate
              </label>
            </div>
          </div>

          <aside className="rounded-2xl border border-[color:var(--ink-line)] bg-[color:var(--panel)]/75 p-5 backdrop-blur-sm">
            <div className="mb-3 flex flex-wrap gap-2">
              <Badge className="rounded-md bg-[color:var(--teal)]/15 text-[color:var(--teal-deep)] hover:bg-[color:var(--teal)]/15">
                Web APIs
              </Badge>
              <Badge variant="outline" className="rounded-md font-mono text-[10px] uppercase tracking-[0.14em]">
                {secure ? "secure context" : "insecure"}
              </Badge>
              {iosGate ? (
                <Badge variant="outline" className="rounded-md font-mono text-[10px] uppercase tracking-[0.14em]">
                  iOS permission gate
                </Badge>
              ) : null}
            </div>
            <p className="text-sm leading-relaxed text-[color:var(--ink-muted)]">{contextNote}</p>
            <div className="mt-4 grid grid-cols-2 gap-3 font-mono text-xs text-[color:var(--ink)]">
              <div className="rounded-xl bg-[color:var(--wash)] p-3">
                <div className="text-[color:var(--ink-muted)]">Tilt X</div>
                <div className="mt-1 text-lg tabular-nums">{tiltX.toFixed(2)}</div>
              </div>
              <div className="rounded-xl bg-[color:var(--wash)] p-3">
                <div className="text-[color:var(--ink-muted)]">Tilt Y</div>
                <div className="mt-1 text-lg tabular-nums">{tiltY.toFixed(2)}</div>
              </div>
            </div>
            <div className="mt-4 h-2 overflow-hidden rounded-full bg-[color:var(--wash)]">
              <div
                className="h-full rounded-full bg-[color:var(--teal)] transition-[margin,width] duration-150"
                style={{
                  width: "28%",
                  marginLeft: `${(tiltX + 1) * 36}%`,
                }}
              />
            </div>
          </aside>
        </header>

        <section className="grid gap-4 md:grid-cols-2">
          <SensorPanel
            title="Motion"
            description="Accelerometer + rotation rate via DeviceMotionEvent (and Generic Sensor API where available)."
            permission={motion.permission}
            enabled={motionOn}
            onEnabledChange={setMotionOn}
            onEnable={motion.enable}
            error={motion.error}
          >
            <ValueRow label="Accel X" value={motion.data?.acceleration?.x} unit="m/s²" />
            <ValueRow label="Accel Y" value={motion.data?.acceleration?.y} unit="m/s²" />
            <ValueRow label="Accel Z" value={motion.data?.acceleration?.z} unit="m/s²" />
            <ValueRow
              label="Gravity Z"
              value={motion.data?.accelerationIncludingGravity?.z}
              unit="m/s²"
            />
            <ValueRow label="Rot α" value={motion.data?.rotationRate?.alpha} unit="°/s" />
            <ValueRow label="Interval" value={motion.data?.interval} unit="ms" digits={0} />
          </SensorPanel>

          <SensorPanel
            title="Orientation"
            description="Device attitude from DeviceOrientationEvent — compass heading (alpha), front tilt (beta), side tilt (gamma)."
            permission={orientation.permission}
            enabled={orientationOn}
            onEnabledChange={setOrientationOn}
            onEnable={orientation.enable}
            error={orientation.error}
          >
            <div className="mb-4">
              <OrientationViz data={orientation.data} />
            </div>
            <ValueRow label="Alpha" value={orientation.data?.alpha} unit="°" />
            <ValueRow label="Beta" value={orientation.data?.beta} unit="°" />
            <ValueRow label="Gamma" value={orientation.data?.gamma} unit="°" />
          </SensorPanel>

          <SensorPanel
            title="Geolocation"
            description="GPS / network location through the Geolocation API. Requires an explicit user permission prompt."
            permission={geo.permission}
            enabled={geoOn}
            onEnabledChange={setGeoOn}
            onEnable={geo.enable}
            error={geo.error}
            actionLabel="Share location"
          >
            <ValueRow label="Latitude" value={geo.data?.latitude} digits={5} />
            <ValueRow label="Longitude" value={geo.data?.longitude} digits={5} />
            <ValueRow label="Accuracy" value={geo.data?.accuracy} unit="m" digits={1} />
            <ValueRow label="Altitude" value={geo.data?.altitude} unit="m" digits={1} />
            <ValueRow label="Heading" value={geo.data?.heading} unit="°" digits={1} />
            <ValueRow label="Speed" value={geo.data?.speed} unit="m/s" digits={2} />
          </SensorPanel>

          <SensorPanel
            title="Ambient light"
            description="Illuminance in lux via the Ambient Light Sensor API. Best supported in Chromium-based browsers."
            permission={light.permission}
            enabled={lightOn}
            onEnabledChange={setLightOn}
            onEnable={light.enable}
            error={light.error}
          >
            <ValueRow label="Illuminance" value={light.illuminance} unit="lux" digits={1} />
            <div className="mt-4 h-16 overflow-hidden rounded-xl border border-[color:var(--ink-line)] bg-[color:var(--wash)]">
              <div
                className="h-full transition-[width,background-color] duration-300"
                style={{
                  width: `${Math.min(100, ((light.illuminance ?? 0) / 400) * 100)}%`,
                  background:
                    "linear-gradient(90deg, rgba(34,197,164,0.35), rgba(250,204,21,0.75))",
                }}
              />
            </div>
          </SensorPanel>
        </section>

        <section className="grid gap-4 lg:grid-cols-[0.9fr_1.1fr]">
          <div className="rounded-2xl border border-[color:var(--ink-line)] bg-[color:var(--panel)]/80 p-5">
            <h2 className="font-[family-name:var(--font-display)] text-xl text-[color:var(--ink)]">
              Haptics out
            </h2>
            <p className="mt-1 text-sm text-[color:var(--ink-muted)]">
              The Vibration API is the simplest web → device actuator control path on Android.
            </p>
            <div className="mt-4 flex flex-wrap gap-2">
              <Button type="button" variant="outline" className="rounded-xl" onClick={() => pulse(40)}>
                Short
              </Button>
              <Button
                type="button"
                variant="outline"
                className="rounded-xl"
                onClick={() => pulse([30, 60, 30, 60, 120])}
              >
                Pattern
              </Button>
              <Button
                type="button"
                variant="outline"
                className="rounded-xl"
                onClick={() => pulse(0)}
              >
                Stop
              </Button>
            </div>
            <p className="mt-3 font-mono text-xs text-[color:var(--ink-muted)]">
              {lastVibrate ?? (vibrateSupported ? "Ready" : "Not supported on this browser")}
            </p>
          </div>

          <div className="rounded-2xl border border-[color:var(--ink-line)] bg-[color:var(--panel)]/80 p-5">
            <h2 className="font-[family-name:var(--font-display)] text-xl text-[color:var(--ink)]">
              How webapps reach phone sensors
            </h2>
            <ol className="mt-3 space-y-3 text-sm leading-relaxed text-[color:var(--ink-muted)]">
              <li>
                <span className="font-medium text-[color:var(--ink)]">1. Browser APIs</span> —
                DeviceMotion / DeviceOrientation, Geolocation, AmbientLightSensor, Vibration. Works
                in a normal HTTPS page.
              </li>
              <li>
                <span className="font-medium text-[color:var(--ink)]">2. Permissions + secure context</span>{" "}
                — iOS needs a user gesture for motion; all platforms need HTTPS outside localhost.
              </li>
              <li>
                <span className="font-medium text-[color:var(--ink)]">3. Deeper hardware</span> — for
                Bluetooth LE, barometer, or always-on background sensing, wrap the webview with
                Capacitor / React Native and bridge native plugins.
              </li>
            </ol>
          </div>
        </section>
      </main>
    </div>
  );
}
