import { useEffect, useMemo, useState, type FormEvent } from "react";
import { api, API_BASE, usesSampleData, type Device, type Message, type User } from "./api";
import { loadLang, saveLang, t, type Lang, type MsgKey } from "./i18n";

type Tab = "inbox" | "send" | "device" | "settings" | "admin";

const PAGE_SIZE = 8;

function applyDocumentLang(lang: Lang) {
  document.documentElement.lang = lang;
  document.documentElement.dir = lang === "fa" ? "rtl" : "ltr";
  document.title = "دروازه";
}

/** Prefer readable IPv4 when present; otherwise keep a wrapped IPv6. */
function formatIp(raw?: string | null): string {
  if (!raw) return "—";
  const v4 = raw.match(/\b(?:\d{1,3}\.){3}\d{1,3}\b/);
  if (v4) return v4[0];
  return raw.replace(/^::ffff:/i, "");
}

function formatWhen(iso: string, lang: Lang) {
  return new Date(iso).toLocaleString(lang === "fa" ? "fa-IR" : "en-NZ", {
    year: "numeric",
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

export default function App() {
  const [lang, setLang] = useState<Lang>(() => loadLang());
  const [token, setToken] = useState(() => api.getToken());
  const [user, setUser] = useState<User | null>(null);
  const [devices, setDevices] = useState<Device[]>([]);
  const [tab, setTab] = useState<Tab>("inbox");
  const [email, setEmail] = useState("");
  const [code, setCode] = useState("");
  const [adminPassword, setAdminPassword] = useState("");
  const [needAdminPassword, setNeedAdminPassword] = useState(false);
  const [otpSent, setOtpSent] = useState(false);
  const [debugCode, setDebugCode] = useState<string | null>(null);
  const [otpHint, setOtpHint] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const sample = usesSampleData();
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<Message[]>([]);
  const [inboxPage, setInboxPage] = useState(0);
  const [toNumber, setToNumber] = useState("");
  const [smsBody, setSmsBody] = useState("");
  const [forwardOn, setForwardOn] = useState(false);
  const [forwardEmail, setForwardEmail] = useState("");
  const [adminUsers, setAdminUsers] = useState<
    Array<{ id: string; email: string; role: string; canRelaySms: boolean; deviceCount: number }>
  >([]);

  const tr = (key: MsgKey) => t(lang, key);
  const isAdmin = user?.role === "Admin";
  const canSend = !!(user?.canRelaySms || isAdmin || devices.length > 0);
  const inboxPages = Math.max(1, Math.ceil(messages.length / PAGE_SIZE));
  const pageSafe = Math.min(inboxPage, inboxPages - 1);
  const pageMessages = messages.slice(pageSafe * PAGE_SIZE, pageSafe * PAGE_SIZE + PAGE_SIZE);

  useEffect(() => {
    applyDocumentLang(lang);
    saveLang(lang);
  }, [lang]);

  async function refresh() {
    const me = await api.me();
    setUser(me);
    setDevices(me.devices || []);
    setForwardOn(me.forwardEmailEnabled);
    setForwardEmail(me.forwardEmailAddress || me.email);
    const msgs = await api.messages();
    setMessages(msgs);
    if (me.role === "Admin") setAdminUsers(await api.adminUsers());
  }

  useEffect(() => {
    if (!token) return;
    refresh().catch((e) => setError(e.message));
    const id = setInterval(() => {
      api.messages().then(setMessages).catch(() => undefined);
    }, 8000);
    return () => clearInterval(id);
  }, [token]);

  async function onRequestOtp(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    setDebugCode(null);
    setOtpHint(null);
    try {
      const res = await api.requestOtp(email);
      setOtpSent(true);
      if (import.meta.env.DEV) setDebugCode(res.debugCode ?? null);
      const detail = res.detail || "";
      const mockHint =
        /000000|mock/i.test(detail) || sample ? tr("sampleOtpHint") : detail;
      setOtpHint(mockHint || detail);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed");
    } finally {
      setBusy(false);
    }
  }

  async function onVerify(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const res = await api.verifyOtp(
        email,
        code,
        needAdminPassword ? adminPassword : undefined,
      );
      api.setToken(res.token);
      setToken(res.token);
      setUser(res.user);
      setNeedAdminPassword(false);
    } catch (err) {
      const e2 = err as Error & { needAdminPassword?: boolean };
      if (e2.needAdminPassword) {
        setNeedAdminPassword(true);
        setError(tr("adminPassHint"));
      } else {
        setError(e2.message);
      }
    } finally {
      setBusy(false);
    }
  }

  function logout() {
    api.setToken(null);
    setToken(null);
    setUser(null);
    setDevices([]);
    setOtpSent(false);
    setCode("");
  }

  function LangSwitch() {
    return (
      <div className="lang-switch">
        <button
          type="button"
          className={`lang-btn ${lang === "fa" ? "active" : ""}`}
          onClick={() => setLang("fa")}
        >
          {tr("langFa")}
        </button>
        <button
          type="button"
          className={`lang-btn ${lang === "en" ? "active" : ""}`}
          onClick={() => setLang("en")}
        >
          {tr("langEn")}
        </button>
      </div>
    );
  }

  const tabs = useMemo(() => {
    const list: { id: Tab; label: MsgKey }[] = [
      { id: "inbox", label: "inbox" },
      { id: "send", label: "send" },
      { id: "device", label: "device" },
      { id: "settings", label: "settings" },
    ];
    if (isAdmin) list.push({ id: "admin", label: "admin" });
    return list;
  }, [isAdmin]);

  if (!token || !user) {
    return (
      <div className="app-shell">
        <header className="hero-gate">
          <LangSwitch />
          <div className="hero-inner">
            <h1 className="brand">{tr("brand")}</h1>
            <p className="tagline">{tr("tagline")}</p>
          </div>
        </header>

        <section className="card" style={{ maxWidth: 480, marginInline: "auto" }}>
          <h2 style={{ marginTop: 0, fontFamily: "var(--font-display)", fontSize: "1.55rem" }}>
            {tr("loginTitle")}
          </h2>
          {sample ? (
            <>
              <span className="sample-chip">{tr("sampleMode")}</span>
              <p className="muted">{tr("sampleBanner")}</p>
            </>
          ) : (
            <p className="muted">{tr("loginAcs")}</p>
          )}

          {!otpSent ? (
            <form onSubmit={onRequestOtp}>
              <div className="field">
                <label htmlFor="email">{tr("email")}</label>
                <input
                  id="email"
                  type="email"
                  required
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  placeholder="you@example.com"
                  autoComplete="email"
                  dir="ltr"
                />
              </div>
              <div className="row" style={{ marginTop: "1rem" }}>
                <button className="btn" disabled={busy} type="submit">
                  {tr("sendCode")}
                </button>
              </div>
            </form>
          ) : (
            <form onSubmit={onVerify}>
              <div className="field">
                <label htmlFor="code">{tr("otpLabel")}</label>
                <input
                  id="code"
                  inputMode="numeric"
                  required
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                  placeholder={tr("otpPlaceholder")}
                  dir="ltr"
                />
              </div>
              {needAdminPassword && (
                <div className="field">
                  <label htmlFor="adminPass">{tr("adminPass")}</label>
                  <input
                    id="adminPass"
                    type="password"
                    required
                    value={adminPassword}
                    onChange={(e) => setAdminPassword(e.target.value)}
                    placeholder={tr("adminPassHint")}
                    dir="ltr"
                  />
                </div>
              )}
              {import.meta.env.DEV && debugCode && (
                <p className="mono muted">Dev: {debugCode}</p>
              )}
              {otpHint && <p className="muted">{otpHint}</p>}
              <div className="row" style={{ marginTop: "1rem" }}>
                <button className="btn" disabled={busy} type="submit">
                  {tr("signIn")}
                </button>
                <button className="btn ghost" type="button" onClick={() => setOtpSent(false)}>
                  {tr("back")}
                </button>
              </div>
            </form>
          )}
          {error && <div className="error">{error}</div>}
          <p className="muted" style={{ marginTop: "1rem", fontSize: "0.85rem" }}>
            {tr("api")}:{" "}
            <span className="mono" dir="ltr">
              {API_BASE || tr("sampleMode")}
            </span>
          </p>
        </section>
      </div>
    );
  }

  const roleLabel = user.role === "Admin" ? tr("roleAdmin") : tr("roleUser");

  return (
    <div className="app-shell">
      <header className="hero-gate hero-app">
        <div className="app-top">
          <div className="app-top-main">
            <p className="brand-sm">{tr("brand")}</p>
            <p className="app-user" dir="ltr">
              {user.email}
              <span> · {roleLabel}</span>
              {canSend ? <span> · {tr("relayOn")}</span> : null}
            </p>
          </div>
          <div className="app-top-actions">
            <LangSwitch />
            <button className="btn ghost btn-signout" type="button" onClick={logout}>
              {tr("signOut")}
            </button>
          </div>
        </div>
      </header>

      <nav className="tabs">
        {tabs.map((item) => (
          <button
            key={item.id}
            className={`tab ${tab === item.id ? "active" : ""}`}
            onClick={() => setTab(item.id)}
            type="button"
          >
            {tr(item.label)}
          </button>
        ))}
      </nav>

      {error && <div className="error">{error}</div>}

      {tab === "inbox" && (
        <section className="card">
          <div className="row" style={{ justifyContent: "space-between" }}>
            <h2 style={{ margin: 0, fontFamily: "var(--font-display)" }}>{tr("inbox")}</h2>
            <button
              className="btn secondary"
              type="button"
              onClick={() => refresh().catch((e) => setError(e.message))}
            >
              {tr("refresh")}
            </button>
          </div>
          <p className="muted">{tr("inboxLead")}</p>
          {messages.length === 0 && <p className="muted">{tr("noMessages")}</p>}
          <div className="msg-list">
            {pageMessages.map((m) => {
              const inbound = m.direction === "inbound";
              return (
                <article key={m.id} className={`msg-card ${inbound ? "in" : "out"}`}>
                  <div className="msg-meta">
                    <span className={`badge ${inbound ? "in" : "out"}`}>
                      {inbound ? tr("inbound") : tr("outbound")}
                    </span>
                    <div className="msg-peer">
                      <span className="msg-peer-label">
                        {inbound ? tr("fromLabel") : tr("toLabel")}
                      </span>
                      <strong className="mono" dir="ltr">
                        {m.peerNumber || "—"}
                      </strong>
                    </div>
                    <time className="msg-time mono" dir="ltr" dateTime={m.createdAt}>
                      {tr("dateLabel")}: {formatWhen(m.createdAt, lang)}
                    </time>
                  </div>
                  <p className="msg-body">{m.body}</p>
                  <p className="msg-status muted">
                    {m.status}
                    {m.emailForwarded ? ` · ${tr("emailed")}` : ""}
                  </p>
                </article>
              );
            })}
          </div>
          {messages.length > PAGE_SIZE && (
            <div className="pager">
              <button
                className="btn secondary"
                type="button"
                disabled={pageSafe <= 0}
                onClick={() => setInboxPage((p) => Math.max(0, p - 1))}
              >
                {tr("prevPage")}
              </button>
              <span className="muted">
                {tr("pageOf")} {pageSafe + 1} / {inboxPages}
              </span>
              <button
                className="btn secondary"
                type="button"
                disabled={pageSafe >= inboxPages - 1}
                onClick={() => setInboxPage((p) => Math.min(inboxPages - 1, p + 1))}
              >
                {tr("nextPage")}
              </button>
            </div>
          )}
        </section>
      )}

      {tab === "send" && (
        <section className="card">
          <h2 style={{ marginTop: 0, fontFamily: "var(--font-display)" }}>{tr("sendTitle")}</h2>
          <p className="muted">{tr("sendLead")}</p>
          {!canSend ? (
            <p className="error" style={{ marginTop: "0.75rem" }}>
              {tr("sendNeedRelay")}
            </p>
          ) : (
            <form
              onSubmit={async (e) => {
                e.preventDefault();
                setBusy(true);
                setError(null);
                try {
                  await api.sendSms(toNumber, smsBody);
                  setSmsBody("");
                  setInboxPage(0);
                  await refresh();
                  setTab("inbox");
                } catch (err) {
                  setError(err instanceof Error ? err.message : "Send failed");
                } finally {
                  setBusy(false);
                }
              }}
            >
              <div className="field">
                <label htmlFor="to">{tr("toNumber")}</label>
                <input
                  id="to"
                  required
                  value={toNumber}
                  onChange={(e) => setToNumber(e.target.value)}
                  placeholder="+98912…"
                  dir="ltr"
                />
              </div>
              <div className="field">
                <label htmlFor="body">{tr("message")}</label>
                <textarea
                  id="body"
                  required
                  value={smsBody}
                  onChange={(e) => setSmsBody(e.target.value)}
                />
              </div>
              <button className="btn" disabled={busy} type="submit" style={{ marginTop: "1rem" }}>
                {tr("queueSms")}
              </button>
            </form>
          )}
        </section>
      )}

      {tab === "device" && (
        <section className="card">
          <div className="row" style={{ justifyContent: "space-between" }}>
            <h2 style={{ margin: 0, fontFamily: "var(--font-display)" }}>{tr("deviceTitle")}</h2>
            <button
              className="btn secondary"
              type="button"
              onClick={() => refresh().catch((e) => setError(e.message))}
            >
              {tr("refresh")}
            </button>
          </div>
          <p className="muted">{tr("deviceLead")}</p>

          <div className="apk-panel">
            <h3 style={{ margin: "0 0 0.35rem", fontFamily: "var(--font-display)", fontSize: "1.15rem" }}>
              {tr("connectPhone")}
            </h3>
            <p className="muted" style={{ marginTop: 0 }}>
              {tr("downloadApkHint")}
            </p>
            <a
              className="btn"
              href={`${import.meta.env.BASE_URL}Darvazeh-SMS.apk`}
              download="Darvazeh-SMS.apk"
            >
              {tr("downloadApk")}
            </a>
          </div>

          {devices.length === 0 && <p className="muted">{tr("noDevices")}</p>}
          {devices.map((d) => (
            <article key={d.id} className="device-card">
              <div className="device-card-head">
                <div>
                  <strong>{d.name || d.deviceModel || tr("deviceTitle")}</strong>
                  <div style={{ marginTop: "0.35rem" }}>
                    <span className={`badge ${d.isOnline ? "in" : "out"}`}>
                      {d.isOnline ? tr("deviceOnline") : tr("deviceOffline")}
                    </span>
                  </div>
                </div>
                <button
                  className="btn danger"
                  type="button"
                  onClick={async () => {
                    try {
                      await api.removeDevice(d.id);
                      await refresh();
                    } catch (err) {
                      setError(err instanceof Error ? err.message : "Remove failed");
                    }
                  }}
                >
                  {tr("removeDevice")}
                </button>
              </div>
              <dl className="device-facts">
                <div>
                  <dt>{tr("phoneType")}</dt>
                  <dd dir="ltr">{d.deviceModel || d.name || "Android"}</dd>
                </div>
                <div>
                  <dt>{tr("platformLabel")}</dt>
                  <dd dir="ltr">{d.platform || "android"}</dd>
                </div>
                <div>
                  <dt>{tr("versionLabel")}</dt>
                  <dd dir="ltr">{d.appVersion || "—"}</dd>
                </div>
                <div>
                  <dt>{tr("ipLabel")}</dt>
                  <dd className="ip-value" dir="ltr">
                    {formatIp(d.lastIpAddress)}
                  </dd>
                </div>
                {d.phoneNumber ? (
                  <div>
                    <dt>{tr("simLabel")}</dt>
                    <dd dir="ltr">{d.phoneNumber}</dd>
                  </div>
                ) : null}
                {d.lastSeenAt ? (
                  <div>
                    <dt>{tr("lastSeen")}</dt>
                    <dd dir="ltr">{formatWhen(d.lastSeenAt, lang)}</dd>
                  </div>
                ) : null}
              </dl>
            </article>
          ))}
        </section>
      )}

      {tab === "settings" && (
        <section className="card">
          <h2 style={{ marginTop: 0, fontFamily: "var(--font-display)" }}>{tr("settingsTitle")}</h2>
          <p className="muted">{tr("settingsLead")}</p>
          <label className="switch">
            <input
              type="checkbox"
              checked={forwardOn}
              onChange={(e) => setForwardOn(e.target.checked)}
            />
            {tr("forwardToggle")}
          </label>
          <div className="field">
            <label>{tr("forwardAddress")}</label>
            <input
              value={forwardEmail}
              onChange={(e) => setForwardEmail(e.target.value)}
              dir="ltr"
            />
          </div>
          <button
            className="btn"
            type="button"
            style={{ marginTop: "1rem" }}
            onClick={async () => {
              setBusy(true);
              try {
                await api.updateSettings(forwardOn, forwardEmail);
                await refresh();
              } catch (err) {
                setError(err instanceof Error ? err.message : "Save failed");
              } finally {
                setBusy(false);
              }
            }}
          >
            {tr("saveSettings")}
          </button>
        </section>
      )}

      {tab === "admin" && isAdmin && (
        <section className="card">
          <h2 style={{ marginTop: 0, fontFamily: "var(--font-display)" }}>{tr("adminTitle")}</h2>
          <p className="muted">{tr("adminLead")}</p>
          {adminUsers.map((u) => (
            <div key={u.id} className="msg row" style={{ justifyContent: "space-between" }}>
              <div>
                <strong dir="ltr">{u.email}</strong>
                <div className="muted mono" style={{ fontSize: "0.8rem" }}>
                  {u.role} · {u.deviceCount} · {u.canRelaySms ? tr("relayOn") : "—"}
                </div>
              </div>
              <button
                className={`btn ${u.canRelaySms ? "danger" : "secondary"}`}
                type="button"
                onClick={async () => {
                  await api.setRelay(u.id, !u.canRelaySms);
                  setAdminUsers(await api.adminUsers());
                }}
              >
                {u.canRelaySms ? tr("revokeRelay") : tr("grantRelay")}
              </button>
            </div>
          ))}
        </section>
      )}
    </div>
  );
}
