"use client";

import { useEffect, useRef, useState } from "react";
import {
  createSimulatedGeo,
  type GeoData,
  type PermissionState,
} from "@/lib/sensors";

type Options = {
  enabled: boolean;
  simulate: boolean;
};

export function useGeolocation({ enabled, simulate }: Options) {
  const [permission, setPermission] = useState<PermissionState>("unknown");
  const [data, setData] = useState<GeoData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const watchId = useRef<number | null>(null);
  const rafRef = useRef<number | null>(null);

  function stop() {
    if (watchId.current !== null && navigator.geolocation) {
      navigator.geolocation.clearWatch(watchId.current);
      watchId.current = null;
    }
    if (rafRef.current) {
      cancelAnimationFrame(rafRef.current);
      rafRef.current = null;
    }
  }

  async function enable() {
    setError(null);
    if (simulate) {
      setPermission("granted");
      return;
    }
    if (!("geolocation" in navigator)) {
      setPermission("unsupported");
      setError("Geolocation API is not available.");
      return;
    }
    setPermission("prompt");
  }

  useEffect(() => {
    if (!enabled) {
      stop();
      return;
    }

    if (simulate) {
      queueMicrotask(() => {
        setPermission("granted");
        setError(null);
      });
      const start = performance.now();
      const tick = (now: number) => {
        setData(createSimulatedGeo(now - start));
        rafRef.current = requestAnimationFrame(tick);
      };
      rafRef.current = requestAnimationFrame(tick);
      return () => stop();
    }

    if (permission !== "prompt" && permission !== "granted") return;
    if (!("geolocation" in navigator)) {
      queueMicrotask(() => setPermission("unsupported"));
      return;
    }

    watchId.current = navigator.geolocation.watchPosition(
      (pos) => {
        setPermission("granted");
        setData({
          latitude: pos.coords.latitude,
          longitude: pos.coords.longitude,
          accuracy: pos.coords.accuracy,
          altitude: pos.coords.altitude,
          heading: pos.coords.heading,
          speed: pos.coords.speed,
          timestamp: pos.timestamp,
        });
      },
      (err) => {
        setPermission(err.code === err.PERMISSION_DENIED ? "denied" : "unsupported");
        setError(err.message);
      },
      { enableHighAccuracy: true, maximumAge: 1000, timeout: 15000 },
    );

    return () => stop();
  }, [enabled, permission, simulate]);

  return { permission, data: enabled ? data : null, error, enable };
}
