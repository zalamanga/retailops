[CmdletBinding()]
param([switch]$Production)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$filename = if ($Production) { '.env.production' } else { '.env' }
$target = Join-Path $repositoryRoot $filename
if (Test-Path -LiteralPath $target) {
    throw "$filename already exists. It has not been overwritten."
}
$docker = Get-Command docker -ErrorAction SilentlyContinue
if (-not $docker) { throw 'Docker CLI is needed to check existing RetailOps volumes before creating credentials.' }
$project = if ($Production) { 'retailops-production' } else { 'retailops' }
$existingVolumes = & $docker.Source volume ls --filter "label=com.docker.compose.project=$project" --format '{{.Name}}'
if ($LASTEXITCODE -ne 0) { throw 'Docker volume inventory failed; no credentials were generated.' }
if ($existingVolumes) { throw "Existing $project volumes require their original $filename. Restore that file; no replacement credentials were generated." }
$bytes = New-Object byte[] 32
$generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
$password = [BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
$content = @(
    'POSTGRES_USER=retailops',
    'POSTGRES_DB=retailops',
    "POSTGRES_PASSWORD=$password"
)
if ($Production) {
    $runtimeBytes = New-Object byte[] 32
    $runtimeGenerator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $runtimeGenerator.GetBytes($runtimeBytes) } finally { $runtimeGenerator.Dispose() }
    $runtimePassword = [BitConverter]::ToString($runtimeBytes).Replace('-', '').ToLowerInvariant()
    $content += @("APP_DB_PASSWORD=$runtimePassword", 'COMPOSE_PROJECT_NAME=retailops-production', 'RETAILOPS_DOMAIN=', 'CADDY_EMAIL=')
    $runtimePassword = $null
} else {
    $content += @(
        "ConnectionStrings__Database=Host=127.0.0.1;Port=5450;Database=retailops;Username=retailops;Password=$password;Maximum Pool Size=20",
        "RETAILOPS_TEST_CONNECTION=Host=127.0.0.1;Port=5450;Database=retailops_test;Username=retailops;Password=$password"
    )
}
$content = $content -join "`n"
[System.IO.File]::WriteAllText($target, $content + "`n", (New-Object System.Text.UTF8Encoding($false)))
$password = $null
$content = $null
Write-Host "Created ignored $filename with a random database password. No credentials were printed."
if ($Production) {
    Write-Host 'Set RETAILOPS_DOMAIN and CADDY_EMAIL, then follow docs/DEPLOYMENT.md.'
} else { Write-Host 'Next: docker compose up --build -d' }
