function Import-DeploymentEnv([string]$Root) {
  $json = & python (Join-Path $Root 'scripts/deployment-env.py') values
  if ($LASTEXITCODE -ne 0) { throw 'Could not load deployment .env' }
  $values = $json | ConvertFrom-Json
  foreach ($property in $values.PSObject.Properties) {
    [Environment]::SetEnvironmentVariable($property.Name, [string]$property.Value, 'Process')
  }
}
