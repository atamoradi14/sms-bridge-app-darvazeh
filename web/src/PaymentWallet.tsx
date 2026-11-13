import { useEffect, useState } from "react";
import {
  api,
  RequestError,
  type WalletHistory,
  type WalletTransactions,
  type WalletView,
} from "./api";
import type { Lang } from "./i18n";

const EZPAY_PORTAL_URL = "https://wallet.ezpay.ir";

/**
 * The default receiving wallet for toman payments, connected by phone number.
 * Same flow as Ravand's superadmin wallet page: connect → Ezpay consent code, or (when Ezpay already
 * gives access) an ownership code sent by SMS from the admin's linked phone; then identity check and
 * merchant switch. Payments go to this wallet only when it is verified and merchant services are on.
 */
export function PaymentWalletAdmin({ lang }: { lang: Lang }) {
  const c = (fa: string, en: string) => (lang === "fa" ? fa : en);
  const money = (value: string | number) =>
    new Intl.NumberFormat(lang === "fa" ? "fa-IR" : "en-US").format(
      typeof value === "string" ? BigInt(value) : value,
    );
  const [wallet, setWallet] = useState<WalletView | null>(null);
  const [reusable, setReusable] = useState<{ phoneNumber: string }[]>([]);
  const [balances, setBalances] = useState<
    { accountType: string; balance: string; availableBalance: string }[] | null
  >(null);
  const [history, setHistory] = useState<WalletHistory | null>(null);
  const [orders, setOrders] = useState<WalletTransactions | null>(null);
  const [page, setPage] = useState(0);
  const [phone, setPhone] = useState("");
  const [code, setCode] = useState("");
  const [proof, setProof] = useState(false);
  const [sent, setSent] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  async function load() {
    const view = await api.wallet();
    setWallet(view);
    if (!view.relayConfigured) return;
    api.walletReusable().then(setReusable).catch(() => setReusable([]));
    api.walletTransactions(0).then(setOrders).catch(() => setOrders(null));
    if (view.connection.status === "Connected") {
      api.walletBalances().then(setBalances).catch(() => setBalances(null));
      api.walletHistory(page).then(setHistory).catch(() => setHistory(null));
    } else {
      setBalances(null);
      setHistory(null);
    }
  }
  useEffect(() => {
    load().catch((e) => setError((e as Error).message));
  }, [page]);

  async function run(work: () => Promise<unknown>, done?: string) {
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const result = await work();
      if (result && typeof result === "object" && "connection" in result)
        setWallet(result as WalletView);
      if (done) setNotice(done);
      await load();
    } catch (e) {
      const failure = e as RequestError;
      if (failure.code === "wallet_phone_proof_required") setProof(true);
      setError(failure.message);
    } finally {
      setBusy(false);
    }
  }

  if (!wallet)
    return (
      <section className="card">
        <h2>{c("کیف پول دریافت تومانی", "Toman receiving wallet")}</h2>
        {error ? <p className="alert error">{error}</p> : <p className="muted">{c("در حال بارگذاری…", "Loading…")}</p>}
      </section>
    );

  const connected = wallet.connection.status === "Connected";
  const challenge = wallet.connection.challengeId;
  const verified = wallet.providerStatus?.identityVerified === true;
  const accessActive = wallet.providerStatus?.access === "Active";
  const ready = wallet.purchaseReady;
  const needsMerchant = connected && verified && accessActive && !wallet.merchantEnabled;
  const title = !wallet.relayConfigured
    ? c("سرور پرداخت ایران پیکربندی نشده است", "The Iran payment relay is not configured")
    : ready
      ? c("آمادهٔ دریافت پرداخت‌های تومانی", "Ready for toman payments")
      : !connected
        ? c("ابتدا کیف پول را متصل کنید", "Connect a wallet first")
        : !wallet.providerStatus
          ? c("وضعیت ایزی‌پی قابل بررسی نیست", "Ezpay status is unavailable")
          : !accessActive
            ? c("دسترسی ایزی‌پی فعال نیست", "Ezpay access is not active")
            : !verified
              ? c("احراز هویت لازم است", "Identity verification is required")
              : c("خدمات پذیرندگی را فعال کنید", "Enable merchant services");

  return (
    <section className="card payment-wallet">
      <h2>{c("کیف پول دریافت تومانی (ایزی‌پی)", "Toman receiving wallet (Ezpay)")}</h2>
      <p className="muted">
        {c(
          "کیف پول پیش‌فرض دروازه برای خرید اشتراک با تومان. اتصال با شمارهٔ موبایل کیف پول و کد ایزی‌پی انجام می‌شود؛ پرداخت‌ها فقط پس از احراز هویت و فعال‌سازی خدمات پذیرندگی به این کیف پول واریز می‌شوند.",
          "Darvazeh's default wallet for toman membership payments. Connect it with the wallet's mobile number and an Ezpay code; payments go here only after identity verification and merchant activation.",
        )}
      </p>
      {error && <p className="alert error" role="alert">{error}</p>}
      {notice && <p className="alert success" role="status">{notice}</p>}
      <div className={`alert ${ready ? "success" : ""}`} aria-live="polite">
        <strong>{title}</strong>
        <p dir="ltr">{wallet.connection.phoneNumber ?? c("کیف پولی انتخاب نشده", "No wallet selected")}</p>
        <p>{wallet.readinessReason}</p>
        {connected && (
          <span className="badge">
            {verified
              ? c("احراز هویت تأیید شده", "Identity verified")
              : c("احراز هویت تأیید نشده یا نامشخص", "Identity unverified or unknown")}
          </span>
        )}
      </div>
      {wallet.relayConfigured && (
        <>
          <h3>{c("انتخاب یا تغییر کیف پول", "Select or change wallet")}</h3>
          <p className="hint">
            {c(
              "تغییر شماره، خدمات پذیرندگی را خاموش می‌کند و باید دوباره فعال شود.",
              "Changing the number switches merchant services off until you enable them again.",
            )}
          </p>
          {reusable.length > 0 && (
            <label>
              {c("کیف پول تأییدشدهٔ قبلی", "Previously verified wallet")}
              <select
                defaultValue=""
                disabled={busy}
                onChange={(e) => {
                  const value = e.target.value;
                  if (!value) return;
                  setPhone(value);
                  setProof(false);
                  setSent(false);
                  void run(() => api.walletReuse(value), c("کیف پول انتخاب شد.", "Wallet selected."));
                }}
              >
                <option value="">{c("انتخاب کنید", "Choose a wallet")}</option>
                {reusable.map((w) => (
                  <option key={w.phoneNumber} value={w.phoneNumber}>{w.phoneNumber}</option>
                ))}
              </select>
            </label>
          )}
          <form
            onSubmit={(e) => {
              e.preventDefault();
              setProof(false);
              setSent(false);
              void run(() => api.walletConnect(phone));
            }}
          >
            <label>
              {c("شمارهٔ موبایل کیف پول", "Wallet phone number")}
              <input
                inputMode="tel"
                dir="ltr"
                value={phone}
                placeholder="09xxxxxxxxx"
                required
                disabled={busy}
                onChange={(e) => {
                  setPhone(e.target.value);
                  setProof(false);
                  setSent(false);
                  setCode("");
                }}
              />
            </label>
            <button className="btn" disabled={busy || !phone}>
              {c("اتصال کیف پول", "Connect wallet")}
            </button>
          </form>
          {proof && (
            <div className="alert">
              <p>
                {c(
                  "این کیف پول در ایزی‌پی وجود دارد. مالکیت شماره را با کد پیامکی تأیید کنید؛ کد از گوشی متصل به حساب مدیر به این شماره پیامک می‌شود.",
                  "This Ezpay wallet already exists. Confirm you hold the number: a code is texted to it from the phone linked to the admin account.",
                )}
              </p>
              <button
                className="btn secondary"
                disabled={busy}
                onClick={() =>
                  void run(async () => {
                    await api.walletRequestProof(phone);
                    setSent(true);
                  }, c("کد مالکیت در صف ارسال پیامک قرار گرفت.", "Ownership code queued for SMS."))
                }
              >
                {c("ارسال کد مالکیت", "Send ownership code")}
              </button>
              {sent && (
                <form
                  onSubmit={(e) => {
                    e.preventDefault();
                    void run(async () => {
                      const view = await api.walletConfirmProof(phone, code);
                      setProof(false);
                      setSent(false);
                      setCode("");
                      return view;
                    });
                  }}
                >
                  <label>
                    {c("کد پیامکی", "SMS code")}
                    <input value={code} onChange={(e) => setCode(e.target.value)} inputMode="numeric" autoComplete="one-time-code" required />
                  </label>
                  <button className="btn" disabled={busy}>
                    {c("تأیید مالکیت و اتصال", "Verify ownership and connect")}
                  </button>
                </form>
              )}
            </div>
          )}
          {challenge && (
            <form
              onSubmit={(e) => {
                e.preventDefault();
                void run(async () => {
                  const view = await api.walletConfirm(challenge, code);
                  setCode("");
                  return view;
                }, c("کیف پول متصل شد.", "Wallet connected."));
              }}
            >
              <p>
                {c("کد ایزی‌پی برای این شماره ارسال شده است:", "Ezpay sent a consent code to:")}{" "}
                <bdi dir="ltr">{wallet.connection.pendingPhoneNumber}</bdi>
              </p>
              <label>
                {c("کد ایزی‌پی", "Ezpay code")}
                <input value={code} onChange={(e) => setCode(e.target.value)} inputMode="numeric" required />
              </label>
              <button className="btn" disabled={busy}>{c("تأیید اتصال ایزی‌پی", "Confirm Ezpay consent")}</button>{" "}
              <button type="button" className="btn secondary" disabled={busy} onClick={() => void run(() => api.walletResend(challenge))}>
                {c("ارسال مجدد", "Resend code")}
              </button>
            </form>
          )}

          <h3>{c("احراز هویت و خدمات پذیرندگی", "Verification and merchant services")}</h3>
          {connected && !verified && accessActive && (
            <a className="btn secondary" href={EZPAY_PORTAL_URL} target="_blank" rel="noopener noreferrer">
              {c("احراز هویت در ایزی‌پی", "Verify identity at Ezpay")}
            </a>
          )}{" "}
          <button className="btn secondary" disabled={busy} onClick={() => void run(async () => undefined)}>
            {c("بررسی دوباره", "Recheck status")}
          </button>
          <label className="check">
            <input
              type="checkbox"
              checked={wallet.merchantEnabled}
              disabled={busy || (!wallet.merchantEnabled && (!verified || !accessActive))}
              onChange={(e) => void run(() => api.walletMerchant(e.target.checked))}
            />
            {c("خدمات پذیرندگی کیف پول دروازه فعال باشد", "Enable Darvazeh wallet merchant services")}
          </label>
          {needsMerchant && (
            <p className="hint">
              {c("احراز هویت تأیید شده است؛ فقط خدمات پذیرندگی را فعال کنید.", "Identity is verified; just enable merchant services.")}
            </p>
          )}
          <p className="hint">
            {c(
              "این تأیید مدیر دروازه است؛ قرارداد پذیرندگی ایزی‌پی نیز باید نزد ایزی‌پی فعال باشد (در غیر این صورت خطای کد ۹۷).",
              "This is Darvazeh's approval; the Ezpay merchant contract must also be active at Ezpay (otherwise error code 97).",
            )}
          </p>
          {connected && (
            <button
              className="btn danger"
              disabled={busy}
              onClick={() => {
                if (confirm(c("اتصال کیف پول قطع شود؟ پرداخت تومانی تا اتصال دوباره غیرفعال می‌شود.", "Disconnect the wallet? Toman checkout stops until you connect again.")))
                  void run(() => api.walletDisconnect(), c("اتصال کیف پول قطع شد.", "Wallet disconnected."));
              }}
            >
              {c("قطع اتصال کیف پول", "Disconnect wallet")}
            </button>
          )}

          {connected && balances && (
            <>
              <h3>{c("موجودی", "Balances")}</h3>
              <div className="stats-grid">
                {balances.map((b) => (
                  <div className="stat-card" key={b.accountType}>
                    <span className="muted">{b.accountType}</span>
                    <strong>{money(b.availableBalance)} {c("ریال", "IRR")}</strong>
                  </div>
                ))}
              </div>
            </>
          )}
          {connected && history && (
            <>
              <h3>{c("گردش حساب", "Wallet activity")}</h3>
              <div className="payment-history">
                {history.items.length === 0 && <p className="muted">{c("گردشی ثبت نشده است.", "No activity yet.")}</p>}
                {history.items.map((h, i) => (
                  <article className="payment-row" key={`${h.transactionNumber ?? ""}-${i}`}>
                    <div>
                      <strong>{h.description ?? h.fromOrTo ?? "—"}</strong>
                      <span className="muted small">{new Date(h.utcTimestamp < 1e12 ? h.utcTimestamp * 1000 : h.utcTimestamp).toLocaleString(lang === "fa" ? "fa-IR" : "en-NZ")}</span>
                    </div>
                    <div><strong>{money(h.amount)} {c("ریال", "IRR")}</strong></div>
                  </article>
                ))}
              </div>
              <div className="section-heading">
                <button className="text-button" disabled={page === 0 || busy} onClick={() => setPage(page - 1)}>{c("قبلی", "Previous")}</button>
                <button className="text-button" disabled={(page + 1) * 50 >= history.total || busy} onClick={() => setPage(page + 1)}>{c("بعدی", "Next")}</button>
              </div>
            </>
          )}
          {orders && orders.items.length > 0 && (
            <>
              <h3>{c("پرداخت‌های تومانی دروازه", "Darvazeh toman payments")}</h3>
              <div className="payment-history">
                {orders.items.map((o) => (
                  <article className="payment-row" key={o.id}>
                    <div>
                      <strong>{o.planName}</strong>
                      <small dir="ltr">{o.email}</small>
                    </div>
                    <div>
                      <strong>{money(o.amountMinor)} {c("تومان", "toman")}</strong>
                      <span className="badge">{o.status}</span>
                    </div>
                  </article>
                ))}
              </div>
            </>
          )}
        </>
      )}
    </section>
  );
}
