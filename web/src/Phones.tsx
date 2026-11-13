import { utcDate } from "./format";
import { useEffect, useRef, useState } from "react";
import { api, type User, type LinkRequest, type PhoneAlert } from "./api";
import type { Lang } from "./i18n";
export function Phones({
  user,
  lang,
  showDevices,
  onChange,
}: {
  user: User;
  lang: Lang;
  showDevices: boolean;
  onChange: () => Promise<unknown>;
}) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en);
  const [links, setLinks] = useState<LinkRequest[]>([]),
    [alerts, setAlerts] = useState<PhoneAlert[]>([]);
  const [error, setError] = useState(""),
    [busy, setBusy] = useState(false);
  const notified = useRef(new Set<string>());
  const [permission, setPermission] = useState(
    typeof Notification !== "undefined" ? Notification.permission : "denied",
  );
  const label = (kind: string) =>
    ({
      offline: tr(
        "ارتباط گوشی بیش از ۳ دقیقه قطع است. علت می‌تواند اینترنت، خاموشی یا توقف برنامه باشد.",
        "Phone has not checked in for over 3 minutes. Check its network, power, and background app.",
      ),
      battery_low: tr(
        "باتری گوشی کمتر از ۲۱٪ است؛ گوشی را به شارژ بزنید.",
        "Phone battery is at 20% or lower. Connect a charger.",
      ),
      battery_critical: tr(
        "باتری گوشی در وضعیت بحرانی است: ۱۰٪ یا کمتر.",
        "Phone battery is critical: 10% or lower.",
      ),
      network_unverified: tr(
        "اینترنت گوشی تأیید نشده است؛ شبکه را بررسی کنید.",
        "The phone reports an unverified internet connection.",
      ),
      sms_permission: tr(
        "مجوز ارسال یا دریافت پیامک روی گوشی داده نشده است.",
        "Allow sending and receiving SMS on the phone.",
      ),
      battery_optimization: tr(
        "محدودیت باتری ممکن است برنامه را متوقف کند.",
        "Battery restrictions may stop the app in the background.",
      ),
    })[kind] || kind;
  useEffect(() => {
    let stopped = false;
    async function refresh() {
      try {
        const [l, a] = await Promise.all([
          api.pendingLinks(),
          api.phoneAlerts(),
        ]);
        if (stopped) return;
        setLinks(l);
        setAlerts(a);
        setError("");
      } catch {
        if (!stopped)
          setError(
            tr(
              "وضعیت گوشی به‌روز نشد؛ دوباره بررسی می‌کنیم.",
              "Could not refresh phone status. Retrying.",
            ),
          );
      }
    }
    refresh();
    const timer = setInterval(refresh, 8000);
    return () => {
      stopped = true;
      clearInterval(timer);
    };
  }, [user.id]);
  useEffect(() => {
    if (permission !== "granted") return;
    for (const a of alerts)
      if (!a.resolvedAt && !a.readAt && !notified.current.has(a.id)) {
        notified.current.add(a.id);
        new Notification("Darvazeh · " + a.deviceName, {
          body: label(a.kind),
          tag: a.id,
          icon: "/favicon.svg",
        });
      }
  }, [alerts, permission, lang]);
  async function decide(link: LinkRequest, approve: boolean) {
    setBusy(true);
    setError("");
    try {
      await api.decideLink(link.id, approve, link.comparisonCode);
      setLinks(await api.pendingLinks());
      await onChange();
    } catch {
      setError(
        tr(
          "درخواست انجام نشد؛ شاید اعتبار آن پایان یافته است.",
          "Could not update the request. It may have expired.",
        ),
      );
    } finally {
      setBusy(false);
    }
  }
  return (
    <>
      {error && (
        <p className="alert error" role="alert">
          {error}
        </p>
      )}
      {links.map((l) => (
        <section
          className="card phone-request"
          key={l.id}
          aria-label={tr("درخواست اتصال گوشی", "Phone connection request")}
        >
          <p className="eyebrow">
            {tr("درخواست اتصال تازه", "NEW PHONE CONNECTION")}
          </p>
          <h2>{l.deviceName}</h2>
          <p>
            {tr(
              "تنها اگر خودتان این درخواست را فرستاده‌اید و کد زیر با کد روی گوشی یکسان است، تأیید کنید.",
              "Approve only if you requested this connection and this code matches the one shown on your phone.",
            )}
          </p>
          <strong className="pair-code" dir="ltr">
            {l.comparisonCode}
          </strong>
          <p className="hint">
            {tr("اعتبار تا", "Expires")}{" "}
            {utcDate(l.expiresAt).toLocaleTimeString(
              lang === "fa" ? "fa-IR" : "en-NZ",
            )}
          </p>
          <div className="row">
            <button
              className="btn"
              disabled={busy}
              onClick={() => decide(l, true)}
            >
              {tr("کد یکسان است؛ تأیید اتصال", "Codes match · approve phone")}
            </button>
            <button
              className="btn secondary"
              disabled={busy}
              onClick={() => decide(l, false)}
            >
              {tr("رد درخواست", "Reject")}
            </button>
          </div>
        </section>
      ))}
      {alerts.some((a) => !a.resolvedAt && !a.readAt) && (
        <details className="health-notices" open={showDevices || alerts.some(a=>!a.resolvedAt && !a.readAt && a.kind==="battery_critical")}>
          <summary>
            {alerts.filter((a) => !a.resolvedAt && !a.readAt).length}{" "}
            {tr(
              "هشدار وضعیت گوشی؛ بررسی کنید",
              alerts.filter((a) => !a.resolvedAt && !a.readAt).length === 1
                ? "phone health alert to review"
                : "phone health alerts to review",
            )}
          </summary>
          {alerts
            .filter((a) => !a.resolvedAt && !a.readAt)
            .map((a) => (
              <section className="alert phone-alert" key={a.id} role="status">
                <strong>{a.deviceName}</strong>
                <p>{label(a.kind)}</p>
                <button
                  className="text-button"
                  onClick={async () => {
                    try {
                      await api.readAlert(a.id);
                      setAlerts(await api.phoneAlerts());
                      setError("");
                    } catch {
                      setError(tr("هشدار بسته نشد؛ دوباره تلاش کنید.", "Could not dismiss this alert. Please try again."));
                    }
                  }}
                >
                  {tr("متوجه شدم", "Dismiss")}
                </button>
              </section>
            ))}
        </details>
      )}
      {showDevices && (
        <section className="card">
          <h2>{tr("گوشی‌های ثبت‌شده", "Registered phones")}</h2>
          <details
            className="phone-setup"
            open={!(user.devices || []).some((d) => !d.revokedAt)}
          >
            <summary>
              {tr(
                (user.devices || []).length
                  ? "اتصال گوشی دیگر"
                  : "اتصال اولین گوشی",
                (user.devices || []).length
                  ? "Connect another phone"
                  : "Connect your first phone",
              )}
            </summary>
            <p className="muted">
              {tr(
                "در برنامه اندروید ایمیل همین حساب را وارد کنید؛ سپس درخواست بالا را در مرورگر تأیید کنید. روی گوشی کد ورود لازم نیست.",
                "Enter this account’s email in the Android app, then approve its request here. No sign-in code is needed on the phone.",
              )}
            </p>
            <a className="btn" href="/Darvazeh-SMS.apk?v=2.2.1" download>
              {tr("دریافت برنامه اندروید", "Download Android app")}
            </a>
            {permission === "default" && (
              <button
                className="btn secondary"
                onClick={async () =>
                  setPermission(await Notification.requestPermission())
                }
              >
                {tr(
                  "فعال‌سازی اعلان وضعیت گوشی",
                  "Enable phone health notifications",
                )}
              </button>
            )}
            <p className="hint">
              {tr(
                "بررسی گوشی حدود هر ۱۲ ثانیه انجام می‌شود؛ پس از ۳ دقیقه بی‌خبری هشدار ثبت می‌شود. اعلان مرورگر هنگام باز بودن این صفحه کار می‌کند.",
                "The phone checks in about every 12 seconds; an alert is recorded after 3 minutes without contact. Browser notifications work while this page is open.",
              )}
            </p>
          </details>
          {(user.devices || []).map((d) => (
            <article className="device-card" key={d.id}>
              <div className="row between">
                <h3>{d.deviceModel || d.name}</h3>
                <span className="badge">
                  {d.revokedAt
                    ? tr("قطع‌شده", "Disconnected")
                    : d.adminPausedAt
                      ? tr("متوقف‌شده توسط مدیر", "Paused by administrator")
                      : d.isOnline
                        ? tr("متصل", "Connected")
                        : tr("بدون ارتباط تازه", "No recent contact")}
                </span>
              </div>
              <p>
                {tr("باتری", "Battery")}: {d.batteryLevel ?? "—"}%{" "}
                {d.isCharging ? tr("در حال شارژ", "Charging") : ""} ·{" "}
                {tr("شبکه", "Network")}:{" "}
                {d.networkType || tr("نامشخص", "Unknown")}
              </p>
              <p className="hint">
                {tr("آخرین ارتباط", "Last contact")}:{" "}
                {d.lastSeenAt
                  ? utcDate(d.lastSeenAt).toLocaleString(
                      lang === "fa" ? "fa-IR" : "en-NZ",
                    )
                  : "—"}
                <br />
                {tr("زمان گزارش باتری", "Battery reported")}:{" "}
                {d.healthReportedAt
                  ? utcDate(d.healthReportedAt).toLocaleString(
                      lang === "fa" ? "fa-IR" : "en-NZ",
                    )
                  : tr("نیازمند نسخه تازه برنامه", "Update the Android app")}
              </p>
              <button
                className="text-button danger"
                disabled={busy}
                onClick={async () => {
                  setBusy(true);
                  try {
                    if (
                      !window.confirm(
                        tr(
                          "اتصال این گوشی لغو شود؟ برای اتصال دوباره باید درخواست گوشی را تأیید کنید.",
                          "Disconnect this phone? Reconnecting will require approval again.",
                        ),
                      )
                    )
                      return;
                    await api.removeDevice(d.id);
                    await onChange();
                  } catch {
                    setError(
                      tr(
                        "قطع ارتباط انجام نشد.",
                        "Could not disconnect phone.",
                      ),
                    );
                  } finally {
                    setBusy(false);
                  }
                }}
              >
                {tr("قطع ارتباط گوشی", "Disconnect phone")}
              </button>
            </article>
          ))}
          <h3>{tr("رویدادهای وضعیت گوشی", "Phone health history")}</h3>
          {alerts.slice(0, 20).map((a) => (
            <p className="hint" key={a.id}>
              {a.deviceName} · {label(a.kind)} ·{" "}
              {utcDate(a.createdAt).toLocaleString(
                lang === "fa" ? "fa-IR" : "en-NZ",
              )}{" "}
              ·{" "}
              {a.resolvedAt
                ? tr("برطرف‌شده", "Resolved")
                : tr("فعال", "Active")}
            </p>
          ))}
        </section>
      )}
    </>
  );
}
