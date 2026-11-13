import {startPolling} from "./polling";
import {utcDate} from "./format";
import { useEffect, useRef, useState, type FormEvent } from "react";
import {
  api,
  RequestError,
  type BillingCatalog,
  type PaymentOrder,
  type Plan,
} from "./api";
import type { Lang } from "./i18n";

const usd = (value: number, lang: Lang) =>
  new Intl.NumberFormat(lang === "fa" ? "fa-IR" : "en-US", {
    style: "currency",
    currency: "USD",
    currencyDisplay: "narrowSymbol",
  }).format(value / 100);
const toman = (value: number, lang: Lang) =>
  new Intl.NumberFormat(lang === "fa" ? "fa-IR" : "en-US").format(value) +
  (lang === "fa" ? " تومان" : " toman");
const normalizeNumber = (value: string) =>
  value
    .replace(/[۰-۹٠-٩]/g, (c) =>
      String(c.charCodeAt(0) - (c >= "۰" ? 1776 : 1632)),
    )
    .replace(/[٬,]/g, "")
    .replace(/٫/g, ".");
function paymentLabel(status: string, lang: Lang) {
  const labels: Record<string, [string, string]> = {
    created: ["آغاز پرداخت", "Checkout started"],
    checkout_open: ["در انتظار پرداخت", "Awaiting payment"],
    paid_queued: [
      "پرداخت‌شده؛ در انتظار شروع دوره",
      "Paid · next period queued",
    ],
    paid_active: ["پرداخت‌شده؛ دوره فعال", "Paid · period activated"],
    failed: ["پرداخت ناموفق", "Payment failed"],
    expired: ["زمان پرداخت گذشته", "Checkout expired"],
    refunded: ["بازپرداخت‌شده", "Refunded"],
  };
  return (labels[status] || ["در حال بررسی", "Checking payment"])[
    lang === "fa" ? 0 : 1
  ];
}
function billingError(error: unknown, lang: Lang) {
  const e = error as RequestError;
  const messages: Record<string, [string, string]> = {
    payment_not_configured: [
      "پرداخت دلاری هنوز فعال نشده است.",
      "USD checkout isn’t connected yet.",
    ],
    payment_method_unavailable: [
      "پرداخت تومانی پس از اتصال کیف پول فعال می‌شود.",
      "Toman checkout will be available once the wallet is connected.",
    ],
    payment_provider_unavailable: [
      "درگاه پرداخت موقتاً در دسترس نیست. دوباره تلاش کنید.",
      "Checkout is temporarily unavailable. Please try again.",
    ],
    checkout_expired: [
      "زمان این درخواست گذشته است. پرداخت تازه‌ای آغاز کنید.",
      "This checkout expired. Start a new payment.",
    ],
    checkout_rate_limit: [
      "چند درخواست پرداخت باز دارید. کمی بعد دوباره تلاش کنید.",
      "You have several open checkouts. Please try again later.",
    ],
    connection: [
      "اتصال برقرار نشد. اینترنت را بررسی کنید.",
      "Couldn’t connect. Please check your connection.",
    ],
  };
  return (messages[e.code || ""] || [
    "پرداخت تأیید نشد. وضعیت آن را دوباره بررسی کنید.",
    "We couldn’t confirm the payment. Please check its status again.",
  ])[lang === "fa" ? 0 : 1];
}

function OrderList({ orders, lang }: { orders: PaymentOrder[]; lang: Lang }) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en);
  return (
    <div className="payment-history">
      {orders.length === 0 ? (
        <p className="muted">
          {tr("هنوز پرداختی ثبت نشده است.", "No payments yet.")}
        </p>
      ) : (
        orders.map((order) => (
          <article className="payment-row" key={order.id}>
            <div>
              <strong>{order.planName}</strong>
              {order.email && <small dir="ltr">{order.email}</small>}
              <span className="muted small">
                {utcDate(order.createdAt).toLocaleString(
                  lang === "fa" ? "fa-IR" : "en-NZ",
                )}
              </span>
            </div>
            <div>
              <strong>
                {order.currency === "USD"
                  ? usd(order.amountMinor, lang)
                  : toman(order.amountMinor, lang)}
              </strong>
              <span className="badge">{paymentLabel(order.status, lang)}</span>
              {order.refundedMinor > 0 && (
                <small>
                  {tr("بازپرداخت: ", "Refund: ")}
                  {usd(order.refundedMinor, lang)}
                </small>
              )}
            </div>
          </article>
        ))
      )}
    </div>
  );
}

export function BillingPanel({
  lang,
  onMembershipChanged,
}: {
  lang: Lang;
  onMembershipChanged: () => Promise<unknown>;
}) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en);
  const [catalog, setCatalog] = useState<BillingCatalog | null>(null);
  const [orders, setOrders] = useState<PaymentOrder[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  // A toman checkout opens Ezpay in its own window; this page waits for it and checks the order.
  const [tomanWaiting, setTomanWaiting] = useState<{ order: string; url: string | null; opened: boolean } | null>(null);
  const [checking, setChecking] = useState(false);
  const reconciliationBusy = useRef(false);
  const waitingPanel = useRef<HTMLDivElement>(null);
  const request = useRef<{
    planId: string;
    currency: string;
    key: string;
  } | null>(null);
  const changed = useRef(onMembershipChanged);
  changed.current = onMembershipChanged;
  async function load() {
    const [c, o] = await Promise.all([
      api.billingCatalog(),
      api.paymentOrders(),
    ]);
    setCatalog(c);
    setOrders(o);
  }
  async function checkToman(order: string, manual = false) {
    if (reconciliationBusy.current) return;
    reconciliationBusy.current = true;
    if (manual) setChecking(true);
    try {
      const result = await api.reconcilePayment(order);
      if (result.status === "checkout_open") {
        if (manual)
          setNotice(
            tr(
              "پرداخت هنوز تأیید نشده است. اگر پرداخت کرده‌اید، تأیید چند دقیقه طول می‌کشد.",
              "The payment isn’t confirmed yet. If you have paid, confirmation can take a few minutes.",
            ),
          );
        return;
      }
      localStorage.removeItem("darvazeh.pendingTomanOrder");
      setTomanWaiting(null);
      request.current = null;
      if (result.status.startsWith("paid_")) {
        setNotice(
          result.status === "paid_active"
            ? tr("پرداخت تأیید شد؛ دوره شما فعال است.", "Payment confirmed. Your period is active.")
            : tr("پرداخت تأیید شد؛ دوره تازه در نوبت است.", "Payment confirmed. Your next period is queued."),
        );
        await changed.current();
      } else setNotice(paymentLabel(result.status, lang));
      await load();
    } catch (e) {
      if (manual) setError(billingError(e, lang));
      else throw e;
    } finally {
      reconciliationBusy.current = false;
      if (manual) setChecking(false);
    }
  }
  useEffect(() => {
    if (!tomanWaiting) return;
    const order = tomanWaiting.order;
    return startPolling(() => checkToman(order), 30000, 10000);
  }, [tomanWaiting?.order]);
  useEffect(() => {
    if (tomanWaiting?.url) waitingPanel.current?.scrollIntoView({ behavior: "smooth", block: "center" });
  }, [tomanWaiting?.url]);
  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const params = new URLSearchParams(location.search);
    // Ezpay has no return address to this site, so a toman checkout remembers its order and is checked when the buyer comes back.
    const pendingToman = localStorage.getItem("darvazeh.pendingTomanOrder");
    const order = params.get("order") || pendingToman;
    const returning = (params.has("payment") && !!params.get("order")) || !!pendingToman;
    async function check(attempt = 0) {
      try {
        if (returning && order) {
          const result = await api.reconcilePayment(order);
          if (stopped) return;
          if (result.status !== "checkout_open") localStorage.removeItem("darvazeh.pendingTomanOrder");
          if (result.status.startsWith("paid_")) {
            setNotice(
              result.status === "paid_active"
                ? tr(
                    "پرداخت تأیید شد؛ دوره شما فعال است.",
                    "Payment confirmed. Your period is active.",
                  )
                : tr(
                    "پرداخت تأیید شد؛ دوره تازه در نوبت است.",
                    "Payment confirmed. Your next period is queued.",
                  ),
            );
            await changed.current();
            history.replaceState({}, "", location.pathname);
          } else if (
            result.status === "refunded" ||
            result.status === "failed" ||
            result.status === "expired"
          ) {
            setNotice(paymentLabel(result.status, lang));
            history.replaceState({}, "", location.pathname);
          } else if (params.get("payment") === "cancelled") {
            setNotice(
              tr(
                "پرداخت کامل نشده است. می‌توانید دوباره طرحی انتخاب کنید.",
                "Checkout wasn’t completed. You can choose a plan again.",
              ),
            );
            history.replaceState({}, "", location.pathname);
          } else if (pendingToman && !params.has("payment")) {
            setTomanWaiting({ order: pendingToman, url: null, opened: false });
          } else if (attempt < 12) {
            setNotice(
              tr(
                "در حال بررسی تأیید پرداخت…",
                "Checking payment confirmation…",
              ),
            );
            timer = setTimeout(() => check(attempt + 1), 5000);
          }
        }
        if (!stopped) await load();
      } catch (e) {
        if (!stopped) setError(billingError(e, lang));
      }
    }
    void check();
    return () => {
      stopped = true;
      if (timer) clearTimeout(timer);
    };
  }, []);
  async function buy(plan: Plan, currency: "USD" | "IRT") {
    if (
      !request.current ||
      request.current.planId !== plan.id ||
      request.current.currency !== currency
    )
      request.current = { planId: plan.id, currency, key: crypto.randomUUID() };
    // Open the payment window now, while the click still counts as a user action; browsers block it after an await.
    let paymentWindow: Window | null = null;
    if (currency === "IRT") {
      paymentWindow = window.open("", "_blank");
      if (paymentWindow) {
        paymentWindow.document.title = tr("در حال باز کردن ایزی‌پی…", "Opening Ezpay…");
        paymentWindow.document.body.style.cssText = "font-family:Tahoma,sans-serif;text-align:center;padding:3rem;color:#174c44";
        paymentWindow.document.body.dir = lang === "fa" ? "rtl" : "ltr";
        paymentWindow.document.body.textContent = tr(
          "در حال باز کردن صفحهٔ پرداخت ایزی‌پی… اگر داخل ایران هستید، فیلترشکن را خاموش کنید.",
          "Opening the Ezpay payment page… If you are in Iran, turn off your VPN.",
        );
      }
    }
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const checkout = await api.checkout(
        plan.id,
        currency,
        request.current.key,
      );
      if (checkout.status.startsWith("paid_")) {
        paymentWindow?.close();
        await load();
        await changed.current();
        setNotice(
          tr(
            "این پرداخت قبلاً تأیید شده است.",
            "This payment has already been confirmed.",
          ),
        );
        request.current = null;
      } else if (checkout.status !== "checkout_open") {
        paymentWindow?.close();
        request.current = null;
        setError(
          tr(
            "این درخواست بسته شده است. دوباره تلاش کنید.",
            "That checkout is closed. Please try again.",
          ),
        );
      } else if (checkout.checkoutUrl) {
        const url = new URL(checkout.checkoutUrl);
        const allowed =
          currency === "USD"
            ? url.hostname === "checkout.stripe.com"
            : url.hostname === "ezpay.ir" || url.hostname.endsWith(".ezpay.ir");
        if (url.protocol !== "https:" || !allowed) throw new Error();
        if (currency === "IRT") {
          localStorage.setItem("darvazeh.pendingTomanOrder", checkout.id);
          const opened = !!paymentWindow && !paymentWindow.closed;
          if (opened) {
            paymentWindow!.opener = null;
            paymentWindow!.location.replace(url.href);
          }
          // If the browser blocked the window, the waiting panel offers a link the buyer can open themselves.
          setTomanWaiting({ order: checkout.id, url: url.href, opened });
          void load().catch(() => undefined);
        } else location.assign(url.href);
      } else paymentWindow?.close();
    } catch (e) {
      paymentWindow?.close();
      setError(billingError(e, lang));
    } finally {
      setBusy(false);
    }
  }
  return (
    <section className="billing-panel">
      <div className="section-heading">
        <h2>{tr("انتخاب دوره بعدی", "Choose your next period")}</h2>
        <button
          className="text-button"
          disabled={busy}
          onClick={() => load().catch((e) => setError(billingError(e, lang)))}
        >
          {tr("به‌روزرسانی", "Refresh")}
        </button>
      </div>
      <p className="muted">
        {tr(
          "هر پرداخت فقط برای یک دوره است؛ برداشت خودکار یا تمدید خودکار نداریم.",
          "Each payment buys one period. There is no automatic renewal or repeat charge.",
        )}
      </p>
      <p className="hint">
        {tr(
          "ارتقا از دوره آزمایشی بلافاصله آغاز می‌شود. اگر دوره پرداخت‌شده هنوز اعتبار و ظرفیت دارد، خرید تازه در نوبت می‌ماند و پس از پایان مدت یا ظرفیت، با استفاده بعدی از حساب آغاز می‌شود.",
          "Upgrading from the trial starts immediately. If a paid period still has time and allowance, your purchase is queued and starts on your next account use after that period expires or its allowance runs out.",
        )}
      </p>
      {error && (
        <p className="alert error" role="alert">
          {error}
        </p>
      )}
      {notice && (
        <p className="alert success" role="status">
          {notice}
        </p>
      )}
      {catalog?.tomanAvailable && (
        <div className="alert vpn-banner" role="note">
          <strong>
            {tr("داخل ایران هستید؟ فیلترشکن (VPN) را خاموش کنید.", "In Iran? Turn off your VPN.")}
          </strong>
          <span>
            {tr(
              "صفحهٔ پرداخت تومانی ایزی‌پی با فیلترشکن روشن باز نمی‌شود یا پرداخت ناموفق می‌ماند. پیش از زدن دکمهٔ پرداخت تومانی آن را خاموش کنید.",
              "Ezpay’s toman payment page won’t open, or the payment fails, while a VPN is on. Turn it off before pressing the toman payment button.",
            )}
          </span>
        </div>
      )}
      {tomanWaiting && (
        <div className="alert toman-waiting" role="status" ref={waitingPanel}>
          <strong>
            {tr("در انتظار پرداخت تومانی", "Waiting for your toman payment")}
          </strong>
          <span>
            {tomanWaiting.url && tomanWaiting.opened
              ? tr(
                  "صفحهٔ پرداخت ایزی‌پی در پنجرهٔ تازه باز شد. پرداخت را آنجا کامل کنید و به این صفحه برگردید؛ تأیید خودکار انجام می‌شود.",
                  "The Ezpay payment page opened in a new window. Finish paying there and come back here; confirmation is automatic.",
                )
              : tomanWaiting.url
              ? tr(
                  "مرورگر پنجرهٔ تازه را باز نکرد. دکمهٔ «باز کردن صفحهٔ پرداخت» را بزنید، پرداخت را کامل کنید و به این صفحه برگردید.",
                  "Your browser didn’t open a new window. Press “Open payment page”, finish paying, then come back here.",
                )
              : tr(
                  "یک پرداخت تومانی باز دارید. پس از پرداخت، تأیید چند دقیقه طول می‌کشد.",
                  "You have an open toman payment. After paying, confirmation can take a few minutes.",
                )}{" "}
            {tr("فیلترشکن باید خاموش باشد.", "Keep your VPN off.")}
          </span>
          <div className="row">
            {tomanWaiting.url && (
              <a className="btn secondary" href={tomanWaiting.url} target="_blank" rel="noopener noreferrer">
                {tr("باز کردن صفحهٔ پرداخت", "Open payment page")}
              </a>
            )}
            <button className="btn" disabled={checking} onClick={() => checkToman(tomanWaiting.order, true)}>
              {checking ? tr("در حال بررسی…", "Checking…") : tr("پرداخت کردم؛ بررسی کن", "I’ve paid — check now")}
            </button>
          </div>
        </div>
      )}
      {catalog?.stripeAvailable && catalog.stripeTestMode && (
        <p className="alert">
          {tr(
            "پرداخت دلاری در حالت آزمایشی است؛ از کارت واقعی استفاده نکنید.",
            "USD checkout is in test mode. Do not use a real card.",
          )}
        </p>
      )}
      {!catalog && (
        <p className="muted">{tr("در حال دریافت طرح‌ها…", "Loading plans…")}</p>
      )}
      <div className="purchase-grid">
        {catalog?.plans
          .filter((p) => p.code !== "trial")
          .map((plan) => (
            <article className="purchase-card" key={plan.id}>
              <h3>{plan.name}</h3>
              <p className="muted">
                {plan.durationDays} {tr("روز", "days")} · {plan.messageLimit}{" "}
                {tr("پیام", "messages")} · {plan.emailForwardLimit} {tr("ارسال ایمیل", "email forwards")}
              </p>
              <p className="small">
                {[
                  plan.sendEnabled ? tr("ارسال", "Sending") : null,
                  plan.receiveEnabled ? tr("دریافت", "Receiving") : null,
                ]
                  .filter(Boolean)
                  .join(" + ")}
              </p>
              <div className="plan-price">
                <span>USD</span>
                <strong>
                  {plan.priceUsdCents != null && plan.priceUsdCents > 0
                    ? usd(plan.priceUsdCents, lang)
                    : tr("قیمت مشخص نشده", "Price not set")}
                </strong>
              </div>
              <button
                className="btn full"
                disabled={
                  busy ||
                  !plan.isPurchasable ||
                  !catalog.stripeAvailable ||
                  !plan.priceUsdCents
                }
                onClick={() => buy(plan, "USD")}
              >
                {busy
                  ? tr("کمی صبر کنید…", "One moment…")
                  : tr("پرداخت دلاری با کارت", "Pay in USD by card")}
              </button>
              <div className="plan-price">
                <span>{tr("تومان", "Toman")}</span>
                <strong>
                  {plan.priceToman != null && plan.priceToman > 0
                    ? toman(plan.priceToman, lang)
                    : tr("قیمت مشخص نشده", "Price not set")}
                </strong>
              </div>
              <button
                className="btn secondary full"
                disabled={
                  busy ||
                  !plan.isPurchasable ||
                  !catalog.tomanAvailable ||
                  !plan.priceToman
                }
                onClick={() => buy(plan, "IRT")}
              >
                {busy
                  ? tr("کمی صبر کنید…", "One moment…")
                  : catalog.tomanAvailable
                    ? tr("پرداخت تومانی (کیف پول ایزی‌پی)", "Pay in toman (Ezpay wallet)")
                    : tr("پرداخت تومانی — به‌زودی", "Toman checkout — coming later")}
              </button>
              {catalog.tomanAvailable && plan.priceToman ? (
                <p className="hint">
                  {tr(
                    "صفحهٔ پرداخت در پنجرهٔ تازه باز می‌شود. فیلترشکن را خاموش کنید.",
                    "The payment page opens in a new window. Turn off your VPN first.",
                  )}
                </p>
              ) : null}
              {!plan.isPurchasable ? (
                <p className="hint">
                  {tr(
                    "فروش این طرح هنوز فعال نشده است.",
                    "This plan isn’t available for purchase yet.",
                  )}
                </p>
              ) : (
                !catalog.stripeAvailable && (
                  <p className="hint">
                    {tr(
                      "پرداخت دلاری هنوز متصل نشده است.",
                      "USD checkout isn’t connected yet.",
                    )}
                  </p>
                )
              )}
            </article>
          ))}
      </div>
      {catalog?.queuedPeriods ? (
        <p className="alert">
          {catalog.queuedPeriods}{" "}
          {tr(
            "دوره پرداخت‌شده در نوبت دارید.",
            "paid periods are queued for your account.",
          )}
        </p>
      ) : null}
      {catalog && (
        <p className="hint">
          {tr("ارائه‌شده توسط", "Provided by")} {catalog.tradingName} ·{" "}
          {catalog.legalName}
        </p>
      )}
      <h3>{tr("پرداخت‌های من", "My payments")}</h3>
      <OrderList orders={orders} lang={lang} />
    </section>
  );
}

export function PlanManager({
  plans,
  lang,
  busy,
  save,
}: {
  plans: Plan[];
  lang: Lang;
  busy: boolean;
  save: (id: string | null, plan: Omit<Plan, "id">) => void;
}) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en);
  return (
    <>
      <p className="muted">
        {tr(
          "قیمت‌ها مستقل هستند؛ دلار برای کارت و تومان برای کیف پول. تغییرات فقط روی خریدهای تازه اثر می‌گذارند.",
          "Set USD and toman prices independently. USD is for cards; toman is for the wallet. Changes apply only to new purchases.",
        )}
      </p>
      {plans.map((plan) => (
        <article className="plan-management" key={plan.id}>
          <div className="row between">
            <div>
              <strong>
                {plan.code === "trial"
                  ? tr("آزمایشی — رایگان", "Trial — free")
                  : plan.name}
              </strong>
              <p className="hint">
                {plan.durationDays} {tr("روز", "days")} · {plan.messageLimit}{" "}
                {tr("پیام", "messages")} · {plan.emailForwardLimit} {tr("ارسال ایمیل", "email forwards")}
              </p>
            </div>
            {plan.code !== "trial" && (
              <span className="badge">
                {plan.isPurchasable
                  ? tr("فروش فعال", "For sale")
                  : tr("فروش غیرفعال", "Not for sale")}
              </span>
            )}
          </div>
          {plan.code !== "trial" && (
            <>
              <p>
                {plan.priceUsdCents != null
                  ? usd(plan.priceUsdCents, lang)
                  : tr("بدون قیمت دلاری", "USD price not set")}{" "}
                ·{" "}
                {plan.priceToman != null
                  ? toman(plan.priceToman, lang)
                  : tr("بدون قیمت تومانی", "Toman price not set")}
              </p>
              <details>
                <summary>
                  {tr("ویرایش طرح و قیمت‌ها", "Edit plan & prices")}
                </summary>
                <PlanEditor
                  initial={plan}
                  lang={lang}
                  busy={busy}
                  save={(p) => save(plan.id, p)}
                />
              </details>
            </>
          )}
        </article>
      ))}
      <details className="plan-form">
        <summary>{tr("افزودن طرح", "Create a plan")}</summary>
        <PlanEditor lang={lang} busy={busy} save={(p) => save(null, p)} />
      </details>
    </>
  );
}

function PlanEditor({
  initial,
  lang,
  busy,
  save,
}: {
  initial?: Plan;
  lang: Lang;
  busy: boolean;
  save: (plan: Omit<Plan, "id">) => void;
}) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en);
  const [name, setName] = useState(initial?.name || "");
  const [code, setCode] = useState(initial?.code || "");
  const [days, setDays] = useState(initial?.durationDays || 30);
  const [limit, setLimit] = useState(initial?.messageLimit || 50);
  const [emailLimit, setEmailLimit] = useState(initial?.emailForwardLimit ?? 0);
  const [send, setSend] = useState(initial?.sendEnabled ?? true);
  const [receive, setReceive] = useState(initial?.receiveEnabled ?? true);
  const [dollars, setDollars] = useState(
    initial?.priceUsdCents != null
      ? (initial.priceUsdCents / 100).toFixed(2)
      : "",
  );
  const [tomanPrice, setTomanPrice] = useState(
    initial?.priceToman?.toString() || "",
  );
  const [published, setPublished] = useState(initial?.isPurchasable ?? false);
  const [invalid, setInvalid] = useState("");
  const suffix = initial?.id || "new";
  function submit(e: FormEvent) {
    e.preventDefault();
    setInvalid("");
    if (
      (dollars && !/^\d+(\.\d{1,2})?$/.test(dollars)) ||
      (tomanPrice && !/^\d+$/.test(tomanPrice))
    ) {
      setInvalid(
        tr(
          "قیمت دلاری حداکثر دو رقم اعشار دارد و تومان باید عدد صحیح باشد.",
          "Use up to two decimals for USD and a whole number for toman.",
        ),
      );
      return;
    }
    const [whole, fraction = ""] = dollars.split(".");
    const cents = dollars
      ? Number(whole) * 100 + Number(fraction.padEnd(2, "0"))
      : null;
    const tomanValue = tomanPrice ? Number(tomanPrice) : null;
    if (
      (cents != null && (cents < 50 || cents > 99999999)) ||
      (tomanValue != null && (tomanValue < 1 || tomanValue > 1e12)) ||
      (published && cents == null && tomanValue == null)
    ) {
      setInvalid(
        tr(
          "قیمت معتبر وارد کنید؛ برای فروش دست‌کم یک قیمت لازم است.",
          "Enter valid prices. At least one price is required to publish a plan.",
        ),
      );
      return;
    }
    save({
      name,
      code,
      durationDays: days,
      messageLimit: limit,
      emailForwardLimit: emailLimit,
      sendEnabled: send,
      receiveEnabled: receive,
      priceUsdCents: cents,
      priceToman: tomanValue,
      isPurchasable: published,
    });
  }
  return (
    <form onSubmit={submit}>
      <div className="form-grid">
        <div>
          <label htmlFor={"planName-" + suffix}>
            {tr("نام طرح", "Plan name")}
          </label>
          <input
            id={"planName-" + suffix}
            value={name}
            required
            maxLength={80}
            onChange={(e) => setName(e.target.value)}
          />
        </div>
        <div>
          <label htmlFor={"planCode-" + suffix}>
            {tr("شناسه کوتاه انگلیسی", "Short identifier")}
          </label>
          <input
            id={"planCode-" + suffix}
            value={code}
            disabled={!!initial}
            pattern="[a-z0-9\-]{2,30}"
            dir="ltr"
            required
            onChange={(e) => setCode(e.target.value)}
          />
        </div>
        <div>
          <label htmlFor={"days-" + suffix}>
            {tr("تعداد روز", "Duration in days")}
          </label>
          <input
            id={"days-" + suffix}
            type="number"
            min={1}
            max={3650}
            value={days}
            required
            onChange={(e) => setDays(+e.target.value)}
          />
        </div>
        <div>
          <label htmlFor={"limit-" + suffix}>
            {tr("ظرفیت مجموع پیام‌ها", "Total message allowance")}
          </label>
          <input
            id={"limit-" + suffix}
            type="number"
            min={1}
            max={1000000}
            value={limit}
            required
            onChange={(e) => setLimit(+e.target.value)}
          />
        </div>
        <div><label htmlFor={"emailLimit-"+suffix}>{tr("ظرفیت ارسال ایمیل", "Email forwarding allowance")}</label><input id={"emailLimit-"+suffix} type="number" min={0} max={1000000} required value={emailLimit} onChange={e=>setEmailLimit(+e.target.value)}/></div>
        <div>
          <label htmlFor={"usd-" + suffix}>
            {tr("قیمت به دلار آمریکا", "Price in USD")}
          </label>
          <input
            id={"usd-" + suffix}
            inputMode="decimal"
            value={dollars}
            placeholder="0.00"
            dir="ltr"
            onChange={(e) => setDollars(normalizeNumber(e.target.value))}
          />
        </div>
        <div>
          <label htmlFor={"toman-" + suffix}>
            {tr("قیمت به تومان", "Price in toman")}
          </label>
          <input
            id={"toman-" + suffix}
            inputMode="numeric"
            value={tomanPrice}
            placeholder={tr("عدد صحیح", "Whole amount")}
            dir="ltr"
            onChange={(e) => setTomanPrice(normalizeNumber(e.target.value))}
          />
        </div>
      </div>
      <p className="hint">
        {tr(
          "خالی گذاشتن یک قیمت یعنی خرید با آن روش در دسترس نیست. قیمت تومانی را به تومان وارد کنید، نه ریال.",
          "Leave a price empty to make that option unavailable. Enter the toman price in toman, not rial.",
        )}
      </p>
      <div className="row">
        <label className="check">
          <input
            type="checkbox"
            checked={send}
            onChange={(e) => setSend(e.target.checked)}
          />
          {tr("ارسال", "Sending")}
        </label>
        <label className="check">
          <input
            type="checkbox"
            checked={receive}
            onChange={(e) => setReceive(e.target.checked)}
          />
          {tr("دریافت", "Receiving")}
        </label>
        <label className="check">
          <input
            type="checkbox"
            checked={published}
            onChange={(e) => setPublished(e.target.checked)}
          />
          {tr("فعال برای خرید کاربران", "Available for customer purchase")}
        </label>
      </div>
      {invalid && (
        <p className="alert error" role="alert">
          {invalid}
        </p>
      )}
      <button className="btn" disabled={busy}>
        {tr("ذخیره طرح", "Save plan")}
      </button>
    </form>
  );
}

export function AdminPayments({ lang }: { lang: Lang }) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en);
  const [orders, setOrders] = useState<PaymentOrder[]>([]);
  const [error, setError] = useState("");
  const [catalog, setCatalog] = useState<BillingCatalog | null>(null);
  async function load() {
    try {
      const [o, c] = await Promise.all([
        api.adminPayments(),
        api.billingCatalog(),
      ]);
      setOrders(o);
      setCatalog(c);
    } catch (e) {
      setError(billingError(e, lang));
    }
  }
  useEffect(() => {
    void load();
  }, []);
  return (
    <section className="card">
      <div className="section-heading">
        <h2>{tr("پرداخت‌های کاربران", "Customer payments")}</h2>
        <button className="text-button" onClick={load}>
          {tr("به‌روزرسانی", "Refresh")}
        </button>
      </div>
      {error && <p className="alert error">{error}</p>}
      {catalog && (
        <p className="hint">
          {catalog.stripeAvailable
            ? catalog.stripeTestMode
              ? tr("Stripe: حالت آزمایشی", "Stripe: test mode")
              : tr("Stripe: فعال", "Stripe: connected")
            : tr(
                "Stripe هنوز متصل نشده است.",
                "Stripe isn’t connected yet.",
              )}{" "}
          ·{" "}
          {catalog.tomanAvailable
            ? tr("کیف پول تومانی: فعال", "Toman wallet: connected")
            : tr("کیف پول تومانی: غیرفعال", "Toman wallet: not active")}
        </p>
      )}
      <OrderList orders={orders} lang={lang} />
    </section>
  );
}
