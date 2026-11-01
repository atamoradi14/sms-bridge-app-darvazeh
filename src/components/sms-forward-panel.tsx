"use client";

import { useState } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import { useSmsForwardSettings } from "@/hooks/use-sms-forward-settings";
import {
  dispatchIncomingSms,
  isValidEmail,
  type IncomingSms,
  type SmsDispatchResult,
} from "@/lib/sms-forward";

export function SmsForwardPanel() {
  const { settings, update, hydrated } = useSmsForwardSettings();
  const [from, setFrom] = useState("+64 21 000 0000");
  const [body, setBody] = useState("Package left at the door. Track: AB123456NZ");
  const [busy, setBusy] = useState(false);
  const [lastResult, setLastResult] = useState<SmsDispatchResult | null>(null);
  const [log, setLog] = useState<string[]>([]);

  function pushLog(line: string) {
    setLog((prev) => [`${new Date().toLocaleTimeString()} — ${line}`, ...prev].slice(0, 8));
  }

  async function runDispatch(sms: IncomingSms) {
    setBusy(true);
    try {
      const result = await dispatchIncomingSms(settings, sms);
      setLastResult(result);
      if (result.email.attempted) pushLog(`Email: ${result.email.detail}`);
      if (result.api.attempted) pushLog(`API: ${result.api.detail}`);
      if (!result.email.attempted && !result.api.attempted) {
        pushLog("Nothing to do — enable Gmail and/or API forward.");
      }
    } finally {
      setBusy(false);
    }
  }

  async function onSimulate() {
    if (!settings.enabled) {
      pushLog("Turn on SMS forward first.");
      return;
    }
    await runDispatch({
      from: from.trim() || "unknown",
      body: body.trim() || "(empty)",
      receivedAt: new Date().toISOString(),
      simulated: true,
    });
  }

  const emailReady = isValidEmail(settings.forwardEmail);
  const apiReady = Boolean(settings.apiEndpoint.trim());

  return (
    <section className="rounded-2xl border border-[color:var(--ink-line)] bg-[color:var(--panel)]/80 p-5 backdrop-blur-sm">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="space-y-1">
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="font-[family-name:var(--font-display)] text-xl text-[color:var(--ink)]">
              SMS → Gmail + API
            </h2>
            <Badge
              variant="outline"
              className="rounded-md font-mono text-[10px] uppercase tracking-[0.14em]"
            >
              {settings.enabled ? "armed" : "off"}
            </Badge>
          </div>
          <p className="max-w-2xl text-sm leading-relaxed text-[color:var(--ink-muted)]">
            Browsers cannot read the SMS inbox. On Android, install the FieldSense companion app
            (Capacitor) so real SMS triggers Gmail compose and an optional authenticated API push.
            Here you configure destinations and simulate a message.
          </p>
        </div>
        <label className="inline-flex items-center gap-2 rounded-xl border border-[color:var(--ink-line)] bg-[color:var(--wash)] px-3 py-2 text-sm">
          <Switch
            checked={settings.enabled}
            onCheckedChange={(enabled) => update({ enabled })}
            disabled={!hydrated}
          />
          Forward on
        </label>
      </div>

      <div className="mt-5 grid gap-4 md:grid-cols-2">
        <div className="space-y-3 rounded-xl border border-[color:var(--ink-line)] bg-[color:var(--wash)]/70 p-4">
          <div className="flex items-center justify-between gap-2">
            <h3 className="text-sm font-medium text-[color:var(--ink)]">Gmail app</h3>
            <Switch
              checked={settings.openGmailApp}
              onCheckedChange={(openGmailApp) => update({ openGmailApp })}
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="forward-email">Forward to email</Label>
            <Input
              id="forward-email"
              type="email"
              placeholder="you@example.com"
              value={settings.forwardEmail}
              onChange={(e) => update({ forwardEmail: e.target.value })}
              className="rounded-xl bg-[color:var(--panel)]"
            />
            <p className="text-xs text-[color:var(--ink-muted)]">
              {emailReady
                ? "Opens Gmail compose with SMS from/body prefilled."
                : "Enter the inbox that should receive forwarded SMS mail."}
            </p>
          </div>
        </div>

        <div className="space-y-3 rounded-xl border border-[color:var(--ink-line)] bg-[color:var(--wash)]/70 p-4">
          <div className="flex items-center justify-between gap-2">
            <h3 className="text-sm font-medium text-[color:var(--ink)]">API webhook</h3>
            <Switch
              checked={settings.apiEnabled}
              onCheckedChange={(apiEnabled) => update({ apiEnabled })}
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="api-endpoint">Endpoint URL</Label>
            <Input
              id="api-endpoint"
              type="url"
              placeholder="https://api.example.com/hooks/sms"
              value={settings.apiEndpoint}
              onChange={(e) => update({ apiEndpoint: e.target.value })}
              className="rounded-xl bg-[color:var(--panel)]"
            />
          </div>
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="space-y-1.5">
              <Label htmlFor="api-header">Auth header</Label>
              <Input
                id="api-header"
                placeholder="Authorization"
                value={settings.apiAuthHeader}
                onChange={(e) => update({ apiAuthHeader: e.target.value })}
                className="rounded-xl bg-[color:var(--panel)]"
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="api-token">Auth token</Label>
              <Input
                id="api-token"
                type="password"
                placeholder="Bearer token / API key"
                value={settings.apiAuthToken}
                onChange={(e) => update({ apiAuthToken: e.target.value })}
                className="rounded-xl bg-[color:var(--panel)]"
                autoComplete="off"
              />
            </div>
          </div>
          <p className="text-xs text-[color:var(--ink-muted)]">
            {apiReady
              ? "POSTs JSON { notification, message, sms } when an SMS arrives (or on simulate)."
              : "Optional — set later when your backend is ready."}
          </p>
          <Button
            type="button"
            variant="outline"
            className="rounded-xl"
            onClick={() =>
              update({
                apiEnabled: true,
                apiEndpoint:
                  settings.apiEndpoint ||
                  `${window.location.origin}/api/sms-webhook`,
              })
            }
          >
            Use built-in test webhook
          </Button>
        </div>
      </div>

      <div className="mt-5 grid gap-4 lg:grid-cols-[1.1fr_0.9fr]">
        <div className="space-y-3">
          <h3 className="text-sm font-medium text-[color:var(--ink)]">Simulate incoming SMS</h3>
          <div className="space-y-1.5">
            <Label htmlFor="sms-from">From</Label>
            <Input
              id="sms-from"
              value={from}
              onChange={(e) => setFrom(e.target.value)}
              className="rounded-xl"
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="sms-body">Message</Label>
            <Textarea
              id="sms-body"
              value={body}
              onChange={(e) => setBody(e.target.value)}
              className="min-h-24 rounded-xl"
            />
          </div>
          <Button
            type="button"
            disabled={busy || !hydrated}
            onClick={() => void onSimulate()}
            className="rounded-xl bg-[color:var(--teal)] text-[color:var(--teal-ink)] hover:bg-[color:var(--teal-deep)]"
          >
            {busy ? "Dispatching…" : "Simulate SMS receive"}
          </Button>
        </div>

        <div className="rounded-xl border border-[color:var(--ink-line)] bg-[color:var(--wash)]/70 p-4">
          <h3 className="text-sm font-medium text-[color:var(--ink)]">Activity</h3>
          {lastResult ? (
            <ul className="mt-2 space-y-1 font-mono text-xs text-[color:var(--ink-muted)]">
              <li>email: {lastResult.email.detail}</li>
              <li>api: {lastResult.api.detail}</li>
            </ul>
          ) : (
            <p className="mt-2 text-sm text-[color:var(--ink-muted)]">No dispatch yet.</p>
          )}
          <ul className="mt-3 space-y-1 font-mono text-[11px] text-[color:var(--ink-muted)]">
            {log.map((line) => (
              <li key={line}>{line}</li>
            ))}
          </ul>
          <ol className="mt-4 list-decimal space-y-1 pl-4 text-xs leading-relaxed text-[color:var(--ink-muted)]">
            <li>Save email (+ optional API) above and arm Forward.</li>
            <li>Simulate here, or build the Android companion for real SMS.</li>
            <li>On device SMS: native receiver opens Gmail and POSTs the webhook.</li>
          </ol>
        </div>
      </div>
    </section>
  );
}
