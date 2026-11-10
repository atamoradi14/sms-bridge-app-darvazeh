#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Deploy FieldSense to Kubernetes namespace sms-forwarder.

.DESCRIPTION
  Default path uses SSH host alias "helsinki-server" (key auth) so you can run
  this from your laptop while kubectl lives on that server.

.EXAMPLE
  ./scripts/deploy.ps1 apply -Username myuser -Tag v2.0.1
  ./scripts/deploy.ps1 apply -Local -SkipIngress
  ./scripts/deploy.ps1 set-image -Tag v2.0.1
  ./scripts/deploy.ps1 status
#>
[CmdletBinding()]
param(
  [Parameter(Position = 0)]
  [ValidateSet("apply", "set-image", "status", "delete")]
  [string]$Mode = "apply",

  [string]$Username = $(if ($env:DOCKERHUB_USERNAME) { $env:DOCKERHUB_USERNAME } elseif ($env:DOCKER_USER) { $env:DOCKER_USER } else { "" }),
  [string]$Tag = $(if ($env:IMAGE_TAG) { $env:IMAGE_TAG } elseif ($env:TAG) { $env:TAG } else { "latest" }),
  [string]$Namespace = $(if ($env:NAMESPACE) { $env:NAMESPACE } else { "sms-forwarder" }),
  [string]$SshHost = $(if ($env:SSH_HOST) { $env:SSH_HOST } else { "helsinki-server" }),
  [switch]$Local,
  [switch]$SkipIngress,
  [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $Root

$UseSsh = -not $Local
if ($Mode -in @("apply", "set-image") -and [string]::IsNullOrWhiteSpace($Username)) {
  throw "Set -Username or DOCKERHUB_USERNAME"
}

$ApiImage = "$Username/fieldsense-api:$Tag"
$WebImage = "$Username/fieldsense-web:$Tag"

function Render-Manifests([string]$OutDir) {
  New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
  Get-ChildItem (Join-Path $Root "deploy/k8s/*.yaml") | ForEach-Object {
    if ($_.Name -eq "kustomization.yaml") { return }
    if ($_.Name -eq "20-secret.example.yaml") { return }
    if ($SkipIngress -and $_.Name -eq "60-ingress.yaml") { return }
    $content = Get-Content $_.FullName -Raw
    $content = $content.Replace("DOCKERHUB_USERNAME", $Username).Replace("IMAGE_TAG", $Tag)
    Set-Content -Path (Join-Path $OutDir $_.Name) -Value $content -NoNewline
  }
}

Write-Host "==> Mode: $Mode"
Write-Host "==> Namespace: $Namespace"
Write-Host "==> SSH: $(if ($UseSsh) { $SshHost } else { 'disabled (local kubectl)' })"
if ($Username) { Write-Host "==> Images: $ApiImage , $WebImage" }

switch ($Mode) {
  "apply" {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("fieldsense-k8s-" + [guid]::NewGuid().ToString("n"))
    Render-Manifests $tmp
    try {
      if ($DryRun) {
        Write-Host "==> Dry-run manifests in $tmp"
        Get-ChildItem $tmp | Format-Table Name
        $files = Get-ChildItem $tmp -Filter *.yaml
        if (-not $files) { throw "no manifests rendered" }
        foreach ($f in $files) {
          $text = Get-Content $f.FullName -Raw
          if ($text -match "DOCKERHUB_USERNAME") { throw "unsubstituted placeholder in $($f.Name)" }
          if ($text -notmatch "apiVersion:" -or $text -notmatch "kind:") { throw "invalid manifest $($f.Name)" }
        }
        Write-Host "==> validated $($files.Count) manifest files"
        if ((Get-Command kubectl -ErrorAction SilentlyContinue) -and (kubectl cluster-info 2>$null)) {
          kubectl apply --dry-run=client --validate=$false -f $tmp | Out-Null
          Write-Host "==> kubectl apply --dry-run=client OK"
        } else {
          Write-Host "==> No local cluster; skipped kubectl dry-run"
        }
        break
      }
      if ($UseSsh) {
        Write-Host "==> Copying manifests to ${SshHost}:/tmp/fieldsense-k8s-$Namespace"
        ssh -o BatchMode=yes $SshHost "rm -rf /tmp/fieldsense-k8s-$Namespace && mkdir -p /tmp/fieldsense-k8s-$Namespace"
        scp -o BatchMode=yes -q -r "$tmp\*" "${SshHost}:/tmp/fieldsense-k8s-$Namespace/"
        ssh -o BatchMode=yes $SshHost "kubectl apply -f /tmp/fieldsense-k8s-$Namespace && kubectl -n $Namespace rollout status deployment/fieldsense-api --timeout=180s && kubectl -n $Namespace rollout status deployment/fieldsense-web --timeout=180s"
      } else {
        kubectl apply -f $tmp
        kubectl -n $Namespace rollout status deployment/fieldsense-api --timeout=180s
        kubectl -n $Namespace rollout status deployment/fieldsense-web --timeout=180s
      }
    } finally {
      Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    }
  }
  "set-image" {
    if ($DryRun) {
      Write-Host "Would set api=$ApiImage web=$WebImage"
      break
    }
    if ($UseSsh) {
      $remote = @"
set -euo pipefail
kubectl -n $Namespace set image deployment/fieldsense-api api=$ApiImage
kubectl -n $Namespace set image deployment/fieldsense-web web=$WebImage
kubectl -n $Namespace rollout status deployment/fieldsense-api --timeout=180s
kubectl -n $Namespace rollout status deployment/fieldsense-web --timeout=180s
"@
      $remote | ssh -o BatchMode=yes $SshHost bash -s
    } else {
      kubectl -n $Namespace set image deployment/fieldsense-api "api=$ApiImage"
      kubectl -n $Namespace set image deployment/fieldsense-web "web=$WebImage"
      kubectl -n $Namespace rollout status deployment/fieldsense-api --timeout=180s
      kubectl -n $Namespace rollout status deployment/fieldsense-web --timeout=180s
    }
  }
  "status" {
    if ($UseSsh) {
      ssh -o BatchMode=yes $SshHost "kubectl -n $Namespace get all,ingress,pvc,configmap,secret"
    } else {
      kubectl -n $Namespace get all,ingress,pvc,configmap,secret
    }
  }
  "delete" {
    if ($UseSsh) {
      ssh -o BatchMode=yes $SshHost "kubectl delete namespace $Namespace --ignore-not-found"
    } else {
      kubectl delete namespace $Namespace --ignore-not-found
    }
  }
}

Write-Host "==> Done"
