const API_BASE = import.meta.env.VITE_API_BASE ?? "http://127.0.0.1:5088";

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

function authHeaders(): Record<string, string> {
  const token = localStorage.getItem("fs_token");
  return token ? { Authorization: `Bearer ${token}` } : {};
}

async function req<T>(path: string, init?: RequestInit): Promise<T> {
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

  const res = await fetch(`${API_BASE}${path}`, {
    ...init,
    headers,
  });
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
};

export { API_BASE };
