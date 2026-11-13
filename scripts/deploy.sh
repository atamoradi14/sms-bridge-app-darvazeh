#!/usr/bin/env bash
# Darvazeh: namespace darvazeh, pods pinned to ravand-worker-01.
# Default: local kubectl --context ravand (the worker has no kubeconfig).
# ./scripts/deploy.sh apply --user myuser --tag v2.0.1
# ./scripts/deploy.sh status
# Optional SSH, once a kubeconfig exists there: --ssh ravand-worker-01
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ "${DARVAZEH_ENV_LOADED:-}" != 1 && "${1:-apply}" != status && "${1:-apply}" != delete ]]; then
  exec python3 "$ROOT/scripts/deployment-env.py" run -- bash "$0" "$@"
fi
NAMESPACE="${NAMESPACE:-darvazeh}"
DOCKERHUB_USERNAME="${DOCKERHUB_USERNAME:-${DOCKER_USER:-}}"
IMAGE_TAG="${IMAGE_TAG:-${TAG:-latest}}"
SSH_HOST="${SSH_HOST:-}"
KUBE_CONTEXT="${KUBE_CONTEXT:-ravand}"
MODE=apply
INCLUDE_INGRESS=0
INCLUDE_NODEPORT=0
DRY_RUN=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    apply|set-image|status|delete) MODE="$1"; shift ;;
    --local) SSH_HOST=''; shift ;;
    --ssh) SSH_HOST="$2"; shift 2 ;;
    --tag) IMAGE_TAG="$2"; shift 2 ;;
    --user) DOCKERHUB_USERNAME="$2"; shift 2 ;;
    --namespace) NAMESPACE="$2"; shift 2 ;;
    --context) KUBE_CONTEXT="$2"; shift 2 ;;
    --include-ingress) INCLUDE_INGRESS=1; shift ;;
    --skip-ingress) INCLUDE_INGRESS=0; shift ;;
    --include-nodeport) INCLUDE_NODEPORT=1; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    -h|--help) head -n 6 "$0"; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 1 ;;
  esac
done
[[ "$NAMESPACE" == darvazeh ]] || { echo 'Namespace must be darvazeh' >&2; exit 1; }
[[ -z "$SSH_HOST" || "$SSH_HOST" == ravand-worker-01 ]] || { echo 'SSH target must be ravand-worker-01' >&2; exit 1; }
[[ "$KUBE_CONTEXT" =~ ^[a-zA-Z0-9_.-]+$ ]] || { echo 'Invalid context' >&2; exit 1; }
[[ "$IMAGE_TAG" =~ ^[a-zA-Z0-9_][a-zA-Z0-9_.-]{0,127}$ ]] || { echo 'Invalid image tag' >&2; exit 1; }
if [[ "$MODE" == apply || "$MODE" == set-image ]]; then
  [[ "$DOCKERHUB_USERNAME" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || { echo 'Set DOCKERHUB_USERNAME or --user' >&2; exit 1; }
fi
kube() {
  if [[ -n "$SSH_HOST" ]]; then
    local command
    printf -v command '%q ' kubectl --context "$KUBE_CONTEXT" -n "$NAMESPACE" "$@"
    ssh -o BatchMode=yes "$SSH_HOST" "$command"
  else
    kubectl --context "$KUBE_CONTEXT" -n "$NAMESPACE" "$@"
  fi
}
wait_rollouts() {
  kube rollout status deployment/fieldsense-api --timeout=180s
  kube rollout status deployment/fieldsense-web --timeout=180s
  kube get pods -o wide
}
TMP=''
REMOTE_DIR=''
cleanup() {
  if [[ -n "$REMOTE_DIR" ]]; then ssh -o BatchMode=yes "$SSH_HOST" "rm -rf -- $REMOTE_DIR" || true; fi
  if [[ -n "$TMP" ]]; then rm -rf -- "$TMP"; fi
}
trap cleanup EXIT
echo "==> $MODE | namespace=$NAMESPACE | node=ravand-worker-01 | context=$KUBE_CONTEXT"
case "$MODE" in
  apply)
    python3 "$ROOT/scripts/deployment-env.py" check
    TMP="$(mktemp -d)"
    for f in "$ROOT"/deploy/k8s/*.yaml; do
      base="$(basename "$f")"
      case "$base" in
        kustomization.yaml|20-secret.example.yaml|*.local.yaml) continue ;;
        60-ingress.yaml) [[ "$INCLUDE_INGRESS" == 1 ]] || continue ;;
        55-web-service-nodeport.yaml) [[ "$INCLUDE_NODEPORT" == 1 ]] || continue ;;
      esac
      sed -e "s|DOCKERHUB_USERNAME|$DOCKERHUB_USERNAME|g" -e "s|IMAGE_TAG|$IMAGE_TAG|g" "$f" > "$TMP/$base"
    done
    python3 "$ROOT/scripts/deployment-env.py" render --output "$TMP"
    if [[ "$DRY_RUN" == 1 ]]; then
      ls "$TMP"
      echo '==> Render completed; no cluster changes'
      exit 0
    fi
    kube get node ravand-worker-01
    if [[ -n "$SSH_HOST" ]]; then
      REMOTE_DIR="$(ssh -o BatchMode=yes "$SSH_HOST" 'mktemp -d /tmp/darvazeh-k8s-XXXXXXXX')"
      [[ "$REMOTE_DIR" =~ ^/tmp/darvazeh-k8s-[a-zA-Z0-9]+$ ]] || { REMOTE_DIR=''; exit 1; }
      scp -o BatchMode=yes -q "$TMP/"*.yaml "$SSH_HOST:$REMOTE_DIR/"
      MANIFEST_DIR="$REMOTE_DIR"
    else MANIFEST_DIR="$TMP"; fi
    kube apply -f "$MANIFEST_DIR/00-namespace.yaml"
    kube apply --dry-run=server -f "$MANIFEST_DIR"
    kube apply -f "$MANIFEST_DIR"
    wait_rollouts
    ;;
  set-image)
    python3 "$ROOT/scripts/deployment-env.py" check
    if [[ "$DRY_RUN" == 1 ]]; then echo "Would deploy $DOCKERHUB_USERNAME/fieldsense-api:$IMAGE_TAG and $DOCKERHUB_USERNAME/fieldsense-web:$IMAGE_TAG"; exit 0; fi
    for name in fieldsense-api fieldsense-web; do
      selector="$(kube get "deployment/$name" -o 'jsonpath={.spec.template.spec.nodeSelector.kubernetes\.io/hostname}')"
      [[ "$selector" == ravand-worker-01 ]] || { echo "Run apply first: $name is not pinned to ravand-worker-01" >&2; exit 1; }
    done
    kube set image deployment/fieldsense-api "api=$DOCKERHUB_USERNAME/fieldsense-api:$IMAGE_TAG"
    kube set image deployment/fieldsense-web "web=$DOCKERHUB_USERNAME/fieldsense-web:$IMAGE_TAG"
    wait_rollouts
    ;;
  status) kube get deployments,pods,services,ingress,pvc -o wide ;;
  delete)
    if [[ "$DRY_RUN" == 1 ]]; then echo 'Would delete namespace darvazeh'; exit 0; fi
    kube delete namespace darvazeh --ignore-not-found
    ;;
esac
echo '==> Done'
