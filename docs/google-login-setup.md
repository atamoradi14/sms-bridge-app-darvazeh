# Google sign-in setup

The portal now offers password sign-in, email OTP and Google sign-in. Google stays
visible but disabled until the server has valid credentials and Google__Enabled=true.
There are no credentials in the frontend bundle. Existing accounts, paid periods and
message history are retained when a trusted Google identity links to the same email.

## Configure Google Cloud

1. Create or select a project in Google Cloud, configure Google Auth Platform branding,
   audience and consent information for Darvazeh, and add daryajan.com as needed.
2. Create an OAuth client of type **Web application**.
3. Add this exact authorized redirect URI:
   `https://api1.daryajan.com/api/auth/google/callback`
4. If the consent app is still in testing, add the Google accounts allowed to test it.
   Complete Google's publication requirements before making it available generally.
5. Save the client ID and secret locally in root `.env`:

```dotenv
Google__Enabled=true
Google__ClientId=YOUR_ID.apps.googleusercontent.com
Google__ClientSecret=YOUR_SECRET
Google__RedirectUri=https://api1.daryajan.com/api/auth/google/callback
Google__PortalOrigin=https://darvazeh.daryajan.com
```

Keep the client secret out of chat, source control and frontend files. Deployment
validation checks the callback, portal origin/CORS and credential presence before
activation. Redeploy configuration with the currently deployed images; no new Google
API permissions beyond `openid email` are needed. The server exchanges the code and
checks Google's signature, issuer, audience, expiry, verified email and nonce.

## Account and session protections

- PKCE S256, a random state and an HttpOnly/Secure/Lax correlation cookie bind each
  attempt to its originating browser. State expires after ten minutes and is single-use.
- A one-minute, browser-bound code returns to the portal in a URL fragment; the app JWT
  never appears in a redirect URL. The portal removes the fragment and exchanges once.
- Google subject is unique and persisted. A verified Gmail or Google-managed Workspace
  email can link to the same existing email; a different linked subject is rejected.
  Unmanaged third-party email identities use the email-OTP route instead, since Google
  is not necessarily authoritative for current ownership of those mailboxes.
- Owner/admin Google sign-in is blocked; administrators use password sign-in and
  the optional database-backed OTP policy. The configured owner password remains an operator recovery credential. Regular Google accounts get the existing
  three-day/20-message trial only when first created.
- Password reset invalidates old web sessions and any pending Google result minted
  before the reset. Android browser approval and phone tokens remain separate.

## Passwords and OTP reset

Signup uses email without a separate username field. Existing usernames remain valid for sign-in.

Signup creates an unverified account immediately, without sending or requiring an OTP. A persistent portal banner offers Google sign-in or an email code to verify the address and start the trial. Unverified accounts cannot approve phones, use messaging, or start checkout. Google and email-code sign-in verify automatically. The trial starts once at verification, for three days and 20 messages. Gmail dot and plus aliases share one trial claim.

On the first independent Google/OTP verification of a previously unverified registration, pre-verification sessions, passwords and forwarding settings are cleared to prevent account pre-claiming. Existing verified accounts keep their credentials and memberships.

The profile supports changing a password using the current password, or resetting it with an email code. Verified accounts without a password may set one within 15 minutes of signing in; otherwise sign in again. Password updates revoke other web sessions. Administrator password login works without OTP by default; the administration security panel can require OTP for future sign-ins.

Signup and setting/resetting a password both require the same password twice, checked
in the UI and server. Passwords use 9–128 Unicode characters; longer passphrases are
encouraged. Common/repetitive/numeric-only passwords and passwords based only on the
account username/email are rejected. Spaces and password-manager paste are supported;
there is no arbitrary uppercase/digit/symbol composition requirement. Sign-in still
accepts existing passwords without applying the new setup policy retroactively.

Choose **Forgot password / set password**, enter the account email and matching new
passwords, request its OTP and verify it. Unknown accounts receive generic errors;
existing users retain their account and membership. Five failed password attempts
lock password sign-in temporarily; email OTP recovery remains available. Admin reset
also needs the configured administrator credential.

## Tests

- `python scripts/verify-password-auth.py`: valid nine-character passwords, weak and
  mismatched confirmation rejection, verified reset, replay/lockout/session boundaries.
- `dotnet run --project scripts/google-auth-tests/GoogleAuth.Tests.csproj`: actual RSA
  token validation plus isolated HTTP/SQLite flow tests using a simulated Google
  provider; wrong issuer/audience/signature/nonce/expiry, state/cookies, replay,
  existing-account preservation, cancellation, admin and password-reset boundaries.
- Full membership, billing, device/health, mail-outbox and environment suites.
- Browser signup, OTP reset, old-password rejection/new-password sign-in and inactive
  Google button. No real Google authorization can be completed until credentials exist.

References: [Google OpenID Connect](https://developers.google.com/identity/openid-connect/openid-connect),
[OWASP authentication guidance](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html).
