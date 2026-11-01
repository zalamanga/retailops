[CmdletBinding()]
param([string]$Directory)

. (Join-Path $PSScriptRoot 'common.ps1')
Import-RetailOpsEnvironment
$docker = Get-RetailOpsDocker
if (-not $Directory) { $Directory = Join-Path $script:RepositoryRoot '.local\backups' }
$absoluteDirectory = [System.IO.Path]::GetFullPath($Directory)
New-Item -ItemType Directory -Path $absoluteDirectory -Force | Out-Null
$filename = 'retailops-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.dump'
$containerPath = '/tmp/' + $filename
$target = Join-Path $absoluteDirectory $filename
Push-Location $script:RepositoryRoot
try {
    & $docker compose exec -T postgres pg_dump -U $env:POSTGRES_USER -d $env:POSTGRES_DB --format=custom --file $containerPath
    Assert-RetailOpsExit 'PostgreSQL backup'
    & $docker compose cp "postgres:$containerPath" $target
    Assert-RetailOpsExit 'Backup copy'
    & $docker compose exec -T postgres rm -f -- $containerPath
    Assert-RetailOpsExit 'Temporary container backup cleanup'
    Write-Host "Backup saved: $target"
    Write-Host 'Protect this file: it contains account records, password hashes, and business data.'
} finally { Pop-Location }
