import { useEffect, useMemo, useState, type FormEvent } from "react";
import { api, API_BASE, usesSampleData, type Message, type User } from "./api";
import { loadLang, saveLang, t, type Lang, type MsgKey } from "./i18n";

type Tab = "inbox" | "send" | "device" | "settings" | "admin";

function applyDocumentLang(lang: Lang) {
  document.documentElement.lang = lang;
  document.documentElement.dir = lang === "fa" ? "rtl" : "ltr";
  document.title = lang === "fa" ? "دروازه" : "Darvazeh";
}

export default function App() {
  const [lang, setLang] = useState<Lang>(() => loadLang());
  const [token, setToken] = useState(() => api.getToken());
  const [user, setUser] = useState<(User & { devices?: unknown }) | null>(null);
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
  const [toNumber, setToNumber] = useState("");
  const [smsBody, setSmsBody] = useState("");
  const [deviceName, setDeviceName] = useState(() =>
    loadLang() === "fa" ? "گوشی ایران" : "Iran phone",
  );
  const [devicePhone, setDevicePhone] = useState("");
  const [pairedToken, setPairedToken] = useState<string | null>(null);
  const [forwardOn, setForwardOn] = useState(false);
  const [forwardEmail, setForwardEmail] = useState("");
  const [adminUsers, setAdminUsers] = useState<
    Array<{ id: string; email: string; role: string; canRelaySms: boolean; deviceCount: number }>
  >([]);

  const tr = (key: MsgKey) => t(lang, key);
  const isAdmin = user?.role === "Admin";
  const canSend = !!(user?.canRelaySms || isAdmin);

  useEffect(() => {
    applyDocumentLang(lang);
    saveLang(lang);
    setDeviceName((prev) => {
      if (prev === "Iran phone" || prev === "گوشی ایران" || !prev) {
        return lang === "fa" ? "گوشی ایران" : "Iran phone";
      }
      return prev;
    });
  }, [lang]);

  async function refresh() {
    const me = await api.me();
    setUser(me);
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
      setOtpHint(sample ? tr("sampleOtpHint") : res.detail || "");
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
      { id: "device", label: "device" },
      { id: "settings", label: "settings" },
    ];
    if (canSend) list.splice(1, 0, { id: "send", label: "send" });
    if (isAdmin) list.push({ id: "admin", label: "admin" });
    return list;
  }, [canSend, isAdmin]);

  if (!token || !user) {
    return (
      <div className="app-shell">
        <header className="hero-gate">
          <LangSwitch />
          <div className="hero-inner">
            <p className="brand-en">{lang === "fa" ? "DARVAZEH" : "دروازه"}</p>
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
      <header className="hero-gate" style={{ paddingBottom: "1.1rem" }}>
        <LangSwitch />
        <div className="app-top" style={{ position: "relative", zIndex: 1 }}>
          <div>
            <p className="brand-sm">{tr("brand")}</p>
            <p className="muted" style={{ margin: "0.2rem 0 0", color: "rgba(243,240,230,0.85)" }}>
              {user.email} · {roleLabel}
              {canSend ? ` · ${tr("relayOn")}` : ""}
            </p>
          </div>
          <button className="btn ghost" onClick={logout} style={{ color: "var(--ivory)", borderColor: "rgba(243,240,230,0.35)" }}>
            {tr("signOut")}
          </button>
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
          {messages.map((m) => (
            <article key={m.id} className="msg">
              <div className="row">
                <span className={`badge ${m.direction === "inbound" ? "in" : "out"}`}>
                  {m.direction}
                </span>
                <strong className="mono" dir="ltr">
                  {m.peerNumber}
                </strong>
                <span className="muted mono" style={{ fontSize: "0.8rem" }} dir="ltr">
                  {new Date(m.createdAt).toLocaleString(lang === "fa" ? "fa-IR" : "en-NZ")}
                </span>
              </div>
              <p style={{ margin: "0.45rem 0 0", whiteSpace: "pre-wrap" }}>{m.body}</p>
              <p className="muted mono" style={{ fontSize: "0.75rem", margin: "0.35rem 0 0" }}>
                {m.status}
                {m.emailForwarded ? ` · ${tr("emailed")}` : ""}
              </p>
            </article>
          ))}
        </section>
      )}

      {tab === "send" && (
        <section className="card">
          <h2 style={{ marginTop: 0, fontFamily: "var(--font-display)" }}>{tr("sendTitle")}</h2>
          <p className="muted">{tr("sendLead")}</p>
          <form
            onSubmit={async (e) => {
              e.preventDefault();
              setBusy(true);
              setError(null);
              try {
                await api.sendSms(toNumber, smsBody);
                setSmsBody("");
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
        </section>
      )}

      {tab === "device" && (
        <section className="card">
          <h2 style={{ marginTop: 0, fontFamily: "var(--font-display)" }}>{tr("deviceTitle")}</h2>
          <p className="muted">{tr("deviceLead")}</p>
          <div className="field">
            <label>{tr("deviceName")}</label>
            <input value={deviceName} onChange={(e) => setDeviceName(e.target.value)} />
          </div>
          <div className="field">
            <label>{tr("simOptional")}</label>
            <input
              value={devicePhone}
              onChange={(e) => setDevicePhone(e.target.value)}
              placeholder="+98…"
              dir="ltr"
            />
          </div>
          <button
            className="btn"
            type="button"
            style={{ marginTop: "1rem" }}
            onClick={async () => {
              setBusy(true);
              setError(null);
              try {
                const d = await api.pairDevice(deviceName, devicePhone || undefined);
                setPairedToken(d.deviceToken);
                await refresh();
              } catch (err) {
                setError(err instanceof Error ? err.message : "Pair failed");
              } finally {
                setBusy(false);
              }
            }}
          >
            {tr("generateToken")}
          </button>
          {pairedToken && (
            <div style={{ marginTop: "1rem" }}>
              <p className="muted">{tr("copyToken")}</p>
              <p className="mono token-box" dir="ltr">
                {pairedToken}
              </p>
              <p className="muted mono" style={{ fontSize: "0.8rem" }} dir="ltr">
                {tr("androidApi")}: {API_BASE || tr("sampleMode")}
              </p>
            </div>
          )}
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
