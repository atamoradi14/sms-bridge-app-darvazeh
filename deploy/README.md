# FieldSense deploy (Docker + Kubernetes)

Namespace: **`sms-forwarder`**

Components:

| Workload | Image | Port |
| --- | --- | --- |
| `fieldsense-api` | `$DOCKERHUB_USERNAME/fieldsense-api` | 8080 |
| `fieldsense-web` | `$DOCKERHUB_USERNAME/fieldsense-web` | 80 (nginx, proxies `/api` → API) |

## 1. Build & push (laptop)

```bash
export DOCKERHUB_USERNAME=yourdockerhub
export IMAGE_TAG=v2.0.0
# optional: export DOCKERHUB_TOKEN=...   # else docker login first
./scripts/build-and-push.sh
```

PowerShell:

```powershell
$env:DOCKERHUB_USERNAME = "yourdockerhub"
$env:IMAGE_TAG = "v2.0.0"
./scripts/build-and-push.ps1
```

Local build only (no push):

```bash
./scripts/build-and-push.sh --no-push --user yourdockerhub --tag local
```

## 2. Deploy via SSH `helsinki-server`

Assumes `~/.ssh/config` has:

```sshconfig
Host helsinki-server
  HostName your.server.ip
  User youruser
  IdentityFile ~/.ssh/id_ed25519
```

Full apply (YAML):

```bash
export DOCKERHUB_USERNAME=yourdockerhub
export IMAGE_TAG=v2.0.0
./scripts/deploy.sh apply --ssh helsinki-server --skip-ingress
```

Roll only images after a new push:

```bash
./scripts/deploy.sh set-image --tag v2.0.0
```

PowerShell equivalents: `./scripts/deploy.ps1 apply`, `./scripts/deploy.ps1 set-image`.

Local kubectl (no SSH):

```bash
./scripts/deploy.sh apply --local --skip-ingress
```

## 3. Secrets / config

- Non-secret settings: `deploy/k8s/10-configmap.yaml`
- Secrets: `deploy/k8s/20-secret.yaml` (bootstrap) or create:

```bash
kubectl -n sms-forwarder create secret generic fieldsense-secrets \
  --from-literal=FieldSense__AdminPassword='...' \
  --from-literal=FieldSense__JwtKey='...' \
  --from-literal=FieldSense__AcsConnectionString='endpoint=...;accesskey=...' \
  --dry-run=client -o yaml | kubectl apply -f -
```

Set `FieldSense__AcsMock=false` in the ConfigMap when ACS is configured.

## 4. GitHub Actions (future)

Repo secrets:

| Secret | Purpose |
| --- | --- |
| `DOCKERHUB_USERNAME` | Docker Hub user/org |
| `DOCKERHUB_TOKEN` | Docker Hub access token |
| `KUBE_CONFIG` | Base64 kubeconfig **or** |
| `HELSINKI_SSH_KEY` + `HELSINKI_SSH_HOST` | SSH deploy path (`user@host`) |

Workflows:

- `.github/workflows/docker-build-push.yml` — build/push on `main` / tags / manual
- `.github/workflows/deploy-k8s.yml` — manual deploy (`apply` or `set-image`)

## 5. Local docker compose smoke test

```bash
DOCKERHUB_USERNAME=local IMAGE_TAG=local \
  docker compose -f deploy/docker/docker-compose.yml up --build
```

- Web: http://127.0.0.1:43180  
- API direct: http://127.0.0.1:15088/health  

Compose builds the web image with `VITE_API_BASE=http://127.0.0.1:15088` so the browser talks to the published API port (no pod/bridge proxy required).

## 6. How the browser finds the API in Kubernetes

`web/public/config.js` is overwritten by ConfigMap `fieldsense-web-config`:

- **Ingress (one hostname):** set `window.__FIELDSENSE_API_BASE__ = "";` and keep Ingress paths `/api` → API, `/` → web.
- **NodePort:** set it to your API NodePort, e.g. `http://HEL_SINKI_IP:30088` (web NodePort is `30431`).

## 7. Laptop → `helsinki-server` checklist

```sshconfig
Host helsinki-server
  HostName <ip-or-dns>
  User <you>
  IdentityFile ~/.ssh/id_ed25519
```

```bash
export DOCKERHUB_USERNAME=yourdockerhub
export IMAGE_TAG=v2.0.0
docker login
./scripts/build-and-push.sh
# edit deploy/k8s/10-configmap.yaml CorsOrigin + fieldsense-web-config API URL
./scripts/deploy.sh apply --ssh helsinki-server --skip-ingress   # NodePort path
# or without --skip-ingress after installing ingress-nginx and editing host
./scripts/deploy.sh status
```

