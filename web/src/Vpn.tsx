import { useEffect, useRef, useState, type FormEvent } from "react";
import QRCode from "qrcode";
import { api, vpnApi, RequestError, type VpnAccount, type VpnAdminOverview, type VpnAdminRow, type VpnHome, type VpnServerStatus } from "./api";
import { utcDate } from "./format";
import type { Lang } from "./i18n";
import { Icon } from "./WorkspaceShell";

const PENDING_TOMAN = "darvazeh.pendingVpnTomanOrder";
const MB = 1024 * 1024;
const GB = 1024 * MB;

export function bytes(value: number, lang: Lang) {
  const units = lang === "fa" ? ["بایت", "کیلوبایت", "مگابایت", "گیگابایت", "ترابایت"] : ["B", "KB", "MB", "GB", "TB"];
  let v = Math.max(0, value), i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return new Intl.NumberFormat(lang === "fa" ? "fa-IR" : "en", { maximumFractionDigits: v < 10 && i > 0 ? 2 : v < 100 && i > 0 ? 1 : 0 }).format(v) + " " + units[i];
}
const usd = (cents: number, lang: Lang) =>
  new Intl.NumberFormat(lang === "fa" ? "fa-IR" : "en-US", { style: "currency", currency: "USD", currencyDisplay: "narrowSymbol" }).format(cents / 100);
const toman = (v: number, lang: Lang) => new Intl.NumberFormat(lang === "fa" ? "fa-IR" : "en-US").format(v) + (lang === "fa" ? " تومان" : " toman");
const when = (v: string | null | undefined, lang: Lang) => (v ? utcDate(v).toLocaleString(lang === "fa" ? "fa-IR" : "en-NZ", { dateStyle: "medium", timeStyle: "short" }) : "—");
const day = (v: string | null | undefined, lang: Lang) => (v ? utcDate(v).toLocaleDateString(lang === "fa" ? "fa-IR" : "en-NZ", { dateStyle: "medium" }) : "—");

export function vpnStatusLabel(status: VpnAccount["status"], lang: Lang): [string, string] {
  const t: Record<string, [string, string, string]> = {
    trial: ["دوره آزمایشی", "Free trial", "green"],
    trial_used: ["حجم آزمایشی تمام شد", "Trial data used", "gold"],
    trial_ended: ["دوره آزمایشی تمام شد", "Trial ended", "gold"],
    awaiting_payment: ["در انتظار پرداخت", "Awaiting payment", "gold"],
    active: ["فعال", "Active", "green"],
    depleted: ["حجم ماه تمام شد", "Monthly data used", "gold"],
    expired: ["منقضی", "Expired", ""],
    suspended: ["متوقف", "Suspended", ""],
    revoked: ["لغوشده", "Revoked", ""],
  };
  const x = t[status] || [status, status, ""];
  return [x[lang === "fa" ? 0 : 1], x[2]];
}

function vpnError(error: unknown, lang: Lang) {
  const e = error as RequestError;
  const m: Record<string, [string, string]> = {
    vpn_closed: ["فروش VPN در حال حاضر بسته است.", "VPN sign-ups are currently closed."],
    vpn_server_unavailable: ["این سرور اکنون در دسترس نیست.", "That server isn’t available right now."],
    email_verification_required: ["ابتدا ایمیل خود را تأیید کنید.", "Verify your email first."],
    vpn_admin_suspended: ["دسترسی شما توسط مدیر متوقف شده است.", "Your access was suspended by an administrator."],
    vpn_rotate_limit: ["امروز چند بار پیوند را عوض کرده‌اید. فردا دوباره تلاش کنید.", "You’ve replaced your link several times today. Try again tomorrow."],
    vpn_request_required: ["ابتدا پیکربندی VPN خود را دریافت کنید.", "Get your VPN configuration first."],
    payment_not_configured: ["پرداخت دلاری هنوز فعال نشده است.", "USD checkout isn’t connected yet."],
    payment_method_unavailable: ["پرداخت تومانی پس از اتصال کیف پول فعال می‌شود.", "Toman checkout will be available once the wallet is connected."],
    toman_requires_iran_ip: ["آی‌پی شما داخل ایران نیست، بنابراین پرداخت تومانی کار نمی‌کند. روش پرداخت دیگری را امتحان کنید یا فیلترشکن را خاموش کنید.", "Your IP address is not in Iran, so toman payment won’t work. Please try another payment method or turn off your VPN."],
    checkout_rate_limit: ["چند درخواست پرداخت باز دارید. کمی بعد دوباره تلاش کنید.", "You have several open checkouts. Please try again later."],
    connection: ["اتصال برقرار نشد. اینترنت را بررسی کنید.", "Couldn’t connect. Please check your connection."],
  };
  return (m[e.code || ""] || [e.message || "درخواست انجام نشد.", e.message || "The request couldn’t be completed."])[lang === "fa" ? 0 : 1];
}

function Qr({ value, label }: { value: string; label: string }) {
  const [src, setSrc] = useState("");
  useEffect(() => {
    let live = true;
    QRCode.toDataURL(value, { margin: 1, width: 240, errorCorrectionLevel: "M" }).then((url) => live && setSrc(url)).catch(() => setSrc(""));
    return () => { live = false; };
  }, [value]);
  return src ? <img className="vpn-qr" src={src} width={240} height={240} alt={label} /> : <div className="vpn-qr" aria-busy="true" />;
}

function expiryOf(a: VpnAccount) { return a.paidUntil ?? a.periodEnd ?? null; }
function Expiry({ account, lang }: { account: VpnAccount; lang: Lang }) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en);
  const end = expiryOf(account);
  const past = !!end && utcDate(end).getTime() <= Date.now();
  return (
    <p className="vpn-expiry">
      <span>{past ? tr("منقضی شده در", "Expired on") : tr("تاریخ انقضای پیکربندی", "Configuration expires")}</span>
      <strong className={past ? "warn-text" : ""}>{end ? when(end, lang) : tr("بدون تاریخ انقضا (تا پایان حجم)", "No expiry date (until data runs out)")}</strong>
    </p>
  );
}

function Validity({ account, lang }: { account: VpnAccount; lang: Lang }) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en);
  const startRaw = account.periodStart ?? account.createdAt;
  const endRaw = account.paidUntil ?? account.periodEnd;
  if (!endRaw) return null;
  const start = utcDate(startRaw).getTime(), end = utcDate(endRaw).getTime(), now = Date.now();
  const total = Math.max(1, end - start), left = Math.max(0, end - now);
  const f = (v: number) => new Intl.NumberFormat(lang === "fa" ? "fa-IR" : "en").format(v);
  const days = Math.floor(left / 86400000), hours = Math.floor((left % 86400000) / 3600000), minutes = Math.floor((left % 3600000) / 60000);
  const remaining = left <= 0 ? tr("پایان یافته", "Ended")
    : days > 0 ? `${f(days)} ${tr("روز", days === 1 ? "day" : "days")}${hours ? ` ${tr("و", "")} ${f(hours)} ${tr("ساعت", "h")}` : ""}`
    : hours > 0 ? `${f(hours)} ${tr("ساعت", "h")} ${f(minutes)} ${tr("دقیقه", "min")}` : `${f(minutes)} ${tr("دقیقه", "min")}`;
  const low = left < Math.min(3 * 86400000, total * 0.2);
  return (
    <div className="vpn-usage">
      <div className="row between">
        <strong>{tr("مدت اعتبار", "Validity")}</strong>
        <span className={low ? "warn-text" : ""}>{tr("باقی‌مانده: ", "Remaining: ")}{remaining}</span>
      </div>
      <progress className={"vpn-validity" + (low ? " low" : "")} aria-label={tr("زمان باقی‌مانده", "Time remaining")} value={left} max={total} />
      <p className="hint">{day(startRaw, lang)}{lang === "fa" ? " ← " : " → "}{day(endRaw, lang)}</p>
    </div>
  );
}

function Usage({ account, lang }: { account: VpnAccount; lang: Lang }) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en);
  const limit = Math.max(1, account.limitBytes);
  return (
    <div className="vpn-usage">
      <div className="row between">
        <strong>{tr("مصرف این دوره", "Data used this period")}</strong>
        <span>{bytes(account.usedTotal, lang)} / {bytes(account.limitBytes, lang)}</span>
      </div>
      <progress aria-label={tr("مصرف داده", "Data used")} value={Math.min(account.usedTotal, limit)} max={limit} />
      <div className="vpn-stats">
        <div><small>{tr("دانلود", "Download")}</small><strong>↓ {bytes(account.usedDown, lang)}</strong></div>
        <div><small>{tr("آپلود", "Upload")}</small><strong>↑ {bytes(account.usedUp, lang)}</strong></div>
        <div><small>{tr("باقی‌مانده", "Remaining")}</small><strong>{bytes(account.remainingBytes, lang)}</strong></div>
        <div>
          <small>{tr("کاربران فعال", "Active users")}</small>
          <strong className={account.activeIps > account.ipLimit ? "warn-text" : ""}>
            {new Intl.NumberFormat(lang === "fa" ? "fa-IR" : "en").format(account.activeIps)} / {new Intl.NumberFormat(lang === "fa" ? "fa-IR" : "en").format(account.ipLimit)}
          </strong>
        </div>
      </div>
      <p className="hint">
        <span className={"vpn-dot " + (account.online ? "on" : "")} aria-hidden="true" />
        {account.online ? tr("اکنون متصل است", "Connected now") : account.lastOnlineAt ? tr("آخرین اتصال: ", "Last connected: ") + when(account.lastOnlineAt, lang) : tr("هنوز متصل نشده", "Not connected yet")}
        {" · "}
        {tr("مجموع کل: ", "All-time: ")}
        <span>↓ {bytes(account.lifetimeDown, lang)} · ↑ {bytes(account.lifetimeUp, lang)}</span>
      </p>
      {account.activeIps > account.ipLimit && (
        <p className="alert error" role="alert">
          {tr("این طرح فقط برای یک کاربر است. پیکربندی را روی دستگاه‌های دیگر حذف کنید یا پیوند را عوض کنید.",
            "This plan is for one user only. Remove the configuration from other devices, or replace your link.")}
        </p>
      )}
    </div>
  );
}

export function VpnPanel({ lang, emailVerified }: { lang: Lang; emailVerified: boolean }) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en);
  const [home, setHome] = useState<VpnHome | null>(null);
  const [server, setServer] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [copied, setCopied] = useState(false);
  const [tomanOrder, setTomanOrder] = useState<{ id: string; url: string | null; alternate?: string | null } | null>(null);
  const requestKey = useRef<{ currency: string; key: string } | null>(null);
  // Ezpay works only from Iranian IP addresses; the server checks again before creating a link.
  const tomanRegion = useRef<boolean | null>(null);
  useEffect(() => {
    api.paymentRegion().then((r) => { tomanRegion.current = r.tomanAllowed; }).catch(() => undefined);
  }, []);

  async function load(refresh = false) {
    const h = await vpnApi.home(refresh);
    setHome(h);
    setServer((s) => s || h.servers[0]?.code || "");
    return h;
  }
  async function confirmOrder(id: string, attempt = 0): Promise<void> {
    const r = await api.reconcilePayment(id);
    if (r.status.startsWith("paid_")) {
      localStorage.removeItem(PENDING_TOMAN);
      setTomanOrder(null);
      requestKey.current = null;
      setNotice(tr("پرداخت تأیید شد؛ VPN شما فعال شد. نیازی به دریافت پیکربندی تازه نیست.", "Payment confirmed — your VPN is active. You don’t need a new configuration."));
      await load(true);
    } else if (r.status === "checkout_open" && attempt < 10 && !localStorage.getItem(PENDING_TOMAN)) {
      setNotice(tr("در حال بررسی تأیید پرداخت…", "Checking payment confirmation…"));
      await new Promise((res) => setTimeout(res, 5000));
      return confirmOrder(id, attempt + 1);
    } else if (r.status !== "checkout_open") {
      localStorage.removeItem(PENDING_TOMAN);
      setTomanOrder(null);
    }
  }
  useEffect(() => {
    const params = new URLSearchParams(location.search);
    const order = params.get("order");
    const pending = localStorage.getItem(PENDING_TOMAN);
    (async () => {
      try {
        await load(true);
        if (order && params.get("payment") === "success") { history.replaceState({}, "", location.pathname + "?view=vpn"); await confirmOrder(order); }
        else if (order && params.get("payment") === "cancelled") {
          history.replaceState({}, "", location.pathname + "?view=vpn");
          setNotice(tr("پرداخت کامل نشد. هر زمان خواستید دوباره تلاش کنید.", "Checkout wasn’t completed. You can try again any time."));
        } else if (pending) { setTomanOrder({ id: pending, url: null }); await confirmOrder(pending); }
      } catch (e) { setError(vpnError(e, lang)); }
    })();
  }, []);
  useEffect(() => {
    if (!home?.account) return;
    const timer = setInterval(() => { if (!document.hidden) load().catch(() => undefined); }, 60000);
    return () => clearInterval(timer);
  }, [home?.account?.id]);

  async function act(task: () => Promise<unknown>, done?: string) {
    setBusy(true); setError(""); setNotice("");
    try { await task(); await load(); if (done) setNotice(done); }
    catch (e) { setError(vpnError(e, lang)); }
    finally { setBusy(false); }
  }

  async function pay(currency: "USD" | "IRT") {
    if (!home?.plan) return;
    if (!requestKey.current || requestKey.current.currency !== currency) requestKey.current = { currency, key: crypto.randomUUID() };
    let win: Window | null = null;
    if (currency === "IRT" && tomanRegion.current === false) {
      setNotice("");
      setError(vpnError({ code: "toman_requires_iran_ip" }, lang));
      return;
    }
    if (currency === "IRT") {
      win = window.open("", "_blank");
      if (win) win.document.body.textContent = tr("در حال باز کردن صفحهٔ پرداخت… فیلترشکن را خاموش کنید.", "Opening the payment page… Turn off your VPN.");
    }
    setBusy(true); setError(""); setNotice("");
    try {
      const c = await api.checkout(home.plan.id, currency, requestKey.current.key);
      if (c.status.startsWith("paid_")) { win?.close(); requestKey.current = null; await load(true); setNotice(tr("این پرداخت قبلاً تأیید شده است.", "This payment was already confirmed.")); return; }
      if (!c.checkoutUrl) { win?.close(); throw new Error(); }
      const url = new URL(c.checkoutUrl);
      const allowed = currency === "USD" ? url.hostname === "checkout.stripe.com" : url.hostname === "ezpay.ir" || url.hostname.endsWith(".ezpay.ir");
      if (url.protocol !== "https:" || !allowed) { win?.close(); throw new Error(); }
      if (currency === "USD") { location.assign(url.href); return; }
      localStorage.setItem(PENDING_TOMAN, c.id);
      if (win && !win.closed) { win.opener = null; win.location.replace(url.href); }
      const alternate = c.alternateUrl && /^https:\/\/([a-z0-9-]+\.)*ezpay\.ir\//i.test(c.alternateUrl) ? c.alternateUrl : null;
      setTomanOrder({ id: c.id, url: url.href, alternate });
    } catch (e) { win?.close(); setError(vpnError(e, lang)); }
    finally { setBusy(false); }
  }

  async function copy(text: string) {
    try { await navigator.clipboard.writeText(text); setCopied(true); setTimeout(() => setCopied(false), 2000); }
    catch { setError(tr("کپی انجام نشد؛ پیوند را دستی انتخاب کنید.", "Couldn’t copy — select the link manually.")); }
  }

  if (!home) return <section className="card"><p className="muted">{error || tr("در حال بارگذاری…", "Loading…")}</p></section>;
  const a = home.account;
  const p = home.policy;
  const plan = home.plan;
  const canPay = !!a && a.status !== "suspended" && a.status !== "revoked" && !!plan?.isPurchasable;
  const [label, tone] = a ? vpnStatusLabel(a.status, lang) : ["", ""];
  const url = a?.subscriptionUrl || "";
  const priceLine = [plan?.priceUsdCents ? usd(plan.priceUsdCents, lang) : null, plan?.priceToman ? toman(plan.priceToman, lang) : null].filter(Boolean).join(tr(" یا ", " or "));

  return (
    <>
      {error && <p className="alert error" role="alert">{error}</p>}
      {notice && <p className="alert success" role="status">{notice}</p>}
      {!a ? (
        <section className="card vpn-intro">
          <p className="eyebrow">{tr("سرویس تازه", "NEW SERVICE")}</p>
          <h2>{tr("فیلترشکن با آیپی ایران", "VPN with an Iran IP")}</h2>
          <ul className="vpn-features">
            <li>{tr("یک کاربر، با یک پیوند اشتراک برای v2rayNG، v2rayTun، NPV Tunnel، Hiddify و برنامه‌های مشابه", "One user, one subscription link for v2rayNG, v2rayTun, NPV Tunnel, Hiddify and similar apps")}</li>
            <li>{bytes(p.monthlyBytes, lang)} {tr("حجم در هر", "of data every")} {new Intl.NumberFormat(lang === "fa" ? "fa-IR" : "en").format(p.periodDays)} {tr("روز", "days")}{priceLine && " · " + priceLine}</li>
            <li>{home.trialAvailable ? (p.trialDays > 0 ? tr(`${bytes(p.trialBytes, lang)} آزمایش رایگان برای ${new Intl.NumberFormat("fa-IR").format(p.trialDays)} روز، بدون پرداخت`, `${bytes(p.trialBytes, lang)} free trial for ${p.trialDays} ${p.trialDays === 1 ? "day" : "days"}, no payment needed`) : tr(`${bytes(p.trialBytes, lang)} آزمایش رایگان، بدون پرداخت`, `${bytes(p.trialBytes, lang)} free trial, no payment needed`)) : tr("دوره آزمایشی این ایمیل قبلاً استفاده شده؛ پس از دریافت، با پرداخت فعال می‌شود.", "This email has already used its trial; after you request it, pay to activate.")}</li>
            <li>{tr("تمدید ماهانه روی همان پیکربندی؛ نیازی به دریافت دوباره نیست", "Monthly renewal keeps the same configuration — nothing to re-import")}</li>
          </ul>
          {!emailVerified ? (
            <p className="hint">{tr("برای دریافت VPN ابتدا ایمیل خود را تأیید کنید.", "Verify your email to request a VPN.")}</p>
          ) : !p.enabled || home.servers.length === 0 ? (
            <p className="hint">{tr("دریافت VPN در حال حاضر فعال نیست. بعداً دوباره سر بزنید.", "VPN sign-ups aren’t open right now. Please check back later.")}</p>
          ) : (
            <div className="row">
              {home.servers.length > 1 && (
                <label>
                  {tr("سرور", "Server")}{" "}
                  <select value={server} onChange={(e) => setServer(e.target.value)}>
                    {home.servers.map((s) => <option key={s.code} value={s.code}>{lang === "fa" ? s.nameFa : s.name}</option>)}
                  </select>
                </label>
              )}
              <button className="btn" disabled={busy} onClick={() => act(() => vpnApi.request(server), tr("پیکربندی شما آماده است.", "Your configuration is ready."))}>
                {busy ? tr("در حال ساخت…", "Creating…") : home.trialAvailable ? tr("دریافت پیکربندی و آزمایش رایگان", "Get my configuration & free trial") : tr("دریافت پیکربندی", "Get my configuration")}
              </button>
            </div>
          )}
        </section>
      ) : (
        <>
          <section className="card">
            <div className="section-heading">
              <div>
                <p className="eyebrow">{tr("VPN شما", "YOUR VPN")} · {lang === "fa" ? a.serverNameFa : a.serverName}</p>
                <h2>{a.paidUntil ? tr("طرح ماهانه", "Monthly plan") : tr("دوره آزمایشی", "Trial")}</h2>
              </div>
              <span className={"badge " + tone}>{label}</span>
            </div>
            {a.pending && <p className="hint">{tr("تغییرات در حال اعمال روی سرور است…", "Applying changes on the server…")}</p>}
            {a.status === "suspended" && (
              <p className="alert">{a.suspendedBy === "admin" ? tr("دسترسی توسط مدیر متوقف شده است.", "Access was suspended by an administrator.") + (a.suspendReason ? " " + a.suspendReason : "") : tr("شما VPN را موقتاً متوقف کرده‌اید.", "You paused your VPN.")}</p>
            )}
            <Expiry account={a} lang={lang} />
            <Validity account={a} lang={lang} />
            <Usage account={a} lang={lang} />
            <div className="vpn-dates">
              {a.periodEnd && a.paidUntil && a.periodEnd !== a.paidUntil && <span>{tr("پایان دوره حجمی: ", "Data period ends: ")}<strong>{day(a.periodEnd, lang)}</strong></span>}
              <span>{tr("به‌روزرسانی: ", "Updated: ")}{when(a.syncedAt, lang)}</span>
            </div>
            <div className="row">
              <button className="btn secondary" disabled={busy} onClick={() => act(() => load(true))}>{tr("به‌روزرسانی مصرف", "Refresh usage")}</button>
              {a.status === "suspended" && a.suspendedBy === "user" ? (
                <button className="btn secondary" disabled={busy} onClick={() => act(() => vpnApi.suspend(false), tr("VPN دوباره فعال شد.", "VPN resumed."))}>{tr("ادامه", "Resume")}</button>
              ) : a.status !== "suspended" && (
                <button className="btn secondary" disabled={busy} onClick={() => confirm(tr("VPN موقتاً متوقف شود؟ زمان اشتراک متوقف نمی‌شود.", "Pause your VPN? Your paid time keeps running.")) && act(() => vpnApi.suspend(true), tr("VPN متوقف شد.", "VPN paused."))}>{tr("توقف موقت", "Pause")}</button>
              )}
            </div>
          </section>

          {url && (
            <section className="card vpn-connect">
              <div>
                <h2>{tr("اتصال در برنامه", "Connect in your app")}</h2>
                <p className="muted">{tr("این پیوند اشتراک همهٔ مسیرهای اتصال را دارد. آن را خصوصی نگه دارید؛ هر کس آن را داشته باشد از حجم شما استفاده می‌کند.", "This subscription link contains every connection route. Keep it private — anyone with it uses your data.")}</p>
                <div className="vpn-link">
                  <input readOnly dir="ltr" value={url} onFocus={(e) => e.currentTarget.select()} aria-label={tr("پیوند اشتراک", "Subscription link")} />
                  <button className="btn" onClick={() => copy(url)}>{copied ? tr("کپی شد", "Copied") : tr("کپی", "Copy")}</button>
                </div>
                <div className="vpn-apps">
                  <a className="btn secondary" href={"v2raytun://import/" + url}>v2rayTun</a>
                  <a className="btn secondary" href={"hiddify://import/" + url}>Hiddify</a>
                  <a className="btn secondary" href={"streisand://import/" + url}>Streisand</a>
                </div>
                <ol className="vpn-steps">
                  <li><strong>v2rayNG:</strong> {tr("پیوند را کپی کنید ← دکمهٔ + ← «وارد کردن از کلیپ‌بورد» یا «اسکن QR»، سپس منو ← «به‌روزرسانی اشتراک».", "copy the link → + button → “Import from clipboard” or “Scan QR code”, then menu → “Update subscription”.")}</li>
                  <li><strong>v2rayTun:</strong> {tr("دکمهٔ بالا یا + ← «افزودن از کلیپ‌بورد» / «اسکن QR».", "use the button above, or + → “Import from clipboard” / “Scan QR”.")}</li>
                  <li><strong>NPV Tunnel:</strong> {tr("+ ← «افزودن اشتراک» یا «وارد کردن از کلیپ‌بورد»، پیوند را بچسبانید.", "+ → “Add subscription” or “Import from clipboard”, then paste the link.")}</li>
                  <li>{tr("اگر یک مسیر وصل نشد، مسیر دیگر همان اشتراک را امتحان کنید.", "If one route doesn’t connect, try another route from the same subscription.")}</li>
                </ol>
                <button className="text-button" disabled={busy} onClick={() => confirm(tr("پیوند تازه ساخته شود؟ پیوند و پیکربندی‌های قبلی روی همهٔ دستگاه‌ها از کار می‌افتند.", "Replace your link? The old link and every imported profile on all devices stop working.")) && act(() => vpnApi.rotate(), tr("پیوند تازه ساخته شد. آن را دوباره در برنامه وارد کنید.", "New link created. Import it again in your app."))}>
                  {tr("ساخت پیوند تازه (لغو پیوند قبلی)", "Replace link (revoke the old one)")}
                </button>
              </div>
              <figure>
                <Qr value={url} label={tr("کد QR اشتراک", "Subscription QR code")} />
                <figcaption className="hint">{tr("با برنامهٔ VPN اسکن کنید", "Scan with your VPN app")}</figcaption>
              </figure>
            </section>
          )}

          <section className="card">
            <div className="section-heading">
              <h2>{a.paidUntil && a.status !== "expired" ? tr("تمدید", "Renew") : tr("فعال‌سازی ماهانه", "Activate monthly plan")}</h2>
              {priceLine && <strong>{priceLine}</strong>}
            </div>
            <p className="muted">
              {tr(`هر پرداخت ${new Intl.NumberFormat("fa-IR").format(plan?.durationDays ?? p.periodDays)} روز با ${bytes(p.monthlyBytes, lang)} حجم به همین پیکربندی اضافه می‌کند. تمدید خودکار نداریم.`,
                `Each payment adds ${plan?.durationDays ?? p.periodDays} days with ${bytes(p.monthlyBytes, lang)} of data to this same configuration. There is no automatic renewal.`)}
            </p>
            <p className="hint">{tr("اگر حجم ماه تمام شده باشد، پرداخت تازه بلافاصله دورهٔ جدید با حجم کامل را شروع می‌کند؛ در غیر این صورت به انتهای اعتبار فعلی اضافه می‌شود.", "If this month’s data is used up, a new payment starts a fresh period with full data immediately; otherwise it’s added after your current paid time.")}</p>
            {tomanOrder && (
              <div className="alert toman-waiting" role="status">
                <strong>{tr("در انتظار پرداخت تومانی", "Waiting for your toman payment")}</strong>
                <span>{tr("پرداخت را در صفحهٔ ایزی‌پی کامل کنید و برگردید. فیلترشکن باید خاموش باشد.", "Finish paying on the Ezpay page and come back. Keep your VPN off.")}</span>
                <div className="row">
                  {tomanOrder.url && <a className="btn secondary" href={tomanOrder.url} target="_blank" rel="noopener noreferrer">{tr("باز کردن صفحهٔ پرداخت", "Open payment page")}</a>}
                  {tomanOrder.alternate && <a className="btn secondary" href={tomanOrder.alternate} target="_blank" rel="noopener noreferrer">{tr("صفحه باز نشد؟ صفحهٔ دیگر ایزی‌پی", "Page didn’t work? Other Ezpay page")}</a>}
                  <button className="btn" disabled={busy} onClick={() => act(() => confirmOrder(tomanOrder.id))}>{tr("پرداخت کردم؛ بررسی کن", "I’ve paid — check now")}</button>
                </div>
              </div>
            )}
            <div className="row">
              <button className="btn" disabled={busy || !canPay || !home.stripeAvailable || !plan?.priceUsdCents} onClick={() => pay("USD")}>
                {tr("پرداخت دلاری با کارت", "Pay in USD by card")}{plan?.priceUsdCents ? " · " + usd(plan.priceUsdCents, lang) : ""}
              </button>
              <button className="btn secondary" disabled={busy || !canPay || !home.tomanAvailable || !plan?.priceToman} onClick={() => pay("IRT")}>
                {home.tomanAvailable && plan?.priceToman ? tr("پرداخت تومانی", "Pay in toman") + " · " + toman(plan.priceToman, lang) : tr("پرداخت تومانی — به‌زودی", "Toman checkout — coming later")}
              </button>
            </div>
            {!home.stripeAvailable && <p className="hint">{tr("پرداخت دلاری هنوز متصل نشده است.", "USD checkout isn’t connected yet.")}</p>}
            {home.orders.length > 0 && (
              <div className="payment-history">
                {home.orders.map((o) => (
                  <article className="payment-row" key={o.id}>
                    <div><strong>{o.planName}</strong><span className="muted small">{when(o.createdAt, lang)}</span></div>
                    <div><strong>{o.currency === "USD" ? usd(o.amountMinor, lang) : toman(o.amountMinor, lang)}</strong><span className="badge">{({ paid_active: tr("پرداخت‌شده", "Paid"), checkout_open: tr("در انتظار پرداخت", "Awaiting payment"), created: tr("آغاز پرداخت", "Started"), failed: tr("ناموفق", "Failed"), expired: tr("منقضی", "Expired"), refunded: tr("بازپرداخت", "Refunded"), paid_queued: tr("در حال فعال‌سازی", "Activating") } as Record<string, string>)[o.status] ?? o.status}</span></div>
                  </article>
                ))}
              </div>
            )}
          </section>
        </>
      )}
    </>
  );
}

export function VpnAdmin({ lang }: { lang: Lang }) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en);
  const n = (v: number) => new Intl.NumberFormat(lang === "fa" ? "fa-IR" : "en").format(v);
  const [overview, setOverview] = useState<VpnAdminOverview | null>(null);
  const [rows, setRows] = useState<VpnAdminRow[]>([]);
  const [servers, setServers] = useState<VpnServerStatus[] | null>(null);
  const [form, setForm] = useState({ enabled: true, trialMb: "10", trialDays: "0", monthlyGb: "5", periodDays: "30", ipLimit: "1", usd: "4", toman: "" });
  const [filter, setFilter] = useState("");
  const [showRevoked, setShowRevoked] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  async function load() {
    const [o, r] = await Promise.all([vpnApi.admin(), vpnApi.accounts(showRevoked)]);
    setOverview(o); setRows(r);
    return o;
  }
  useEffect(() => {
    load().then((o) => setForm({
      enabled: o.policy.enabled, trialMb: String(+(o.policy.trialBytes / MB).toFixed(2)), trialDays: String(o.policy.trialDays),
      monthlyGb: String(+(o.policy.monthlyBytes / GB).toFixed(3)), periodDays: String(o.policy.periodDays), ipLimit: String(o.policy.ipLimit),
      usd: o.plan?.priceUsdCents != null ? String(o.plan.priceUsdCents / 100) : "", toman: o.plan?.priceToman != null ? String(o.plan.priceToman) : "",
    })).catch((e) => setError(vpnError(e, lang)));
  }, []);
  useEffect(() => { vpnApi.accounts(showRevoked).then(setRows).catch(() => undefined); }, [showRevoked]);

  async function act(task: () => Promise<unknown>, done: string) {
    setBusy(true); setError(""); setNotice("");
    try { await task(); await load(); setNotice(done); }
    catch (e) { setError(vpnError(e, lang)); }
    finally { setBusy(false); }
  }
  const num = (v: string) => Number(v.replace(/[۰-۹]/g, (c) => String(c.charCodeAt(0) - 1776)).replace(/,/g, ""));
  function save(e: FormEvent) {
    e.preventDefault();
    const priceUsd = form.usd.trim() ? Math.round(num(form.usd) * 100) : null;
    const priceToman = form.toman.trim() ? Math.round(num(form.toman)) : null;
    void act(() => vpnApi.savePolicy({
      enabled: form.enabled, trialBytes: Math.round(num(form.trialMb) * MB), trialDays: num(form.trialDays),
      monthlyBytes: Math.round(num(form.monthlyGb) * GB), periodDays: num(form.periodDays), ipLimit: num(form.ipLimit),
      priceUsdCents: priceUsd, priceToman,
    }), tr("تنظیمات VPN ذخیره شد.", "VPN settings saved."));
  }

  const s = overview?.stats;
  const visible = rows.filter((r) => !filter || r.email.toLowerCase().includes(filter.toLowerCase()) || r.clientEmail.includes(filter));
  return (
    <>
      {error && <p className="alert error" role="alert">{error}</p>}
      {notice && <p className="alert success" role="status">{notice}</p>}
      <div className="overview-metrics admin-metrics">
        {[
          ["vpn", tr("مشترکان فعال", "Active subscribers"), s ? n(s.active) : "…", s ? `${n(s.trial)} ${tr("آزمایشی", "on trial")} · ${n(s.total)} ${tr("کل", "total")}` : ""],
          ["activity", tr("متصل اکنون", "Online now"), s ? n(s.online) : "…", s && s.overIpLimit ? `${n(s.overIpLimit)} ${tr("بیش از حد مجاز کاربر", "over user limit")}` : tr("کاربران متصل", "connected users")],
          ["inbox", tr("ترافیک کل", "Total traffic"), s ? bytes(s.trafficBytes, lang) : "…", `${s ? n(s.expired) : "…"} ${tr("منقضی/تمام‌شده", "expired / used up")} · ${s ? n(s.suspended) : "…"} ${tr("متوقف", "suspended")}`],
          ["payment", tr("درآمد VPN", "VPN revenue"), s ? usd(s.revenueUsdCents, lang) : "…", s ? toman(s.revenueToman, lang) : ""],
        ].map(([icon, label, value, detail]) => (
          <div className="metric-card" key={label}>
            <div className="metric-label"><span>{label}</span><Icon name={icon} /></div>
            <strong>{value}</strong><small>{detail}</small>
          </div>
        ))}
      </div>

      <section className="card">
        <div className="section-heading"><h2>{tr("قیمت و حجم", "Price & data")}</h2></div>
        <form className="vpn-policy" onSubmit={save}>
          <label className="check"><input type="checkbox" checked={form.enabled} onChange={(e) => setForm({ ...form, enabled: e.target.checked })} /> {tr("فروش و درخواست تازه باز است", "New requests and sales are open")}</label>
          <label>{tr("حجم آزمایشی (مگابایت، ۰ = بدون آزمایش)", "Trial data (MB, 0 = no trial)")}<input inputMode="decimal" value={form.trialMb} onChange={(e) => setForm({ ...form, trialMb: e.target.value })} /></label>
          <label>{tr("مدت آزمایش (روز، ۰ = بدون محدودیت)", "Trial length (days, 0 = no limit)")}<input inputMode="numeric" value={form.trialDays} onChange={(e) => setForm({ ...form, trialDays: e.target.value })} /></label>
          <label>{tr("حجم هر دوره (گیگابایت)", "Data per period (GB)")}<input inputMode="decimal" value={form.monthlyGb} onChange={(e) => setForm({ ...form, monthlyGb: e.target.value })} /></label>
          <label>{tr("طول دوره (روز)", "Period length (days)")}<input inputMode="numeric" value={form.periodDays} onChange={(e) => setForm({ ...form, periodDays: e.target.value })} /></label>
          <label>{tr("حداکثر کاربر هم‌زمان", "Simultaneous users")}<input inputMode="numeric" value={form.ipLimit} onChange={(e) => setForm({ ...form, ipLimit: e.target.value })} /></label>
          <label>{tr("قیمت دلاری (USD)", "Price (USD)")}<input inputMode="decimal" value={form.usd} placeholder="4" onChange={(e) => setForm({ ...form, usd: e.target.value })} /></label>
          <label>{tr("قیمت تومانی", "Price (toman)")}<input inputMode="numeric" value={form.toman} placeholder={tr("خالی = غیرفعال", "empty = off")} onChange={(e) => setForm({ ...form, toman: e.target.value })} /></label>
          <button className="btn" disabled={busy}>{tr("ذخیره", "Save")}</button>
        </form>
        <p className="hint">{tr("تغییر حجم از دورهٔ بعدی هر مشترک اعمال می‌شود. تغییر تعداد کاربر هم‌زمان روی همهٔ پیکربندی‌ها اعمال می‌شود؛ اجرای آن به Fail2ban روی سرور 3x-ui نیاز دارد.",
          "Data changes apply from each subscriber’s next period. A new simultaneous-user limit is written to every client; 3x-ui enforces it only when its Fail2ban IP limit is installed on the server.")}</p>
      </section>

      <section className="card">
        <div className="section-heading">
          <h2>{tr("سرورها", "Servers")}</h2>
          <button className="text-button" disabled={busy} onClick={() => act(async () => setServers(await vpnApi.servers()), tr("وضعیت سرورها بررسی شد.", "Server status checked."))}>{tr("بررسی اتصال", "Check connection")}</button>
        </div>
        {(servers ?? overview?.servers.map((x) => ({ ...x, reachable: undefined as boolean | undefined, error: undefined as string | null | undefined, online: undefined as number | undefined, inbounds: [] as VpnServerStatus["inbounds"], missingInbounds: [] as number[] })) ?? []).map((x) => (
          <article className="payment-row" key={x.code}>
            <div>
              <strong>{x.name} <small dir="ltr">({x.code})</small></strong>
              <span className="muted small">
                {!x.configured ? tr("پیکربندی نشده — نشانی پنل، نشانی اشتراک و توکن API را در .env تنظیم کنید.", "Not configured — set the panel URL, subscription URL and API token in .env.")
                  : x.reachable === undefined ? tr("برای آزمایش اتصال «بررسی اتصال» را بزنید.", "Press “Check connection” to test it.")
                  : x.reachable ? `${tr("در دسترس", "Reachable")} · ${n(x.online ?? 0)} ${tr("متصل", "online")} · ${x.inbounds.filter((i) => i.used).map((i) => `#${i.id} ${i.remark ?? ""}${i.enable ? "" : " (off)"}`).join(", ")}`
                  : x.error}
              </span>
              {!!x.missingInbounds?.length && <span className="alert error">{tr("ورودی‌های ناموجود: ", "Missing inbounds: ")}{x.missingInbounds.join(", ")}</span>}
            </div>
            <span className={"badge " + (x.reachable ? "green" : "")}>{x.configured ? (x.reachable ? tr("سالم", "Healthy") : x.reachable === false ? tr("خطا", "Error") : tr("پیکربندی‌شده", "Configured")) : tr("خاموش", "Off")}</span>
          </article>
        ))}
      </section>

      <section className="card">
        <div className="section-heading">
          <h2>{tr("مشترکان", "Subscribers")} ({n(visible.length)})</h2>
          <div className="row">
            <input type="search" placeholder={tr("جستجوی ایمیل", "Search email")} value={filter} onChange={(e) => setFilter(e.target.value)} />
            <label className="check"><input type="checkbox" checked={showRevoked} onChange={(e) => setShowRevoked(e.target.checked)} /> {tr("لغوشده‌ها", "Revoked")}</label>
          </div>
        </div>
        <div className="vpn-table-wrap">
          <table className="vpn-table">
            <thead><tr>
              <th>{tr("کاربر", "Member")}</th><th>{tr("وضعیت", "Status")}</th><th>{tr("مصرف", "Usage")}</th>
              <th>{tr("انقضا", "Expires")}</th><th>{tr("اتصال", "Connection")}</th><th>{tr("اقدام", "Actions")}</th>
            </tr></thead>
            <tbody>
              {visible.map(({ email, clientEmail, account: a }) => {
                const [label, tone] = vpnStatusLabel(a.status, lang);
                return (
                  <tr key={a.id}>
                    <td><strong dir="ltr">{email}</strong><small dir="ltr" className="muted">{clientEmail} · {a.serverCode}</small>{a.lastError && <small className="warn-text" title={a.lastError}>{tr("خطای همگام‌سازی", "Sync error")}: {a.lastError.slice(0, 80)}</small>}</td>
                    <td><span className={"badge " + tone}>{label}</span>{a.pending && <small className="muted">{tr("در انتظار اعمال", "pending write")}</small>}</td>
                    <td>{bytes(a.usedTotal, lang)} / {bytes(a.limitBytes, lang)}<small className="muted">↓{bytes(a.usedDown, lang)} ↑{bytes(a.usedUp, lang)}{a.limitOverrideBytes ? ` · ${tr("سفارشی", "custom")} ${bytes(a.limitOverrideBytes, lang)}` : ""}</small></td>
                    <td className={expiryOf(a) && utcDate(expiryOf(a)!).getTime() <= Date.now() ? "warn-text" : ""}>{expiryOf(a) ? when(expiryOf(a), lang) : tr("بدون انقضا", "No expiry")}<small className="muted">{a.paidUntil ? tr("پرداخت‌شده", "paid") : tr("آزمایشی", "trial")}{a.paidUntil && a.periodEnd && a.periodEnd !== a.paidUntil ? ` · ${tr("دوره حجمی تا ", "data period to ")}${day(a.periodEnd, lang)}` : ""}</small></td>
                    <td><span className={"vpn-dot " + (a.online ? "on" : "")} aria-hidden="true" />{a.online ? tr("متصل", "Online") : tr("قطع", "Offline")}<small className={a.activeIps > a.ipLimit ? "warn-text" : "muted"}>{n(a.activeIps)}/{n(a.ipLimit)} {tr("کاربر", "users")}</small></td>
                    <td className="vpn-actions">
                      {a.status === "revoked" ? "—" : <>
                        {a.suspendedAt ? (
                          <button className="text-button" disabled={busy} onClick={() => act(() => vpnApi.adminSuspend(a.id, false), tr("فعال شد.", "Resumed."))}>{tr("ادامه", "Resume")}</button>
                        ) : (
                          <button className="text-button" disabled={busy} onClick={() => { const r = prompt(tr("دلیل توقف (اختیاری):", "Reason for suspension (optional):")); if (r !== null) void act(() => vpnApi.adminSuspend(a.id, true, r), tr("متوقف شد.", "Suspended.")); }}>{tr("توقف", "Suspend")}</button>
                        )}
                        <button className="text-button" disabled={busy} onClick={() => { const d = prompt(tr("چند روز اضافه شود؟ (عدد منفی برای کم کردن)", "Add how many days? (negative to remove)"), "30"); if (d && Number(d)) void act(() => vpnApi.adminExtend(a.id, Math.trunc(Number(d))), tr("اعتبار تغییر کرد.", "Paid time adjusted.")); }}>{tr("تمدید", "Extend")}</button>
                        <button className="text-button" disabled={busy} onClick={() => { const g = prompt(tr("حجم ماهانهٔ سفارشی (گیگابایت). خالی = پیش‌فرض.", "Custom monthly data (GB). Empty = default."), a.limitOverrideBytes ? String(a.limitOverrideBytes / GB) : ""); if (g === null) return; const monthly = g.trim() ? Math.round(Number(g) * GB) : null; const cur = prompt(tr("حجم دورهٔ فعلی (گیگابایت). خالی = بدون تغییر.", "Current period data (GB). Empty = unchanged."), ""); if (cur === null) return; void act(() => vpnApi.adminLimit(a.id, monthly, cur.trim() ? Math.round(Number(cur) * GB) : null), tr("حجم تغییر کرد.", "Data limit changed.")); }}>{tr("حجم", "Data")}</button>
                        <button className="text-button" disabled={busy} onClick={() => confirm(tr("مصرف این دوره صفر شود؟", "Reset this period’s usage to zero?")) && act(() => vpnApi.adminReset(a.id), tr("مصرف صفر شد.", "Usage reset."))}>{tr("صفر کردن", "Reset")}</button>
                        <button className="text-button" disabled={busy} onClick={() => act(() => vpnApi.adminSync(a.id), tr("همگام شد.", "Synced."))}>{tr("همگام", "Sync")}</button>
                        <button className="text-button" disabled={busy} onClick={() => confirm(tr("پیوند تازه ساخته شود؟ پیوند قبلی کار نمی‌کند.", "Replace the link? The old link stops working.")) && act(() => vpnApi.adminRotate(a.id), tr("پیوند عوض شد.", "Link replaced."))}>{tr("پیوند تازه", "New link")}</button>
                        <button className="text-button danger" disabled={busy} onClick={() => confirm(tr("این VPN لغو و از سرور حذف شود؟ قابل بازگشت نیست.", "Revoke this VPN and delete it from the server? This can’t be undone.")) && act(() => vpnApi.adminRevoke(a.id), tr("لغو شد.", "Revoked."))}>{tr("لغو", "Revoke")}</button>
                      </>}
                    </td>
                  </tr>
                );
              })}
              {visible.length === 0 && <tr><td colSpan={6} className="muted">{tr("هنوز مشترکی نیست.", "No subscribers yet.")}</td></tr>}
            </tbody>
          </table>
        </div>
      </section>
    </>
  );
}
