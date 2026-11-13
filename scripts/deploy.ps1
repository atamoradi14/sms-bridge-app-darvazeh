#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Deploy Darvazeh into namespace darvazeh, pinned to ravand-worker-01.
.EXAMPLE
  ./scripts/deploy.ps1 apply -Username myuser -Tag v2.0.1
  ./scripts/deploy.ps1 apply -Username myuser -DryRun
  ./scripts/deploy.ps1 status
  ./scripts/deploy.ps1 status -SshHost ravand-worker-01
#>
[CmdletBinding()]
param(
  [Parameter(Position = 0)]
  [ValidateSet('apply', 'set-image', 'status', 'delete')]
  [string]$Mode = 'apply',
  [string]$Username = $(if ($env:DOCKERHUB_USERNAME) { $env:DOCKERHUB_USERNAME } else { $env:DOCKER_USER }),
  [string]$Tag = $(if ($env:IMAGE_TAG) { $env:IMAGE_TAG } else { 'latest' }),
  [ValidateSet('darvazeh')][string]$Namespace = 'darvazeh',
  [string]$Context = 'ravand',
  [string]$SshHost = $env:SSH_HOST,
  [switch]$Local,
  [switch]$IncludeIngress,
  [switch]$IncludeNodePort,
  [switch]$SkipIngress,
  [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'deployment-env.ps1')
if ($Mode -in @('apply', 'set-image')) {
  Import-DeploymentEnv $Root
  if (-not $PSBoundParameters.ContainsKey('Username')) { $Username = $env:DOCKERHUB_USERNAME }
  if (-not $PSBoundParameters.ContainsKey('Tag') -and $env:IMAGE_TAG) { $Tag = $env:IMAGE_TAG }
  & python (Join-Path $PSScriptRoot 'deployment-env.py') check
  if ($LASTEXITCODE -ne 0) { throw 'Deployment .env validation failed' }
}
$UseSsh = $SshHost -and -not $Local
if ($UseSsh -and $SshHost -ne 'ravand-worker-01') { throw 'SSH target must be ravand-worker-01' }
if ($Context -notmatch '^[a-zA-Z0-9_.-]+$') { throw 'Invalid context name' }
if ($Mode -in @('apply', 'set-image') -and $Username -notmatch '^[a-z0-9][a-z0-9_-]*$') { throw 'Set -Username or DOCKERHUB_USERNAME to a Docker Hub user/org' }
if ($Tag -notmatch '^[a-zA-Z0-9_][a-zA-Z0-9_.-]{0,127}$') { throw 'Invalid image tag' }

function Invoke-Native([string]$Command, [string[]]$Arguments) {
  & $Command @Arguments
  if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
}
function Invoke-Kube([string[]]$Arguments) {
  if ($UseSsh) {
    # Remote arguments come from internal commands or validated parameters.
    # Quote every argument for the remote POSIX shell, including JSONPath braces.
    $quoted = foreach ($arg in (@('--context', $Context, '-n', $Namespace) + $Arguments)) {
      "'" + $arg.Replace("'", "'\''") + "'"
    }
    Invoke-Native 'ssh' @('-o', 'BatchMode=yes', $SshHost, "kubectl $($quoted -join ' ')")
  } else {
    Invoke-Native 'kubectl' (@('--context', $Context, '-n', $Namespace) + $Arguments)
  }
}
function Wait-Rollouts {
  foreach ($name in @('fieldsense-api', 'fieldsense-web')) {
    Invoke-Kube @('rollout', 'status', "deployment/$name", '--timeout=180s')
  }
  Invoke-Kube @('get', 'pods', '-o', 'wide')
}

Write-Host "==> $Mode | namespace=$Namespace | node=ravand-worker-01 | context=$Context"
switch ($Mode) {
  'apply' {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('darvazeh-k8s-' + [guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    $remoteDir = $null
    try {
      foreach ($file in Get-ChildItem (Join-Path $Root 'deploy/k8s') -Filter '*.yaml') {
        if ($file.Name -in @('kustomization.yaml', '20-secret.example.yaml') -or $file.Name -like '*.local.yaml') { continue }
        if ($file.Name -eq '60-ingress.yaml' -and (-not $IncludeIngress -or $SkipIngress)) { continue }
        if ($file.Name -eq '55-web-service-nodeport.yaml' -and -not $IncludeNodePort) { continue }
        $content = (Get-Content $file.FullName -Raw).Replace('DOCKERHUB_USERNAME', $Username).Replace('IMAGE_TAG', $Tag)
        if ($content -match 'DOCKERHUB_USERNAME|IMAGE_TAG') { throw "Unresolved manifest: $($file.Name)" }
        [IO.File]::WriteAllText((Join-Path $tmp $file.Name), $content)
      }
      Invoke-Native 'python' @((Join-Path $PSScriptRoot 'deployment-env.py'), 'render', '--output', $tmp)
      if ($DryRun) {
        Get-ChildItem $tmp | Select-Object Name
        Write-Host '==> Render completed; no cluster changes'
        break
      }
      Invoke-Kube @('get', 'node', 'ravand-worker-01')
      if ($UseSsh) {
        $remoteDir = '/tmp/darvazeh-k8s-' + [guid]::NewGuid().ToString('n')
        Invoke-Native 'ssh' @('-o', 'BatchMode=yes', $SshHost, "mkdir -m 700 $remoteDir")
        foreach ($file in Get-ChildItem $tmp -Filter '*.yaml') {
          Invoke-Native 'scp' @('-o', 'BatchMode=yes', '-q', $file.FullName, "${SshHost}:$remoteDir/")
        }
        $manifestDir = $remoteDir
      } else { $manifestDir = $tmp }
      Invoke-Kube @('apply', '-f', "$manifestDir/00-namespace.yaml")
      Invoke-Kube @('apply', '--dry-run=server', '-f', $manifestDir)
      Invoke-Kube @('apply', '-f', $manifestDir)
      Wait-Rollouts
    } finally {
      if ($remoteDir) { & ssh -o BatchMode=yes $SshHost "rm -rf -- $remoteDir" }
      # Verify the absolute deletion target remains the unique directory under Temp.
      $resolvedTmp = [IO.Path]::GetFullPath($tmp)
      $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
      if ($resolvedTmp.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolvedTmp -Leaf) -match '^darvazeh-k8s-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolvedTmp -Recurse -Force -ErrorAction SilentlyContinue
      }
    }
  }
  'set-image' {
    if ($DryRun) { Write-Host "Would deploy $Username/fieldsense-api:$Tag and $Username/fieldsense-web:$Tag"; break }
    foreach ($name in @('fieldsense-api', 'fieldsense-web')) {
      $selector = Invoke-Kube @('get', "deployment/$name", '-o', 'jsonpath={.spec.template.spec.nodeSelector.kubernetes\.io/hostname}')
      if (($selector -join '').Trim() -ne 'ravand-worker-01') { throw "Run apply first: $name is not pinned to ravand-worker-01" }
    }
    Invoke-Kube @('set', 'image', 'deployment/fieldsense-api', "api=$Username/fieldsense-api:$Tag")
    Invoke-Kube @('set', 'image', 'deployment/fieldsense-web', "web=$Username/fieldsense-web:$Tag")
    Wait-Rollouts
  }
  'status' { Invoke-Kube @('get', 'deployments,pods,services,ingress,pvc', '-o', 'wide') }
  'delete' {
    if ($DryRun) { Write-Host 'Would delete namespace darvazeh'; break }
    Invoke-Kube @('delete', 'namespace', $Namespace, '--ignore-not-found')
  }
}
Write-Host '==> Done'
