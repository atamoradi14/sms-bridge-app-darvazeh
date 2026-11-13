# Administrator access and message management

Release: `release-20261113-05`.

## Admin login

Use `admin@ndorbit.co.nz` with the configured `FieldSense__AdminPassword` from root `.env`, or a password set on the owner account. No OTP is required by default. The configured password remains an operator recovery credential even after a profile password change; rotate it in `.env` when needed.

Administration → Administrator login security controls **Require email OTP for administrator login**. Saving requires a current owner password. The policy is persisted in SecuritySettings and applies to future password sign-ins, not existing sessions. The legacy email-code login and password-reset paths still require an email code and configured extra owner credential. Google remains unavailable for owner accounts. Wrong passwords retain the account lockout and request limits.

## People and devices

The administration panel can create ordinary unverified users; edit email, optional username and password; suspend/resume; and delete accounts. Public signup/verification/trial protections remain. Changing email revokes sessions and phone credentials and requires email verification/phone approval again. The owner cannot be edited, suspended or deleted through these account-management routes.

Suspension invalidates existing web sessions, blocks all login methods and phone endpoints, and cancels queued outgoing work. Resuming allows fresh login without extending memberships. Device disconnect/reconnect is a separate administrator pause that preserves the existing device credential; it does not claim the handset is online or bypass a user's revoked registration. Revoked devices need fresh owner approval. Paused/suspended devices do not generate new offline/battery alerts while intentionally disabled.

Deleting an account closes sign-in, revokes devices, cancels outgoing work and removes message content from storage. Financial, usage, trial-claim and audit records are retained; this is a logical account deletion, not a refund or erasure of payment records. Existing copies handed to a phone/email provider cannot be recalled.

## Message history

Each account has server-side text/number search, incoming/outgoing filters, 10/20/50/100 row page sizes, total counts and first/previous/next/last controls. Results are scoped by the authenticated user; owner-only routes offer the same history for each account. Dates and IDs produce deterministic ordering.

Single and selected deletion clear message text/number, hide the history row, scrub related job content and cancel pending email copies. Ledger and retry identifiers remain so deletion cannot refund quota or allow duplicate incoming retries. Queued outgoing messages cannot be deleted until final; previously fetched work or sent copies cannot be recalled. Destructive actions require an in-portal confirmation.

Validation uses disposable SQLite databases and mock email: administrator bootstrap/OTP policy persistence; owner and cross-account boundaries; create/edit/suspend/delete; phone pause/resume and revoked-phone boundary; message search, paging and deletion; unchanged usage and SQLite integrity; existing password/Google/membership/billing/device/health/outbox/env suites; desktop/mobile UI flows. Production checks do not delete accounts/messages, create users, toggle OTP or send real SMS/payments.

Follow-up release `release-20261113-06`: deleted email/username identities may register a new account through Google, password signup or email-code sign-in. The old record remains deleted. Partial unique indexes enforce uniqueness among nondeleted users. Trial claims, old billing/history/device associations and suspension blocks are preserved; no additional free trial is granted for an already claimed email identity. Google suspension/administrator/state errors now explain the reason rather than showing one generic failure.
