# Darvazeh · دروازه

A private message inbox inspired by the welcoming gateways of Safavid caravanserais.
Connect an Android phone holding your SIM, then read and send messages from your account.

Public portal: https://darvazeh.daryajan.com/

## Accounts and membership

- Email sign-in uses a six-digit, single-use code valid for ten minutes.
- New accounts start with a **three-day trial and 20 messages total**, shared across sending and receiving. Both features are enabled.
- One received message or queued outgoing message consumes one unit. Failed outgoing messages remain counted. Retrying the same message identifier does not consume another unit.
- Expiry or an exhausted allowance stops new work; existing messages remain readable. A previously reserved outgoing message can still be dispatched at the quota boundary, while explicit feature revocation or expiry stops dispatch.
- Administrators can create plans with a duration, message allowance and separate sending/receiving permissions, activate or renew them, and independently change each member's feature permissions.
- Members can buy one fixed period at a time through USD card checkout after Stripe is configured. No automatic renewal is created. Toman prices are independent; the Ezpay merchant connection is planned for later.
- Paid purchases preserve an existing unused paid period and queue the next period. Upgrading from the trial starts immediately. Administrators can also activate plans directly.

The administrator signs in at the same portal with **admin@ndorbit.co.nz**, their email code and the additional administrator password from root `.env`. The Administration tab shows all users, received/sent/queued/failed counts, subscription details, feature controls, plans and recent account activity. The activity view excludes private message content.

## Components

- `server/FieldSense.Api`: ASP.NET 8 authentication, membership, quota enforcement, message/device endpoints and persistent activity records.
- `web`: bilingual React/Vite portal, administrator tools and Android download.
- `android-companion`: Android 7+ application with phone linking, background connection and message synchronization.
- SQLite state lives on the existing persistent volume, not inside the application image. Additive startup upgrades preserve existing users and messages, initialize trial subscriptions, and backfill message usage.

## Development and checks

Install web dependencies with `npm --prefix web ci`. Run the API with environment variables from your private `.env`, then `dotnet run --project server/FieldSense.Api --urls http://127.0.0.1:49199`. Run the web UI with `npm --prefix web run dev`. The development UI uses that local API by default.

```powershell
dotnet build server/FieldSense.Api
npm --prefix web run build
python scripts/verify-membership.py
python scripts/verify-billing.py
python scripts/verify-vpn.py
python -m unittest discover -s scripts -p test_deployment_env.py
```

Membership verification runs against disposable local databases with simulated email delivery. It tests legacy migration, account permissions, combined/concurrent quota enforcement, retry safety, expiry, plan renewal, usage history, sign-in and restart persistence. It sends no actual email or SMS.

## Android

Version **2.2.1 (8)** uses the same signing certificate as the previous published APK. Download the current build from the portal's My phone tab, or https://darvazeh.daryajan.com/Darvazeh-SMS.apk?v=2.2.1.

Build with Node/npm, Java 21 and Android SDK 36:

```powershell
cd android-companion
npm ci
npx cap sync android
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
# Windows desktop process workaround for the Java NIO selector:
$env:JAVA_TOOL_OPTIONS = '-Djava.nio.channels.spi.SelectorProvider=sun.nio.ch.WindowsSelectorProvider'
./android/gradlew.bat -p android --no-daemon --max-workers=2 assembleDebug
```

Publish `android/app/build/outputs/apk/debug/app-debug.apk` to `web/public/Darvazeh-SMS.apk` before building the web image. Check certificate continuity before replacing a published APK. Device permissions, SMS dispatch and OEM background behavior still require verification on a real SIM-bearing phone.

## Deployment

Deployment uses Kubernetes context `ravand`, namespace **`darvazeh`**, with both application pods pinned to **`ravand-worker-01`**. The existing Hamafarin worker Caddy serves the public hostnames.

The root `.env` is the source for application settings and scoped Kubernetes Secrets. It is excluded from image build contexts and version control. See [deployment instructions](deploy/README.md) and [image provenance](docs/branding-image.md).

```powershell
python scripts/deployment-env.py check
python scripts/backup-database.py
./scripts/build-and-push.ps1 -Tag your-release-tag
./scripts/deploy.ps1 apply -Local -Tag your-release-tag
```

Take a verified database backup before any schema upgrade. The backup helper uses SQLite's online backup API on the worker, checks integrity, retains an off-node copy under ignored `.local/backups/`, and verifies matching SHA-256 values.

## Payments and business setup

Business: **Niliarya Limited**, trading as **ND ORBIT**, New Zealand.
Read the [Stripe account and integration setup guide](docs/stripe-setup.md).

In Administration, existing plans (including Silver) have editable USD and toman
price fields and a customer-purchase switch. Silver is USD 5 for 30 days / 200
messages; its toman price is not yet set. Enter USD in dollars; it is stored
as integer cents. Enter toman as a whole number. Empty prices are unavailable,
not free offers. No conversion rate or paid plan price is invented automatically.
The three-day trial remains free.

Stripe uses server-created, one-time Checkout Sessions. Amounts and entitlements
are snapshotted on each order, signatures are checked on raw webhook requests,
provider payment details are retrieved before fulfillment, and fulfillment is
idempotent. Payment history and refunds are persisted. Fully refunded periods
are expired or removed from the queue. Existing administrator feature restrictions
remain in effect after a purchase.

The current deployment leaves Stripe disabled until root `.env` has the account's
secret API key, webhook signing secret and `Billing__StripeEnabled=true`. No live
payment or real card was used during local protocol-fixture verification. A Stripe
sandbox end-to-end test is required after connecting your account.

## VPN service (3x-ui)

Members can request a personal VPN configuration under **Services → فیلترشکن با آیپی ایران (VPN with an Iran IP)**. Each member gets exactly one 3x-ui client on a configured server, attached to all configured inbounds, so one subscription link (with QR code and v2rayTun/Hiddify/Streisand import buttons; v2rayNG and NPV Tunnel import from clipboard or QR) carries every route.

- **Trial:** the first configuration per email identity includes a free data allowance (default **10 MB for 1 day**). A revoked or re-created account with the same identity gets no second trial.
- **Monthly plan:** default **USD 4 per 30 days with 5 GB**, one simultaneous user. Payment uses the normal USD/toman checkout through the hidden `vpn-iran` plan and activates the **same** configuration: no new link to import. Paying again while a period is running adds 30 days; paying after the data is used up starts a fresh period immediately. Longer paid time rolls over to a new period with a fresh quota every 30 days. There is no automatic charge.
- **Members** see download/upload/remaining data, all-time totals, online state active users vs. the limit and a validity bar showing the time left, and can pause/resume or replace their link (revokes the old UUID and subscription).
- **Administrators** (**Commerce → VPN service**) set trial data/length, data per period, period length, simultaneous users, USD/toman prices and whether new sign-ups are open; check panel connectivity and inbounds; and per subscriber suspend/resume (overrides the member), extend or shorten paid days, set a custom monthly quota, reset usage, force a sync, replace the link or revoke (deletes the panel client).
- Darvazeh is the source of truth; every change is written to the panel, which enforces quota (`totalGB`), expiry and `limitIp`. Panel writes happen outside payment transactions; failures are stored and retried every two minutes by the VPN worker, which also reads usage and online users. Refunds remove the refunded days.

Configure the server in root `.env` (the token is stored as a Kubernetes Secret, never sent to browsers):

```text
Vpn__Servers__0__PanelBaseUrl=https://PANEL_HOST/PRIVATE_PANEL_BASE_PATH
Vpn__Servers__0__SubscriptionBaseUrl=https://DIRECT_HOST/PRIVATE_SUB_PATH/
Vpn__Servers__0__ApiToken=TOKEN_FROM_3X-UI_SETTINGS_API_TOKEN
# Optional: Vpn__Servers__0__Code/Name/NameFa (default tehran/Tehran/تهران), InboundIds (default 1,2,3)
```

Limits to know: 3x-ui enforces `limitIp` only when its Fail2ban IP-limit job is installed on the panel server; otherwise Darvazeh still shows and flags extra active users. Usage numbers are as fresh as the last sync (two minutes, or on refresh). Profiles exit through the server's own network, so a server inside Iran inherits its upstream filtering.

`scripts/verify-vpn.py` exercises the whole flow against a stateful local 3x-ui fixture and the Stripe fixture; it never contacts a real panel.

## Browser-approved phones and health (2.2.0)

On Android, enter the account email and wait. Sign into that account on the website
with email OTP, compare the six-digit connection code, then approve or reject the
phone. Link requests expire after ten minutes. Changing email stops the old local
connection and revokes its server token; the new email owner must approve again.
Existing installed versions continue to work, but update to 2.2.0 for health reports.

Every device API request from 2.2.0 includes battery percentage, charging state,
network type/validation, SMS permissions and battery-optimization exemption. Incoming
messages and outgoing delivery results keep battery/network snapshots. Forwarded
emails include the same health footer. SMS bodies sent to recipients stay unchanged.
The foreground service checks in roughly every 12 seconds after its last response;
a server worker checks every 30 seconds and records an offline alert after three
minutes without contact. Offline means lost contact; the server cannot distinguish
a dead battery, stopped app, and lost internet while the device cannot report.
Low battery (20%), critical battery (10%), network and permission problems produce
persistent, deduplicated alerts that resolve on recovery. Portal alerts are visible
on all tabs; optional browser notifications work while the page is open. This release
does not provide closed-browser push or automatic alert emails.

| Plan | Period | Total SMS messages | Email forwards | USD |
| --- | --- | --- | --- | --- |
| Trial | 3 days | 20 | 20 | Free |
| Silver | 30 days | 200 | 10 | $2.50 |
| Gold | 30 days | 200 | 200 | $3.50 |

Forwarding counts independently from the SMS allowance. At its cap, incoming messages
still reach the web inbox; no additional email is sent. Definitive email send failures
release the email reservation; interrupted pending sends remain conservatively counted.
Existing payment amounts and purchased SMS-period snapshots are preserved. This
version applies the requested forwarding allowance to existing Silver periods and
counts historical forwarded messages before enforcing it. Existing trials retain
original start/expiry dates but use the new 20-message cap. Prices remain editable.

Verify with `python scripts/verify-device-links.py`, `python scripts/verify-membership.py`
and `python scripts/verify-billing.py`; all use private local fixtures without real mail,
SMS or card charges.

## Password access and email diagnostics

The portal supports immediate email/password signup with email verification before trial activation, and sign-in with username/email plus password,
with email-code login still available. Existing users can set a password from
Settings after confirming their email. Password reset revokes old web sessions.
Administrator password sign-in works without email by default. Administration can enforce OTP for future logins; legacy email-code login retains its extra owner credential.

Email codes and SMS copies now use a durable, encrypted queue with separate workers,
retry tracking and provider-operation polling. Administration → Email sending status
shows queued/accepted/submitted/failed outcomes; submitted is not proof of recipient
inbox arrival. See [the investigation and operating notes](docs/email-and-password-auth-20261113.md).

Google sign-in setup and current password/reset requirements: [operator guide](docs/google-login-setup.md). Google remains disabled until its `.env` credentials are supplied.

Signup now uses email and matching passwords without a separate user ID/username.
New passwords must be longer than eight characters (9–128); existing usernames and
passwords remain valid for sign-in. Signup creates an unverified account immediately; Google or email-code verification activates the trial.
Current Persian tagline: «دروازه ای برای ارتباطات شما» across portal, OTP email and
Android 2.2.1. The APK keeps the published signing certificate.

Account activation and profile password behavior: [deployment notes](docs/account-activation-20261113.md).

Administrator access, user/device controls and searchable message history: [operator notes](docs/admin-and-message-management-20261113.md).
