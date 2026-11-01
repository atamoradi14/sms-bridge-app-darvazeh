"use client";

import { useEffect, useRef, useState } from "react";
import {
  createSimulatedMotion,
  requestMotionPermission,
  type MotionData,
  type PermissionState,
} from "@/lib/sensors";

type Options = {
  enabled: boolean;
  simulate: boolean;
};

export function useDeviceMotion({ enabled, simulate }: Options) {
  const [permission, setPermission] = useState<PermissionState>("unknown");
  const [data, setData] = useState<MotionData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const rafRef = useRef<number | null>(null);

  async function enable() {
    setError(null);
    if (simulate) {
      setPermission("granted");
      return;
    }
    const result = await requestMotionPermission();
    setPermission(result);
    if (result === "denied") setError("Motion permission was denied.");
    if (result === "unsupported") setError("DeviceMotionEvent is not supported in this browser.");
  }

  useEffect(() => {
    if (!enabled) {
      setData(null);
      return;
    }

    if (simulate) {
      const start = performance.now();
      const tick = (now: number) => {
        setData(createSimulatedMotion(now - start));
        rafRef.current = requestAnimationFrame(tick);
      };
      rafRef.current = requestAnimationFrame(tick);
      return () => {
        if (rafRef.current) cancelAnimationFrame(rafRef.current);
      };
    }

    if (permission !== "granted") return;
    if (!("DeviceMotionEvent" in window)) {
      setPermission("unsupported");
      return;
    }

    const onMotion = (event: DeviceMotionEvent) => {
      setData({
        acceleration: event.acceleration
          ? {
              x: event.acceleration.x ?? 0,
              y: event.acceleration.y ?? 0,
              z: event.acceleration.z ?? 0,
            }
          : null,
        accelerationIncludingGravity: event.accelerationIncludingGravity
          ? {
              x: event.accelerationIncludingGravity.x ?? 0,
              y: event.accelerationIncludingGravity.y ?? 0,
              z: event.accelerationIncludingGravity.z ?? 0,
            }
          : null,
        rotationRate: event.rotationRate
          ? {
              alpha: event.rotationRate.alpha,
              beta: event.rotationRate.beta,
              gamma: event.rotationRate.gamma,
            }
          : null,
        interval: event.interval,
      });
    };

    window.addEventListener("devicemotion", onMotion);
    return () => window.removeEventListener("devicemotion", onMotion);
  }, [enabled, permission, simulate]);

  return { permission, data, error, enable };
}
