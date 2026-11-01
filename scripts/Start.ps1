[CmdletBinding()]
param([switch]$SkipBuild)

. (Join-Path $PSScriptRoot 'common.ps1')
$docker = Get-RetailOpsDocker
Push-Location -LiteralPath $script:RepositoryRoot
try {
    if (-not (Test-Path -LiteralPath '.env')) { & (Join-Path $PSScriptRoot 'init-env.ps1') }
    $engine = & $docker info --format '{{.OSType}}'
    Assert-RetailOpsExit 'Docker availability'
    if ($engine -ne 'linux') { throw 'RetailOps requires Docker Linux containers.' }
    $composeFile = Join-Path $script:RepositoryRoot 'compose.yml'
    if ($SkipBuild) { & $docker compose --project-name retailops --project-directory $script:RepositoryRoot --file $composeFile up -d --wait }
    else { & $docker compose --project-name retailops --project-directory $script:RepositoryRoot --file $composeFile up --build -d --wait }
    Assert-RetailOpsExit 'RetailOps startup'
    $health = Invoke-RestMethod -Uri 'http://127.0.0.1:8150/health/ready' -TimeoutSec 10
    if ($health.status -ne 'ready') { throw 'RetailOps did not report database readiness.' }
    $workspace = Invoke-RestMethod -Uri 'http://127.0.0.1:8150/api/auth/status' -TimeoutSec 30
    if ($workspace.setupRequired -isnot [bool]) { throw 'Workspace readiness did not return the expected state.' }
    Write-Host 'RetailOps: http://127.0.0.1:8150'
    Write-Host 'New workspaces start empty. Create your administrator, then configure products, branches and prices.'
    Write-Host 'Stop with scripts/Stop.ps1. Database and session-key volumes are retained.'
} finally { Pop-Location }
