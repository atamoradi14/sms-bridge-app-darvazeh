export type SmsForwardSettings = {
  enabled: boolean;
  forwardEmail: string;
  openGmailApp: boolean;
  apiEnabled: boolean;
  apiEndpoint: string;
  apiAuthHeader: string;
  apiAuthToken: string;
};

export type IncomingSms = {
  from: string;
  body: string;
  receivedAt: string;
  simulated?: boolean;
};

export type SmsDispatchResult = {
  email: { attempted: boolean; ok: boolean; detail: string };
  api: { attempted: boolean; ok: boolean; detail: string; status?: number };
};

export const SMS_FORWARD_STORAGE_KEY = "fieldsense.sms-forward.v1";

export const defaultSmsForwardSettings = (): SmsForwardSettings => ({
  enabled: false,
  forwardEmail: "",
  openGmailApp: true,
  apiEnabled: false,
  apiEndpoint: "",
  apiAuthHeader: "Authorization",
  apiAuthToken: "",
});

export function loadSmsForwardSettings(): SmsForwardSettings {
  if (typeof window === "undefined") return defaultSmsForwardSettings();
  try {
    const raw = window.localStorage.getItem(SMS_FORWARD_STORAGE_KEY);
    if (!raw) return defaultSmsForwardSettings();
    return { ...defaultSmsForwardSettings(), ...JSON.parse(raw) };
  } catch {
    return defaultSmsForwardSettings();
  }
}

export function saveSmsForwardSettings(settings: SmsForwardSettings) {
  window.localStorage.setItem(SMS_FORWARD_STORAGE_KEY, JSON.stringify(settings));
  window.dispatchEvent(new CustomEvent("fieldsense:sms-settings", { detail: settings }));
}

export function isValidEmail(value: string): boolean {
  return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value.trim());
}

export function buildSmsEmail(sms: IncomingSms, to: string) {
  const subject = `SMS from ${sms.from || "unknown"}`;
  const body = [
    `From: ${sms.from || "unknown"}`,
    `Received: ${sms.receivedAt}`,
    sms.simulated ? "Source: simulated (web)" : "Source: device SMS",
    "",
    sms.body,
  ].join("\n");
  return { to: to.trim(), subject, body };
}

export function buildGmailMailto(sms: IncomingSms, to: string): string {
  const { subject, body } = buildSmsEmail(sms, to);
  const params = new URLSearchParams({
    subject,
    body,
  });
  // mailto opens Gmail/email app chooser on Android.
  return `mailto:${encodeURIComponent(to.trim())}?${params.toString()}`;
}

/** Android intent URL that prefers the Gmail app when installed. */
export function buildGmailIntentUrl(sms: IncomingSms, to: string): string {
  const { subject, body } = buildSmsEmail(sms, to);
  const intent = [
    "intent://send",
    "#Intent",
    "action=android.intent.action.SEND",
    "type=message/rfc822",
    `S.android.intent.extra.EMAIL=${to.trim()}`,
    `S.android.intent.extra.SUBJECT=${subject}`,
    `S.android.intent.extra.TEXT=${body}`,
    "package=com.google.android.gm",
    "end",
  ].join(";");
  return intent;
}

export function buildApiPayload(sms: IncomingSms) {
  return {
    type: "sms.received",
    notification: {
      title: `SMS from ${sms.from || "unknown"}`,
      message: sms.body,
    },
    message: {
      from: sms.from,
      body: sms.body,
      receivedAt: sms.receivedAt,
      simulated: Boolean(sms.simulated),
    },
    sms,
  };
}

export async function pushSmsToApi(
  settings: SmsForwardSettings,
  sms: IncomingSms,
): Promise<{ ok: boolean; detail: string; status?: number }> {
  const endpoint = settings.apiEndpoint.trim();
  if (!endpoint) {
    return { ok: false, detail: "API endpoint is empty." };
  }

  try {
    const headers: Record<string, string> = {
      "Content-Type": "application/json",
      Accept: "application/json",
    };
    const headerName = settings.apiAuthHeader.trim() || "Authorization";
    const token = settings.apiAuthToken.trim();
    if (token) {
      headers[headerName] = headerName.toLowerCase() === "authorization" && !token.toLowerCase().startsWith("bearer ")
        ? `Bearer ${token}`
        : token;
    }

    const res = await fetch(endpoint, {
      method: "POST",
      headers,
      body: JSON.stringify(buildApiPayload(sms)),
    });

    const text = await res.text();
    return {
      ok: res.ok,
      status: res.status,
      detail: res.ok
        ? `API ${res.status} OK${text ? `: ${text.slice(0, 120)}` : ""}`
        : `API ${res.status}: ${text.slice(0, 180) || res.statusText}`,
    };
  } catch (err) {
    return {
      ok: false,
      detail: err instanceof Error ? err.message : "API request failed.",
    };
  }
}

export async function dispatchIncomingSms(
  settings: SmsForwardSettings,
  sms: IncomingSms,
): Promise<SmsDispatchResult> {
  const result: SmsDispatchResult = {
    email: { attempted: false, ok: false, detail: "Skipped" },
    api: { attempted: false, ok: false, detail: "Skipped" },
  };

  if (!settings.enabled) {
    result.email.detail = "SMS forward is disabled.";
    result.api.detail = "SMS forward is disabled.";
    return result;
  }

  if (settings.openGmailApp) {
    result.email.attempted = true;
    if (!isValidEmail(settings.forwardEmail)) {
      result.email.detail = "Set a valid forward-to email first.";
    } else if (typeof window === "undefined") {
      result.email.detail = "No window to open Gmail.";
    } else {
      const mailto = buildGmailMailto(sms, settings.forwardEmail);
      // Prefer Gmail package intent on Android; fall back to mailto.
      const ua = navigator.userAgent || "";
      const isAndroid = /Android/i.test(ua);
      try {
        if (isAndroid) {
          window.location.href = buildGmailIntentUrl(sms, settings.forwardEmail);
        } else {
          window.location.href = mailto;
        }
        result.email.ok = true;
        result.email.detail = isAndroid
          ? "Opened Gmail app compose intent."
          : "Opened mailto handler (choose Gmail if prompted).";
      } catch {
        window.location.href = mailto;
        result.email.ok = true;
        result.email.detail = "Opened mailto handler.";
      }
    }
  }

  if (settings.apiEnabled) {
    result.api.attempted = true;
    const api = await pushSmsToApi(settings, sms);
    result.api = { attempted: true, ...api };
  }

  return result;
}
