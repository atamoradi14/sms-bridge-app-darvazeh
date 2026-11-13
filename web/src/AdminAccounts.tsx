import { useEffect, useId, useState } from "react";
import { api, RequestError, type AdminUser, type Device } from "./api";
import type { Lang } from "./i18n";
import { MessageHistory } from "./MessageHistory";
function problem(e: unknown, lang: Lang) {
  const code = (e as RequestError).code;
  return lang === "fa"
    ? code?.startsWith("password_")
      ? "رمز باید ۹ تا ۱۲۸ نویسه و با تکرار آن یکسان باشد."
      : code === "approval_required"
        ? "گوشی لغوشده به تأیید دوباره کاربر نیاز دارد. حساب باید فعال و تأییدشده باشد."
        : code === "account_unavailable"
          ? "این ایمیل یا نام کاربری در دسترس نیست."
          : "درخواست انجام نشد. اطلاعات و دسترسی را بررسی کنید."
    : code?.startsWith("password_")
      ? "Use 9–128 characters and matching passwords."
      : code === "approval_required"
        ? "Revoked phones need fresh owner approval. The account must be active and verified."
        : code === "account_unavailable"
          ? "That email or username is unavailable."
          : "Could not complete the request. Check your details and access.";
}
export function AdminSecurity({ lang, email }: { lang: Lang; email?: string }) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en),
    id = useId();
  const [otp, setOtp] = useState(false),
    [password, setPassword] = useState(""),
    [ready, setReady] = useState(false),
    [busy, setBusy] = useState(false),
    [error, setError] = useState(""),
    [notice, setNotice] = useState("");
  useEffect(() => {
    api
      .adminSecurity()
      .then((r) => {
        setOtp(r.adminRequireOtp);
        setReady(true);
      })
      .catch(() =>
        setError(tr("تنظیمات بارگیری نشد.", "Could not load settings.")),
      );
  }, []);
  return (
    <section className="card narrow">
      <h2>{tr("امنیت ورود مدیر", "Administrator login security")}</h2>
      <p className="hint">
        {tr(
          "وقتی این گزینه خاموش است، ایمیل و رمز برای ورود مدیر کافی است. روشن کردن آن، کد ایمیل را برای ورودهای بعدی الزامی می‌کند.",
          "When off, email and password are enough for administrator sign-in. Enabling it requires an email code on future sign-ins.",
        )}
      </p>
      <form
        onSubmit={async (e) => {
          e.preventDefault();
          setBusy(true);
          setError("");
          setNotice("");
          try {
            await api.setAdminSecurity(otp, password);
            setPassword("");
            setNotice(tr("تنظیمات ورود ذخیره شد.", "Login policy saved."));
          } catch (e) {
            setError(problem(e, lang));
          } finally {
            setBusy(false);
          }
        }}
      >
        {email && (
          <input
            type="hidden"
            name="username"
            autoComplete="username"
            value={email}
          />
        )}
        <label className="check">
          <input
            type="checkbox"
            disabled={!ready || busy}
            checked={otp}
            onChange={(e) => setOtp(e.target.checked)}
          />
          {tr(
            "الزام کد ایمیل برای ورود مدیر",
            "Require email OTP for administrator login",
          )}
        </label>
        <label htmlFor={id}>
          {tr(
            "رمز فعلی مدیر برای تأیید تغییر",
            "Current administrator password to confirm",
          )}
        </label>
        <input
          id={id}
          required
          type="password"
          autoComplete="current-password"
          dir="ltr"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />
        <button className="btn" disabled={!ready || busy}>
          {tr("ذخیره تنظیمات امنیتی", "Save security settings")}
        </button>
      </form>
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
    </section>
  );
}
export function AdminCreateUser({
  lang,
  onChange,
}: {
  lang: Lang;
  onChange: () => Promise<void>;
}) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en),
    id = useId();
  const [open, setOpen] = useState(false),
    [email, setEmail] = useState(""),
    [password, setPassword] = useState(""),
    [confirm, setConfirm] = useState(""),
    [busy, setBusy] = useState(false),
    [error, setError] = useState("");
  return (
    <section className="card narrow">
      <button
        className="btn secondary"
        onClick={() => setOpen(!open)}
        aria-expanded={open}
      >
        {tr("ساخت کاربر جدید", "Create a user")}
      </button>
      {open && (
        <form
          onSubmit={async (e) => {
            e.preventDefault();
            setBusy(true);
            setError("");
            try {
              await api.createUser(email, password, confirm);
              setPassword("");
              setConfirm("");
              setEmail("");
              await onChange();
              setOpen(false);
            } catch (e) {
              setError(problem(e, lang));
            } finally {
              setBusy(false);
            }
          }}
        >
          <p className="hint">
            {tr(
              "حساب ساخته می‌شود؛ دوره آزمایشی پس از تأیید ایمیل توسط کاربر شروع خواهد شد. سپس می‌توانید طرح را از کارت کاربر تغییر دهید.",
              "The account is created immediately. Its trial starts after the user verifies their email. You can assign a plan from their account card.",
            )}
          </p>
          <label htmlFor={id + "email"}>
            {tr("ایمیل کاربر", "User email")}
          </label>
          <input
            id={id + "email"}
            type="email"
            dir="ltr"
            required
            value={email}
            onChange={(e) => setEmail(e.target.value)}
          />
          <label htmlFor={id + "password"}>
            {tr("رمز اولیه", "Initial password")}
          </label>
          <input
            id={id + "password"}
            type="password"
            dir="ltr"
            required
            minLength={9}
            maxLength={256}
            autoComplete="new-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
          <label htmlFor={id + "confirm"}>
            {tr("تکرار رمز اولیه", "Confirm initial password")}
          </label>
          <input
            id={id + "confirm"}
            type="password"
            dir="ltr"
            required
            minLength={9}
            maxLength={256}
            autoComplete="new-password"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
          />
          <button className="btn" disabled={busy}>
            {tr("ساخت حساب کاربر", "Create user account")}
          </button>
          {error && (
            <p className="alert error" role="alert">
              {error}
            </p>
          )}
        </form>
      )}
    </section>
  );
}
export function AdminAccountControls({
  user,
  lang,
  busy,
  update,
}: {
  user: AdminUser;
  lang: Lang;
  busy: boolean;
  update: (fn: () => Promise<unknown>) => void;
}) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en),
    id = useId();
  const [open, setOpen] = useState(false),
    [email, setEmail] = useState(user.email),
    [username, setUsername] = useState(user.username || ""),
    [password, setPassword] = useState(""),
    [confirm, setConfirm] = useState(""),
    [devices, setDevices] = useState<Device[]>([]),
    [error, setError] = useState(""),
    [messages, setMessages] = useState(false);
  async function load() {
    try {
      setDevices(await api.adminDevices(user.id));
    } catch (e) {
      setError(problem(e, lang));
    }
  }
  useEffect(() => {
    if (open) load();
  }, [open, user.id, user.suspendedAt]);
  return (
    <div className="admin-account-controls">
      <p className="hint">
        {user.suspendedAt
          ? tr("حساب تعلیق شده", "Account suspended")
          : user.emailVerified
            ? tr("ایمیل تأیید شده", "Email verified")
            : tr("ایمیل در انتظار تأیید", "Awaiting email verification")}
      </p>
      <div className="row">
        <button
          className="text-button"
          onClick={() => setOpen(!open)}
          aria-expanded={open}
        >
          {tr("ویرایش و مدیریت گوشی‌ها", "Edit account and manage phones")}
        </button>
        <button
          className="text-button"
          onClick={() => setMessages(!messages)}
          aria-expanded={messages}
        >
          {tr("مشاهده پیام‌ها", "View messages")}
        </button>
        {user.role !== "Admin" && (
          <>
            <button
              className="text-button"
              disabled={busy}
              onClick={() => {
                if (
                  confirmAction(
                    tr(
                      user.suspendedAt
                        ? "حساب دوباره فعال شود؟"
                        : "حساب تعلیق و پیام‌های در صف لغو شوند؟",
                      user.suspendedAt
                        ? "Resume this account?"
                        : "Suspend this account and cancel queued outgoing messages?",
                    ),
                  )
                )
                  update(() => api.suspendUser(user.id, !user.suspendedAt));
              }}
            >
              {user.suspendedAt
                ? tr("رفع تعلیق", "Resume account")
                : tr("تعلیق حساب", "Suspend account")}
            </button>
          </>
        )}
      </div>
      {open && (
        <div className="account-editor">
          {user.role !== "Admin" && (
            <form
              onSubmit={(e) => {
                e.preventDefault();
                update(async () => {
                  await api.modifyUser(
                    user.id,
                    email,
                    username,
                    password || undefined,
                    confirm || undefined,
                  );
                  setPassword("");
                  setConfirm("");
                });
              }}
            >
              <label htmlFor={id + "email"}>{tr("ایمیل", "Email")}</label>
              <input
                id={id + "email"}
                type="email"
                autoComplete="username"
                required
                dir="ltr"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
              />
              <p className="hint">
                {tr(
                  "تغییر ایمیل، تأیید دوباره و اتصال دوباره گوشی‌ها را لازم می‌کند و نشست‌ها را می‌بندد.",
                  "Changing email requires verification and phone approval again, and signs out existing sessions.",
                )}
              </p>
              <label htmlFor={id + "username"}>
                {tr("نام کاربری اختیاری", "Optional username")}
              </label>
              <input
                id={id + "username"}
                dir="ltr"
                value={username}
                maxLength={30}
                onChange={(e) => setUsername(e.target.value)}
              />
              <label htmlFor={id + "password"}>
                {tr(
                  "رمز جدید؛ خالی بگذارید تا تغییر نکند",
                  "New password; leave blank to keep current",
                )}
              </label>
              <input
                id={id + "password"}
                type="password"
                dir="ltr"
                autoComplete="new-password"
                minLength={9}
                maxLength={256}
                value={password}
                onChange={(e) => setPassword(e.target.value)}
              />
              <label htmlFor={id + "confirm"}>
                {tr("تکرار رمز جدید", "Confirm new password")}
              </label>
              <input
                id={id + "confirm"}
                type="password"
                dir="ltr"
                autoComplete="new-password"
                required={!!password}
                value={confirm}
                onChange={(e) => setConfirm(e.target.value)}
              />
              <button className="btn secondary" disabled={busy}>
                {tr("ذخیره تغییرات کاربر", "Save user changes")}
              </button>
            </form>
          )}
          <h3>{tr("گوشی‌های ثبت‌شده", "Registered phones")}</h3>
          <p className="hint">
            {tr(
              "توقف، اتصال این گوشی را غیرفعال و ارسال‌های در صف را لغو می‌کند. ازسرگیری، اتصال ثبت‌شده را مجاز می‌کند؛ آنلاین شدن به اینترنت گوشی وابسته است. گوشی لغوشده به تأیید دوباره نیاز دارد.",
              "Disconnect pauses this phone and cancels queued sends. Reconnect allows the existing registration to communicate again; the phone still needs internet. Revoked phones need fresh owner approval.",
            )}
          </p>
          {devices.length === 0 && (
            <p>{tr("گوشی ثبت نشده است.", "No registered phones.")}</p>
          )}
          {devices.map((d) => (
            <div className="row between device-admin-row" key={d.id}>
              <div>
                <strong>{d.name}</strong>
                <p className="hint">
                  {d.revokedAt
                    ? tr(
                        "لغوشده؛ تأیید دوباره لازم است",
                        "Revoked; fresh approval needed",
                      )
                    : d.adminPausedAt
                      ? tr("متوقف‌شده توسط مدیر", "Paused by administrator")
                      : tr("اتصال مجاز", "Connection allowed")}{" "}
                  · {d.batteryLevel ?? "—"}%
                </p>
              </div>
              <button
                className="btn secondary"
                disabled={busy || !!d.revokedAt || !!user.suspendedAt}
                onClick={() => {
                  if (
                    confirmAction(
                      tr(
                        d.adminPausedAt
                          ? "اتصال این گوشی مجاز شود؟"
                          : "اتصال این گوشی متوقف و ارسال‌های در صف لغو شوند؟",
                        d.adminPausedAt
                          ? "Allow this phone to reconnect?"
                          : "Pause this phone and cancel queued sends?",
                      ),
                    )
                  )
                    update(async () => {
                      await api.setDeviceConnection(
                        user.id,
                        d.id,
                        !!d.adminPausedAt,
                      );
                      await load();
                    });
                }}
              >
                {d.adminPausedAt
                  ? tr("اتصال دوباره", "Reconnect")
                  : tr("قطع اتصال", "Disconnect")}
              </button>
            </div>
          ))}
          {error && (
            <p className="alert error" role="alert">
              {error}
            </p>
          )}
        </div>
      )}
      {user.role !== "Admin" && (
        <details className="danger-zone">
          <summary>{tr("حذف حساب", "Delete account")}</summary>
          <p className="hint">
            {tr(
              "دسترسی حساب بسته و متن پیام‌ها پاک می‌شود. سوابق مالی و حسابرسی نگهداری خواهند شد.",
              "Closes account access and removes message content. Billing and audit records are retained.",
            )}
          </p>
          <button
            className="text-button danger"
            disabled={busy}
            onClick={() => {
              if (
                confirmAction(
                  tr(
                    "حساب حذف شود؟ ورود و گوشی‌ها لغو و متن پیام‌ها پاک می‌شوند. سوابق پرداخت، مصرف و حسابرسی نگهداری می‌شوند.",
                    "Delete this account? Sign-in and phones are revoked and message contents removed. Billing, usage and audit records are retained.",
                  ),
                )
              )
                update(() => api.deleteUser(user.id));
            }}
          >
            {tr("حذف کاربر", "Delete user")}
          </button>
        </details>
      )}
      {messages && <MessageHistory lang={lang} userId={user.id} />}
    </div>
  );
}
function confirmAction(message: string) {
  return window.confirm(message);
}
