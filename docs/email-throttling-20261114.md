# Email and API polling reliability

## Findings

Checked production application logs, a read-only database backup, Azure `ApiRequests` metrics (2026-11-11 through the investigation), and the email operational diagnostic tables. No retained HTTP 429 was found. Four OTP dispatches had reached `unknown` after acceptance, without a saved provider error. Diagnostic events include accepted sends, OutForDelivery and Delivered. These sources do **not** prove that get-status throttling caused the recent delays: the available metrics did not include the worker's full send-operation polling traffic, and the current pod has limited log history.

The code did have a concrete throttling risk: immediate status checks followed by fixed five-second polling, repeated checks during ambiguous-send recovery, SDK retries in addition to worker retries, and status failures whose backoff never increased because it depended on send attempts. Neither queue shared a request budget or cooldown.

Microsoft documents custom-domain default subscription limits of 30 sends/minute, 100 sends/hour, 60 status requests/minute and **200 status requests/hour**. Five-second polling of a single operation for an hour would generate 720 status calls.

Reference: [Communication Services service limits](https://learn.microsoft.com/en-us/azure/communication-services/concepts/service-limits#rate-limits-for-email).

## Controls

- `EmailRequestBudget` reserves each actual send or status attempt in SQLite, with rolling windows: sends 25/minute and 90/hour; status 50/minute and 180/hour. Each operation type also has at least two seconds between calls. Both queues use these budgets. The write transaction serializes reservations, and history survives process restarts. Records older than one hour are removed.
- A 429 or 503 starts a shared, persisted cooldown for both kinds of requests. Numeric/date `Retry-After` and `retry-after-ms` are respected. SDK retries are disabled so every network attempt passes through the durable budget.
- Status checks wait initially, then slow from approximately 15 to 30, 60 and 120 seconds, with jitter. Server hints can extend the delay. Consecutive failures are persisted and increase the delay up to five minutes, with jitter; provider hints can extend it further.
- Work is ordered by its next due time, then age. A running operation does not continuously jump ahead of other due work. Claims last 45 seconds; SDK network timeout is 15 seconds and there are no hidden retries. No database transaction remains open during provider I/O.
- Deferred work wakes by its expiry time to erase expired secrets. Accepted operations are checked, never resubmitted; ambiguous sends retain the original operation ID. Forwarding reservations are released once on definitive failure, preserving the existing billing semantics.
- Browser polling is sequential, pauses hidden/offline tabs, retains backoff across resume, and respects exposed `Retry-After`. Main user/phone/message reads use 15-second intervals; admin/email/payment polling uses 30 seconds. Concurrent identical GET requests with the same token share one in-flight request. Fetches time out after 20 seconds. Payment checks cannot overlap in the panel.
- Existing authentication rate limiters now return `Retry-After` and log the endpoint when rejecting a request. CORS exposes the header. Mutation requests are not automatically replayed.

## Diagnostics and operations

Metadata-only worker logs include dispatch ID, queue kind, operation (`send` or `status`), send/poll counts, consecutive failures, elapsed time, response status, sanitized provider error code, server retry hint, and next due time. They do not include recipients, codes, content, connection strings or exception bodies. Default HttpClient information logs are suppressed because request query strings can contain personal/payment identifiers.

The admin email report exposes poll counts, consecutive failures and next-check time. `accepted` and `submitted` describe submission progress, **not inbox delivery**. Use Azure delivery events to investigate actual delivery delays; excessive status checks cannot accelerate delivery.

```powershell
kubectl --context ravand -n darvazeh logs deployment/fieldsense-api --since=2h |
  Select-String 'Email .*provider HTTP|Email .*status checked|Email .*deferred|Email worker retry|API rate limit'
```

For aggregates, query Azure Monitor `ApiRequests` by Operation and StatusCode, and Log Analytics `ACSEmailSendMailOperational` / `ACSEmailStatusUpdateOperational`. Keep recipient/content fields out of exported evidence.

The budgets cover this application and processes sharing its database. Other applications/resources in the same Azure subscription, portal activity, or lower provider-assigned quotas can still cause throttling. Coordinate a subscription-wide gateway or allocate a smaller per-app budget when sharing the quota; request a verified quota increase before increasing these constants. Multiple independent databases do not share this budget. Keep the current single worker deployment and PVC topology.

## Verification

```powershell
dotnet run --project scripts/email-outbox-tests/EmailOutbox.Tests.csproj --no-restore
node scripts/verify-polling.mjs
python scripts/verify-password-auth.py
npm --prefix web run build
npm --prefix web run lint
```

Tests cover rolling limits, concurrency, persistent cooldown, Retry-After formats, increasing status failure counts, expiry, stable operation IDs, ambiguous-timeout recovery, quota release, hidden/offline pause, cleanup and slow-request non-overlap. Browser fixture checks verify GET coalescing and sequential polling. All mail transport tests use fakes; no production email or payment is generated for verification.
