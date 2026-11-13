# Account activation and profile security

Release: `release-20261113-04`.

- Password signup uses email, password and confirmation. No OTP is requested or required at signup. A signed-in, unverified account has a zero-capacity inactive placeholder period.
- A persistent banner above the account navigation offers Google sign-in using the same address, or an email code. Authenticated code verification always uses the account email from the database, not an address supplied by the browser.
- Google and email-code sign-in prove email ownership automatically. Verification starts the trial once: three days, 20 combined messages and 20 email forwards. Repeating verification or signing in never renews it.
- Unverified accounts cannot approve/pair phones, reserve incoming/outgoing message usage, dispatch outgoing jobs or create checkout sessions. Paid and administrator assignments cannot bypass the verified-email check.
- Gmail dot/plus aliases and googlemail.com share a hashed trial claim. Claims are retained independently from account records. This limits email-alias abuse; it does not establish one trial per physical person across unrelated inboxes.
- If someone pre-registers another person's mailbox, the owner's first independent Google/OTP sign-in clears the unproven password, username and forwarding preferences, and invalidates previous sessions. The owner can then set a password. Existing verified accounts keep their passwords, memberships and history.
- Settings includes password setup/change and OTP reset. Existing passwords require the current password. A verified account without a password may set one within 15 minutes of sign-in, otherwise it must sign in again. Reset by email code remains available. Passwords are entered twice, require 9–128 Unicode characters, and reject common/repetitive/personal values.
- Password changes invalidate other web sessions. Existing administrator extra-password and OTP requirements remain. Password failures use lockout, signup has an IP rate limit, and email challenges have a request/attempt budget.
- Additive migration backfills verification only for accounts with previous successful login/Google identity, preserves existing trial consumption and dates, and does not restart existing trials. The database is backed up before deployment.

Validation: password/activation/profile security, Google token/flow, membership, billing, device/link/health, email outbox and environment suites; browser signup, verification, profile change and mobile overflow checks. Local tests use disposable databases and mock email; no real SMS or card charges are generated.
