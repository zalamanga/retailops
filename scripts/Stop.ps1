[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'common.ps1')
$docker = Get-RetailOpsDocker
Push-Location -LiteralPath $script:RepositoryRoot
try {
    & $docker compose --project-name retailops --project-directory $script:RepositoryRoot --file (Join-Path $script:RepositoryRoot 'compose.yml') stop
    Assert-RetailOpsExit 'RetailOps stop'
    Write-Host 'RetailOps stopped. Its database and session keys are preserved.'
} finally { Pop-Location }
