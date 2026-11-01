"use client";

import { useEffect, useRef, useState } from "react";
import {
  createSimulatedOrientation,
  requestOrientationPermission,
  type OrientationData,
  type PermissionState,
} from "@/lib/sensors";

type Options = {
  enabled: boolean;
  simulate: boolean;
};

export function useDeviceOrientation({ enabled, simulate }: Options) {
  const [permission, setPermission] = useState<PermissionState>("unknown");
  const [data, setData] = useState<OrientationData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [eventCount, setEventCount] = useState(0);
  const rafRef = useRef<number | null>(null);

  async function enable() {
    setError(null);
    if (simulate) {
      setPermission("granted");
      return;
    }
    const result = await requestOrientationPermission();
    setPermission(result);
    if (result === "denied") setError("Orientation permission was denied.");
    if (result === "unsupported") {
      setError("DeviceOrientationEvent is not supported in this browser.");
    }
  }

  useEffect(() => {
    if (!enabled) return;

    if (simulate) {
      queueMicrotask(() => {
        setPermission("granted");
        setError(null);
      });
      const start = performance.now();
      const tick = (now: number) => {
        setData(createSimulatedOrientation(now - start));
        setEventCount((n) => n + 1);
        rafRef.current = requestAnimationFrame(tick);
      };
      rafRef.current = requestAnimationFrame(tick);
      return () => {
        if (rafRef.current) cancelAnimationFrame(rafRef.current);
      };
    }

    if (permission !== "granted") return;
    if (!("DeviceOrientationEvent" in window)) {
      queueMicrotask(() => setPermission("unsupported"));
      return;
    }

    const onOrientation = (event: DeviceOrientationEvent) => {
      setEventCount((n) => n + 1);
      setData({
        alpha: event.alpha,
        beta: event.beta,
        gamma: event.gamma,
        absolute: Boolean(event.absolute),
      });
    };

    window.addEventListener("deviceorientation", onOrientation);
    return () => window.removeEventListener("deviceorientation", onOrientation);
  }, [enabled, permission, simulate]);

  return {
    permission,
    data: enabled ? data : null,
    error,
    enable,
    eventCount: enabled ? eventCount : 0,
  };
}
