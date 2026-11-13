import { utcDate } from "./format";
import { WorkspaceShell } from "./WorkspaceShell";
import { screenInfo, readScreen, type Screen } from "./workspaceNavigation";
import { Overview, AdminOverview } from "./Overview";
import { MessageHistory } from "./MessageHistory";
import {
  AdminSecurity,
  AdminCreateUser,
  AdminAccountControls,
} from "./AdminAccounts";
import { VerifyEmailBanner, ProfileSecurity } from "./AccountSecurity";
import { GoogleLogin } from "./GoogleLogin";
import { PasswordAccess, EmailDiagnostics } from "./PasswordAccess";
import { Phones } from "./Phones";
import { BillingPanel, PlanManager, AdminPayments } from "./Billing";
import { PaymentWalletAdmin } from "./PaymentWallet";
import { useCallback, useEffect, useState, type FormEvent } from "react";
import {
  api,
  RequestError,
  type User,
  type AdminUser,
  type Plan,
  type Activity,
  type Stats,
} from "./api";
import { loadLang, saveLang, type Lang } from "./i18n";
const digits = (s: string) =>
  s.replace(/[۰-۹٠-٩]/g, (c) =>
    String(c.charCodeAt(0) - (c >= "۰" ? 1776 : 1632)),
  );
export default function App() {
  const [lang, setLang] = useState<Lang>(loadLang);
  const [token, setToken] = useState(api.getToken);
  const [googleReturn, setGoogleReturn] = useState(() =>
    new URLSearchParams(location.hash.slice(1)).has("google_code"),
  );
  const [user, setUser] = useState<User | null>(null);
  const [requestedScreen, setScreen] = useState<Screen>(readScreen);
  const tab =
    user && user.role !== "Admin" && requestedScreen.startsWith("admin")
      ? "overview"
      : requestedScreen;
  const [peopleStatus, setPeopleStatus] = useState("all");
  const [peoplePage, setPeoplePage] = useState(1);
  const [email, setEmail] = useState("");
  const [code, setCode] = useState("");
  const [password, setPassword] = useState("");
  const [usePassword, setUsePassword] = useState(true);
  const [otpSent, setOtpSent] = useState(false);
  const [countdown, setCountdown] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [to, setTo] = useState("");
  const [body, setBody] = useState("");
  const [forward, setForward] = useState(false);
  const [forwardEmail, setForwardEmail] = useState("");
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [plans, setPlans] = useState<Plan[]>([]);
  const [activities, setActivities] = useState<Activity[]>([]);
  const [stats, setStats] = useState<Stats | null>(null);
  const [search, setSearch] = useState("");
  function navigate(next: Screen) {
    if (next === requestedScreen) return;
    setScreen(next);
    setError("");
    setNotice("");
    window.scrollTo(0, 0);
    const url = new URL(location.href);
    url.searchParams.set("view", next);
    history.pushState({}, "", url.pathname + url.search + url.hash);
  }
  useEffect(() => {
    const back = () => {
      setScreen(readScreen());
      setError("");
      setNotice("");
    };
    window.addEventListener("popstate", back);
    return () => window.removeEventListener("popstate", back);
  }, []);

  useEffect(() => {
    if (user?.role && user.role !== "Admin" && requestedScreen.startsWith("admin")) {
      const url = new URL(location.href);
      url.searchParams.set("view", "overview");
      history.replaceState({}, "", url.pathname + url.search);
    }
  }, [user?.role, requestedScreen]);
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en);
  const date = (value: string) =>
    utcDate(value).toLocaleString(lang === "fa" ? "fa-IR" : "en-NZ", {
      dateStyle: "medium",
      timeStyle: "short",
    });
  const planName = (s: User["subscription"]) =>
    !user?.emailVerified && user
      ? tr("در انتظار تأیید", "Awaiting verification")
      : s.planCode === "trial"
        ? tr("آزمایشی", "Trial")
        : s.planName;
  const fail = (e: unknown) => {
    const r = e as RequestError;
    setError(
      r.code === "connection"
        ? tr(
            "اتصال برقرار نشد. اینترنت را بررسی کنید.",
            "Couldn’t connect. Check your connection and try again.",
          )
        : [
              "subscription_expired",
              "feature_disabled",
              "message_limit_reached",
            ].includes(r.code || "")
          ? tr(
              "اشتراک یا ظرفیت پیام‌های شما اجازه این کار را نمی‌دهد.",
              "Your plan or remaining allowance doesn’t permit this action.",
            )
          : tr(
              r.message || "دوباره تلاش کنید.",
              "We couldn’t complete that request. Check your details and try again.",
            ),
    );
  };
  const adminRefresh = useCallback(async () => {
    const [u, p, a, s] = await Promise.all([
      api.adminUsers(),
      api.plans(),
      api.activities(),
      api.adminStats(),
    ]);
    setUsers(u);
    setPlans(p);
    setActivities(a);
    setStats(s);
  }, []);
  useEffect(() => {
    if (user?.role !== "Admin" || !tab.startsWith("admin")) return;
    const load = () =>
      adminRefresh().catch(() =>
        setError(
          lang === "fa"
            ? "گزارش مدیریت به‌روز نشد؛ دوباره تلاش کنید."
            : "Could not refresh administration data. Please try again.",
        ),
      );
    void load();
    const timer = setInterval(load, 30000);
    return () => clearInterval(timer);
  }, [tab, user?.role, lang, adminRefresh]);
  const refresh = useCallback(async (admin = false) => {
    const me = await api.me();
    setUser(me);

    if (admin && me.role === "Admin") await adminRefresh();
    return me;
  }, [adminRefresh]);
  useEffect(() => {
    const params = new URLSearchParams(location.hash.slice(1));
    const code = params.get("google_code"),
      flow = params.get("google_flow"),
      problem = params.get("google_error");
    if (!code && !problem) return;
    history.replaceState({}, "", location.pathname + location.search);
    const googleFailure = (reason: string) =>
      setError(
        (
          {
            admin_password_required: tr(
              "برای ورود مدیر از ورود با رمز استفاده کنید.",
              "Administrators must use password sign-in.",
            ),
            admin_otp_required: tr(
              "برای ورود مدیر از ورود با رمز استفاده کنید.",
              "Administrators must use password sign-in.",
            ),
            account_deleted: tr(
              "حساب قبلی حذف شده است. برای ساخت حساب تازه، دوباره با گوگل ادامه دهید.",
              "The previous account was deleted. Continue with Google again to create a fresh account.",
            ),
            account_suspended: tr(
              "این حساب توسط مدیر تعلیق شده است. برای رفع تعلیق با مدیر تماس بگیرید.",
              "This account is suspended. Contact the administrator to resume access.",
            ),
            verify_email: tr(
              "برای این نشانی، از کد تأیید ایمیل استفاده کنید.",
              "Use an email verification code for this address.",
            ),
            account_conflict: tr(
              "این ایمیل به حساب گوگل دیگری متصل است. برای بررسی با مدیر تماس بگیرید.",
              "This email is linked to another Google identity. Contact the administrator.",
            ),
            invalid_state: tr(
              "درخواست ورود منقضی یا نامعتبر است. دوباره روی ادامه با گوگل بزنید.",
              "This sign-in request expired or is invalid. Choose Continue with Google again.",
            ),
          } as Record<string, string>
        )[reason] ||
          tr(
            "ورود گوگل کامل نشد. دوباره تلاش کنید یا از روش دیگری وارد شوید.",
            "Google sign-in did not complete. Try again or use another sign-in method.",
          ),
      );
    if (problem) {
      googleFailure(problem);
      setGoogleReturn(false);
      return;
    }
    if (!code || !flow) {
      googleFailure("invalid_response");
      setGoogleReturn(false);
      return;
    }
    api
      .googleExchange(flow, code)
      .then((result) => {
        api.setToken(result.token);
        setToken(result.token);
      })
      .catch(() => googleFailure("exchange_failed"))
      .finally(() => setGoogleReturn(false));
  }, []);
  useEffect(() => {
    saveLang(lang);
    document.documentElement.lang = lang;
    document.documentElement.dir = lang === "fa" ? "rtl" : "ltr";
    document.title = token
      ? screenInfo(tab, lang).title + " · Darvazeh"
      : lang === "fa"
        ? "دروازه | Darvazeh"
        : "Darvazeh";
  }, [lang, tab, token]);
  useEffect(() => {
    if (!token || googleReturn) return;
    let active = true;
    refresh(true)
      .then((me) => {
        if (active) {
          setForward(me.forwardEmailEnabled);
          setForwardEmail(me.forwardEmailAddress || me.email);
        }
      })
      .catch((e) => {
        if (e.status === 401) {
          api.setToken(null);
          setToken(null);
        } else fail(e);
      });
    const timer = setInterval(() => refresh().catch(() => {}), 8000);
    return () => {
      active = false;
      clearInterval(timer);
    };
  }, [token, googleReturn, refresh]);
  useEffect(() => {
    if (!countdown) return;
    const timer = setTimeout(() => setCountdown(countdown - 1), 1000);
    return () => clearTimeout(timer);
  }, [countdown]);
  async function action(fn: () => Promise<unknown>, success = "") {
    setBusy(true);
    setError("");
    setNotice("");
    try {
      await fn();
      if (success) setNotice(success);
    } catch (e) {
      fail(e);
    } finally {
      setBusy(false);
    }
  }
  async function requestCode(e?: FormEvent) {
    e?.preventDefault();
    await action(async () => {
      await api.requestOtp(email.trim());
      setOtpSent(true);
      setCountdown(45);
    });
  }
  async function verify(e: FormEvent) {
    e.preventDefault();
    await action(async () => {
      const r = await api.verifyOtp(
        email.trim(),
        digits(code),
        password || undefined,
      );
      api.setToken(r.token);
      setToken(r.token);
      setCode("");
      setPassword("");
    });
  }
  function logout() {
    api.setToken(null);
    setToken(null);
    setUser(null);
    setOtpSent(false);
    navigate("overview");
    setError("");
    setNotice("");
  }
  const switcher = (
    <div className="lang-switch" aria-label="Language">
      <button
        type="button"
        className={lang === "fa" ? "selected" : ""}
        onClick={() => setLang("fa")}
      >
        فارسی
      </button>
      <button
        type="button"
        className={lang === "en" ? "selected" : ""}
        onClick={() => setLang("en")}
      >
        English
      </button>
    </div>
  );
  const brand = (
    <a className="brand-lockup" href="/" aria-label="Darvazeh">
      <img src="/darvazeh-mark.svg" alt="" />
      <span>
        <strong>{tr("دروازه", "Darvazeh")}</strong>
        <small>{tr("DARVAZEH", "دروازه")}</small>
      </span>
    </a>
  );
  const alerts = (
    <>
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}
      {notice && (
        <div className="alert success" role="status">
          {notice}
        </div>
      )}
    </>
  );
  if (googleReturn)
    return (
      <main className="login-shell">
        <section className="login-card">
          <h1>{tr("در حال ورود با گوگل…", "Completing Google sign-in…")}</h1>
          <p role="status">
            {tr("چند لحظه صبر کنید.", "Please wait a moment.")}
          </p>
        </section>
      </main>
    );
  if (!token || !user)
    return (
      <main className="login-shell">
        <section className="story-panel">
          <div className="story-top">
            {brand}
            {switcher}
          </div>
          <div className="story-copy">
            <p className="eyebrow">
              {tr("همراه شما، از هر کجا", "YOUR MESSAGES, WHEREVER YOU ARE")}
            </p>
            <h1>
              {tr(
                "دروازه ای برای\nارتباطات شما",
                "A familiar doorway.\nA world within reach.",
              )}
            </h1>
            <p>
              {tr(
                "الهام‌گرفته از دروازه‌های کاروانسراهای صفوی؛ جایی برای رسیدن، پیوند و آسودگی.",
                "Inspired by the welcoming gateways of Safavid caravanserais. A place to arrive, connect, and feel at home.",
              )}
            </p>
          </div>
          <img
            className="caravanserai"
            src="/caravanserai-gateway.png"
            alt={tr(
              "تصویر معماری دروازه کاروانسرا، با آجرهای گرم و کاشی فیروزه‌ای",
              "Caravanserai gateway with warm brick and turquoise tile",
            )}
          />
          <span className="image-caption">
            {tr(
              "فاصله‌ها می‌گذرند؛ پیوندها می‌مانند.",
              "Distances change. Connections stay.",
            )}
          </span>
        </section>
        <section className="login-side">
          <div className="login-card">
            <span className="step-label">
              {tr("خوش آمدید", "WELCOME TO DARVAZEH")}
            </span>
            <h2>
              {token
                ? tr("در حال باز کردن صندوق…", "Opening your inbox…")
                : usePassword
                  ? tr("به دروازه خوش آمدید", "Welcome to Darvazeh")
                  : otpSent
                    ? tr("ایمیل‌تان را بررسی کنید", "Check your email")
                    : tr("از همین‌جا وارد شوید", "Your doorway starts here")}
            </h2>
            {!usePassword && (
              <p className="muted">
                {otpSent
                  ? tr(
                      "کد شش‌رقمی در صف ارسال به نشانی زیر است.",
                      "Your six-digit code is queued for the address below.",
                    )
                  : tr(
                      "ایمیل‌تان را وارد کنید تا کد ورود را برایتان بفرستیم.",
                      "Enter your email and we’ll send you a sign-in code.",
                    )}
              </p>
            )}
            {!token && usePassword ? (
              <PasswordAccess
                lang={lang}
                onSignedIn={(value) => {
                  api.setToken(value);
                  setToken(value);
                }}
                onUseOtp={() => {
                  setUsePassword(false);
                  setError("");
                }}
              />
            ) : (
              !token &&
              (!otpSent ? (
                <form onSubmit={requestCode}>
                  <label htmlFor="email">{tr("ایمیل", "Email address")}</label>
                  <input
                    id="email"
                    type="email"
                    autoComplete="email"
                    dir="ltr"
                    required
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    placeholder="you@example.com"
                  />
                  <button className="btn full" disabled={busy} type="submit">
                    {busy
                      ? tr("در حال ارسال…", "Sending…")
                      : tr("دریافت کد ورود", "Send sign-in code")}
                    <span aria-hidden="true">↗</span>
                  </button>
                </form>
              ) : (
                <form onSubmit={verify}>
                  <p className="email-address" dir="ltr">
                    {email}
                  </p>
                  <label htmlFor="code">{tr("کد ورود", "Sign-in code")}</label>
                  <input
                    className="otp-input"
                    id="code"
                    inputMode="numeric"
                    autoComplete="one-time-code"
                    pattern="[0-9]{6}"
                    maxLength={6}
                    required
                    value={code}
                    onChange={(e) =>
                      setCode(digits(e.target.value).replace(/\D/g, ""))
                    }
                    placeholder="— — — — — —"
                    dir="ltr"
                    autoFocus
                  />
                  {email.trim().toLowerCase() === "admin@ndorbit.co.nz" && (
                    <>
                      <label htmlFor="admin-password">
                        {tr("رمز مدیریت", "Administrator password")}
                      </label>
                      <input
                        id="admin-password"
                        type="password"
                        autoComplete="current-password"
                        required
                        value={password}
                        onChange={(e) => setPassword(e.target.value)}
                        dir="ltr"
                      />
                    </>
                  )}
                  <p className="hint">
                    {tr(
                      "کد تا ۱۰ دقیقه معتبر است.",
                      "Your code expires in 10 minutes.",
                    )}
                  </p>
                  <button
                    className="btn full"
                    type="submit"
                    disabled={busy || code.length !== 6}
                  >
                    {busy
                      ? tr("کمی صبر کنید…", "One moment…")
                      : tr("ورود به دروازه", "Enter Darvazeh")}
                  </button>
                  <div className="row between">
                    <button
                      className="text-button"
                      type="button"
                      onClick={() => {
                        setOtpSent(false);
                        setError("");
                      }}
                    >
                      {tr("تغییر ایمیل", "Change email")}
                    </button>
                    <button
                      className="text-button"
                      type="button"
                      disabled={busy || countdown > 0}
                      onClick={() => requestCode()}
                    >
                      {countdown > 0
                        ? `${tr("ارسال دوباره تا", "Resend in")} ${countdown}s`
                        : tr("ارسال دوباره کد", "Resend code")}
                    </button>
                  </div>
                </form>
              ))
            )}
            {!usePassword && (
              <button
                type="button"
                className="text-button"
                onClick={() => {
                  setUsePassword(true);
                  setOtpSent(false);
                  setError("");
                }}
              >
                {tr("ورود با نام کاربری و رمز", "Use username and password")}
              </button>
            )}
            {!token && <GoogleLogin lang={lang} />}
            {alerts}
            <div className="trial-note">
              <img src="/darvazeh-mark.svg" alt="" />
              <div>
                <strong>
                  {tr(
                    "سه روز مهمان دروازه باشید",
                    "Three days to feel at home",
                  )}
                </strong>
                <p>
                  {tr(
                    "۲۰ پیام برای ارسال و دریافت، با همه امکانات آغازین.",
                    "20 messages across sending and receiving. Both features included.",
                  )}
                </p>
              </div>
            </div>
            <p className="privacy-note">
              {tr(
                "کد ورودتان را فقط در دروازه وارد کنید.",
                "Only enter your sign-in code in Darvazeh.",
              )}
            </p>
          </div>
        </section>
      </main>
    );
  const sub = user.subscription;
  const devices = (user.devices || []).filter((d) => !d.revokedAt);
  const isAdmin = user.role === "Admin";
  const visiblePeople = users
    .filter((u) =>
      (u.email + " " + (u.username || ""))
        .toLowerCase()
        .includes(search.trim().toLowerCase()),
    )
    .filter(
      (u) =>
        peopleStatus === "all" ||
        (peopleStatus === "suspended"
          ? !!u.suspendedAt
          : peopleStatus === "unverified"
            ? !u.emailVerified && !u.suspendedAt
            : !u.suspendedAt && u.emailVerified),
    );
  const currentPeoplePage = Math.min(
    peoplePage,
    Math.max(1, Math.ceil(visiblePeople.length / 10)),
  );
  const activity = (s: string) =>
    ({
      signed_in: tr("ورود به حساب", "Signed in"),
      account_created_by_admin: tr(
        "ساخت کاربر توسط مدیر",
        "User created by administrator",
      ),
      account_modified: tr("ویرایش کاربر", "User updated"),
      account_suspended: tr("تعلیق حساب", "Account suspended"),
      account_resumed: tr("رفع تعلیق حساب", "Account resumed"),
      account_deleted: tr("حذف حساب", "Account deleted"),
      device_paused: tr("توقف اتصال گوشی", "Phone connection paused"),
      device_resumed: tr("ازسرگیری اتصال گوشی", "Phone connection resumed"),
      messages_deleted: tr("حذف تاریخچه پیام", "Message history deleted"),
      admin_login_policy_updated: tr(
        "تغییر امنیت ورود مدیر",
        "Administrator login policy updated",
      ),
      trial_started: tr("آغاز دوره آزمایشی", "Trial started"),
      subscription_assigned: tr("فعال‌سازی اشتراک", "Membership activated"),
      features_updated: tr("تغییر دسترسی‌ها", "Feature access changed"),
      plan_created: tr("ساخت طرح", "Plan created"),
      plan_updated: tr("ویرایش طرح و قیمت", "Plan and pricing updated"),
      payment_confirmed: tr("تأیید پرداخت", "Payment confirmed"),
      paid_membership_activated: tr(
        "شروع دوره خریداری‌شده",
        "Purchased period activated",
      ),
      payment_refunded: tr("بازپرداخت", "Payment refunded"),
      message_received: tr("دریافت پیام", "Message received"),
      message_queued: tr("ثبت پیام برای ارسال", "Message queued"),
      message_sent: tr("ارسال پیام", "Message sent"),
      message_failed: tr("ارسال ناموفق", "Message failed"),
    })[s] || tr("فعالیت حساب", "Account activity");
  return (
    <WorkspaceShell
      user={user}
      lang={lang}
      screen={tab}
      onNavigate={navigate}
      onLogout={logout}
      onLanguage={setLang}
    >
      {!user.emailVerified && (
        <VerifyEmailBanner
          lang={lang}
          onVerified={async () => {
            await refresh();
            setNotice(
              tr(
                "ایمیل تأیید و حساب فعال شد.",
                "Email verified and account activated.",
              ),
            );
          }}
        />
      )}
      {alerts}
      <Phones
        user={user}
        lang={lang}
        showDevices={tab === "device"}
        onChange={() => refresh()}
      />
      {tab === "overview" && (
        <Overview user={user} lang={lang} onNavigate={navigate} />
      )}
      {tab === "inbox" && <MessageHistory lang={lang} />}
      {tab === "send" && (
        <section className="card narrow">
          <h2>{tr("پیام تازه", "New message")}</h2>
          <p className="muted">
            {tr(
              "پیام از سیم‌کارت گوشی متصل شما فرستاده می‌شود. هزینه اپراتور جداگانه است.",
              "Sent using your connected phone’s SIM. Your mobile operator’s charges apply separately.",
            )}
          </p>
          {!sub.canSend ? (
            <div className="alert">
              {tr(
                "ارسال پیام در حال حاضر فعال نیست. اشتراک و ظرفیت باقی‌مانده را بررسی کنید.",
                "Sending is currently unavailable. Check your membership and remaining allowance.",
              )}
              <div>
                <button
                  className="text-button"
                  onClick={() =>
                    navigate(user.emailVerified ? "membership" : "overview")
                  }
                >
                  {tr(
                    user.emailVerified
                      ? "مشاهده اشتراک و ظرفیت"
                      : "فعال‌سازی حساب",
                    user.emailVerified
                      ? "Review plan & allowance"
                      : "Activate your account",
                  )}
                </button>
              </div>
            </div>
          ) : (
            <form
              onSubmit={(e) => {
                e.preventDefault();
                action(
                  async () => {
                    await api.sendSms(to, body, crypto.randomUUID());
                    setBody("");
                    await refresh();
                    navigate("inbox");
                  },
                  tr("پیام برای ارسال ثبت شد.", "Your message is queued."),
                );
              }}
            >
              <label htmlFor="to">
                {tr("شماره گیرنده", "Recipient number")}
              </label>
              <input
                id="to"
                type="tel"
                required
                maxLength={40}
                value={to}
                onChange={(e) => setTo(e.target.value)}
                placeholder="+98912…"
                dir="ltr"
              />
              <label htmlFor="body">{tr("پیام شما", "Your message")}</label>
              <textarea
                id="body"
                required
                maxLength={4000}
                value={body}
                onChange={(e) => setBody(e.target.value)}
                dir="auto"
              />
              <div className="row between">
                <span className="muted small">{body.length} / 4000</span>
                <button
                  className="btn"
                  disabled={
                    busy ||
                    !devices.filter((d) => !d.adminPausedAt).length ||
                    !to.trim() ||
                    !body.trim()
                  }
                >
                  {tr("ارسال پیام", "Send message")}
                </button>
              </div>
              {!devices.length && (
                <p className="hint">
                  {tr(
                    "ابتدا گوشی‌تان را از بخش «گوشی من» وصل کنید.",
                    "Connect a phone in Phones & connection first.",
                  )}
                  <button
                    type="button"
                    className="text-button"
                    onClick={() => navigate("device")}
                  >
                    {tr("اتصال گوشی", "Connect a phone")}
                  </button>
                </p>
              )}
            </form>
          )}
        </section>
      )}
      {tab === "settings" && (
        <div className="settings-grid">
          {tab === "settings" && (
            <section className="card narrow">
              <h2>{tr("ارسال نسخه ایمیلی", "Email forwarding")}</h2>
              <p className="muted">
                {tr(
                  "از پیام‌های دریافتی، یک نسخه در ایمیل‌تان داشته باشید.",
                  "Keep an email copy of incoming messages.",
                )}
              </p>
              <p className="hint">
                {tr(
                  "ظرفیت ارسال ایمیل در این دوره",
                  "Email forwards this period",
                )}
                : {sub.usedEmailForwards} / {sub.emailForwardLimit}.{" "}
                {tr(
                  "با پایان ظرفیت ایمیل، پیام‌ها همچنان در صندوق وب ثبت می‌شوند.",
                  "When email allowance runs out, messages still arrive in your web inbox.",
                )}
              </p>
              <form
                onSubmit={(e) => {
                  e.preventDefault();
                  action(
                    () => api.updateSettings(forward, forwardEmail),
                    tr("تنظیمات ذخیره شد.", "Preferences saved."),
                  );
                }}
              >
                <label className="check">
                  <input
                    type="checkbox"
                    checked={forward}
                    onChange={(e) => setForward(e.target.checked)}
                  />
                  {tr(
                    "ارسال نسخه ایمیلی پیام‌ها",
                    "Email me a copy of incoming messages",
                  )}
                </label>
                <label htmlFor="forwardEmail">
                  {tr("نشانی ایمیل", "Email address")}
                </label>
                <input
                  id="forwardEmail"
                  type="email"
                  required
                  value={forwardEmail}
                  onChange={(e) => setForwardEmail(e.target.value)}
                  dir="ltr"
                />
                <button className="btn" disabled={busy}>
                  {tr("ذخیره تغییرات", "Save changes")}
                </button>
              </form>
            </section>
          )}
          {tab === "settings" && (
            <ProfileSecurity
              user={user}
              lang={lang}
              onSignedIn={(value) => {
                api.setToken(value);
                setToken(value);
                refresh().catch(fail);
                setNotice(tr("رمز ذخیره شد.", "Password saved."));
              }}
            />
          )}
        </div>
      )}
      {tab === "membership" && (
        <>
          <section className="card membership-summary">
            <div className="section-heading">
              <div>
                <p className="eyebrow">
                  {tr("اشتراک فعلی", "CURRENT MEMBERSHIP")}
                </p>
                <h2>{planName(sub)}</h2>
              </div>
              <span className={"badge " + (sub.active ? "green" : "")}>
                {!user.emailVerified
                  ? tr("در انتظار تأیید", "Awaiting verification")
                  : sub.active
                    ? tr("فعال", "Active")
                    : tr("غیرفعال", "Inactive")}
              </span>
            </div>
            {!user.emailVerified ? (
              <p className="hint">
                {tr(
                  "دوره آزمایشی پس از تأیید ایمیل شروع می‌شود.",
                  "Your trial starts after you verify your email.",
                )}
              </p>
            ) : sub.messageLimit === 0 ? (
              <p className="hint">
                {tr(
                  "این نشانی قبلاً از دوره آزمایشی استفاده کرده؛ برای ادامه یک طرح انتخاب کنید.",
                  "This email identity has already used its trial. Choose a plan to continue.",
                )}
              </p>
            ) : (
              <div className="membership-allowances">
                <div>
                  <div className="row between">
                    <strong>
                      {tr("ارسال و دریافت پیام", "Sending & receiving")}
                    </strong>
                    <span>
                      {sub.usedMessages} / {sub.messageLimit}
                    </span>
                  </div>
                  <progress
                    aria-label={tr("مصرف پیام‌ها", "Messages used")}
                    value={Math.min(sub.usedMessages, sub.messageLimit)}
                    max={Math.max(1, sub.messageLimit)}
                  />
                  <p className="hint">
                    {sub.remainingMessages}{" "}
                    {tr("پیام باقی‌مانده", "messages remaining")}
                  </p>
                </div>
                <div>
                  <div className="row between">
                    <strong>
                      {tr("نسخه ایمیلی پیام‌ها", "Email forwarding")}
                    </strong>
                    <span>
                      {sub.usedEmailForwards} / {sub.emailForwardLimit}
                    </span>
                  </div>
                  <progress
                    aria-label={tr("مصرف ارسال ایمیل", "Email forwards used")}
                    value={Math.min(
                      sub.usedEmailForwards,
                      sub.emailForwardLimit,
                    )}
                    max={Math.max(1, sub.emailForwardLimit)}
                  />
                  <p className="hint">
                    {sub.remainingEmailForwards}{" "}
                    {tr("ارسال ایمیل باقی‌مانده", "email forwards remaining")}
                  </p>
                </div>
              </div>
            )}
            <p className="hint">
              {tr("اعتبار دوره تا", "Period valid until")} {date(sub.expiresAt)}
            </p>
          </section>
          {user.emailVerified && (
            <section className="card">
              <BillingPanel lang={lang} onMembershipChanged={() => refresh()} />
            </section>
          )}
          <details className="card allowance-explainer">
            <summary>
              {tr(
                "ظرفیت پیام و دوره‌های خرید چگونه کار می‌کنند؟",
                "How do allowances and purchased periods work?",
              )}
            </summary>
            <p className="hint">
              {tr(
                "هر پیام دریافتی یا ثبت‌شده برای ارسال، یک پیام از ظرفیت اشتراک کم می‌کند؛ ارسال ناموفق هم در این ظرفیت حساب می‌شود. حذف تاریخچه، مصرف اشتراک را کم نمی‌کند.",
                "Each incoming or queued outgoing message uses one unit, including failed sends. Deleting history does not restore allowance.",
              )}
            </p>
            <p className="hint">
              {tr(
                "خریدها برای یک دوره‌اند و تمدید خودکار ندارند. نسخه ایمیلی پیام‌ها ظرفیت جداگانه دارد؛ با پایان آن، پیام‌ها همچنان در صندوق وب ثبت می‌شوند.",
                "Each purchase covers one period with no automatic renewal. Email forwarding has a separate allowance; messages still arrive in your web inbox when it runs out.",
              )}
            </p>
          </details>
        </>
      )}
      {isAdmin && tab === "admin" && (
        <AdminOverview
          lang={lang}
          stats={stats}
          users={users}
          activities={activities}
          onNavigate={navigate}
          onPeople={(status) => {
            setPeopleStatus(status);
            setPeoplePage(1);
            setSearch("");
            navigate("admin-users");
          }}
          activityLabel={activity}
        />
      )}
      {isAdmin && tab === "admin-users" && (
        <>
          <AdminCreateUser lang={lang} onChange={adminRefresh} />
          <section className="card">
            <div className="section-heading">
              <h2>{tr("کاربران و دسترسی‌ها", "People & access")}</h2>
              <button
                className="btn secondary"
                disabled={busy}
                onClick={() => action(adminRefresh)}
              >
                {tr("به‌روزرسانی", "Refresh")}
              </button>
            </div>
            <label htmlFor="userSearch">
              {tr("جست‌وجوی ایمیل", "Find by email")}
            </label>
            <input
              id="userSearch"
              type="search"
              value={search}
              onChange={(e) => {
                setSearch(e.target.value);
                setPeoplePage(1);
              }}
              dir="ltr"
            />
            <div className="people-toolbar">
              <span className="hint">
                {visiblePeople.length}{" "}
                {tr(
                  "حساب",
                  visiblePeople.length === 1 ? "account" : "accounts",
                )}
              </span>
              <label className="sr-only" htmlFor="people-status">
                {tr("وضعیت حساب", "Account status")}
              </label>
              <select
                id="people-status"
                value={peopleStatus}
                onChange={(e) => {
                  setPeopleStatus(e.target.value);
                  setPeoplePage(1);
                }}
              >
                <option value="all">{tr("همه حساب‌ها", "All accounts")}</option>
                <option value="active">{tr("فعال", "Active")}</option>
                <option value="unverified">
                  {tr("در انتظار تأیید", "Unverified")}
                </option>
                <option value="suspended">
                  {tr("تعلیق‌شده", "Suspended")}
                </option>
              </select>
            </div>
            {!visiblePeople.length && (
              <div className="empty-state compact">
                <h3>{tr("حسابی پیدا نشد", "No matching accounts")}</h3>
                <p>
                  {tr(
                    "جست‌وجو یا فیلتر وضعیت را تغییر دهید.",
                    "Try a different search or account status.",
                  )}
                </p>
                <button
                  className="text-button"
                  onClick={() => {
                    setSearch("");
                    setPeopleStatus("all");
                  }}
                >
                  {tr("پاک کردن فیلترها", "Clear filters")}
                </button>
              </div>
            )}
            {visiblePeople
              .slice((currentPeoplePage - 1) * 10, currentPeoplePage * 10)
              .map((u) => (
                <AdminUserCard
                  key={u.id}
                  user={u}
                  plans={plans}
                  lang={lang}
                  busy={busy}
                  update={(fn) =>
                    action(async () => {
                      await fn();
                      await adminRefresh();
                      await refresh();
                    })
                  }
                />
              ))}
            <nav
              className="row between pager"
              aria-label={tr("صفحه‌بندی کاربران", "People pagination")}
            >
              <button
                className="text-button"
                disabled={currentPeoplePage <= 1}
                onClick={() => setPeoplePage((p) => p - 1)}
              >
                {tr("پیشین", "Previous")}
              </button>
              <span>
                {currentPeoplePage} /{" "}
                {Math.max(1, Math.ceil(visiblePeople.length / 10))}
              </span>
              <button
                className="text-button"
                disabled={currentPeoplePage * 10 >= visiblePeople.length}
                onClick={() => setPeoplePage((p) => p + 1)}
              >
                {tr("بعدی", "Next")}
              </button>
            </nav>
          </section>
        </>
      )}
      {isAdmin && tab === "admin-plans" && (
        <>
          <section className="card">
            <h2>{tr("طرح‌های اشتراک", "Membership plans")}</h2>
            <PlanManager
              plans={plans}
              lang={lang}
              busy={busy}
              save={(id, p) =>
                action(
                  async () => {
                    if (id) await api.updatePlan(id, p);
                    else await api.createPlan(p);
                    await adminRefresh();
                  },
                  tr("طرح و قیمت‌ها ذخیره شد.", "Plan and prices saved."),
                )
              }
            />
          </section>
        </>
      )}
      {isAdmin && tab === "admin-payments" && <AdminPayments lang={lang} />}
      {isAdmin && tab === "admin-wallet" && <PaymentWalletAdmin lang={lang} />}
      {isAdmin && tab === "admin-security" && (
        <AdminSecurity lang={lang} email={user.email} />
      )}
      {isAdmin && tab === "admin-delivery" && <EmailDiagnostics lang={lang} />}
      {isAdmin && tab === "admin-activity" && (
        <>
          <section className="card">
            <h2>{tr("فعالیت‌های تازه", "Recent activity")}</h2>
            <p className="hint">
              {tr(
                "رویدادهای حساب، تغییر اشتراک و وضعیت پیام‌ها ثبت می‌شوند؛ متن خصوصی پیام‌ها در این بخش نمایش داده نمی‌شود.",
                "Account events, plan changes and message outcomes are recorded. Private message content is not shown here.",
              )}
            </p>
            {activities.map((a) => (
              <div className="activity-row" key={a.id}>
                <div>
                  <strong>{activity(a.action)}</strong>
                  <small dir="ltr">{a.email}</small>
                </div>
                <time className="muted small">{date(a.createdAt)}</time>
              </div>
            ))}
          </section>
        </>
      )}
    </WorkspaceShell>
  );
}
function AdminUserCard({
  user: u,
  plans,
  lang,
  busy,
  update,
}: {
  user: AdminUser;
  plans: Plan[];
  lang: Lang;
  busy: boolean;
  update: (fn: () => Promise<unknown>) => void;
}) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en);
  const [selected, setSelected] = useState("");
  const s = u.subscription;
  return (
    <article className="admin-user">
      <div className="row between">
        <div>
          <strong dir="ltr" className="account-email">
            {u.email}
          </strong>
          <p className="small muted">
            {s.planCode === "trial" ? tr("آزمایشی", "Trial") : s.planName} ·{" "}
            {s.usedMessages}/{s.messageLimit} {tr("پیام", "messages")} ·{" "}
            {s.active ? tr("فعال", "Active") : tr("پایان‌یافته", "Expired")}
          </p>
        </div>
        <span className="badge">
          {u.role === "Admin"
            ? tr("مدیر", "Administrator")
            : tr("کاربر", "Member")}
        </span>
      </div>
      <div className="user-counters">
        <span>
          {tr("دریافت", "Received")}: {u.received}
        </span>
        <span>
          {tr("ارسال", "Sent")}: {u.sent}
        </span>
        <span>
          {tr("در انتظار", "Queued")}: {u.queued}
        </span>
        <span>
          {tr("ناموفق", "Failed")}: {u.failed}
        </span>
        <span>
          {tr("گوشی", "Phones")}: {u.deviceCount}
        </span>
      </div>
      <details className="account-details">
        <summary>
          {tr("مدیریت حساب و دسترسی‌ها", "Manage account & access")}
        </summary>
        <AdminAccountControls
          user={u}
          lang={lang}
          busy={busy}
          update={update}
        />
        <div className="row">
          <label className="check">
            <input
              type="checkbox"
              checked={s.sendEnabled}
              disabled={busy}
              onChange={(e) =>
                update(() =>
                  api.setFeatures(u.id, e.target.checked, s.receiveEnabled),
                )
              }
            />
            {tr("اجازه ارسال", "Allow sending")}
          </label>
          <label className="check">
            <input
              type="checkbox"
              checked={s.receiveEnabled}
              disabled={busy}
              onChange={(e) =>
                update(() =>
                  api.setFeatures(u.id, s.sendEnabled, e.target.checked),
                )
              }
            />
            {tr("اجازه دریافت", "Allow receiving")}
          </label>
        </div>
        <button
          className="text-button"
          disabled={busy}
          onClick={() => update(() => api.setFeatures(u.id, null, null))}
        >
          {tr("استفاده از دسترسی‌های طرح", "Use plan permissions")}
        </button>
        <p className="hint">
          {tr("اعتبار تا", "Valid until")}{" "}
          {utcDate(s.expiresAt).toLocaleString(
            lang === "fa" ? "fa-IR" : "en-NZ",
          )}
        </p>
        <div className="row">
          <select
            aria-label={
              tr("انتخاب طرح برای", "Choose plan for") + " " + u.email
            }
            value={selected}
            onChange={(e) => setSelected(e.target.value)}
          >
            <option value="">{tr("انتخاب طرح…", "Choose a plan…")}</option>
            {plans.map((p) => (
              <option key={p.id} value={p.id}>
                {p.code === "trial" ? tr("آزمایشی", "Trial") : p.name} ·{" "}
                {p.durationDays} {tr("روز", "days")} / {p.messageLimit}
              </option>
            ))}
          </select>
          <button
            className="btn secondary"
            disabled={busy || !selected}
            onClick={() => {
              if (
                window.confirm(
                  tr(
                    "اشتراک جدید از اکنون آغاز و ظرفیت پیام‌ها از نو محاسبه شود؟",
                    "Start a new membership now with a fresh message allowance?",
                  ),
                )
              )
                update(() => api.assignPlan(u.id, selected));
            }}
          >
            {tr("فعال‌سازی / تمدید", "Activate / renew")}
          </button>
        </div>
        {u.lastLoginAt && (
          <p className="hint">
            {tr("آخرین ورود", "Last sign-in")}:{" "}
            {utcDate(u.lastLoginAt).toLocaleString(
              lang === "fa" ? "fa-IR" : "en-NZ",
            )}
          </p>
        )}
      </details>
    </article>
  );
}
