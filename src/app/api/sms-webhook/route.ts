import { NextResponse } from "next/server";

type SmsWebhookBody = {
  type?: string;
  notification?: { title?: string; message?: string };
  message?: { from?: string; body?: string; receivedAt?: string };
  sms?: { from?: string; body?: string; receivedAt?: string };
};

/**
 * Built-in test sink for SMS forward API pushes.
 * Accepts optional Bearer / custom auth header when present.
 */
export async function POST(request: Request) {
  const auth =
    request.headers.get("authorization") ||
    request.headers.get("x-api-key") ||
    request.headers.get("x-fieldsense-token");

  let payload: SmsWebhookBody = {};
  try {
    payload = (await request.json()) as SmsWebhookBody;
  } catch {
    return NextResponse.json({ ok: false, error: "Invalid JSON body" }, { status: 400 });
  }

  const from = payload.sms?.from || payload.message?.from || "unknown";
  const body = payload.sms?.body || payload.message?.body || payload.notification?.message || "";

  console.info("[sms-webhook]", {
    authPresent: Boolean(auth),
    type: payload.type,
    from,
    bodyPreview: String(body).slice(0, 120),
  });

  return NextResponse.json({
    ok: true,
    received: true,
    authPresent: Boolean(auth),
    echo: {
      from,
      body,
      notification: payload.notification ?? null,
      receivedAt: payload.sms?.receivedAt || payload.message?.receivedAt || new Date().toISOString(),
    },
  });
}

export async function GET() {
  return NextResponse.json({
    ok: true,
    usage: "POST JSON { notification, message, sms } with optional Authorization: Bearer <token>",
  });
}
