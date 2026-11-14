import type { Lang } from "./i18n";
export type Screen =
  | "overview"
  | "inbox"
  | "send"
  | "device"
  | "settings"
  | "membership"
  | "vpn"
  | "admin-vpn"
  | "admin"
  | "admin-users"
  | "admin-plans"
  | "admin-payments"
  | "admin-wallet"
  | "admin-security"
  | "admin-delivery"
  | "admin-activity";
const pages: Record<Screen, [string, string, string, string, string]> = {
  overview: [
    "نمای کلی",
    "Overview",
    "وضعیت گوشی، پیام‌ها و حساب شما در یک نگاه.",
    "Your phone, messages and account, at a glance.",
    "home",
  ],
  inbox: [
    "پیام‌ها",
    "Messages",
    "پیام‌های دریافتی و ارسالی را پیدا و مدیریت کنید.",
    "Find and manage your received and sent messages.",
    "inbox",
  ],
  send: [
    "نوشتن پیام",
    "Compose",
    "یک پیام تازه، از سیم‌کارت گوشی شما.",
    "A new message, sent from your phone’s SIM.",
    "send",
  ],
  device: [
    "گوشی‌ها و اتصال",
    "Phones & connection",
    "گوشی‌های متصل، درخواست‌های اتصال و وضعیت سلامت.",
    "Connected phones, connection requests and health.",
    "phone",
  ],
  membership: [
    "اشتراک و پرداخت‌ها",
    "Plan & payments",
    "ظرفیت باقی‌مانده، انتخاب طرح و سابقه پرداخت.",
    "Your allowance, plan options and payment history.",
    "plan",
  ],
  vpn: [
    "فیلترشکن با آیپی ایران",
    "VPN with an Iran IP",
    "پیکربندی شخصی، کد QR، مصرف و تمدید ماهانه.",
    "Your personal configuration, QR code, usage and monthly renewal.",
    "vpn",
  ],
  "admin-vpn": [
    "فروش VPN",
    "VPN service",
    "قیمت، حجم آزمایشی و ماهانه، سرورها و مشترکان VPN.",
    "Pricing, trial and monthly data, servers and VPN subscribers.",
    "vpn",
  ],
  settings: [
    "حساب و تنظیمات",
    "Account & settings",
    "تنظیمات ارسال ایمیل و امنیت حساب شما.",
    "Email forwarding preferences and account security.",
    "settings",
  ],
  admin: [
    "نمای کلی مدیریت",
    "Administration overview",
    "وضعیت کاربران، پیام‌ها و موارد نیازمند توجه.",
    "Account activity, message delivery and what needs attention.",
    "home",
  ],
  "admin-users": [
    "کاربران و دسترسی‌ها",
    "People & access",
    "حساب‌ها، گوشی‌ها و دسترسی‌های هر کاربر را مدیریت کنید.",
    "Manage accounts, registered phones and member permissions.",
    "people",
  ],
  "admin-plans": [
    "طرح‌ها و قیمت‌ها",
    "Plans & pricing",
    "مدت، ظرفیت و قیمت طرح‌های اشتراک.",
    "Set membership periods, allowances and prices.",
    "plan",
  ],
  "admin-payments": [
    "پرداخت‌ها",
    "Payments",
    "سابقه پرداخت و وضعیت خرید کاربران.",
    "Customer payments and purchase status.",
    "payment",
  ],
  "admin-wallet": [
    "کیف پول تومانی",
    "Toman wallet",
    "کیف پول دریافت پرداخت‌ها و خدمات پذیرندگی.",
    "Your receiving wallet and merchant services.",
    "wallet",
  ],
  "admin-security": [
    "امنیت مدیریت",
    "Admin security",
    "روش ورود مدیر و الزام تأیید ایمیل.",
    "Administrator sign-in policy and email verification.",
    "shield",
  ],
  "admin-delivery": [
    "وضعیت ارسال ایمیل",
    "Email delivery",
    "صف ارسال، نتیجه تلاش‌ها و خطاهای ایمیل.",
    "Email queue, submission progress and delivery errors.",
    "mail",
  ],
  "admin-activity": [
    "رویدادهای حساب‌ها",
    "Activity log",
    "سابقه ورود، تغییر دسترسی‌ها و فعالیت حساب‌ها.",
    "Sign-ins, access changes and account activity.",
    "activity",
  ],
};
export function screenInfo(screen: Screen, lang: Lang) {
  const p = pages[screen];
  return {
    title: p[lang === "fa" ? 0 : 1],
    description: p[lang === "fa" ? 2 : 3],
    icon: p[4],
  };
}
export function readScreen(): Screen {
  const q = new URLSearchParams(location.search);
  const value = q.get("view") || "";
  return Object.hasOwn(pages, value)
    ? (value as Screen)
    : q.has("payment")
      ? "membership"
      : "overview";
}
