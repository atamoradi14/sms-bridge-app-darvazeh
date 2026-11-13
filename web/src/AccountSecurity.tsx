import {useState} from "react";
import {api,RequestError,type User} from "./api";
import type {Lang} from "./i18n";
import {GoogleLogin} from "./GoogleLogin";
import {PasswordAccess} from "./PasswordAccess";
import {passwordError} from "./passwordPolicy";
const digits=(s:string)=>s.replace(/[۰-۹٠-٩]/g,c=>String(c.charCodeAt(0)-(c>="۰"?1776:1632)));
export function VerifyEmailBanner({lang,onVerified}:{lang:Lang;onVerified:()=>Promise<void>}) {
 const tr=(fa:string,en:string)=>lang==="fa"?fa:en;
 const [open,setOpen]=useState(false),[sent,setSent]=useState(false),[code,setCode]=useState(""),[busy,setBusy]=useState(false),[error,setError]=useState("");
 async function request(){setBusy(true);setError("");try{await api.requestVerification();setSent(true);}catch{setError(tr("درخواست ارسال نشد؛ کمی بعد دوباره تلاش کنید.","Could not request a code. Try again shortly."));}finally{setBusy(false);}}
 return <section className="card verification-banner" aria-label={tr("فعال‌سازی حساب","Activate account")}>
 <div className="row between"><div><strong>{tr("برای فعال‌سازی دوره آزمایشی، ایمیل‌تان را تأیید کنید","Verify your email to activate your trial")}</strong><p className="hint">{tr("۳ روز و ۲۰ پیام؛ از زمان تأیید شروع می‌شود. تا آن زمان می‌توانید حساب‌تان را آماده کنید.","3 days and 20 messages, starting when you verify. You can prepare your account now.")}</p></div><button className="btn" type="button" onClick={()=>setOpen(!open)} aria-expanded={open}>{tr("تأیید ایمیل","Verify email")}</button></div>
 {open&&<div className="verification-options"><p>{tr("با همان ایمیل از گوگل وارد شوید، یا کد تأیید دریافت کنید.","Continue with Google using this same email, or request an email code.")}</p><GoogleLogin lang={lang}/>
 <button className="text-button" type="button" disabled={busy||sent} onClick={request}>{sent?tr("کد درخواست شد","Code requested"):tr("دریافت کد ایمیل","Send email code")}</button>
 {sent&&<form onSubmit={async e=>{e.preventDefault();setBusy(true);setError("");try{await api.verifyAccountEmail(code);await onVerified();}catch{setError(tr("کد معتبر نیست یا منقضی شده است.","The code is invalid or expired."));}finally{setBusy(false);}}}>
 <label htmlFor="activate-email-code">{tr("کد تأیید","Verification code")}</label><input id="activate-email-code" required inputMode="numeric" autoComplete="one-time-code" pattern="[0-9]{6}" maxLength={6} dir="ltr" value={code} onChange={e=>setCode(digits(e.target.value))}/>
 <p className="hint" role="status">{tr("کد در صف ارسال است. رسیدن ایمیل ممکن است طول بکشد. در صورت تأخیر می‌توانید با گوگل تأیید کنید.","Your code is queued. Email delivery may be delayed; Google is another way to verify.")}</p>
 <button className="btn" disabled={busy}>{tr("تأیید و فعال‌سازی","Verify and activate")}</button><button className="text-button" type="button" disabled={busy} onClick={request}>{tr("ارسال دوباره کد","Resend code")}</button></form>}
 {error&&<p className="alert error" role="alert">{error}</p>}</div>}
 </section>;
}
export function ProfileSecurity({user,lang,onSignedIn}:{user:User;lang:Lang;onSignedIn:(token:string)=>void}) {
 const tr=(fa:string,en:string)=>lang==="fa"?fa:en;
 const [reset,setReset]=useState(false),[current,setCurrent]=useState(""),[password,setPassword]=useState(""),[confirm,setConfirm]=useState(""),[busy,setBusy]=useState(false),[error,setError]=useState("");
 return <section className="card narrow"><h2>{tr("امنیت حساب و رمز عبور","Account security and password")}</h2><p className="muted">{user.email} · {user.emailVerified?tr("ایمیل تأیید شده","Email verified"):tr("در انتظار تأیید ایمیل","Email not verified")}</p>
 {reset?<><PasswordAccess lang={lang} accountEmail={user.email} initialMode="reset" onSignedIn={onSignedIn}/>{user.role!=="Admin"&&<button type="button" className="text-button" onClick={()=>setReset(false)}>{tr("بازگشت","Back")}</button>}</>:<>
 <p className="hint">{user.hasPassword?tr("برای تغییر رمز، رمز فعلی را وارد کنید.","Enter your current password to change it."):tr("پس از ورود اخیر با گوگل یا کد ایمیل، می‌توانید بدون کد تازه رمز تنظیم کنید.","After a recent Google or email-code sign-in, you can set a password without another code.")}</p>
 <form onSubmit={async e=>{e.preventDefault();setError("");if(passwordError(password,confirm,user.email,user.username||"")){setError(tr("رمز باید ۹ تا ۱۲۸ نویسه، غیررایج و با تکرار آن یکسان باشد.","Use 9–128 characters, avoid common passwords and match both entries."));return;}setBusy(true);try{const r=await api.profilePassword(password,confirm,current||undefined);setCurrent("");setPassword("");setConfirm("");onSignedIn(r.token);}catch(e){setError((e as RequestError).code==="recent_signin_required"?tr("برای تنظیم رمز، دوباره با گوگل یا کد ایمیل وارد شوید.","Sign in again with Google or an email code before setting a password."):tr("رمز فعلی یا اطلاعات واردشده معتبر نیست؛ می‌توانید از بازنشانی با کد استفاده کنید.","Check your current password and details, or reset with an email code."));}finally{setBusy(false);}}}>
 {(user.hasPassword||user.role==="Admin")&&<><label htmlFor="profile-current-password">{tr("رمز فعلی","Current password")}</label><input id="profile-current-password" required type="password" autoComplete="current-password" dir="ltr" maxLength={256} value={current} onChange={e=>setCurrent(e.target.value)}/></>}
 <label htmlFor="profile-new-password">{tr("رمز جدید","New password")}</label><input id="profile-new-password" required type="password" autoComplete="new-password" dir="ltr" minLength={9} maxLength={256} value={password} onChange={e=>setPassword(e.target.value)}/>
 <label htmlFor="profile-confirm-password">{tr("تکرار رمز جدید","Confirm new password")}</label><input id="profile-confirm-password" required type="password" autoComplete="new-password" dir="ltr" minLength={9} maxLength={256} value={confirm} onChange={e=>setConfirm(e.target.value)}/>
 <p className="hint">{tr("۹ تا ۱۲۸ نویسه؛ یک عبارت طولانی و غیررایج انتخاب کنید. نشست‌های دیگر پس از تغییر رمز بسته می‌شوند.","9–128 characters; choose a long, uncommon passphrase. Changing your password signs out other sessions.")}</p><button className="btn" disabled={busy}>{tr("ذخیره رمز","Save password")}</button></form>
 {error&&<p className="alert error" role="alert">{error}</p>}<button type="button" className="text-button" onClick={()=>setReset(true)}>{tr("فراموشی رمز؟ بازنشانی با کد ایمیل","Forgot password? Reset with an email code")}</button></>}
 </section>;
}
