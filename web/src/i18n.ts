export type Lang = "fa" | "en";

const dict = {
  fa: {
    brand: "دروازه",
    tagline: "دروازهٔ پیامک ایران — سیم‌کارت در خانه، صندوق در هر کجا",
    loginTitle: "ورود با ایمیل",
    loginAcs: "کد یک‌بارمصرف از طریق ایمیل (Azure ACS) ارسال می‌شود.",
    sampleBanner:
      "حالت نمونه — هنوز آدرس API عمومی تنظیم نشده. پس از ارسال کد، از ۰۰۰۰۰۰ استفاده کنید. برای OTP واقعی، راز FIELDSENSE_API_BASE را به آدرس ASP.NET خود وصل کنید.",
    email: "ایمیل",
    sendCode: "ارسال کد",
    otpLabel: "کد یک‌بارمصرف",
    otpPlaceholder: "کد ۶ رقمی",
    adminPass: "رمز مدیر",
    adminPassHint: "از متغیر محیطی سرور API",
    signIn: "ورود",
    back: "بازگشت",
    signOut: "خروج",
    api: "API",
    sampleMode: "(حالت نمونه)",
    inbox: "صندوق",
    send: "ارسال",
    device: "دستگاه",
    settings: "تنظیمات",
    admin: "مدیر",
    refresh: "بازخوانی",
    inboxLead: "پیامک‌های رسیده به گوشی ایران اینجاست.",
    noMessages: "هنوز پیامی نیست.",
    sendTitle: "ارسال از طریق گوشی ایران",
    sendLead: "پیام در صف دستگاه جفت‌شده قرار می‌گیرد؛ گوشی باید آنلاین باشد.",
    toNumber: "شماره مقصد",
    message: "متن پیام",
    queueSms: "صف‌کردن پیامک",
    deviceTitle: "گوشی ایران",
    deviceLead:
      "اپ دروازه را روی گوشی دارای سیم در ایران نصب کنید و با همان ایمیل وارد شوید. اتصال به‌صورت خودکار برقرار می‌شود.",
    connectPhone: "اتصال گوشی",
    downloadApk: "دانلود اپ اندروید (APK)",
    downloadApkHint:
      "روی گوشی ایران نصب کنید، سپس با همان ایمیل این حساب وارد شوید. توکن لازم نیست.",
    noDevices: "هنوز گوشی‌ای وصل نشده. اپ را نصب کنید و با همان ایمیل وارد شوید.",
    deviceOnline: "آنلاین",
    deviceOffline: "آفلاین",
    lastSeen: "آخرین بازدید",
    removeDevice: "حذف",
    simLabel: "شماره سیم",
    settingsTitle: "ارسال به ایمیل",
    settingsLead: "نسخهٔ پیامک‌های ورودی را با ACS به ایمیل بفرستید.",
    forwardToggle: "هدایت پیامک ورودی به ایمیل",
    forwardAddress: "آدرس ایمیل",
    saveSettings: "ذخیره",
    adminTitle: "مدیر · مجوز رله",
    adminLead: "اجازه دهید کاربر از طریق گوشی ایران پیامک بفرستد.",
    relayOn: "رله روشن",
    grantRelay: "اعطای رله",
    revokeRelay: "لغو رله",
    roleUser: "کاربر",
    roleAdmin: "مدیر",
    langFa: "فارسی",
    langEn: "English",
    emailed: "ایمیل شد",
    sampleOtpHint: "حالت نمونه: کد ۰۰۰۰۰۰ را وارد کنید",
  },
  en: {
    brand: "Darvazeh",
    tagline: "Iran SMS gateway — SIM at home, inbox anywhere",
    loginTitle: "Email sign-in",
    loginAcs: "A one-time code is emailed via Azure Communication Services.",
    sampleBanner:
      "Sample data — no public API URL yet. After sending a code, use 000000. For real ACS OTP, set the FIELDSENSE_API_BASE secret to your ASP.NET API.",
    email: "Email",
    sendCode: "Send code",
    otpLabel: "One-time code",
    otpPlaceholder: "6-digit code",
    adminPass: "Admin password",
    adminPassHint: "From the API server environment",
    signIn: "Sign in",
    back: "Back",
    signOut: "Sign out",
    api: "API",
    sampleMode: "(sample mode)",
    inbox: "Inbox",
    send: "Send",
    device: "Device",
    settings: "Settings",
    admin: "Admin",
    refresh: "Refresh",
    inboxLead: "SMS arriving on your Iran phone appear here.",
    noMessages: "No messages yet.",
    sendTitle: "Send via Iran phone",
    sendLead: "Queued to your paired device — the phone must be online in Iran.",
    toNumber: "To number",
    message: "Message",
    queueSms: "Queue SMS",
    deviceTitle: "Iran phone",
    deviceLead:
      "Install the Darvazeh app on the SIM phone in Iran and sign in with the same email. Linking happens automatically.",
    connectPhone: "Connect your phone",
    downloadApk: "Download Android app (APK)",
    downloadApkHint:
      "Install on the Iran SIM phone, then sign in with the same email as this account. No token paste needed.",
    noDevices: "No phone linked yet. Install the app and sign in with the same email.",
    deviceOnline: "Online",
    deviceOffline: "Offline",
    lastSeen: "Last seen",
    removeDevice: "Remove",
    simLabel: "SIM number",
    settingsTitle: "Email forward",
    settingsLead: "Also send inbound SMS copies by email via ACS.",
    forwardToggle: "Forward inbound SMS to email",
    forwardAddress: "Forward address",
    saveSettings: "Save settings",
    adminTitle: "Admin · relay grants",
    adminLead: "Allow a user to send SMS through their paired Iran phone.",
    relayOn: "relay on",
    grantRelay: "Grant relay",
    revokeRelay: "Revoke relay",
    roleUser: "User",
    roleAdmin: "Admin",
    langFa: "فارسی",
    langEn: "English",
    emailed: "emailed",
    sampleOtpHint: "Sample mode: enter 000000",
  },
} as const;

export type MsgKey = keyof typeof dict.fa;

export function t(lang: Lang, key: MsgKey): string {
  return dict[lang][key];
}

export function loadLang(): Lang {
  try {
    const stored = localStorage.getItem("darvazeh_lang");
    if (stored === "en" || stored === "fa") return stored;
  } catch {
    /* ignore */
  }
  return "fa";
}

export function saveLang(lang: Lang) {
  try {
    localStorage.setItem("darvazeh_lang", lang);
  } catch {
    /* ignore */
  }
}
