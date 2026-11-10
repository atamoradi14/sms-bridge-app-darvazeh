#!/usr/bin/env bash
# Deploy FieldSense to Kubernetes (namespace sms-forwarder).
#
# Designed for your laptop:
#   SSH host alias "helsinki-server" (ssh key auth) runs kubectl against the cluster.
#
# Usage:
#   ./scripts/deploy.sh apply
#   ./scripts/deploy.sh apply --local                 # kubectl on this machine
#   ./scripts/deploy.sh apply --ssh helsinki-server
#   ./scripts/deploy.sh apply --skip-ingress
#   ./scripts/deploy.sh set-image --tag v2.0.1
#   DOCKERHUB_USERNAME=myuser IMAGE_TAG=v2.0.1 ./scripts/deploy.sh apply
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

NAMESPACE="${NAMESPACE:-sms-forwarder}"
DOCKERHUB_USERNAME="${DOCKERHUB_USERNAME:-${DOCKER_USER:-}}"
IMAGE_TAG="${IMAGE_TAG:-${TAG:-latest}}"
SSH_HOST="${SSH_HOST:-helsinki-server}"
MODE="apply"
USE_SSH=1
SKIP_INGRESS=0
DRY_RUN=0
KUBE_CONTEXT="${KUBE_CONTEXT:-}"

usage() {
  sed -n '2,18p' "$0"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    apply|set-image|status|delete)
      MODE="$1"; shift ;;
    --local) USE_SSH=0; shift ;;
    --ssh) SSH_HOST="$2"; USE_SSH=1; shift 2 ;;
    --tag) IMAGE_TAG="$2"; shift 2 ;;
    --user) DOCKERHUB_USERNAME="$2"; shift 2 ;;
    --namespace) NAMESPACE="$2"; shift 2 ;;
    --skip-ingress) SKIP_INGRESS=1; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    --context) KUBE_CONTEXT="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown arg: $1" >&2; usage; exit 1 ;;
  esac
done

if [[ -z "$DOCKERHUB_USERNAME" && "$MODE" != "status" && "$MODE" != "delete" ]]; then
  echo "Set DOCKERHUB_USERNAME (images are \$USER/fieldsense-api|web)." >&2
  exit 1
fi

API_IMAGE="${DOCKERHUB_USERNAME}/fieldsense-api:${IMAGE_TAG}"
WEB_IMAGE="${DOCKERHUB_USERNAME}/fieldsense-web:${IMAGE_TAG}"

render_manifests() {
  local out="$1"
  mkdir -p "$out"
  # Copy YAML (optionally skip ingress)
  shopt -s nullglob
  for f in deploy/k8s/*.yaml; do
    base="$(basename "$f")"
    if [[ "$SKIP_INGRESS" -eq 1 && "$base" == "60-ingress.yaml" ]]; then
      continue
    fi
    if [[ "$base" == "kustomization.yaml" ]]; then
      continue
    fi
    if [[ "$base" == "20-secret.example.yaml" ]]; then
      continue
    fi
    sed \
      -e "s|DOCKERHUB_USERNAME|${DOCKERHUB_USERNAME}|g" \
      -e "s|IMAGE_TAG|${IMAGE_TAG}|g" \
      "$f" > "$out/$base"
  done
}

remote_kubectl() {
  local cmd=("$@")
  if [[ "$USE_SSH" -eq 1 ]]; then
    # shellcheck disable=SC2029
    ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new "$SSH_HOST" \
      "kubectl $([[ -n \"$KUBE_CONTEXT\" ]] && echo --context \"$KUBE_CONTEXT\") ${cmd[*]}"
  else
    if [[ -n "$KUBE_CONTEXT" ]]; then
      kubectl --context "$KUBE_CONTEXT" "${cmd[@]}"
    else
      kubectl "${cmd[@]}"
    fi
  fi
}

copy_and_apply() {
  local tmp
  tmp="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf '$tmp'" RETURN
  render_manifests "$tmp/manifests"

  if [[ "$DRY_RUN" -eq 1 ]]; then
    echo "==> Dry-run rendered manifests in $tmp/manifests"
    ls -la "$tmp/manifests"
    # Parse YAML locally; full kubectl dry-run needs a live cluster API.
    python3 - <<'PY' "$tmp/manifests"
import sys, pathlib
try:
    import yaml  # type: ignore
except ImportError:
    yaml = None
root = pathlib.Path(sys.argv[1])
files = sorted(root.glob("*.yaml"))
assert files, "no manifests rendered"
for f in files:
    text = f.read_text()
    assert "DOCKERHUB_USERNAME" not in text, f"unsubstituted placeholder in {f.name}"
    assert "kind:" in text and "apiVersion:" in text, f"invalid manifest {f.name}"
    if yaml:
        for doc in yaml.safe_load_all(text):
            if doc is None:
                continue
            assert "kind" in doc and "apiVersion" in doc
print(f"validated {len(files)} manifest files")
PY
    if command -v kubectl >/dev/null 2>&1 && kubectl cluster-info >/dev/null 2>&1; then
      kubectl apply --dry-run=client --validate=false -f "$tmp/manifests" >/dev/null
      echo "==> kubectl apply --dry-run=client OK (cluster reachable)"
    else
      echo "==> No local cluster; skipped kubectl dry-run (manifest render OK)"
    fi
    return 0
  fi

  if [[ "$USE_SSH" -eq 1 ]]; then
    echo "==> Copying manifests to ${SSH_HOST}:/tmp/fieldsense-k8s-${NAMESPACE}"
    ssh -o BatchMode=yes "$SSH_HOST" "rm -rf /tmp/fieldsense-k8s-${NAMESPACE} && mkdir -p /tmp/fieldsense-k8s-${NAMESPACE}"
    scp -o BatchMode=yes -q -r "$tmp/manifests/." "${SSH_HOST}:/tmp/fieldsense-k8s-${NAMESPACE}/"
    echo "==> kubectl apply -n ${NAMESPACE} via SSH ${SSH_HOST}"
    # shellcheck disable=SC2029
    ssh -o BatchMode=yes "$SSH_HOST" \
      "kubectl apply -f /tmp/fieldsense-k8s-${NAMESPACE} && kubectl -n ${NAMESPACE} rollout status deployment/fieldsense-api --timeout=180s && kubectl -n ${NAMESPACE} rollout status deployment/fieldsense-web --timeout=180s"
  else
    echo "==> kubectl apply -f manifests (local)"
    kubectl apply -f "$tmp/manifests"
    kubectl -n "$NAMESPACE" rollout status deployment/fieldsense-api --timeout=180s || true
    kubectl -n "$NAMESPACE" rollout status deployment/fieldsense-web --timeout=180s || true
  fi
}

set_images() {
  if [[ "$DRY_RUN" -eq 1 ]]; then
    echo "Would set:"
    echo "  fieldsense-api -> ${API_IMAGE}"
    echo "  fieldsense-web -> ${WEB_IMAGE}"
    return 0
  fi
  echo "==> kubectl set image (api=${API_IMAGE}, web=${WEB_IMAGE})"
  if [[ "$USE_SSH" -eq 1 ]]; then
    # shellcheck disable=SC2029
    ssh -o BatchMode=yes "$SSH_HOST" bash -s <<EOF
set -euo pipefail
kubectl -n ${NAMESPACE} set image deployment/fieldsense-api api=${API_IMAGE}
kubectl -n ${NAMESPACE} set image deployment/fieldsense-web web=${WEB_IMAGE}
kubectl -n ${NAMESPACE} rollout status deployment/fieldsense-api --timeout=180s
kubectl -n ${NAMESPACE} rollout status deployment/fieldsense-web --timeout=180s
EOF
  else
    kubectl -n "$NAMESPACE" set image deployment/fieldsense-api "api=${API_IMAGE}"
    kubectl -n "$NAMESPACE" set image deployment/fieldsense-web "web=${WEB_IMAGE}"
    kubectl -n "$NAMESPACE" rollout status deployment/fieldsense-api --timeout=180s
    kubectl -n "$NAMESPACE" rollout status deployment/fieldsense-web --timeout=180s
  fi
}

show_status() {
  if [[ "$USE_SSH" -eq 1 ]]; then
    ssh -o BatchMode=yes "$SSH_HOST" "kubectl -n ${NAMESPACE} get all,ingress,pvc,configmap,secret"
  else
    kubectl -n "$NAMESPACE" get all,ingress,pvc,configmap,secret
  fi
}

delete_all() {
  echo "==> Deleting namespace ${NAMESPACE}"
  if [[ "$USE_SSH" -eq 1 ]]; then
    ssh -o BatchMode=yes "$SSH_HOST" "kubectl delete namespace ${NAMESPACE} --ignore-not-found"
  else
    kubectl delete namespace "$NAMESPACE" --ignore-not-found
  fi
}

echo "==> Mode: ${MODE}"
echo "==> Namespace: ${NAMESPACE}"
echo "==> SSH: $([[ "$USE_SSH" -eq 1 ]] && echo "$SSH_HOST" || echo "disabled (local kubectl)")"
if [[ -n "${DOCKERHUB_USERNAME}" ]]; then
  echo "==> Images: ${API_IMAGE} , ${WEB_IMAGE}"
fi

case "$MODE" in
  apply) copy_and_apply ;;
  set-image) set_images ;;
  status) show_status ;;
  delete) delete_all ;;
  *) usage; exit 1 ;;
esac

echo "==> Done"
