import { useEffect, useRef, type ReactNode } from "react";
import type { Lang } from "./i18n";
import type { User } from "./api";
import { screenInfo, type Screen } from "./workspaceNavigation";
export function Icon({ name }: { name: string }) {
  const paths: Record<string, ReactNode> = {
    home: (
      <>
        <path d="m3 10 9-7 9 7v10H3Z" />
        <path d="M9 20v-7h6v7" />
      </>
    ),
    inbox: (
      <>
        <rect x="3" y="4" width="18" height="16" rx="3" />
        <path d="M3 13h5l2 3h4l2-3h5M8 8h8" />
      </>
    ),
    send: (
      <>
        <path d="m21 3-7 18-4-7-7-4Z" />
        <path d="m10 14 6-6" />
      </>
    ),
    phone: (
      <>
        <rect x="6" y="2" width="12" height="20" rx="3" />
        <path d="M10 5h4M11 18h2" />
      </>
    ),
    plan: (
      <>
        <rect x="4" y="3" width="16" height="18" rx="3" />
        <path d="M8 8h8M8 12h8M8 16h4" />
      </>
    ),
    people: (
      <>
        <circle cx="9" cy="8" r="3" />
        <path d="M3 21v-3a6 6 0 0 1 12 0v3M16 5a3 3 0 0 1 0 6M18 15c2 0 3 2 3 4v2" />
      </>
    ),
    payment: (
      <>
        <rect x="2" y="5" width="20" height="14" rx="3" />
        <path d="M2 10h20M6 15h3" />
      </>
    ),
    wallet: (
      <>
        <path d="M20 8V5a2 2 0 0 0-2-2H5a3 3 0 0 0-3 3v12a3 3 0 0 0 3 3h15V8H5a2 2 0 0 1 0-4" />
        <path d="M20 12h-5v5h5" />
      </>
    ),
    shield: (
      <>
        <path d="m12 2 8 4v6c0 5-8 10-8 10S4 17 4 12V6Z" />
        <path d="m8 12 3 3 5-6" />
      </>
    ),
    mail: (
      <>
        <rect x="2" y="4" width="20" height="16" rx="3" />
        <path d="m2 6 10 8L22 6" />
      </>
    ),
    activity: (
      <>
        <path d="M2 12h5l3-8 4 16 3-8h5" />
      </>
    ),
    settings: (
      <>
        <path d="M4 6h16M4 12h16M4 18h16" />
        <circle cx="8" cy="6" r="2" />
        <circle cx="16" cy="12" r="2" />
        <circle cx="10" cy="18" r="2" />
      </>
    ),
    vpn: (
      <>
        <circle cx="12" cy="12" r="9" />
        <path d="M3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18" />
      </>
    ),
    menu: <path d="M4 6h16M4 12h16M4 18h16" />,
    close: <path d="m6 6 12 12M18 6 6 18" />,
    arrow: <path d="M5 12h14m-5-5 5 5-5 5" />,
    logout: (
      <>
        <path d="M10 4H4v16h6M13 8l4 4-4 4M8 12h13" />
      </>
    ),
  };
  return (
    <svg
      className="ui-icon"
      width="20"
      height="20"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.6"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      {paths[name] || paths.home}
    </svg>
  );
}
export function WorkspaceShell({
  user,
  lang,
  screen,
  onNavigate,
  onLogout,
  onLanguage,
  children,
}: {
  user: User;
  lang: Lang;
  screen: Screen;
  onNavigate: (next: Screen) => void;
  onLogout: () => void;
  onLanguage: (lang: Lang) => void;
  children: ReactNode;
}) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en),
    dialog = useRef<HTMLDialogElement>(null),
    admin = screen.startsWith("admin"),
    info = screenInfo(screen, lang);
  const groups: { label: string; items: Screen[] }[] = admin
    ? [
        {
          label: tr("مدیریت", "OPERATIONS"),
          items: ["admin", "admin-users", "admin-activity"],
        },
        {
          label: tr("اشتراک و درآمد", "COMMERCE"),
          items: ["admin-plans", "admin-vpn", "admin-payments", "admin-wallet"],
        },
        {
          label: tr("سامانه", "SYSTEM"),
          items: ["admin-delivery", "admin-security"],
        },
      ]
    : [
        {
          label: tr("فضای شما", "WORKSPACE"),
          items: ["overview", "inbox", "send", "device"],
        },
        {
          label: tr("سرویس‌ها", "SERVICES"),
          items: ["vpn"],
        },
        {
          label: tr("حساب شما", "YOUR ACCOUNT"),
          items: ["membership", "settings"],
        },
      ];
  function go(next: Screen) {
    dialog.current?.close();
    onNavigate(next);
  }
  useEffect(() => {
    document.getElementById("workspace-title")?.focus({ preventScroll: true });
  }, [screen]);
  const navigation = (
    <>
      <div className="workspace-brand">
        <img src="/darvazeh-mark.svg" alt="" />
        <div>
          <strong>{tr("دروازه", "Darvazeh")}</strong>
          <small>
            {tr("خانه‌ای برای ارتباطات شما", "A home for your connections")}
          </small>
        </div>
      </div>
      {user.role === "Admin" && (
        <div
          className="space-switch"
          aria-label={tr("انتخاب فضای کاری", "Choose workspace")}
        >
          <button
            className={!admin ? "selected" : ""}
            aria-pressed={!admin}
            onClick={() => go("overview")}
          >
            {tr("حساب من", "My workspace")}
          </button>
          <button
            className={admin ? "selected" : ""}
            aria-pressed={admin}
            onClick={() => go("admin")}
          >
            {tr("مدیریت", "Administration")}
          </button>
        </div>
      )}
      <nav aria-label={tr("منوی اصلی", "Main navigation")}>
        {groups.map((g) => (
          <div className="nav-group" key={g.label}>
            <p>{g.label}</p>
            {g.items.map((key) => (
              <button
                key={key}
                className={screen === key ? "nav-item current" : "nav-item"}
                aria-current={screen === key ? "page" : undefined}
                onClick={() => go(key)}
              >
                <Icon name={screenInfo(key, lang).icon} />
                <span>{screenInfo(key, lang).title}</span>
                {key === "inbox" && <small aria-hidden="true">SMS</small>}
              </button>
            ))}
          </div>
        ))}
      </nav>
      <div className="sidebar-bottom">
        <div className="sidebar-account">
          <span className="account-avatar">
            {user.email.slice(0, 1).toUpperCase()}
          </span>
          <div>
            <strong>
              {tr(
                admin ? "مدیر دروازه" : "حساب شخصی",
                admin ? "Darvazeh administrator" : "Personal account",
              )}
            </strong>
            <bdi>{user.email}</bdi>
          </div>
        </div>
        <div className="sidebar-tools">
          <div className="lang-switch" aria-label={tr("زبان", "Language")}>
            <button
              className={lang === "fa" ? "selected" : ""}
              onClick={() => onLanguage("fa")}
            >
              فارسی
            </button>
            <button
              className={lang === "en" ? "selected" : ""}
              onClick={() => onLanguage("en")}
            >
              English
            </button>
          </div>
          <button
            className="icon-button"
            aria-label={tr("خروج از حساب", "Sign out")}
            onClick={onLogout}
          >
            <Icon name="logout" />
          </button>
        </div>
      </div>
    </>
  );
  return (
    <div className="workspace" data-space={admin ? "admin" : "personal"}>
      <a className="skip-link" href="#workspace-main">
        {tr("رفتن به محتوا", "Skip to content")}
      </a>
      <aside className="workspace-sidebar">{navigation}</aside>
      <dialog
        id="workspace-navigation-dialog"
        className="mobile-navigation"
        ref={dialog}
        aria-label={tr("منوی حساب", "Account menu")}
      >
        <button
          className="icon-button close-menu"
          aria-label={tr("بستن منو", "Close menu")}
          onClick={() => dialog.current?.close()}
        >
          <Icon name="close" />
        </button>
        {navigation}
      </dialog>
      <div className="workspace-body">
        <header className="workspace-topbar">
          <button
            className="icon-button menu-trigger"
            aria-haspopup="dialog"
            aria-controls="workspace-navigation-dialog"
            aria-label={tr("باز کردن منو", "Open menu")}
            onClick={() => dialog.current?.showModal()}
          >
            <Icon name="menu" />
          </button>
          <div className="workspace-breadcrumb">
            <span>
              {tr(
                admin ? "فضای مدیریت" : "فضای شخصی",
                admin ? "Administration" : "My workspace",
              )}
            </span>
            <span aria-hidden="true">/</span>
            <strong>{info.title}</strong>
          </div>
          <button
            className="topbar-account"
            onClick={() => go("settings")}
            aria-label={tr("حساب و تنظیمات", "Account & settings")}
          >
            <span className="account-avatar">
              {user.email[0].toUpperCase()}
            </span>
            <bdi>{user.email}</bdi>
          </button>
        </header>
        <main id="workspace-main" className="workspace-main">
          <div className="page-heading">
            <div>
              <p className="eyebrow">
                {tr(
                  admin ? "مدیریت دروازه" : "همراه شما، از هر کجا",
                  admin
                    ? "DARVAZEH ADMINISTRATION"
                    : "CONNECTED, WHEREVER YOU ARE",
                )}
              </p>
              <h1 id="workspace-title" tabIndex={-1}>
                {info.title}
              </h1>
              <p>{info.description}</p>
            </div>
            {!admin && user.emailVerified && screen !== "send" && (
              <button className="btn" onClick={() => go("send")}>
                <Icon name="send" />
                {tr("پیام تازه", "New message")}
              </button>
            )}
            {admin && (
              <button
                className="btn secondary"
                onClick={() => go("admin-users")}
              >
                <Icon name="people" />
                {tr("مدیریت کاربران", "Manage people")}
              </button>
            )}
          </div>
          {children}
        </main>
        <footer className="workspace-footer">
          <span>Darvazeh · {tr("پیوندها می‌مانند.", "Connections stay.")}</span>
          <span>
            {tr(
              admin ? "فضای مدیریت" : "فضای شخصی",
              admin ? "Administrator workspace" : "Personal workspace",
            )}
          </span>
        </footer>
      </div>
    </div>
  );
}
