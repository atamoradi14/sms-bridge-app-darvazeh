import { formatDateTime } from "./format";
import { useEffect, useState } from "react";
import {
  api,
  type User,
  type Message,
  type Stats,
  type AdminUser,
  type Activity,
} from "./api";
import type { Lang } from "./i18n";
import { Icon } from "./WorkspaceShell";
import type { Screen } from "./workspaceNavigation";
export function Overview({
  user,
  lang,
  onNavigate,
}: {
  user: User;
  lang: Lang;
  onNavigate: (screen: Screen) => void;
}) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en),
    n = (v: number) =>
      new Intl.NumberFormat(lang === "fa" ? "fa-IR" : "en").format(v),
    s = user.subscription;
  const phones = (user.devices || []).filter((d) => !d.revokedAt),
    online = phones.filter((d) => d.isOnline && !d.adminPausedAt),
    phone = online[0] || phones[0];
  const [messages, setMessages] = useState<Message[]>([]),
    [loading, setLoading] = useState(true),
    [error, setError] = useState(false);
  useEffect(() => {
    let active = true;
    api
      .messagePage("", "", 1, 10)
      .then((r) => {
        if (active) {
          setMessages(r.items.slice(0, 4));
          setError(false);
        }
      })
      .catch(() => {
        if (active) setError(true);
      })
      .finally(() => {
        if (active) setLoading(false);
      });
    return () => {
      active = false;
    };
  }, [user.id, s.usedMessages]);
  const next: Screen = !s.active
    ? "membership"
    : !phones.length
      ? "device"
      : "send";
  return (
    <>
      <div className="overview-metrics">
        <Metric
          icon="inbox"
          label={tr("پیام باقی‌مانده", "Messages remaining")}
          value={n(s.remainingMessages)}
          detail={tr(
            `از ${n(s.messageLimit)} پیام در این دوره`,
            `of ${n(s.messageLimit)} in this period`,
          )}
        />
        <Metric
          icon="phone"
          label={tr("گوشی‌های متصل", "Connected phones")}
          value={n(online.length)}
          detail={
            phones.length
              ? tr(
                  `${n(phones.length)} گوشی ثبت‌شده`,
                  `${n(phones.length)} registered ${phones.length === 1 ? "phone" : "phones"}`,
                )
              : tr("اولین گوشی را وصل کنید", "Connect your first phone")
          }
        />
        <Metric
          icon="mail"
          label={tr("ارسال ایمیل باقی‌مانده", "Email forwards remaining")}
          value={n(s.remainingEmailForwards)}
          detail={tr(
            "نسخه ایمیلی پیام‌های دریافتی",
            "Email copies of incoming messages",
          )}
        />
      </div>
      <div className="overview-columns">
        <section className="card connection-card">
          <div className="section-heading">
            <div>
              <p className="eyebrow">{tr("ارتباط شما", "YOUR CONNECTION")}</p>
              <h2>
                {phone
                  ? phone.deviceModel || phone.name
                  : tr(
                      "از گوشی شما، به هر کجا",
                      "From your phone, to wherever you are",
                    )}
              </h2>
            </div>
            <span className={"badge " + (online.length ? "green" : "")}>
              {online.length
                ? tr("متصل", "Connected")
                : phones.length
                  ? tr("نیازمند بررسی", "Needs attention")
                  : tr("آماده اتصال", "Ready to connect")}
            </span>
          </div>
          {phone ? (
            <>
              <div className="phone-vitals">
                <div>
                  <span>{tr("باتری", "Battery")}</span>
                  <strong>
                    {phone.batteryLevel == null
                      ? "—"
                      : n(phone.batteryLevel) + "%"}
                  </strong>
                  <progress
                    aria-label={tr("سطح باتری", "Battery level")}
                    max={100}
                    value={phone.batteryLevel || 0}
                  />
                  <small>
                    {phone.isCharging
                      ? tr("در حال شارژ", "Charging")
                      : tr("با باتری گوشی", "On battery")}
                  </small>
                </div>
                <div>
                  <span>{tr("شبکه", "Network")}</span>
                  <strong>
                    {(
                      {
                        wifi: tr("وای‌فای", "Wi-Fi"),
                        cellular: tr("داده همراه", "Mobile data"),
                        offline: tr("بدون اتصال", "Offline"),
                      } as Record<string, string>
                    )[phone.networkType || ""] ||
                      phone.networkType ||
                      "—"}
                  </strong>
                  <small>
                    {online.length
                      ? tr(
                          "آخرین گزارش گوشی: متصل",
                          "Latest phone report: connected",
                        )
                      : tr(
                          "اینترنت و اجرای برنامه را بررسی کنید",
                          "Check the internet and background app",
                        )}
                  </small>
                </div>
              </div>
              <button
                className="text-button arrow-link"
                onClick={() => onNavigate("device")}
              >
                {tr("مشاهده وضعیت گوشی‌ها", "View phone status")}
                <Icon name="arrow" />
              </button>
            </>
          ) : (
            <>
              <p className="muted">
                {tr(
                  "برنامه دروازه را روی گوشی نصب کنید، ایمیل همین حساب را وارد کنید و اتصال را اینجا تأیید کنید.",
                  "Install Darvazeh on your Android phone, enter this account’s email and approve the connection here.",
                )}
              </p>
              <ol className="setup-steps">
                <li>
                  <span>01</span>
                  {tr("نصب برنامه اندروید", "Install the Android app")}
                </li>
                <li>
                  <span>02</span>
                  {tr("ورود ایمیل روی گوشی", "Enter your email on the phone")}
                </li>
                <li>
                  <span>03</span>
                  {tr("تأیید اتصال در دروازه", "Approve the connection here")}
                </li>
              </ol>
              <button
                className="btn secondary"
                onClick={() => onNavigate("device")}
              >
                {tr("اتصال اولین گوشی", "Connect your first phone")}
                <Icon name="arrow" />
              </button>
            </>
          )}
        </section>
        <section className="card allowance-card">
          <p className="eyebrow">{tr("اشتراک شما", "YOUR MEMBERSHIP")}</p>
          <h2>
            {s.planCode === "trial"
              ? tr("مهمان دروازه", "Darvazeh trial")
              : s.planName}
          </h2>
          <span className={"badge " + (s.active ? "green" : "")}>
            {!user.emailVerified
              ? tr("در انتظار تأیید ایمیل", "Awaiting verification")
              : s.active
                ? tr("فعال", "Active")
                : tr("غیرفعال", "Inactive")}
          </span>
          <div className="allowance-value">
            <strong>{n(s.usedMessages)}</strong>
            <span> / {n(s.messageLimit)}</span>
          </div>
          <p>
            {tr("پیام استفاده‌شده در این دوره", "messages used in this period")}
          </p>
          <progress
            aria-label={tr("مصرف اشتراک", "Message allowance used")}
            value={Math.min(s.usedMessages, s.messageLimit)}
            max={Math.max(s.messageLimit, 1)}
          />
          <p className="hint">
            {tr(
              "ظرفیت ارسال و دریافت مشترک است. هر دوره فقط یک بار خریداری می‌شود.",
              "Sending and receiving share this allowance. Each purchase covers one period.",
            )}
          </p>
          <button
            className="text-button arrow-link"
            onClick={() => onNavigate(next)}
          >
            {next === "membership"
              ? tr("بررسی اشتراک و طرح‌ها", "Review plans & allowance")
              : next === "device"
                ? tr("آماده‌سازی گوشی", "Set up your phone")
                : tr("نوشتن پیام تازه", "Compose a message")}
            <Icon name="arrow" />
          </button>
        </section>
      </div>
      <section className="card">
        <div className="section-heading">
          <div>
            <p className="eyebrow">{tr("صندوق شما", "YOUR INBOX")}</p>
            <h2>{tr("پیام‌های تازه", "Recent messages")}</h2>
          </div>
          <button
            className="text-button arrow-link"
            onClick={() => onNavigate("inbox")}
          >
            {tr("همه پیام‌ها", "All messages")}
            <Icon name="arrow" />
          </button>
        </div>
        {loading ? (
          <p className="hint" role="status">
            {tr("در حال دریافت پیام‌ها…", "Loading your messages…")}
          </p>
        ) : error ? (
          <div className="empty-state compact">
            <p>
              {tr(
                "پیام‌های تازه بارگیری نشدند. برای بررسی دوباره، صندوق پیام‌ها را باز کنید.",
                "Recent messages could not be loaded. Open Messages to try again.",
              )}
            </p>
            <button
              className="btn secondary"
              onClick={() => onNavigate("inbox")}
            >
              {tr("باز کردن پیام‌ها", "Open messages")}
            </button>
          </div>
        ) : messages.length ? (
          messages.map((m) => (
            <button
              className="recent-message"
              key={m.id}
              onClick={() => onNavigate("inbox")}
            >
              <span className={"message-direction " + m.direction}>
                <Icon name={m.direction === "inbound" ? "inbox" : "send"} />
              </span>
              <span className="recent-copy">
                <strong>
                  <bdi>{m.peerNumber}</bdi>
                </strong>
                <span dir="auto">{m.body}</span>
              </span>
              <span className="badge">
                {m.direction === "inbound"
                  ? tr("دریافتی", "Received")
                  : tr("ارسالی", "Outgoing")}
              </span>
            </button>
          ))
        ) : (
          <div className="empty-state compact">
            <span className="empty-icon">
              <Icon name="inbox" />
            </span>
            <h3>
              {tr(
                "اولین پیام شما از اینجا شروع می‌شود",
                "Your first conversation starts here",
              )}
            </h3>
            <p>
              {tr(
                "پس از اتصال گوشی، پیام‌های دریافتی و ارسالی اینجا نمایش داده می‌شوند.",
                "Once your phone is connected, incoming and outgoing messages appear here.",
              )}
            </p>
            <button
              className="btn secondary"
              onClick={() => onNavigate(phones.length ? "send" : "device")}
            >
              {phones.length
                ? tr("نوشتن پیام", "Compose a message")
                : tr("اتصال گوشی", "Connect a phone")}
            </button>
          </div>
        )}
      </section>
    </>
  );
}
function Metric({
  icon,
  label,
  value,
  detail,
}: {
  icon: string;
  label: string;
  value: string;
  detail: string;
}) {
  return (
    <div className="metric-card">
      <div className="metric-label">
        <span>{label}</span>
        <Icon name={icon} />
      </div>
      <strong>{value}</strong>
      <small>{detail}</small>
    </div>
  );
}
export function AdminOverview({
  lang,
  stats,
  users,
  activities,
  onNavigate,
  onPeople,
  activityLabel,
}: {
  lang: Lang;
  stats: Stats | null;
  users: AdminUser[];
  activities: Activity[];
  onNavigate: (screen: Screen) => void;
  onPeople: (status: string) => void;
  activityLabel: (s: string) => string;
}) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en),
    n = (v: number) =>
      new Intl.NumberFormat(lang === "fa" ? "fa-IR" : "en").format(v),
    suspended = users.filter((u) => u.suspendedAt).length,
    unverified = users.filter((u) => !u.emailVerified && !u.suspendedAt).length;
  return (
    <>
      <div className="overview-metrics admin-metrics">
        {stats ? (
          [
            [tr("کاربران", "People"), stats.users, "people"],
            [
              tr("اشتراک فعال", "Active memberships"),
              stats.activeSubscriptions,
              "plan",
            ],
            [tr("ارسال ناموفق", "Failed messages"), stats.failed, "activity"],
          ].map(([label, v, icon]) => (
            <Metric
              key={String(label)}
              label={String(label)}
              value={n(Number(v))}
              icon={String(icon)}
              detail={tr("گزارش ثبت‌شده سامانه", "Recorded account activity")}
            />
          ))
        ) : (
          <p role="status">
            {tr("در حال بارگیری گزارش…", "Loading overview…")}
          </p>
        )}
      </div>
      <div className="overview-columns">
        <section className="card">
          <p className="eyebrow">{tr("پیگیری امروز", "NEEDS ATTENTION")}</p>
          <h2>
            {tr("چیزهایی که باید بدانید", "A clear view of your workspace")}
          </h2>
          <button
            className="attention-row"
            onClick={() => onPeople("unverified")}
          >
            <span>
              {tr(
                "حساب‌های در انتظار تأیید ایمیل",
                "Accounts awaiting email verification",
              )}
            </span>
            <strong>{n(unverified)}</strong>
            <Icon name="arrow" />
          </button>
          <button
            className="attention-row"
            onClick={() => onPeople("suspended")}
          >
            <span>{tr("حساب‌های تعلیق‌شده", "Suspended accounts")}</span>
            <strong>{n(suspended)}</strong>
            <Icon name="arrow" />
          </button>
          <button
            className="attention-row"
            onClick={() => onNavigate("admin-delivery")}
          >
            <span>
              {tr(
                "بررسی صف و خطاهای ایمیل",
                "Check the email queue and errors",
              )}
            </span>
            <Icon name="arrow" />
          </button>
        </section>
        <section className="card">
          <p className="eyebrow">{tr("جریان پیام‌ها", "MESSAGE ACTIVITY")}</p>
          <h2>{tr("ارسال و دریافت", "Sending & receiving")}</h2>
          <div className="delivery-totals">
            {[
              [tr("دریافتی", "Received"), stats?.received],
              [tr("ارسال‌شده", "Sent"), stats?.sent],
              [tr("در صف", "Queued"), stats?.queued],
            ].map(([label, v]) => (
              <div key={String(label)}>
                <strong>{v == null ? "—" : n(Number(v))}</strong>
                <span>{label}</span>
              </div>
            ))}
          </div>
          <p className="hint">
            {tr(
              "وضعیت تحویل ایمیل را از بخش «وضعیت ارسال ایمیل» دنبال کنید؛ پذیرش ارسال به معنای رسیدن به صندوق گیرنده نیست.",
              "Track email submission separately in Email delivery. Acceptance does not confirm arrival in a recipient’s inbox.",
            )}
          </p>
        </section>
      </div>
      <section className="card">
        <div className="section-heading">
          <h2>{tr("آخرین رویدادها", "Latest activity")}</h2>
          <button
            className="text-button arrow-link"
            onClick={() => onNavigate("admin-activity")}
          >
            {tr("همه رویدادها", "View activity log")}
            <Icon name="arrow" />
          </button>
        </div>
        {activities.length ? (
          activities.slice(0, 6).map((a) => (
            <div className="activity-row" key={a.id}>
              <div>
                <strong>{activityLabel(a.action)}</strong>
                <small>
                  <bdi>{a.email}</bdi>
                </small>
              </div>
              <time className="muted small" dateTime={a.createdAt}>
                {formatDateTime(a.createdAt, lang)}
              </time>
            </div>
          ))
        ) : (
          <p className="hint">
            {tr("هنوز رویدادی ثبت نشده است.", "No activity recorded yet.")}
          </p>
        )}
      </section>
    </>
  );
}
