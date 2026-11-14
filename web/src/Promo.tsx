import { useEffect, useState, type FormEvent } from "react";
import { api, RequestError, type PromoCode, type PromoCodeInput, type PromoPrice, type PromoQuote } from "./api";
import type { Lang } from "./i18n";

const usd = (cents: number, lang: Lang) =>
  new Intl.NumberFormat(lang === "fa" ? "fa-IR" : "en-US", { style: "currency", currency: "USD", currencyDisplay: "narrowSymbol" }).format(cents / 100);
const toman = (value: number, lang: Lang) =>
  new Intl.NumberFormat(lang === "fa" ? "fa-IR" : "en-US").format(value) + (lang === "fa" ? " تومان" : " toman");
const digits = (value: string) =>
  value.replace(/[۰-۹٠-٩]/g, (c) => String(c.charCodeAt(0) - (c >= "۰" ? 1776 : 1632))).replace(/[٬,\s]/g, "").replace(/٫/g, ".");

const promoMessages: Record<string, [string, string]> = {
  promo_invalid: ["کد تخفیف معتبر نیست.", "That promo code isn’t valid."],
  promo_expired: ["مهلت استفاده از این کد تخفیف تمام شده است.", "This promo code has expired."],
  promo_not_started: ["زمان استفاده از این کد تخفیف هنوز نرسیده است.", "This promo code isn’t active yet."],
  promo_used_up: ["ظرفیت این کد تخفیف تمام شده است.", "This promo code has been fully used."],
  promo_user_limit: ["شما قبلاً از این کد تخفیف استفاده کرده‌اید.", "You’ve already used this promo code."],
  promo_wrong_product: ["این کد تخفیف برای این سرویس نیست.", "This promo code isn’t for this service."],
  promo_wrong_currency: ["این کد تخفیف برای این روش پرداخت نیست.", "This promo code doesn’t apply to this payment method."],
};

/** Readable text for a promo code error, or null when the error is about something else. */
export function promoError(error: unknown, lang: Lang): string | null {
  const e = error as RequestError;
  if (e?.status === 429 && !e.code) return lang === "fa" ? "تلاش‌های زیادی انجام شد. چند دقیقه بعد دوباره امتحان کنید." : "Too many attempts. Please try again in a few minutes.";
  const m = e?.code ? promoMessages[e.code] : undefined;
  return m ? m[lang === "fa" ? 0 : 1] : null;
}

/** "Have a promo code?" box. Prices come from the server; the code is applied again at checkout. */
export function PromoField({ product, lang, value, onChange, disabled }: {
  product: "sms" | "vpn"; lang: Lang; value: PromoQuote | null; onChange: (q: PromoQuote | null) => void; disabled?: boolean;
}) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en);
  const [open, setOpen] = useState(false);
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  async function apply(e: FormEvent) {
    e.preventDefault();
    const c = code.trim();
    if (!c) return;
    setBusy(true); setError("");
    try { onChange(await api.promoQuote(c, product)); setOpen(false); }
    catch (err) { onChange(null); setError(promoError(err, lang) ?? tr("بررسی کد انجام نشد. دوباره تلاش کنید.", "Couldn’t check the code. Please try again.")); }
    finally { setBusy(false); }
  }
  if (value)
    return (
      <div className="promo-applied" role="status">
        <span>
          {tr("کد تخفیف ", "Promo code ")}<strong dir="ltr">{value.code}</strong>
          {value.discountType === "percent" ? " · " + new Intl.NumberFormat(lang === "fa" ? "fa-IR" : "en-US").format(value.discountPercent) + tr("٪ تخفیف", "% off") : tr(" اعمال شد", " applied")}
          {value.description ? " — " + value.description : ""}
        </span>
        <button type="button" className="text-button" disabled={disabled} onClick={() => { onChange(null); setCode(""); }}>{tr("حذف", "Remove")}</button>
      </div>
    );
  if (!open)
    return <button type="button" className="text-button promo-toggle" disabled={disabled} onClick={() => setOpen(true)}>{tr("کد تخفیف دارید؟", "Have a promo code?")}</button>;
  return (
    <form className="promo-form" onSubmit={apply}>
      <input value={code} dir="ltr" maxLength={32} autoFocus autoCapitalize="characters" spellCheck={false}
        placeholder={tr("کد تخفیف", "Promo code")} aria-label={tr("کد تخفیف", "Promo code")}
        onChange={(e) => setCode(e.target.value.toUpperCase().replace(/\s/g, ""))} />
      <button className="btn secondary" disabled={busy || disabled || code.trim().length < 3}>{busy ? tr("در حال بررسی…", "Checking…") : tr("اعمال", "Apply")}</button>
      <button type="button" className="text-button" onClick={() => { setOpen(false); setError(""); }}>{tr("انصراف", "Cancel")}</button>
      {error && <p className="alert error promo-error" role="alert">{error}</p>}
    </form>
  );
}

/** A price, struck through with the discounted price beside it when a promo applies. */
export function PromoPriceText({ price, original, currency, lang }: { price: PromoPrice | null | undefined; original: number; currency: "USD" | "IRT"; lang: Lang }) {
  const f = (v: number) => (currency === "USD" ? usd(v, lang) : toman(v, lang));
  if (!price || price.discount <= 0) return <>{f(original)}</>;
  return (
    <span className="promo-price">
      <s>{f(price.original)}</s>{" "}
      <strong>{price.final === 0 ? (lang === "fa" ? "رایگان" : "Free") : f(price.final)}</strong>
    </span>
  );
}

const emptyForm = {
  code: "", description: "", product: "all" as PromoCode["product"], discountType: "percent" as PromoCode["discountType"],
  percent: "10", offToman: "", offUsd: "", maxUses: "", maxPerUser: "1", startsAt: "", expiresAt: "", isActive: true,
};
type Form = typeof emptyForm;
const localInput = (iso: string | null) => {
  if (!iso) return "";
  const d = new Date(iso);
  return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
};
const toForm = (p: PromoCode): Form => ({
  code: p.code, description: p.description, product: p.product, discountType: p.discountType,
  percent: p.discountType === "percent" ? String(p.discountPercent) : "10",
  offToman: p.amountOffToman != null ? String(p.amountOffToman) : "", offUsd: p.amountOffUsdCents != null ? (p.amountOffUsdCents / 100).toFixed(2) : "",
  maxUses: p.maxUses != null ? String(p.maxUses) : "", maxPerUser: p.maxUsesPerUser != null ? String(p.maxUsesPerUser) : "",
  startsAt: localInput(p.startsAt), expiresAt: localInput(p.expiresAt), isActive: p.isActive,
});

function PromoEditor({ initial, lang, busy, save }: { initial?: PromoCode; lang: Lang; busy: boolean; save: (p: PromoCodeInput) => Promise<boolean> }) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en);
  const [f, setF] = useState<Form>(initial ? toForm(initial) : emptyForm);
  const [invalid, setInvalid] = useState("");
  const id = initial?.id || "new";
  const set = <K extends keyof Form>(k: K, v: Form[K]) => setF((x) => ({ ...x, [k]: v }));
  const whole = (v: string) => (v.trim() === "" ? null : /^\d+$/.test(v) ? Number(v) : NaN);
  async function submit(e: FormEvent) {
    e.preventDefault();
    setInvalid("");
    const percent = Number(f.percent);
    const offToman = whole(f.offToman);
    const offUsd = f.offUsd.trim() === "" ? null : /^\d+(\.\d{1,2})?$/.test(f.offUsd) ? Math.round(Number(f.offUsd) * 100) : NaN;
    const maxUses = whole(f.maxUses), maxPerUser = whole(f.maxPerUser);
    const bad = (msg: [string, string]) => { setInvalid(tr(...msg)); return; };
    if (!/^[A-Za-z0-9_-]{3,32}$/.test(f.code.trim())) return bad(["کد باید ۳ تا ۳۲ حرف انگلیسی، عدد، خط تیره یا زیرخط باشد.", "Use 3–32 letters, digits, dashes or underscores."]);
    if (f.discountType === "percent" && !(Number.isInteger(percent) && percent >= 1 && percent <= 100)) return bad(["درصد تخفیف باید عددی بین ۱ تا ۱۰۰ باشد.", "The percentage must be a whole number from 1 to 100."]);
    if (f.discountType === "fixed" && (Number.isNaN(offToman) || Number.isNaN(offUsd) || (offToman == null && offUsd == null) || offToman === 0 || offUsd === 0))
      return bad(["مبلغ تخفیف تومانی (عدد صحیح) یا دلاری را وارد کنید.", "Enter a toman amount (whole number) and/or a USD amount off."]);
    if (Number.isNaN(maxUses) || maxUses === 0 || Number.isNaN(maxPerUser) || maxPerUser === 0) return bad(["تعداد استفاده باید عدد صحیح مثبت باشد یا خالی بماند.", "Use limits must be positive whole numbers, or empty for no limit."]);
    if (f.startsAt && f.expiresAt && new Date(f.expiresAt) <= new Date(f.startsAt)) return bad(["تاریخ پایان باید بعد از تاریخ شروع باشد.", "The end date must be after the start date."]);
    const ok = await save({
      code: f.code.trim().toUpperCase(), description: f.description.trim(), product: f.product, discountType: f.discountType,
      discountPercent: f.discountType === "percent" ? percent : 0,
      amountOffToman: f.discountType === "fixed" ? offToman : null, amountOffUsdCents: f.discountType === "fixed" ? offUsd : null,
      maxUses, maxUsesPerUser: maxPerUser,
      startsAt: f.startsAt ? new Date(f.startsAt).toISOString() : null, expiresAt: f.expiresAt ? new Date(f.expiresAt).toISOString() : null,
      isActive: f.isActive,
    });
    if (ok && !initial) setF(emptyForm);
  }
  return (
    <form onSubmit={submit}>
      <div className="form-grid">
        <div>
          <label htmlFor={"promoCode-" + id}>{tr("کد", "Code")}</label>
          <input id={"promoCode-" + id} value={f.code} dir="ltr" maxLength={32} required placeholder="NOWRUZ1405"
            onChange={(e) => set("code", e.target.value.toUpperCase().replace(/\s/g, ""))} />
        </div>
        <div>
          <label htmlFor={"promoProduct-" + id}>{tr("سرویس", "Service")}</label>
          <select id={"promoProduct-" + id} value={f.product} onChange={(e) => set("product", e.target.value as Form["product"])}>
            <option value="all">{tr("هر دو سرویس", "Both services")}</option>
            <option value="sms">{tr("فقط پیامک‌رسان", "SMS forwarder only")}</option>
            <option value="vpn">{tr("فقط فیلترشکن", "VPN only")}</option>
          </select>
        </div>
        <div>
          <label htmlFor={"promoType-" + id}>{tr("نوع تخفیف", "Discount type")}</label>
          <select id={"promoType-" + id} value={f.discountType} onChange={(e) => set("discountType", e.target.value as Form["discountType"])}>
            <option value="percent">{tr("درصدی", "Percentage")}</option>
            <option value="fixed">{tr("مبلغ ثابت", "Fixed amount")}</option>
          </select>
        </div>
        {f.discountType === "percent" ? (
          <div>
            <label htmlFor={"promoPercent-" + id}>{tr("درصد تخفیف (۱۰۰ = رایگان)", "Percent off (100 = free)")}</label>
            <input id={"promoPercent-" + id} inputMode="numeric" dir="ltr" value={f.percent} onChange={(e) => set("percent", digits(e.target.value))} />
          </div>
        ) : (
          <>
            <div>
              <label htmlFor={"promoToman-" + id}>{tr("تخفیف تومانی", "Toman off")}</label>
              <input id={"promoToman-" + id} inputMode="numeric" dir="ltr" value={f.offToman} placeholder={tr("خالی = ندارد", "empty = none")} onChange={(e) => set("offToman", digits(e.target.value))} />
            </div>
            <div>
              <label htmlFor={"promoUsd-" + id}>{tr("تخفیف دلاری", "USD off")}</label>
              <input id={"promoUsd-" + id} inputMode="decimal" dir="ltr" value={f.offUsd} placeholder="0.00" onChange={(e) => set("offUsd", digits(e.target.value))} />
            </div>
          </>
        )}
        <div>
          <label htmlFor={"promoMax-" + id}>{tr("حداکثر کل استفاده", "Total uses")}</label>
          <input id={"promoMax-" + id} inputMode="numeric" dir="ltr" value={f.maxUses} placeholder={tr("خالی = نامحدود", "empty = unlimited")} onChange={(e) => set("maxUses", digits(e.target.value))} />
        </div>
        <div>
          <label htmlFor={"promoPer-" + id}>{tr("استفادهٔ هر کاربر", "Uses per member")}</label>
          <input id={"promoPer-" + id} inputMode="numeric" dir="ltr" value={f.maxPerUser} placeholder={tr("خالی = نامحدود", "empty = unlimited")} onChange={(e) => set("maxPerUser", digits(e.target.value))} />
        </div>
        <div>
          <label htmlFor={"promoStart-" + id}>{tr("شروع (اختیاری)", "Starts (optional)")}</label>
          <input id={"promoStart-" + id} type="datetime-local" value={f.startsAt} onChange={(e) => set("startsAt", e.target.value)} />
        </div>
        <div>
          <label htmlFor={"promoEnd-" + id}>{tr("انقضا (اختیاری)", "Expires (optional)")}</label>
          <input id={"promoEnd-" + id} type="datetime-local" value={f.expiresAt} onChange={(e) => set("expiresAt", e.target.value)} />
        </div>
        <div className="span-2">
          <label htmlFor={"promoDesc-" + id}>{tr("توضیح برای کاربر (اختیاری)", "Note shown to members (optional)")}</label>
          <input id={"promoDesc-" + id} value={f.description} maxLength={200} onChange={(e) => set("description", e.target.value)} />
        </div>
      </div>
      <p className="hint">
        {tr("تخفیف روی قیمت طرح در لحظهٔ پرداخت حساب می‌شود. اگر مبلغ پس از تخفیف کمتر از حداقل درگاه باشد (۱٬۰۰۰ تومان یا ۰٫۵۰ دلار)، همان حداقل گرفته می‌شود؛ تخفیف ۱۰۰٪ بدون پرداخت فعال می‌شود. ساعت‌ها به وقت مرورگر شماست.",
          "The discount is taken off the plan price at checkout. If the result is below the payment minimum (1,000 toman or $0.50), the minimum is charged; a 100% discount activates without payment. Times are in your browser’s time zone.")}
      </p>
      <label className="check">
        <input type="checkbox" checked={f.isActive} onChange={(e) => set("isActive", e.target.checked)} />
        {tr("فعال", "Active")}
      </label>
      {invalid && <p className="alert error" role="alert">{invalid}</p>}
      <button className="btn" disabled={busy}>{initial ? tr("ذخیرهٔ کد", "Save code") : tr("ساخت کد تخفیف", "Create promo code")}</button>
    </form>
  );
}

/** Owner page: create and edit promo codes, with uses and discount given so far. */
export function AdminPromos({ lang }: { lang: Lang }) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en);
  const n = (v: number) => new Intl.NumberFormat(lang === "fa" ? "fa-IR" : "en-US").format(v);
  const [codes, setCodes] = useState<PromoCode[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  async function load() {
    try { setCodes(await api.adminPromos()); } catch (e) { setError((e as Error).message); }
  }
  useEffect(() => { void load(); }, []);
  async function save(id: string | null, p: PromoCodeInput) {
    setBusy(true); setError(""); setNotice("");
    try {
      if (id) await api.updatePromo(id, p); else await api.createPromo(p);
      setNotice(tr("کد تخفیف ذخیره شد.", "Promo code saved."));
      await load();
      return true;
    } catch (e) { setError((e as Error).message); return false; }
    finally { setBusy(false); }
  }
  const statusLabel: Record<PromoCode["status"], [string, string]> = {
    active: ["فعال", "Active"], inactive: ["غیرفعال", "Inactive"], expired: ["منقضی", "Expired"], scheduled: ["زمان‌بندی‌شده", "Scheduled"], used_up: ["تمام‌شده", "Used up"],
  };
  const productLabel: Record<PromoCode["product"], [string, string]> = { all: ["هر دو سرویس", "Both services"], sms: ["پیامک‌رسان", "SMS forwarder"], vpn: ["فیلترشکن", "VPN"] };
  const when = (iso: string) => new Date(iso).toLocaleString(lang === "fa" ? "fa-IR" : "en-GB", { dateStyle: "medium", timeStyle: "short" });
  return (
    <section className="card">
      <div className="section-heading">
        <h2>{tr("کدهای تخفیف", "Promo codes")}</h2>
        <button className="text-button" onClick={load}>{tr("به‌روزرسانی", "Refresh")}</button>
      </div>
      <p className="muted">{tr("کاربران کد را هنگام خرید طرح پیامک‌رسان یا تمدید فیلترشکن وارد می‌کنند. تغییرات فقط روی خریدهای تازه اثر دارد.", "Members enter a code when buying an SMS plan or renewing the VPN. Changes apply to new checkouts only.")}</p>
      {error && <p className="alert error" role="alert">{error}</p>}
      {notice && <p className="alert success" role="status">{notice}</p>}
      {!codes && !error && <p className="muted">{tr("در حال بارگذاری…", "Loading…")}</p>}
      {codes?.length === 0 && <p className="hint">{tr("هنوز کد تخفیفی نساخته‌اید.", "No promo codes yet.")}</p>}
      {codes?.map((c) => (
        <article className="plan-management" key={c.id}>
          <div className="row between">
            <div>
              <strong dir="ltr">{c.code}</strong>
              <p className="hint">
                {c.discountType === "percent" ? n(c.discountPercent) + tr("٪ تخفیف", "% off")
                  : [c.amountOffToman ? toman(c.amountOffToman, lang) : null, c.amountOffUsdCents ? usd(c.amountOffUsdCents, lang) : null].filter(Boolean).join(" / ") + tr(" تخفیف", " off")}
                {" · "}{tr(...productLabel[c.product])}
                {c.description ? " · " + c.description : ""}
              </p>
            </div>
            <span className={"badge" + (c.status === "active" ? " ok" : "")}>{tr(...statusLabel[c.status])}</span>
          </div>
          <p className="small">
            {tr("استفاده: ", "Used: ")}<strong>{n(c.used)}</strong>{c.maxUses != null ? " / " + n(c.maxUses) : tr(" (نامحدود)", " (no limit)")}
            {c.holding > 0 && <> · {tr("در حال پرداخت: ", "in checkout: ")}{n(c.holding)}</>}
            {" · "}{tr("هر کاربر: ", "per member: ")}{c.maxUsesPerUser != null ? n(c.maxUsesPerUser) : tr("نامحدود", "no limit")}
            {c.startsAt && <> · {tr("شروع: ", "starts: ")}{when(c.startsAt)}</>}
            {" · "}{c.expiresAt ? tr("انقضا: ", "expires: ") + when(c.expiresAt) : tr("بدون انقضا", "no expiry")}
            {(c.discountToman > 0 || c.discountUsdCents > 0) && <> · {tr("مجموع تخفیف: ", "discount given: ")}{[c.discountToman ? toman(c.discountToman, lang) : null, c.discountUsdCents ? usd(c.discountUsdCents, lang) : null].filter(Boolean).join(" + ")}</>}
          </p>
          <details>
            <summary>{tr("ویرایش کد", "Edit code")}</summary>
            <PromoEditor initial={c} lang={lang} busy={busy} save={(p) => save(c.id, p)} />
          </details>
        </article>
      ))}
      <details className="plan-form" open={codes?.length === 0}>
        <summary>{tr("ساخت کد تخفیف", "Create a promo code")}</summary>
        <PromoEditor lang={lang} busy={busy} save={(p) => save(null, p)} />
      </details>
    </section>
  );
}
