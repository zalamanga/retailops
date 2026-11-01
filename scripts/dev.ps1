[CmdletBinding()]
param([switch]$SkipInstall)

. (Join-Path $PSScriptRoot 'common.ps1')
Import-RetailOpsEnvironment
$dotnet = Get-RetailOpsDotnet
$docker = Get-RetailOpsDocker
$npm = Assert-RetailOpsCommand 'npm.cmd'
$apiProcess = $null
$webProcess = $null
$logs = Join-Path $script:RepositoryRoot '.local\logs'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$session = Get-Date -Format 'yyyyMMdd-HHmmss'
$apiDirectory = Join-Path $script:RepositoryRoot 'backend\RetailOps.Api'
Push-Location $script:RepositoryRoot
try {
    & $docker compose up -d --wait postgres
    Assert-RetailOpsExit 'PostgreSQL startup'
    if (-not $SkipInstall) {
        & $dotnet restore RetailOps.slnx --locked-mode
        Assert-RetailOpsExit 'Locked .NET restore'
        Push-Location frontend
        try {
            & $npm ci --no-audit --no-fund
            Assert-RetailOpsExit 'Locked frontend restore'
        } finally { Pop-Location }
    }
    & $dotnet build backend/RetailOps.Api/RetailOps.Api.csproj -c Debug --no-restore -m:1
    Assert-RetailOpsExit '.NET build'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:Security__SecureCookies = 'false'
    $env:ASPNETCORE_URLS = 'http://127.0.0.1:5190'
    Push-Location $apiDirectory
    try {
        & $dotnet bin/Debug/net10.0/RetailOps.Api.dll --migrate
        Assert-RetailOpsExit 'Database migration'
    } finally { Pop-Location }
    $apiProcess = Start-Process -FilePath $dotnet -ArgumentList 'bin/Debug/net10.0/RetailOps.Api.dll' -WorkingDirectory $apiDirectory -WindowStyle Hidden -RedirectStandardOutput (Join-Path $logs "api-$session.log") -RedirectStandardError (Join-Path $logs "api-$session.error.log") -PassThru
    $webProcess = Start-Process -FilePath $env:ComSpec -ArgumentList '/d', '/s', '/c', 'npm.cmd run dev -- --host 127.0.0.1 --port 5191 --strictPort' -WorkingDirectory (Join-Path $script:RepositoryRoot 'frontend') -WindowStyle Hidden -RedirectStandardOutput (Join-Path $logs "web-$session.log") -RedirectStandardError (Join-Path $logs "web-$session.error.log") -PassThru
    Write-Host 'RetailOps: http://127.0.0.1:5191'
    Write-Host 'API: http://127.0.0.1:5190 | Development OpenAPI: /openapi/v1.json'
    Write-Host "Logs: $logs"
    Write-Host 'Press Ctrl+C to stop the two processes started by this script. PostgreSQL stays running.'
    while (-not $apiProcess.HasExited -and -not $webProcess.HasExited) { Start-Sleep -Seconds 1 }
    throw 'An application process exited. Read its log in .local/logs.'
} finally {
    foreach ($process in @($webProcess, $apiProcess)) {
        if ($process -and -not $process.HasExited) {
            & taskkill.exe /PID $process.Id /T /F | Out-Null
        }
    }
    Pop-Location
}
