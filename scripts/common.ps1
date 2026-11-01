$ErrorActionPreference = 'Stop'
$script:RepositoryRoot = Split-Path -Parent $PSScriptRoot

function Import-RetailOpsEnvironment {
    param([string]$Path = (Join-Path $script:RepositoryRoot '.env'))
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'Local .env is missing. Run scripts/init-env.ps1 first.'
    }
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }
        if ($trimmed -notmatch '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
            throw 'An environment line is invalid. Use NAME=value without shell commands.'
        }
        $name = $Matches[1]
        $value = $Matches[2].Trim()
        if ($value.Length -ge 2 -and (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'")))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    }
}

function Get-RetailOpsDotnet {
    $candidates = @($env:RETAILOPS_DOTNET, 'D:\DevTools\dotnet\dotnet.exe')
    $installed = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($installed) { $candidates += $installed.Source }
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            $sdkList = & $candidate --list-sdks
            if ($LASTEXITCODE -eq 0 -and ($sdkList -match '^10\.0\.')) {
                $env:DOTNET_ROOT = Split-Path -Parent $candidate
                $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
                if (Test-Path -LiteralPath 'D:\DevCache' -PathType Container) {
                    $env:DOTNET_CLI_HOME = 'D:\DevCache\dotnet'
                    $env:NUGET_PACKAGES = 'D:\DevCache\nuget\packages'
                }
                $env:DOTNET_NOLOGO = '1'
                $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
                return $candidate
            }
        }
    }
    throw 'Install the .NET SDK version specified in global.json, or set RETAILOPS_DOTNET to its executable.'
}

function Get-RetailOpsDocker {
    $installed = Get-Command docker -ErrorAction SilentlyContinue
    if ($installed) { return $installed.Source }
    $desktop = Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin\docker.exe'
    if (Test-Path -LiteralPath $desktop -PathType Leaf) { return $desktop }
    throw 'Docker CLI is missing. Install Docker Desktop and start its Linux engine.'
}

function Assert-RetailOpsCommand {
    param([string]$Name)
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $command) { throw "$Name is required. See docs/DEPLOYMENT.md." }
    return $command.Source
}

function Assert-RetailOpsExit {
    param([string]$Action)
    if ($LASTEXITCODE -ne 0) { throw "$Action failed (exit $LASTEXITCODE)." }
}
