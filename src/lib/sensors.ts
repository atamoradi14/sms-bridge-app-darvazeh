export type PermissionState = "unknown" | "granted" | "denied" | "prompt" | "unsupported";

export type Vec3 = { x: number; y: number; z: number };

export type OrientationData = {
  alpha: number | null;
  beta: number | null;
  gamma: number | null;
  absolute: boolean;
};

export type MotionData = {
  acceleration: Vec3 | null;
  accelerationIncludingGravity: Vec3 | null;
  rotationRate: { alpha: number | null; beta: number | null; gamma: number | null } | null;
  interval: number | null;
};

export type GeoData = {
  latitude: number;
  longitude: number;
  accuracy: number;
  altitude: number | null;
  heading: number | null;
  speed: number | null;
  timestamp: number;
};

export function isSecureContext(): boolean {
  if (typeof window === "undefined") return false;
  return window.isSecureContext;
}

export function formatNumber(value: number | null | undefined, digits = 2): string {
  if (value === null || value === undefined || Number.isNaN(value)) return "—";
  return value.toFixed(digits);
}

type PermissionRequestable = {
  requestPermission?: () => Promise<"granted" | "denied">;
};

export async function requestOrientationPermission(): Promise<PermissionState> {
  if (typeof window === "undefined") return "unsupported";
  if (!("DeviceOrientationEvent" in window)) return "unsupported";

  const ctor = DeviceOrientationEvent as unknown as PermissionRequestable;
  if (typeof ctor.requestPermission === "function") {
    try {
      const result = await ctor.requestPermission();
      return result === "granted" ? "granted" : "denied";
    } catch {
      return "denied";
    }
  }
  return "granted";
}

export async function requestMotionPermission(): Promise<PermissionState> {
  if (typeof window === "undefined") return "unsupported";
  if (!("DeviceMotionEvent" in window)) return "unsupported";

  const ctor = DeviceMotionEvent as unknown as PermissionRequestable;
  if (typeof ctor.requestPermission === "function") {
    try {
      const result = await ctor.requestPermission();
      return result === "granted" ? "granted" : "denied";
    } catch {
      return "denied";
    }
  }
  return "granted";
}

export function needsIOSMotionGesture(): boolean {
  if (typeof window === "undefined") return false;
  const orientation = DeviceOrientationEvent as unknown as PermissionRequestable;
  const motion = DeviceMotionEvent as unknown as PermissionRequestable;
  return (
    typeof orientation.requestPermission === "function" ||
    typeof motion.requestPermission === "function"
  );
}

export function createSimulatedMotion(t: number): MotionData {
  const sway = Math.sin(t / 600);
  const bob = Math.cos(t / 900);
  return {
    acceleration: { x: sway * 0.4, y: bob * 0.3, z: 0.05 },
    accelerationIncludingGravity: {
      x: sway * 0.4,
      y: bob * 0.3,
      z: 9.81 + bob * 0.1,
    },
    rotationRate: {
      alpha: sway * 12,
      beta: bob * 8,
      gamma: sway * -5,
    },
    interval: 16,
  };
}

export function createSimulatedOrientation(t: number): OrientationData {
  return {
    alpha: ((t / 40) % 360 + 360) % 360,
    beta: Math.sin(t / 1200) * 35,
    gamma: Math.cos(t / 900) * 25,
    absolute: false,
  };
}

export function createSimulatedGeo(t: number): GeoData {
  return {
    latitude: -36.8485 + Math.sin(t / 5000) * 0.001,
    longitude: 174.7633 + Math.cos(t / 5000) * 0.001,
    accuracy: 8 + Math.abs(Math.sin(t / 2000)) * 4,
    altitude: 42,
    heading: ((t / 50) % 360 + 360) % 360,
    speed: 1.2 + Math.abs(Math.sin(t / 1800)) * 0.4,
    timestamp: Date.now(),
  };
}
