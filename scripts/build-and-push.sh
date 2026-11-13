#!/usr/bin/env bash
# Build FieldSense API + web images and push to Docker Hub.
# Usage (from repo root or anywhere):
#   ./scripts/build-and-push.sh
#   DOCKERHUB_USERNAME=myuser IMAGE_TAG=v2.0.1 ./scripts/build-and-push.sh
#   ./scripts/build-and-push.sh --no-push          # local build only
#   ./scripts/build-and-push.sh --api-only
#   ./scripts/build-and-push.sh --web-only
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
if [[ "${DARVAZEH_ENV_LOADED:-}" != 1 ]]; then
  exec python3 "$ROOT/scripts/deployment-env.py" run -- bash "$0" "$@"
fi

DOCKERHUB_USERNAME="${DOCKERHUB_USERNAME:-${DOCKER_USER:-}}"
IMAGE_TAG="${IMAGE_TAG:-${TAG:-latest}}"
PLATFORM="${PLATFORM:-linux/amd64}"
PUSH=1
BUILD_API=1
BUILD_WEB=1

while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-push) PUSH=0; shift ;;
    --api-only) BUILD_WEB=0; shift ;;
    --web-only) BUILD_API=0; shift ;;
    --tag) IMAGE_TAG="$2"; shift 2 ;;
    --user) DOCKERHUB_USERNAME="$2"; shift 2 ;;
    -h|--help)
      sed -n '2,12p' "$0"
      exit 0
      ;;
    *)
      echo "Unknown arg: $1" >&2
      exit 1
      ;;
  esac
done

if [[ -z "$DOCKERHUB_USERNAME" ]]; then
  echo "Set DOCKERHUB_USERNAME (Docker Hub account/org)." >&2
  exit 1
fi

API_IMAGE="${DOCKERHUB_USERNAME}/fieldsense-api:${IMAGE_TAG}"
WEB_IMAGE="${DOCKERHUB_USERNAME}/fieldsense-web:${IMAGE_TAG}"

echo "==> Docker Hub user: ${DOCKERHUB_USERNAME}"
echo "==> Tag: ${IMAGE_TAG}"
echo "==> Platform: ${PLATFORM}"

if [[ "$PUSH" -eq 1 ]]; then
  python3 "$ROOT/scripts/deployment-env.py" check --build-only
  if ! docker info >/dev/null 2>&1; then
    echo "Docker daemon not reachable." >&2
    exit 1
  fi
  if [[ -n "${DOCKERHUB_TOKEN:-}" && -n "${DOCKERHUB_USERNAME:-}" ]]; then
    echo "$DOCKERHUB_TOKEN" | docker login -u "$DOCKERHUB_USERNAME" --password-stdin
  else
    echo "Note: ensure you are logged in (docker login) or set DOCKERHUB_TOKEN."
  fi
fi

if [[ "$BUILD_API" -eq 1 ]]; then
  echo "==> Building ${API_IMAGE}"
  docker build \
    --platform "$PLATFORM" \
    -f deploy/docker/Dockerfile.api \
    -t "$API_IMAGE" \
    -t "${DOCKERHUB_USERNAME}/fieldsense-api:latest" \
    .
  if [[ "$PUSH" -eq 1 ]]; then
    echo "==> Pushing ${API_IMAGE}"
    docker push "$API_IMAGE"
    docker push "${DOCKERHUB_USERNAME}/fieldsense-api:latest"
  fi
fi

if [[ "$BUILD_WEB" -eq 1 ]]; then
  echo "==> Building ${WEB_IMAGE}"
  docker build \
    --platform "$PLATFORM" \
    -f deploy/docker/Dockerfile.web \
    --build-arg "VITE_API_BASE=${VITE_API_BASE:-}" \
    -t "$WEB_IMAGE" \
    -t "${DOCKERHUB_USERNAME}/fieldsense-web:latest" \
    .
  if [[ "$PUSH" -eq 1 ]]; then
    echo "==> Pushing ${WEB_IMAGE}"
    docker push "$WEB_IMAGE"
    docker push "${DOCKERHUB_USERNAME}/fieldsense-web:latest"
  fi
fi

echo "==> Done"
echo "API: ${API_IMAGE}"
echo "WEB: ${WEB_IMAGE}"
