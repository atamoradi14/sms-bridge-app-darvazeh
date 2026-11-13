# Email delay investigation and password access — 13 November 2026

## Observed evidence

The app had no provider operation IDs, submission latency, durable send queue or
recipient delivery logs. Both OTP and SMS email used `WaitUntil.Started`; a successful
return means acceptance only. SMS forwarding marked success at this stage and
swallowed later errors, so historical “sent” records cannot prove inbox arrival.

Retained application logs had no submission exceptions. The live database contained
10 OTP requests over 11–12 November UTC and three forwarded incoming messages. The
configured sending domain is ndoorbit.co.nz. SPF and both DKIM verification states
were Verified; public DNS contains DMARC with p=none. No diagnostic settings existed
on the shared email resource before this investigation.

Provider metrics for the reported window recorded successful SendMail HTTP 202
responses in the 20:02 and 20:04 UTC minute buckets on 12 November, and one
ndorbit.co.nz Delivered/Success event at 20:10 UTC. This is consistent with a
several-minute provider/delivery delay (09:02/09:04 to 09:10 NZDT on 13 November),
but aggregated metrics cannot correlate an individual recipient or identify a root
cause. No 429 or failed SendMail status was present in the inspected metrics. A
suppressed event belonged to another sending domain and does not explain this
incident. No inbox headers were available; do not claim Gmail itself caused it.

Enabled EmailSendMailOperational and EmailStatusUpdateOperational diagnostic logs
on the existing communication resource, routed to the existing AtaDevelopment
workspace. This creates future evidence, not retroactive delivery history. It does
not change sender domains, DNS records, or account quotas. Shared resource metrics
include other applications, so correlation must use dispatch operation IDs/domain.
No diagnostic email was sent during this investigation.

## Email changes

OTP and SMS email use a durable SQLite outbox. Two independent worker loops give
login codes their own processing path. Requests return after queueing; sending and
provider-status polling happen in background. Mail payloads are AES-GCM encrypted
with a purpose-derived key from the deployment JWT secret and are deleted after
provider acceptance or a terminal outcome. Retain the same JWT key across restarts;
rotating it makes still-queued encrypted payloads unreadable.

Every send uses its persisted dispatch GUID as the provider operation ID. A retry
after an ambiguous timeout first queries that operation; it does not blindly issue
a new email. Transient failures back off; expired/consumed OTPs are skipped. Definitive
forwarding failures release the original period's reservation. Ambiguous expired
operations remain conservatively counted. Queue state, accepted/submitted times,
attempts and sanitized errors are visible to the owner in Administration. No code,
email body or secret appears in that report. Submitted means out for delivery;
recipient delivery must be checked in provider delivery logs. This cannot force
instant Gmail delivery or guarantee an existing provider delay has been resolved.

## Password access

Signup requires a verified email and a 9–128 character password (current policy; see Google/password setup guide). Email ownership
is verified once with OTP before the account is created. Existing OTP users use
Settings → Password and username, or Set or reset password from the login form;
verification updates their existing account rather than starting another trial.

Regular users can sign in with username or email plus password. Passwords are
SHA-256 prehashed then BCrypt hashed with cost 12 to support long/Unicode passwords
without BCrypt's input-length truncation. Five failed attempts temporarily lock
password login for 15 minutes. OTP recovery remains available. Endpoint rate limiting
adds an outer request cap. Password reset requires a fresh, single-use email OTP
and invalidates old web sessions through the account auth-version claim. It does
not revoke the deliberately separate phone token. Administrator password sign-in
retains OTP and the additional configured administrator credential. Browser phone
approval and existing plan/payment records are preserved.

## Validation

- `python scripts/verify-password-auth.py`: ownership, signup/signin, hashes,
  lockout, verified reset, session invalidation, legacy accounts and admin boundary.
- `dotnet run --project scripts/email-outbox-tests/EmailOutbox.Tests.csproj`: durable
  encryption, 429 retry, stable operation identity, polling, ambiguous recovery,
  expiry, permanent failure and exactly-once forwarding-reservation release.
- Existing membership, billing, device-link/health and environment suites.
- Browser password/signup forms and OTP fallback on a disposable local environment.
- No real email, SMS or payment is sent by these test suites.

Provider reference: [operation success versus inbox delivery](https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/email/send-email)
and [delivery operational logs](https://learn.microsoft.com/en-us/azure/communication-services/concepts/analytics/logs/email-logs).
