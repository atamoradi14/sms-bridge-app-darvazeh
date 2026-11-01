"use client";

import { useEffect, useRef, useState } from "react";
import type { PermissionState } from "@/lib/sensors";

type Options = {
  enabled: boolean;
  simulate: boolean;
};

type AmbientLightSensorLike = {
  illuminance: number;
  start: () => void;
  stop: () => void;
  addEventListener: (type: string, listener: () => void) => void;
  removeEventListener: (type: string, listener: () => void) => void;
};

declare global {
  interface Window {
    AmbientLightSensor?: new (options?: { frequency?: number }) => AmbientLightSensorLike;
  }
}

export function useAmbientLight({ enabled, simulate }: Options) {
  const [permission, setPermission] = useState<PermissionState>("unknown");
  const [illuminance, setIlluminance] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const sensorRef = useRef<AmbientLightSensorLike | null>(null);
  const rafRef = useRef<number | null>(null);

  async function enable() {
    setError(null);
    if (simulate) {
      setPermission("granted");
      return;
    }
    if (!("AmbientLightSensor" in window)) {
      setPermission("unsupported");
      setError("Ambient Light Sensor API is not supported here (mainly Chromium).");
      return;
    }
    try {
      if (navigator.permissions?.query) {
        const status = await navigator.permissions.query({
          name: "ambient-light-sensor" as PermissionName,
        });
        if (status.state === "denied") {
          setPermission("denied");
          setError("Ambient light permission denied.");
          return;
        }
      }
      setPermission("granted");
    } catch {
      setPermission("granted");
    }
  }

  useEffect(() => {
    if (!enabled) {
      setIlluminance(null);
      return;
    }

    if (simulate) {
      const start = performance.now();
      const tick = (now: number) => {
        const t = now - start;
        setIlluminance(180 + Math.sin(t / 1400) * 90);
        rafRef.current = requestAnimationFrame(tick);
      };
      rafRef.current = requestAnimationFrame(tick);
      return () => {
        if (rafRef.current) cancelAnimationFrame(rafRef.current);
      };
    }

    if (permission !== "granted" || !window.AmbientLightSensor) return;

    try {
      const sensor = new window.AmbientLightSensor({ frequency: 5 });
      const onReading = () => setIlluminance(sensor.illuminance);
      sensor.addEventListener("reading", onReading);
      sensor.start();
      sensorRef.current = sensor;
      return () => {
        sensor.removeEventListener("reading", onReading);
        sensor.stop();
        sensorRef.current = null;
      };
    } catch (err) {
      setPermission("unsupported");
      setError(err instanceof Error ? err.message : "Failed to start ambient light sensor.");
    }
  }, [enabled, permission, simulate]);

  return { permission, illuminance, error, enable };
}
