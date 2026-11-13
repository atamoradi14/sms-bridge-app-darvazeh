# Darvazeh workspace UI/UX review

## Branch and review boundary

Branch: `codex/darvazeh-uiux`, in the separate `sms-bridge-app-darvazeh-uiux` worktree. Based on updated local main `352bc6f`, including fetched origin/main `3937388`, the committed default-wallet change and the newer toman popup/VPN-banner change. Main advanced during the review; the design commit was rebased cleanly onto it and rebuilt. Origin/main was fetched again after implementation and is an ancestor of this branch.

The original checkout's Billing/index.css edits, release evidence and .claude directory were left untouched. The Billing/index.css work was committed to main during this task and is now included via the rebase. No credentials were copied into the worktree, no production database was used, and this branch has not been deployed or pushed.

## Review findings and implemented changes

| Finding | Result |
| --- | --- |
| Administration mixed security, users, plans, payments, wallet setup and delivery logs in one long page | Separate Operations, Commerce and System navigation groups, with a dedicated screen for each task |
| Personal tools and administration shared an undifferentiated tab bar | Explicit My workspace / Administration switch for owners; members see only their personal workspace |
| No useful landing view | User overview combines allowance, connection/battery/network status and recent messages. Admin overview combines recorded totals, attention items and recent account events |
| Admin attention counts had no direct workflow | Unverified/suspended dashboard links open People with the corresponding status filter |
| Account cards exposed all controls at once | Compact account summaries with expandable management, status filtering, pagination and a separate disclosure for account deletion |
| Navigation state disappeared on reload; browser back could be confusing | View URLs persist screen state, support back/forward, retain payment return parameters and normalize unauthorized administration deep links to Overview |
| Desktop tabs wrapped awkwardly on phones | Persistent desktop sidebar and native modal mobile navigation with focus trapping, Escape/close controls and automatic dismissal on selection |
| Onboarding occupied space even after connecting a phone | First-phone setup is open for new accounts and collapses into Connect another phone for existing users |
| Global phone warnings could dominate unrelated pages | Grouped health notices with critical battery warnings expanded; requests for connection approval remain visible |
| Membership details buried the plan choices | Compact current-period summary, two allowance indicators, separate purchase section and collapsible allowance explanation |
| UTC timestamps without a suffix were interpreted as local time | Central UTC parser for displayed messages, phone reports, payments and audit timestamps; display still uses the viewer's local timezone |
| Password managers lacked account context in profile/security forms | Hidden username/email autocomplete context, without adding a visible signup username field |
| Destructive actions competed with routine actions | Progressive disclosure for account deletion and confirmation before disconnecting a phone; existing irreversible-action confirmations and quota rules retained |
| Product copy exposed payment infrastructure terminology | Wallet empty-state copy explains that toman payments are not connected, without naming the underlying transport |

## Design system

The workspace retains Darvazeh's gateway mark, Persian-first identity, deep green, warm stone surfaces and restrained gold details. It adds consistent line icons, compact status badges, predictable spacing, calmer form and history surfaces, meaningful empty states, strong keyboard focus and a skip-to-content link. Layout uses logical CSS properties for mirrored RTL/LTR behavior. Login imagery and branding remain the established assets.

## Validation performed

- Frontend TypeScript/Vite production build passed.
- Backend built for the isolated local preview; no backend source changes were required.
- UTC parsing checked for timezone-less, Z and explicit-offset timestamps.
- Browser checks against a private local API/database: password sign-in; overview, messages, compose, phone, settings and plan screens; search and no-result state; queueing a sample message; saving email preferences; reload and browser back; Persian/English direction switching.
- Simulated Google return exchanged exactly once, removed the URL fragment and loaded the member workspace using a local fixture token. This was a UI return-path check, not a new external Google consent test.
- Every administration destination opened successfully: overview, people, plans, payments, wallet, security, email delivery and activity. Checked people search, applied dashboard filters, expandable editing and hidden deletion controls.
- Critical battery notices were checked to expand automatically; a simulated failed dismissal showed an actionable error.
- Mobile checks: native menu selection and Escape dismissal, normal/unverified member states, no member administration switch, unauthorized admin deep-link fallback, and no horizontal overflow at tested 320, 390, 768, 1280 and 1440 px widths for the covered layouts.
- After the rebase, the toman/VPN banner and purchase-card layout were checked on mobile with an intercepted sample catalog. No payment was initiated.
- Existing React hook/ref lint warnings remain in legacy components; lint completes with warnings and no errors. New workspace/overview/navigation/format modules introduce no lint warnings.
- Real SMS, email delivery, card charges, Ezpay merchant operations and physical-phone behavior were not exercised. The preview uses explicit sample users/messages and mock email. Queued sample messages have no physical phone attached.

## Local review artifacts

Screenshots are retained in the ignored `output/playwright` directory in this worktree:

- `user-overview-final-fa.png`
- `user-overview-english.png`
- `user-overview-mobile-fa.png`
- `messages-mobile-en.png`
- `admin-overview-final-fa.png`
- `admin-overview-desktop.png`
- `admin-overview-mobile-fa.png`
- `unverified-mobile-fa.png`
- `plan-payments-mobile-fa.png`

The private preview database is ignored under `.local`. Do not use sample accounts, sample price data or preview credentials for production.
