import { useEffect, useState } from "react";
import { vpnApi, type User, type VpnHome } from "./api";
import type { Lang } from "./i18n";
import type { Screen } from "./workspaceNavigation";

/** Simplified outline of Iran (equirectangular, ~190×170 units) used by both service illustrations. */
const IRAN =
  "M2.0 6.9 L9.8 3.4 L16.6 5.8 L26.3 12.7 L31.2 9.2 L40.0 4.6 L43.9 11.5 L41.0 16.1 L49.7 18.4 L49.7 24.2 L52.7 27.6 L56.6 29.3 L62.4 31.1 L66.3 35.1 L75.1 37.9 L85.8 37.4 L98.5 35.7 L99.5 31.1 L113.1 23.0 L122.9 21.8 L130.7 20.7 L142.4 27.6 L151.2 28.8 L161.9 39.1 L169.7 39.1 L169.7 50.6 L162.9 66.7 L166.8 74.8 L163.8 80.5 L166.8 97.8 L174.6 98.9 L175.5 105.8 L166.8 116.2 L173.6 124.2 L182.4 134.5 L185.3 146.0 L190.2 148.3 L189.2 154.1 L173.6 170.2 L163.8 169.0 L148.2 167.9 L136.5 164.5 L131.7 158.7 L127.8 149.5 L121.9 147.2 L114.1 151.8 L106.3 155.2 L94.6 150.7 L84.8 144.9 L74.1 139.2 L68.3 127.7 L62.4 116.2 L54.6 113.8 L49.7 110.4 L45.8 115.6 L41.0 109.2 L38.0 103.5 L38.0 98.9 L31.2 93.2 L24.4 87.4 L17.6 80.5 L15.6 70.2 L20.5 58.7 L16.6 48.3 L12.7 39.1 L9.8 32.2 L3.9 24.2 L5.9 18.4 L2.0 6.9Z";

function useMotion() {
  const [motion, setMotion] = useState(false);
  useEffect(() => {
    const q = window.matchMedia("(prefers-reduced-motion: reduce)");
    const set = () => setMotion(!q.matches);
    set();
    q.addEventListener("change", set);
    return () => q.removeEventListener("change", set);
  }, []);
  return motion;
}

function Envelope({ fill }: { fill: string }) {
  return (
    <g>
      <rect x="-9" y="-6" width="18" height="12" rx="2" fill={fill} />
      <path d="M-9 -5 L0 1.5 L9 -5" fill="none" stroke="#123d35" strokeWidth="1.4" />
    </g>
  );
}

/** A plugged-in phone inside Iran relays SMS to the web app open anywhere in the world. */
export function SmsIllustration({ lang }: { lang: Lang }) {
  const motion = useMotion();
  const up = "M150 112 C 205 30, 270 26, 318 84";
  const down = "M318 128 C 270 190, 205 186, 156 140";
  return (
    <svg className="service-art" viewBox="0 0 460 240" role="img"
      aria-label={lang === "fa" ? "گوشی متصل به برق در ایران که پیامک‌ها را به وب‌اپ دروازه در خارج از ایران می‌فرستد" : "A plugged-in phone inside Iran relaying SMS to the Darvazeh web app abroad"}>
      <defs>
        <linearGradient id="sms-map" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="#2c7564" />
          <stop offset="1" stopColor="#174c40" />
        </linearGradient>
        <pattern id="sms-dots" width="7" height="7" patternUnits="userSpaceOnUse">
          <circle cx="1.5" cy="1.5" r="0.9" fill="#8fc7b4" opacity=".35" />
        </pattern>
        <radialGradient id="sms-glow">
          <stop offset="0" stopColor="#f0d48f" stopOpacity=".55" />
          <stop offset="1" stopColor="#f0d48f" stopOpacity="0" />
        </radialGradient>
        <linearGradient id="sms-screen" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#fffdf6" />
          <stop offset="1" stopColor="#efeadb" />
        </linearGradient>
      </defs>

      {/* Iran */}
      <g transform="translate(14 34) scale(1.12)">
        <path d={IRAN} fill="url(#sms-map)" stroke="#d9bd7c" strokeWidth="1.6" strokeLinejoin="round" />
        <path d={IRAN} fill="url(#sms-dots)" />
      </g>
      <text x="40" y="214" className="art-label" fill="#d9bd7c">{lang === "fa" ? "ایران" : "IRAN"}</text>
      <circle cx="128" cy="124" r="56" fill="url(#sms-glow)" />

      {/* Wall socket and cable */}
      <rect x="70" y="160" width="26" height="30" rx="5" fill="#fffaf0" stroke="#c9b27a" />
      <circle cx="79" cy="173" r="2" fill="#123d35" />
      <circle cx="87" cy="173" r="2" fill="#123d35" />
      <path d="M83 178 C 83 205, 128 205, 128 160" fill="none" stroke="#f3e2b3" strokeWidth="3" strokeLinecap="round" />

      {/* Phone */}
      <g transform="rotate(-6 128 124)">
        <rect x="108" y="86" width="40" height="74" rx="8" fill="#0f2622" stroke="#e9d39b" strokeWidth="1.5" />
        <rect x="112" y="94" width="32" height="56" rx="3" fill="url(#sms-screen)" />
        <rect x="116" y="100" width="20" height="7" rx="3.5" fill="#2c7564" />
        <rect x="120" y="111" width="20" height="7" rx="3.5" fill="#d9bd7c" />
        <rect x="116" y="122" width="16" height="7" rx="3.5" fill="#2c7564" />
        <rect x="134" y="139" width="6" height="8" rx="1" fill="none" stroke="#2c7564" strokeWidth="1.2" />
        <path d="M137.5 140.5 L136 143.5 H139 L137.5 146" fill="none" stroke="#c08a2a" strokeWidth="1" />
        <circle cx="128" cy="155" r="1.6" fill="#e9d39b" />
      </g>
      {/* Signal */}
      <g fill="none" stroke="#f0d48f" strokeWidth="2" strokeLinecap="round" opacity=".9">
        <path d="M118 76 a14 14 0 0 1 22 0" />
        <path d="M112 69 a22 22 0 0 1 34 0" opacity=".6" />
      </g>

      {/* Links between the phone and the web app */}
      <path d={up} fill="none" stroke="#f0d48f" strokeWidth="2" strokeDasharray="5 7" className={motion ? "art-flow" : ""} />
      <path d={down} fill="none" stroke="#8fd3bd" strokeWidth="2" strokeDasharray="5 7" className={motion ? "art-flow" : ""} />
      <g>
        <Envelope fill="#f0d48f" />
        {motion ? <animateMotion dur="3.6s" repeatCount="indefinite" path={up} /> : <set attributeName="transform" to="translate(232 52)" />}
      </g>
      <g>
        <Envelope fill="#8fd3bd" />
        {motion ? <animateMotion dur="3.6s" begin="1.8s" repeatCount="indefinite" path={down} /> : <set attributeName="transform" to="translate(236 172)" />}
      </g>
      <text x="236" y="36" textAnchor="middle" className="art-chip" fill="#f0d48f">{lang === "fa" ? "دریافت پیامک" : "Incoming SMS"}</text>
      <text x="250" y="226" textAnchor="middle" className="art-chip" fill="#8fd3bd">{lang === "fa" ? "ارسال پیامک" : "Send SMS"}</text>

      {/* Web app on a laptop, anywhere in the world */}
      <g>
        <circle cx="380" cy="104" r="62" fill="none" stroke="#3f7a6b" strokeDasharray="2 5" />
        <ellipse cx="380" cy="104" rx="26" ry="62" fill="none" stroke="#3f7a6b" strokeDasharray="2 5" />
        <rect x="316" y="62" width="128" height="86" rx="8" fill="#0f2622" stroke="#e9d39b" strokeWidth="1.5" />
        <rect x="322" y="68" width="116" height="74" rx="4" fill="url(#sms-screen)" />
        <rect x="322" y="68" width="116" height="11" rx="4" fill="#123d35" />
        <circle cx="329" cy="73.5" r="1.8" fill="#e07a5f" />
        <circle cx="335" cy="73.5" r="1.8" fill="#f0d48f" />
        <circle cx="341" cy="73.5" r="1.8" fill="#8fd3bd" />
        <rect x="328" y="85" width="62" height="11" rx="5.5" fill="#2c7564" />
        <rect x="352" y="101" width="80" height="11" rx="5.5" fill="#d9bd7c" />
        <rect x="328" y="117" width="48" height="11" rx="5.5" fill="#2c7564" />
        <rect x="328" y="132" width="90" height="6" rx="3" fill="#dcd5c1" />
        <path d="M304 148 H456 L446 160 H314 Z" fill="#e9d39b" />
      </g>
      <text x="380" y="190" textAnchor="middle" className="art-label" fill="#f3ecd8">{lang === "fa" ? "هر کجای دنیا" : "Anywhere"}</text>
    </svg>
  );
}

/** A device anywhere in the world tunnels through a shield into an Iranian IP address. */
export function VpnIllustration({ lang }: { lang: Lang }) {
  const motion = useMotion();
  const tunnel = "M96 128 C 160 128, 175 92, 232 92 S 320 126, 372 112";
  return (
    <svg className="service-art" viewBox="0 0 460 240" role="img"
      aria-label={lang === "fa" ? "گوشی در هر کجای دنیا که از تونل امن به آیپی ایران وصل می‌شود" : "A phone anywhere in the world connecting through a secure tunnel to an Iranian IP"}>
      <defs>
        <linearGradient id="vpn-tunnel" x1="0" y1="0" x2="1" y2="0">
          <stop offset="0" stopColor="#5fd4c4" stopOpacity=".15" />
          <stop offset=".5" stopColor="#5fd4c4" stopOpacity=".55" />
          <stop offset="1" stopColor="#f0d48f" stopOpacity=".6" />
        </linearGradient>
        <linearGradient id="vpn-shield" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#f6dc98" />
          <stop offset="1" stopColor="#c99a3e" />
        </linearGradient>
        <linearGradient id="vpn-map" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="#1f5f7a" />
          <stop offset="1" stopColor="#163d55" />
        </linearGradient>
        <radialGradient id="vpn-glow">
          <stop offset="0" stopColor="#5fd4c4" stopOpacity=".5" />
          <stop offset="1" stopColor="#5fd4c4" stopOpacity="0" />
        </radialGradient>
      </defs>

      {/* World behind the device */}
      <g stroke="#2f5b78" fill="none" strokeDasharray="2 5">
        <circle cx="70" cy="122" r="58" />
        <ellipse cx="70" cy="122" rx="24" ry="58" />
        <path d="M14 106 H126 M14 138 H126" />
      </g>
      <text x="70" y="214" textAnchor="middle" className="art-label" fill="#cfe3ee">{lang === "fa" ? "شما، هر کجای دنیا" : "You, anywhere"}</text>

      {/* Phone with the configuration QR */}
      <g transform="rotate(5 70 124)">
        <rect x="48" y="80" width="44" height="84" rx="9" fill="#0b1c2b" stroke="#9fe3d8" strokeWidth="1.5" />
        <rect x="52" y="88" width="36" height="66" rx="3" fill="#f4fbfa" />
        <rect x="57" y="94" width="26" height="26" rx="2" fill="#0b1c2b" />
        <g fill="#f4fbfa">
          <rect x="60" y="97" width="6" height="6" /><rect x="74" y="97" width="6" height="6" /><rect x="60" y="111" width="6" height="6" />
          <rect x="69" y="104" width="3" height="3" /><rect x="74" y="110" width="3" height="3" /><rect x="77" y="114" width="3" height="3" />
          <rect x="69" y="111" width="3" height="6" />
        </g>
        <rect x="57" y="126" width="26" height="9" rx="4.5" fill="#2bb3a0" />
        <path d="M64 130.5 l2.5 2.5 l5-5" fill="none" stroke="#fff" strokeWidth="1.5" />
        <rect x="57" y="140" width="26" height="4" rx="2" fill="#cfdcdf" />
      </g>

      {/* Tunnel */}
      <path d={tunnel} fill="none" stroke="url(#vpn-tunnel)" strokeWidth="22" strokeLinecap="round" />
      <path d={tunnel} fill="none" stroke="#9fe3d8" strokeWidth="1.5" strokeDasharray="4 8" className={motion ? "art-flow" : ""} />
      {[0, 1.1, 2.2].map((delay) => (
        <circle key={delay} r="3.5" fill="#f6dc98">
          {motion ? <animateMotion dur="3.3s" begin={`${delay}s`} repeatCount="indefinite" path={tunnel} /> : null}
        </circle>
      ))}

      {/* Shield */}
      <circle cx="232" cy="92" r="46" fill="url(#vpn-glow)" />
      <path d="M232 52 L262 64 V90 C262 112 249 126 232 134 C215 126 202 112 202 90 V64 Z" fill="url(#vpn-shield)" stroke="#fff5d6" strokeWidth="1.5" />
      <rect x="221" y="88" width="22" height="18" rx="3" fill="#0b1c2b" />
      <path d="M225 88 V82 a7 7 0 0 1 14 0 V88" fill="none" stroke="#0b1c2b" strokeWidth="3" />
      <circle cx="232" cy="96" r="2.4" fill="#f6dc98" />

      {/* Iran with the exit location */}
      <g transform="translate(300 52) scale(0.78)">
        <path d={IRAN} fill="url(#vpn-map)" stroke="#9fe3d8" strokeWidth="1.8" strokeLinejoin="round" />
      </g>
      <circle cx="358" cy="90" r="16" fill="url(#vpn-glow)" />
      <path d="M358 92 c-8 -9 -11 -14 -11 -19 a11 11 0 0 1 22 0 c0 5 -3 10 -11 19 z" fill="#f6dc98" stroke="#0b1c2b" strokeWidth="1" />
      <circle cx="358" cy="73" r="4" fill="#0b1c2b" />
      <g transform="translate(322 158)">
        <rect width="104" height="26" rx="13" fill="#0b1c2b" stroke="#9fe3d8" />
        <text x="52" y="17.5" textAnchor="middle" className="art-chip" fill="#9fe3d8">{lang === "fa" ? "آیپی ایران · تهران" : "Iran IP · Tehran"}</text>
      </g>

      {/* Feature chips */}
      <g className="art-chip">
        <rect x="150" y="166" width="58" height="22" rx="11" fill="#0b1c2b" stroke="#2f5b78" />
        <text x="179" y="181" textAnchor="middle" fill="#cfe3ee">QR</text>
        <rect x="216" y="166" width="78" height="22" rx="11" fill="#0b1c2b" stroke="#2f5b78" />
        <text x="255" y="181" textAnchor="middle" fill="#cfe3ee">v2rayNG · NPV</text>
      </g>
    </svg>
  );
}

function vpnStatus(home: VpnHome | null, lang: Lang): [string, string] {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en);
  const a = home?.account;
  if (!home) return ["", ""];
  if (!a) return home.trialAvailable ? [tr("آزمایش رایگان آماده است", "Free trial ready"), "gold"] : [tr("آماده خرید", "Ready to buy"), ""];
  const map: Record<string, [string, string]> = {
    active: [tr("فعال", "Active"), "green"],
    trial: [tr("آزمایشی فعال", "Trial active"), "green"],
    suspended: [tr("متوقف", "Paused"), ""],
    depleted: [tr("حجم تمام شد", "Data used up"), ""],
    expired: [tr("منقضی", "Expired"), ""],
  };
  return map[a.status] ?? [tr("نیازمند تمدید", "Needs renewal"), ""];
}

/** Two large entry cards on the overview, one per Darvazeh service. */
export function ServiceShowcase({ user, lang, onNavigate }: { user: User; lang: Lang; onNavigate: (screen: Screen) => void }) {
  const tr = (fa: string, en: string) => (lang === "fa" ? fa : en);
  const [vpn, setVpn] = useState<VpnHome | null>(null);
  useEffect(() => {
    let live = true;
    vpnApi.home().then((h) => { if (live) setVpn(h); }).catch(() => undefined);
    return () => { live = false; };
  }, [user.id]);
  const s = user.subscription;
  const phones = (user.devices || []).filter((d) => !d.revokedAt);
  const smsNext: Screen = !s.active ? "membership" : !phones.length ? "device" : "send";
  const [vpnLabel, vpnTone] = vpnStatus(vpn, lang);
  const n = (v: number) => new Intl.NumberFormat(lang === "fa" ? "fa-IR" : "en").format(v);
  return (
    <div className="service-showcase">
      <article className="service-card sms">
        <SmsIllustration lang={lang} />
        <div className="service-copy">
          <div className="service-heading">
            <span className="service-eyebrow">{tr("سرویس پیامک‌رسان", "SMS forwarder")}</span>
            <span className={"badge " + (s.active ? "green" : "")}>{s.active ? tr("اشتراک فعال", "Plan active") : tr("بدون اشتراک", "No plan")}</span>
          </div>
          <h2>{tr("گوشی در ایران، پیامک‌ها همراه شما در هر کجای دنیا", "Your phone stays in Iran; your SMS travels with you")}</h2>
          <p>{tr(
            "یک گوشی اندرویدی با سیم‌کارت ایرانی را به برق و اینترنت وصل کنید. پیامک‌های بانک و کدهای تأیید را همین‌جا ببینید و از همین‌جا پیامک بفرستید.",
            "Leave an Android phone with an Iranian SIM plugged in at home. Read bank messages and verification codes here, and send SMS from here.",
          )}</p>
          <div className="row">
            <button className="btn" onClick={() => onNavigate(smsNext)}>
              {smsNext === "membership" ? tr("انتخاب اشتراک", "Choose a plan") : smsNext === "device" ? tr("اتصال گوشی", "Connect a phone") : tr("ارسال پیامک", "Send an SMS")}
            </button>
            <button className="btn secondary" onClick={() => onNavigate("inbox")}>{tr("صندوق پیام‌ها", "Inbox")}</button>
            {s.active && <span className="service-meta">{tr(`${n(s.remainingMessages)} پیام باقی‌مانده`, `${n(s.remainingMessages)} messages left`)}</span>}
          </div>
        </div>
      </article>
      <article className="service-card vpn">
        <VpnIllustration lang={lang} />
        <div className="service-copy">
          <div className="service-heading">
            <span className="service-eyebrow">{tr("فیلترشکن با آیپی ایران", "VPN with an Iran IP")}</span>
            {vpnLabel && <span className={"badge " + vpnTone}>{vpnLabel}</span>}
          </div>
          <h2>{tr("آیپی ایران، از هر جای دنیا", "An Iranian IP, from anywhere in the world")}</h2>
          <p>{tr(
            "بانک‌ها، درگاه‌های پرداخت و سایت‌هایی که فقط از داخل ایران باز می‌شوند را از خارج باز کنید. پیکربندی با یک کلیک و کد QR برای v2rayNG، v2rayTun و NPV Tunnel.",
            "Open banks, payment gateways and sites that only work from inside Iran. One-click setup with a QR code for v2rayNG, v2rayTun and NPV Tunnel.",
          )}</p>
          <div className="row">
            <button className="btn" onClick={() => onNavigate("vpn")}>
              {vpn?.account ? tr("مشاهده و تمدید", "View & renew") : tr("دریافت پیکربندی", "Get your configuration")}
            </button>
            {vpn?.plan?.priceToman && (
              <span className="service-meta">{tr(`ماهانه ${n(vpn.plan.priceToman)} تومان`, `${n(vpn.plan.priceToman)} toman / month`)}</span>
            )}
          </div>
        </div>
      </article>
    </div>
  );
}
