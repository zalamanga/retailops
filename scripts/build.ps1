[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'common.ps1')
$dotnet = Get-RetailOpsDotnet
$npm = Assert-RetailOpsCommand 'npm.cmd'
Push-Location $script:RepositoryRoot
try {
    & $dotnet restore RetailOps.slnx --locked-mode
    Assert-RetailOpsExit 'Locked .NET restore'
    & $dotnet build RetailOps.slnx -c Release --no-restore -m:1
    Assert-RetailOpsExit '.NET build'
    Push-Location frontend
    try {
        & $npm ci --no-audit --no-fund
        Assert-RetailOpsExit 'Locked frontend restore'
        & $npm run build
        Assert-RetailOpsExit 'Frontend build'
    } finally { Pop-Location }
} finally { Pop-Location }
