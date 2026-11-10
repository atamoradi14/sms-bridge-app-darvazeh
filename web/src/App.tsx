import { useEffect, useMemo, useState, type FormEvent } from "react";
import { api, API_BASE, usesSampleData, type Message, type User } from "./api";

type Tab = "inbox" | "send" | "device" | "settings" | "admin";

export default function App() {
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
  const [deviceName, setDeviceName] = useState("Iran phone");
  const [devicePhone, setDevicePhone] = useState("");
  const [pairedToken, setPairedToken] = useState<string | null>(null);
  const [forwardOn, setForwardOn] = useState(false);
  const [forwardEmail, setForwardEmail] = useState("");
  const [adminUsers, setAdminUsers] = useState<
    Array<{ id: string; email: string; role: string; canRelaySms: boolean; deviceCount: number }>
  >([]);

  const isAdmin = user?.role === "Admin";
  const canSend = !!(user?.canRelaySms || isAdmin);

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
      // Never surface real OTP codes in production builds.
      if (import.meta.env.DEV) setDebugCode(res.debugCode ?? null);
      if (sample) setOtpHint("Sample mode: enter 000000");
      else setOtpHint(res.detail || "Check your email for the code.");
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
        setError("Enter admin password from the API server env");
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

  const tabs = useMemo(() => {
    const t: Tab[] = ["inbox", "device", "settings"];
    if (canSend) t.splice(1, 0, "send");
    if (isAdmin) t.push("admin");
    return t;
  }, [canSend, isAdmin]);

  if (!token || !user) {
    return (
      <div className="app-shell">
        <header style={{ marginBottom: "1.25rem" }}>
          <p className="brand">FieldSense</p>
          <p className="muted">Iran SIM relay — receive OTPs abroad, send SMS through your home phone.</p>
        </header>
        <section className="card" style={{ maxWidth: 460 }}>
          <h2 style={{ marginTop: 0 }}>Email login</h2>
          {sample ? (
            <p className="muted">
              Running on <strong>sample data</strong> — no public API URL is configured yet.
              Add app secret <span className="mono">FIELDSENSE_API_BASE</span> (your ASP.NET URL)
              for real ACS email OTP.
            </p>
          ) : (
            <p className="muted">We email a one-time code via Azure Communication Services.</p>
          )}
          {!otpSent ? (
            <form onSubmit={onRequestOtp}>
              <div className="field">
                <label htmlFor="email">Email</label>
                <input
                  id="email"
                  type="email"
                  required
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  placeholder="you@example.com"
                  autoComplete="email"
                />
              </div>
              <div className="row" style={{ marginTop: "1rem" }}>
                <button className="btn" disabled={busy} type="submit">
                  Send code
                </button>
              </div>
            </form>
          ) : (
            <form onSubmit={onVerify}>
              <div className="field">
                <label htmlFor="code">One-time code</label>
                <input
                  id="code"
                  inputMode="numeric"
                  required
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                  placeholder="6-digit code"
                />
              </div>
              {needAdminPassword && (
                <div className="field">
                  <label htmlFor="adminPass">Admin password</label>
                  <input
                    id="adminPass"
                    type="password"
                    required
                    value={adminPassword}
                    onChange={(e) => setAdminPassword(e.target.value)}
                    placeholder="From server FieldSense__AdminPassword"
                  />
                </div>
              )}
              {import.meta.env.DEV && debugCode && (
                <p className="mono muted">Dev mock code: {debugCode}</p>
              )}
              {otpHint && <p className="muted">{otpHint}</p>}
              <div className="row" style={{ marginTop: "1rem" }}>
                <button className="btn" disabled={busy} type="submit">
                  Sign in
                </button>
                <button className="btn ghost" type="button" onClick={() => setOtpSent(false)}>
                  Back
                </button>
              </div>
            </form>
          )}
          {error && <div className="error">{error}</div>}
          <p className="muted" style={{ marginTop: "1rem", fontSize: "0.85rem" }}>
            API: <span className="mono">{API_BASE || "(sample mode)"}</span>
          </p>
        </section>
      </div>
    );
  }

  return (
    <div className="app-shell">
      <header className="row" style={{ justifyContent: "space-between", marginBottom: "0.5rem" }}>
        <div>
          <p className="brand" style={{ fontSize: "1.8rem" }}>FieldSense</p>
          <p className="muted" style={{ margin: 0 }}>
            {user.email} · {user.role}
            {canSend ? " · relay on" : ""}
          </p>
        </div>
        <button className="btn ghost" onClick={logout}>
          Sign out
        </button>
      </header>

      <nav className="tabs">
        {tabs.map((t) => (
          <button
            key={t}
            className={`tab ${tab === t ? "active" : ""}`}
            onClick={() => setTab(t)}
            type="button"
          >
            {t}
          </button>
        ))}
      </nav>

      {error && <div className="error">{error}</div>}

      {tab === "inbox" && (
        <section className="card">
          <div className="row" style={{ justifyContent: "space-between" }}>
            <h2 style={{ margin: 0 }}>Inbox</h2>
            <button className="btn secondary" type="button" onClick={() => refresh().catch((e) => setError(e.message))}>
              Refresh
            </button>
          </div>
          <p className="muted">SMS arriving on your Iran phone appear here.</p>
          {messages.length === 0 && <p className="muted">No messages yet.</p>}
          {messages.map((m) => (
            <article key={m.id} className="msg">
              <div className="row">
                <span className={`badge ${m.direction === "inbound" ? "in" : "out"}`}>
                  {m.direction}
                </span>
                <strong className="mono">{m.peerNumber}</strong>
                <span className="muted mono" style={{ fontSize: "0.8rem" }}>
                  {new Date(m.createdAt).toLocaleString()}
                </span>
              </div>
              <p style={{ margin: "0.45rem 0 0", whiteSpace: "pre-wrap" }}>{m.body}</p>
              <p className="muted mono" style={{ fontSize: "0.75rem", margin: "0.35rem 0 0" }}>
                {m.status}
                {m.emailForwarded ? " · emailed" : ""}
              </p>
            </article>
          ))}
        </section>
      )}

      {tab === "send" && (
        <section className="card">
          <h2 style={{ marginTop: 0 }}>Send via Iran phone</h2>
          <p className="muted">
            Message is queued to your paired device. The phone must be online in Iran.
          </p>
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
              <label htmlFor="to">To number</label>
              <input
                id="to"
                required
                value={toNumber}
                onChange={(e) => setToNumber(e.target.value)}
                placeholder="+98912…"
              />
            </div>
            <div className="field">
              <label htmlFor="body">Message</label>
              <textarea
                id="body"
                required
                value={smsBody}
                onChange={(e) => setSmsBody(e.target.value)}
                placeholder="Type SMS text"
              />
            </div>
            <button className="btn" disabled={busy} type="submit" style={{ marginTop: "1rem" }}>
              Queue SMS
            </button>
          </form>
        </section>
      )}

      {tab === "device" && (
        <section className="card">
          <h2 style={{ marginTop: 0 }}>Pair Iran Android phone</h2>
          <p className="muted">
            Create a device token, paste it into the FieldSense Android app on the phone with the SIM.
          </p>
          <div className="field">
            <label>Device name</label>
            <input value={deviceName} onChange={(e) => setDeviceName(e.target.value)} />
          </div>
          <div className="field">
            <label>SIM number (optional)</label>
            <input value={devicePhone} onChange={(e) => setDevicePhone(e.target.value)} placeholder="+98…" />
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
            Generate device token
          </button>
          {pairedToken && (
            <div style={{ marginTop: "1rem" }}>
              <p className="muted">Copy into Android app → Device token</p>
              <p className="mono" style={{ wordBreak: "break-all", background: "var(--wash)", padding: "0.85rem", borderRadius: "0.9rem" }}>
                {pairedToken}
              </p>
              <p className="muted mono" style={{ fontSize: "0.8rem" }}>
                API base for Android: {API_BASE}
              </p>
            </div>
          )}
        </section>
      )}

      {tab === "settings" && (
        <section className="card">
          <h2 style={{ marginTop: 0 }}>Email forward</h2>
          <p className="muted">Also send inbound SMS copies to email via ACS.</p>
          <label className="switch">
            <input type="checkbox" checked={forwardOn} onChange={(e) => setForwardOn(e.target.checked)} />
            Forward inbound SMS to email
          </label>
          <div className="field">
            <label>Forward address</label>
            <input value={forwardEmail} onChange={(e) => setForwardEmail(e.target.value)} />
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
            Save settings
          </button>
        </section>
      )}

      {tab === "admin" && isAdmin && (
        <section className="card">
          <h2 style={{ marginTop: 0 }}>Admin · relay grants</h2>
          <p className="muted">Allow users to send SMS through their paired Iran phone.</p>
          {adminUsers.map((u) => (
            <div key={u.id} className="msg row" style={{ justifyContent: "space-between" }}>
              <div>
                <strong>{u.email}</strong>
                <div className="muted mono" style={{ fontSize: "0.8rem" }}>
                  {u.role} · devices {u.deviceCount} · relay {u.canRelaySms ? "on" : "off"}
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
                {u.canRelaySms ? "Revoke relay" : "Grant relay"}
              </button>
            </div>
          ))}
        </section>
      )}
    </div>
  );
}
