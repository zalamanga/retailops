[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'common.ps1')
Import-RetailOpsEnvironment
if (-not $env:RETAILOPS_TEST_CONNECTION) {
    throw 'Set RETAILOPS_TEST_CONNECTION to a PostgreSQL role with CREATEDB permission. Tests use isolated retailops_test_<guid> databases.'
}
$dotnet = Get-RetailOpsDotnet
$npm = Assert-RetailOpsCommand 'npm.cmd'
Push-Location $script:RepositoryRoot
try {
    & $dotnet restore RetailOps.slnx --locked-mode
    Assert-RetailOpsExit 'Locked .NET restore'
    & $dotnet test RetailOps.slnx -c Release --no-restore -m:1 --logger trx --results-directory artifacts/test-results
    Assert-RetailOpsExit '.NET tests'
    Push-Location frontend
    try {
        if (-not (Test-Path -LiteralPath node_modules -PathType Container)) {
            & $npm ci --no-audit --no-fund
            Assert-RetailOpsExit 'Locked frontend restore'
        }
        & $npm test
        Assert-RetailOpsExit 'Frontend tests'
    } finally { Pop-Location }
} finally { Pop-Location }
