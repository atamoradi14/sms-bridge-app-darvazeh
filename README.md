# دروازه ها باز است (v2)

Iran SIM home-phone SMS gateway: leave your phone + SIM in Iran, use the web app abroad to **read inbound SMS** and (when admin grants relay) **send SMS out through that phone**.

Tag **`v1.0`** is the earlier Next.js sensors + SMTP Android forwarder. Current `main` is the v2 stack below.

## Architecture

| Piece | Role |
| --- | --- |
| **ASP.NET 8 API** (`server/FieldSense.Api`) | Auth, inbox, admin grants, device agent endpoints, ACS email |
| **Vite React web** (`web/`) | Single UI for admin + users (email OTP login) |
| **Android agent** (`android-companion/`) | Phone in Iran: upload inbound SMS, send queued outbound SMS |

```
Foreign user (web)
    ↕ HTTPS JWT
API (دروازه ها باز است)  ←→  Azure ACS (OTP + optional SMS→email)
    ↕ X-Device-Token
Android phone (Iran SIM)
```

## Quick start

### 1. API

```bash
cd server/FieldSense.Api
# optional: export FieldSense__AdminEmail=... FieldSense__AdminPassword=... FieldSense__AcsConnectionString=...
dotnet run --urls http://0.0.0.0:5088
```

Swagger: http://127.0.0.1:5088/swagger  
Health: http://127.0.0.1:5088/health

Default admin (change in production):

- Email: `admin@ndorbit.co.nz` (`FieldSense__AdminEmail`)
- Password: `ChangeMeAdmin!` (`FieldSense__AdminPassword`) — required **in addition** to OTP when that email signs in
- ACS mock on by default (`FieldSense__AcsMock=true`) — OTP printed in API logs / returned as `debugCode`

### 2. Web

```bash
cd web
npm install
npm run dev
```

App: [http://127.0.0.1:43123](http://127.0.0.1:43123)

Set `VITE_API_BASE` if the API is not on `http://127.0.0.1:5088`.

### 3. Android phone (Iran)

1. Web → **Device** → generate device token  
2. Install companion APK (`android-companion`, minSdk 24)  
3. Paste API base URL + token → Relay on  
4. Keep SMS + battery exemptions granted  

See `android-companion/README.md`.

## Auth model

- **Everyone**: email login with OTP via Azure Communication Services Email  
- **Admin**: email listed in `.env` / `FieldSense__AdminEmail` must also enter `FieldSense__AdminPassword` after OTP  
- **Users**: OTP only; **Send via Iran phone** appears only after admin grants relay  

## User features

- Inbox of inbound + outbound SMS for their paired device  
- Optional **forward inbound SMS to email** (ACS)  
- Pair Android device token  
- Send SMS (queued → phone → `SmsManager`) when `CanRelaySms` is true  

## Admin APIs

- `GET /api/admin/users`  
- `PUT /api/admin/users/{id}/relay` `{ "canRelaySms": true }`  
- `GET /api/admin/stats`  

## Device agent APIs

Header: `X-Device-Token: <token>`

- `POST /api/device/heartbeat`  
- `POST /api/device/inbound-sms` `{ "from", "body", "receivedAt?" }`  
- `GET /api/device/outbound/pending`  
- `POST /api/device/outbound/{jobId}/result` `{ "status": "sent"|"failed", "error?" }`  

## ACS configuration

```bash
export FieldSense__AcsMock=false
export FieldSense__AcsConnectionString='endpoint=https://....;accesskey=...'
export FieldSense__AcsSenderAddress='DoNotReply@your-verified-domain'
export FieldSense__AdminEmail='you@example.com'
export FieldSense__AdminPassword='strong-secret'
export FieldSense__JwtKey='long-random-secret-at-least-32-chars'
export FieldSense__CorsOrigin='http://127.0.0.1:43123,https://your-web-host'
```

## Legacy v1.0

Checkout tag `v1.0` for the original Next.js sensor demo + Gmail SMTP Android forwarder.

## Deploy (Docker + Kubernetes)

See **[deploy/README.md](deploy/README.md)** for Docker Hub build/push, Helm-less YAML under namespace `sms-forwarder`, and laptop scripts that SSH to `helsinki-server`.

```bash
export DOCKERHUB_USERNAME=yourdockerhub
export IMAGE_TAG=v2.0.0
./scripts/build-and-push.sh
./scripts/deploy.sh apply --ssh helsinki-server --skip-ingress
# later image rolls:
./scripts/deploy.sh set-image --tag v2.0.0
```

GitHub Actions: `.github/workflows/docker-build-push.yml` and `deploy-k8s.yml` (add Docker Hub / kube or SSH secrets).
