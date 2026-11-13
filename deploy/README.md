# Darvazeh deployment — existing Ravand worker

Namespace: **`darvazeh`**. Both application Deployments require
`kubernetes.io/hostname: ravand-worker-01`. Services are namespace-local ClusterIP
resources; Services themselves are not scheduled onto nodes. The API uses a 1 Gi
PVC. The cluster's default `local-path` StorageClass uses `WaitForFirstConsumer`,
so a new volume follows the API pod's worker placement.

## Build and push

From the repository root:

```powershell
$env:DOCKERHUB_USERNAME = "yourdockerhub"
$env:IMAGE_TAG = "v2.0.0"
docker login
./scripts/build-and-push.ps1
./scripts/deploy.ps1 apply
./scripts/deploy.ps1 status
```

Bash equivalents:

```bash
export DOCKERHUB_USERNAME=yourdockerhub IMAGE_TAG=v2.0.0
./scripts/build-and-push.sh
./scripts/deploy.sh apply
./scripts/deploy.sh status
```

Both deploy scripts default to **local kubectl, context `ravand`**. The worker is
a k3s agent and currently has no kubectl kubeconfig; SSH access alone does not
grant Kubernetes API access. This matches Hamafarin's deployment approach. The
scripts check the worker exists, apply the namespace, validate the rendered
resources on the server, apply them, wait for both rollouts and show pod nodes.
Native command failures stop deployment.

Image-only updates require existing worker selectors (otherwise run `apply`):

```powershell
./scripts/deploy.ps1 set-image -Tag v2.0.1
./scripts/deploy.ps1 apply -Username yourdockerhub -Tag v2.0.1 -DryRun
```

`-DryRun` / `--dry-run` renders locally without cluster changes; real `apply`
performs server-side validation. `delete` removes the entire `darvazeh` namespace,
including its PVC. No other namespace is accepted by the deployment scripts.

## Configuration before first deployment

The root `.env` is the authoritative source for deployment credentials and app
settings. Scripts parse it as data; it is never sourced as shell code. Python 3
is required for both the PowerShell and Bash workflows.

Required keys:

- `DOCKERHUB_USERNAME` (or `DOCKERHUB_USER`), `DOCKERHUB_TOKEN` (also creates `darvazeh-registry` for image pulls)
- `FieldSense__AdminEmail`, `FieldSense__AdminPassword`, `FieldSense__JwtKey`
- `FieldSense__AcsConnectionString`, `FieldSense__AcsSenderAddress`, `FieldSense__AcsMock=false`
- `FieldSense__CorsOrigin` (allowed browser origins)

Optional `FieldSense__PublicApiBase` sets a public HTTPS origin; when supplied,
include it in CORS too. It may be omitted for an internal deployment, and the
API derives its public base from forwarded headers when Caddy is configured.

Optional `IMAGE_TAG` selects the release tag. JWT keys must contain at least 32
UTF-8 bytes. Production apply rejects development credential placeholders and
mock email login. Verify configuration without displaying secret values:

```powershell
python scripts/deployment-env.py check
```

Every full `apply` generates application and registry Secrets plus ConfigMaps
from `.env` into a temporary directory. It does not deploy credentials from the
checked-in bootstrap `20-secret.yaml`. Temporary generated files are removed
when the command exits. Root `.dockerignore` excludes environment files from
both image build contexts. Secret values are not passed as build arguments.

Runtime `config.js` uses `FieldSense__PublicApiBase` when configured, or
`window.location.origin` otherwise, enabling live API mode through Caddy. The persistent SQLite connection remains
`Data Source=/data/fieldsense.db`. Full apply updates config and Secret resources;
image-only `set-image` validates `.env` but does not update configuration. Use
`apply` after editing `.env`; generated pod-template hashes trigger a rollout when settings change.

## Public URLs and shared worker Caddy

Live endpoints:

- UI: https://darvazeh.daryajan.com/
- API health: https://api1.daryajan.com/health
- API documentation: https://api1.daryajan.com/swagger/
- Mobile configuration: https://darvazeh.daryajan.com/mobile-config.json
- API mobile configuration: https://api1.daryajan.com/api/public/mobile-config

Both hostnames have proxied Cloudflare A records pointing to `2.29.120.138`.
Caddy in `hamafarin-apps` terminates origin TLS using its existing Cloudflare
DNS-challenge configuration and routes across namespaces to Darvazeh's ClusterIP
Services. The UI also proxies `/api`, `/health` and `/swagger` for same-origin
compatibility. No additional Caddy, Ingress, NodePort or LoadBalancer was created.

The managed site block is `deploy/caddy/darvazeh.Caddyfile`. The root `.env` has
`FieldSense__PublicApiBase=https://api1.daryajan.com`; CORS includes the UI origin.
The generated web ConfigMap supplies both `config.js` and `mobile-config.json`,
so endpoint changes do not require rebuilding images. Full `apply` rolls pods
when the generated configuration hash changes.

To validate or reapply only these gateway routes and DNS records:

```powershell
python scripts/configure-public-endpoints.py
python scripts/configure-public-endpoints.py --apply
```

This helper reads `CLOUDFLARE_API_TOKEN` from root `.env`, changes only the two
named DNS records, validates the combined Caddyfile, and stores recovery snapshots
under ignored `.local/`. It reloads Caddy through stdin because the existing
ConfigMap mount uses `subPath`, and updates the gateway's owning desired-state
files in sibling `hamafarin-cluster/manifests/`. Existing routes are preserved.
The token is used by the operator helper and existing gateway; it is not passed
to the Darvazeh API container.

Ingress and NodePort remain excluded by default. Their manifests are optional
alternatives for a different gateway setup, not part of the live public path.

## Optional SSH mode

The alias is now **`ravand-worker-01`** everywhere in this project:

```sh
ssh ravand-worker-01
```

Once a working kubeconfig with context `ravand` is explicitly provisioned on that
worker, SSH mode is available:

```powershell
./scripts/deploy.ps1 apply -SshHost ravand-worker-01
```

```bash
./scripts/deploy.sh apply --ssh ravand-worker-01
```

The scripts do not copy cluster credentials to the worker. Prefer the working
local `ravand` context today.

## GitHub Actions

- `docker-build-push.yml`: build/push images using `DOCKERHUB_USERNAME` and
  `DOCKERHUB_TOKEN`.
- `deploy-k8s.yml`: manual apply or image update. Supply base64 `KUBE_CONFIG` with
  a reachable API and context `ravand`, or `RAVAND_WORKER_SSH_KEY` plus
  `RAVAND_WORKER_SSH_HOST` (`user@host`) for the optional provisioned SSH path.
  Existing deployment SSH secrets must be renamed to the new worker names.
  Hosted runners need network access to the Kubernetes API when using kubeconfig.
  Also supply `DARVAZEH_ENV_B64`, a base64 encoding of the root `.env`; the workflow
  reconstructs that private file and validates it before deployment.

## Local Docker smoke test

```bash
DOCKERHUB_USERNAME=local IMAGE_TAG=local \
  docker compose -f deploy/docker/docker-compose.yml up --build
```

Web: `http://127.0.0.1:43180`; API health: `http://127.0.0.1:15088/health`.
Compose is independent of the Kubernetes namespace and node settings.

## Membership and brand release — 11 November 2026

Release `release-20261111-05` includes the refreshed portal/email/Android branding,
admin account `admin@ndorbit.co.nz`, three-day trials with a combined allowance of
50 messages, independent send/receive controls, administrator-managed plans,
subscription history, a persistent usage ledger and metadata-only activity logs.
Android version 2.1.1 (6) is published with signing-certificate continuity.

Before applying a database upgrade run `python scripts/backup-database.py`.
Verified, private online snapshots are retained on the worker and off-node under
`.local/backups/`. Startup upgrades are additive and idempotent; existing message
and account data are preserved. Trial history is initialized for existing users
from their original account creation times and existing messages are backfilled
into usage accounting. Subsequent restarts do not reset allowances or renew trials.

Run `python scripts/verify-membership.py` for isolated HTTP/database regression
checks. The suite does not send real email or SMS. Browser checks cover the login
page, plan creation/admin rendering, device screen, responsive layouts and email
preview. Native dispatch still needs a real SIM-bearing Android phone to verify
carrier behavior; the release build, packaged UI, signature and download are checked.

Plans are currently activated by administrators; automatic payment collection is
not configured. Counts include accepted incoming messages and queued outgoing
messages (including failed outgoing attempts). A retry with the same message
identifier is not counted twice. Explicit expiry/feature revocation also prevents
queued dispatch; quota exhaustion alone does not strand an already-reserved send.
