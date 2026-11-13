#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Build FieldSense API + web images and push to Docker Hub.

.EXAMPLE
  ./scripts/build-and-push.ps1 -Username myuser -Tag v2.0.1
  ./scripts/build-and-push.ps1 -NoPush
  $env:DOCKERHUB_USERNAME='myuser'; ./scripts/build-and-push.ps1
#>
[CmdletBinding()]
param(
  [string]$Username = $(if ($env:DOCKERHUB_USERNAME) { $env:DOCKERHUB_USERNAME } elseif ($env:DOCKER_USER) { $env:DOCKER_USER } else { "" }),
  [string]$Tag = $(if ($env:IMAGE_TAG) { $env:IMAGE_TAG } elseif ($env:TAG) { $env:TAG } else { "latest" }),
  [string]$Platform = $(if ($env:PLATFORM) { $env:PLATFORM } else { "linux/amd64" }),
  [switch]$NoPush,
  [switch]$ApiOnly,
  [switch]$WebOnly
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $Root
. (Join-Path $PSScriptRoot 'deployment-env.ps1')
Import-DeploymentEnv $Root
if (-not $PSBoundParameters.ContainsKey('Username')) { $Username = $env:DOCKERHUB_USERNAME }
if (-not $PSBoundParameters.ContainsKey('Tag') -and $env:IMAGE_TAG) { $Tag = $env:IMAGE_TAG }
if (-not $NoPush) {
  & python (Join-Path $PSScriptRoot 'deployment-env.py') check --build-only
  if ($LASTEXITCODE -ne 0) { throw 'Docker Hub credentials missing from .env' }
}
# Fail immediately on Docker login/build/push errors (PowerShell 7.3+).
$PSNativeCommandUseErrorActionPreference = $true

if ([string]::IsNullOrWhiteSpace($Username)) {
  throw "Set -Username or DOCKERHUB_USERNAME"
}

$ApiImage = "$Username/fieldsense-api:$Tag"
$WebImage = "$Username/fieldsense-web:$Tag"
$Push = -not $NoPush
$BuildApi = -not $WebOnly
$BuildWeb = -not $ApiOnly

Write-Host "==> Docker Hub user: $Username"
Write-Host "==> Tag: $Tag"
Write-Host "==> Platform: $Platform"

if ($Push) {
  if ($env:DOCKERHUB_TOKEN) {
    $env:DOCKERHUB_TOKEN | docker login -u $Username --password-stdin
  } else {
    Write-Host "Note: ensure you are logged in (docker login) or set DOCKERHUB_TOKEN."
  }
}

if ($BuildApi) {
  Write-Host "==> Building $ApiImage"
  docker build --platform $Platform -f deploy/docker/Dockerfile.api `
    -t $ApiImage -t "$Username/fieldsense-api:latest" .
  if ($Push) {
    docker push $ApiImage
    docker push "$Username/fieldsense-api:latest"
  }
}

if ($BuildWeb) {
  Write-Host "==> Building $WebImage"
  $viteBase = if ($env:VITE_API_BASE) { $env:VITE_API_BASE } else { "" }
  docker build --platform $Platform -f deploy/docker/Dockerfile.web `
    --build-arg "VITE_API_BASE=$viteBase" `
    -t $WebImage -t "$Username/fieldsense-web:latest" .
  if ($Push) {
    docker push $WebImage
    docker push "$Username/fieldsense-web:latest"
  }
}

Write-Host "==> Done"
Write-Host "API: $ApiImage"
Write-Host "WEB: $WebImage"
