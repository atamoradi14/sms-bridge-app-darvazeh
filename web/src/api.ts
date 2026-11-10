declare global {
  interface Window {
    __FIELDSENSE_API_BASE__?: string;
  }
}

const TOKEN_KEY = "fs_token";
const SAMPLE_KEY = "fs_sample";

/** True when no remote API is configured — UI runs on in-browser sample data. */
export function usesSampleData(): boolean {
  return !resolveApiBase();
}

function resolveApiBase(): string {
  const fromWindow =
    typeof window !== "undefined" ? (window.__FIELDSENSE_API_BASE__ || "").trim() : "";
  const fromBuild =
    typeof __FIELDSENSE_API_BASE__ !== "undefined" ? String(__FIELDSENSE_API_BASE__ || "").trim() : "";
  const fromVite = (import.meta.env.VITE_API_BASE || "").trim();
  if (fromWindow) return fromWindow.replace(/\/$/, "");
  if (fromBuild) return fromBuild.replace(/\/$/, "");
  if (fromVite) return fromVite.replace(/\/$/, "");
  // Local Vite only: talk to the ASP.NET process on this machine.
  if (import.meta.env.DEV) return "http://127.0.0.1:5088";
  return "";
}

export const API_BASE = resolveApiBase();

export type User = {
  id: string;
  email: string;
  role: string;
  canRelaySms: boolean;
  forwardEmailEnabled: boolean;
  forwardEmailAddress?: string | null;
};

export type Message = {
  id: string;
  direction: string;
  peerNumber: string;
  body: string;
  status: string;
  emailForwarded: boolean;
  createdAt: string;
};

export type Device = {
  id: string;
  name: string;
  phoneNumber?: string | null;
  isOnline: boolean;
  lastSeenAt?: string | null;
};

type SampleState = {
  user: User;
  devices: Device[];
  messages: Message[];
  adminUsers: Array<{
    id: string;
    email: string;
    role: string;
    canRelaySms: boolean;
    deviceCount: number;
    messageCount: number;
  }>;
  pendingOtp?: { email: string; code: string };
};

function storeGet(): Storage {
  return sessionStorage;
}

function authHeaders(): Record<string, string> {
  const token = storeGet().getItem(TOKEN_KEY);
  return token ? { Authorization: `Bearer ${token}` } : {};
}

function loadSample(): SampleState {
  try {
    const raw = storeGet().getItem(SAMPLE_KEY);
    if (raw) return JSON.parse(raw) as SampleState;
  } catch {
    /* ignore */
  }
  return {
    user: {
      id: "sample-user",
      email: "sample@example.com",
      role: "User",
      canRelaySms: true,
      forwardEmailEnabled: false,
      forwardEmailAddress: "sample@example.com",
    },
    devices: [],
    messages: [
      {
        id: "sample-msg-1",
        direction: "inbound",
        peerNumber: "Bank",
        body: "Sample OTP 123456 — connect FIELDSENSE_API_BASE for live SMS.",
        status: "received",
        emailForwarded: false,
        createdAt: new Date().toISOString(),
      },
    ],
    adminUsers: [
      {
        id: "sample-admin",
        email: "admin@example.com",
        role: "Admin",
        canRelaySms: true,
        deviceCount: 0,
        messageCount: 0,
      },
    ],
  };
}

function saveSample(state: SampleState) {
  storeGet().setItem(SAMPLE_KEY, JSON.stringify(state));
}

function sampleToken(email: string) {
  return `sample.${btoa(unescape(encodeURIComponent(email))).replace(/=+$/, "")}`;
}

async function sampleApi<T>(path: string, init?: RequestInit): Promise<T> {
  const method = (init?.method || "GET").toUpperCase();
  const body = init?.body ? (JSON.parse(String(init.body)) as Record<string, string>) : {};
  const state = loadSample();

  if (path === "/api/auth/request-otp" && method === "POST") {
    const email = String(body.email || "").trim().toLowerCase();
    const code = String(100000 + Math.floor(Math.random() * 900000));
    state.pendingOtp = { email, code };
    saveSample(state);
    // Never return the code in the JSON for production UI; App only shows it in DEV.
    return {
      ok: true,
      detail: "Sample mode — no ACS API configured. Use code 000000 (or check this browser session).",
      ...(import.meta.env.DEV ? { debugCode: code } : {}),
      // Always accept 000000 in sample mode for published demos.
      _sampleHint: "000000",
    } as T;
  }

  if (path === "/api/auth/verify-otp" && method === "POST") {
    const email = String(body.email || "").trim().toLowerCase();
    const code = String(body.code || "").trim();
    const pending = state.pendingOtp;
    const ok =
      code === "000000" ||
      (pending && pending.email === email && pending.code === code);
    if (!ok) throw Object.assign(new Error("Invalid or expired code"), { status: 401 });

    const isAdmin = email.startsWith("admin@");
    state.user = {
      id: "sample-" + email,
      email,
      role: isAdmin ? "Admin" : "User",
      canRelaySms: true,
      forwardEmailEnabled: false,
      forwardEmailAddress: email,
    };
    state.pendingOtp = undefined;
    if (isAdmin && !state.adminUsers.some((u) => u.email === email)) {
      state.adminUsers.push({
        id: state.user.id,
        email,
        role: "Admin",
        canRelaySms: true,
        deviceCount: state.devices.length,
        messageCount: state.messages.length,
      });
    }
    saveSample(state);
    return { token: sampleToken(email), user: state.user } as T;
  }

  if (path === "/api/user/me") {
    return { ...state.user, devices: state.devices } as T;
  }
  if (path === "/api/user/messages") return state.messages as T;
  if (path === "/api/user/settings" && method === "PUT") {
    state.user.forwardEmailEnabled = !!body.forwardEmailEnabled;
    state.user.forwardEmailAddress = body.forwardEmailAddress || state.user.email;
    saveSample(state);
    return { ok: true } as T;
  }
  if (path === "/api/user/devices/pair" && method === "POST") {
    const device: Device = {
      id: crypto.randomUUID(),
      name: body.name || "Iran phone",
      phoneNumber: body.phoneNumber || null,
      isOnline: false,
      lastSeenAt: null,
    };
    state.devices = [device, ...state.devices];
    saveSample(state);
    return {
      id: device.id,
      name: device.name,
      deviceToken: "sample-device-" + device.id.slice(0, 8),
      phoneNumber: device.phoneNumber,
    } as T;
  }
  if (path === "/api/user/sms/send" && method === "POST") {
    const msg: Message = {
      id: crypto.randomUUID(),
      direction: "outbound",
      peerNumber: body.toNumber,
      body: body.body,
      status: "queued",
      emailForwarded: false,
      createdAt: new Date().toISOString(),
    };
    state.messages = [msg, ...state.messages];
    saveSample(state);
    return { messageId: msg.id, status: "queued" } as T;
  }
  if (path === "/api/admin/users") return state.adminUsers as T;
  if (path.startsWith("/api/admin/users/") && path.endsWith("/relay") && method === "PUT") {
    const id = path.split("/")[4];
    state.adminUsers = state.adminUsers.map((u) =>
      u.id === id ? { ...u, canRelaySms: !!body.canRelaySms } : u,
    );
    saveSample(state);
    return { ok: true } as T;
  }
  if (path === "/api/admin/stats") {
    return {
      users: state.adminUsers.length,
      devices: state.devices.length,
      messages: state.messages.length,
      queuedOutbound: state.messages.filter((m) => m.status === "queued").length,
    } as T;
  }

  throw new Error(`Sample API: unsupported ${method} ${path}`);
}

async function req<T>(path: string, init?: RequestInit): Promise<T> {
  const base = resolveApiBase();
  if (!base) return sampleApi<T>(path, init);

  const headers: Record<string, string> = {
    "Content-Type": "application/json",
    ...authHeaders(),
  };
  if (init?.headers) {
    const extra = new Headers(init.headers);
    extra.forEach((value, key) => {
      headers[key] = value;
    });
  }

  let res: Response;
  try {
    res = await fetch(`${base}${path}`, { ...init, headers });
  } catch {
    throw new Error(
      `Cannot reach API at ${base}. Deploy FieldSense.Api and set the FIELDSENSE_API_BASE app secret.`,
    );
  }

  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    const err = new Error(
      (data as { error?: string }).error || res.statusText || "Request failed",
    ) as Error & {
      needAdminPassword?: boolean;
      status?: number;
    };
    err.needAdminPassword = (data as { needAdminPassword?: boolean }).needAdminPassword;
    err.status = res.status;
    throw err;
  }
  return data as T;
}

export const api = {
  requestOtp: (email: string) =>
    req<{ ok: boolean; detail: string; debugCode?: string }>("/api/auth/request-otp", {
      method: "POST",
      body: JSON.stringify({ email }),
    }),
  verifyOtp: (email: string, code: string, adminPassword?: string) =>
    req<{ token: string; user: User }>("/api/auth/verify-otp", {
      method: "POST",
      body: JSON.stringify({ email, code, adminPassword }),
    }),
  me: () => req<User & { devices: Device[] }>("/api/user/me"),
  messages: () => req<Message[]>("/api/user/messages"),
  updateSettings: (forwardEmailEnabled: boolean, forwardEmailAddress: string) =>
    req("/api/user/settings", {
      method: "PUT",
      body: JSON.stringify({ forwardEmailEnabled, forwardEmailAddress }),
    }),
  pairDevice: (name: string, phoneNumber?: string) =>
    req<{ id: string; name: string; deviceToken: string; phoneNumber?: string }>(
      "/api/user/devices/pair",
      { method: "POST", body: JSON.stringify({ name, phoneNumber }) },
    ),
  sendSms: (toNumber: string, body: string) =>
    req("/api/user/sms/send", {
      method: "POST",
      body: JSON.stringify({ toNumber, body }),
    }),
  adminUsers: () =>
    req<
      Array<{
        id: string;
        email: string;
        role: string;
        canRelaySms: boolean;
        deviceCount: number;
        messageCount: number;
      }>
    >("/api/admin/users"),
  setRelay: (id: string, canRelaySms: boolean) =>
    req(`/api/admin/users/${id}/relay`, {
      method: "PUT",
      body: JSON.stringify({ canRelaySms }),
    }),
  adminStats: () =>
    req<{ users: number; devices: number; messages: number; queuedOutbound: number }>(
      "/api/admin/stats",
    ),
  setToken: (token: string | null) => {
    if (token) storeGet().setItem(TOKEN_KEY, token);
    else storeGet().removeItem(TOKEN_KEY);
  },
  getToken: () => storeGet().getItem(TOKEN_KEY),
};
