<#
.SYNOPSIS
    Build a self-contained single-file distribution of Wally.

.DESCRIPTION
    Produces one Wally.exe (win-x64) with the .NET 8 runtime bundled in, so the
    target machine needs nothing preinstalled. Output lands in dist/.

    The .NET SDK is required. If `dotnet` is not on PATH this script falls back
    to %USERPROFILE%\.dotnet\dotnet.exe.

.PARAMETER Runtime
    Target runtime identifier. Default win-x64. Use win-arm64 for ARM machines.

.EXAMPLE
    .\build\build-dist.ps1
    .\build\build-dist.ps1 -Runtime win-arm64
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

# Resolve repo root (this script lives in build/).
$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\Wally.App\Wally.App.csproj'
$distDir = Join-Path $repoRoot 'dist'

# Locate a dotnet that actually has an SDK. A bare `dotnet` shim can exist on
# PATH yet report "No .NET SDKs were found", so we verify, not just resolve.
function Test-DotnetSdk($exe) {
    try {
        $sdks = & $exe --list-sdks 2>$null
        return $LASTEXITCODE -eq 0 -and $sdks
    } catch {
        return $false
    }
}

$dotnet = $null
if ((Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-DotnetSdk 'dotnet')) {
    $dotnet = 'dotnet'
} else {
    $fallback = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if ((Test-Path $fallback) -and (Test-DotnetSdk $fallback)) {
        $dotnet = $fallback
    } else {
        throw "No .NET 8 SDK found on PATH or at $fallback. Install it: https://dotnet.microsoft.com/download/dotnet/8.0"
    }
}

Write-Host "Using dotnet: $dotnet"
Write-Host "Publishing Wally ($Runtime, self-contained single-file)..." -ForegroundColor Cyan

# Clean previous dist output.
if (Test-Path $distDir) {
    Remove-Item $distDir -Recurse -Force
}

& $dotnet publish $appProject `
    -c Release `
    -r $Runtime `
    -o $distDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $distDir 'Wally.exe'
if (-not (Test-Path $exe)) {
    throw "Publish completed but $exe was not produced."
}

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Distribution ready:" -ForegroundColor Green
Write-Host "  $exe  ($sizeMb MB)"
Write-Host ""
Write-Host "This single file runs on any Windows 11 x64 machine with no .NET install required."
Write-Host "Target requirement: Windows 11 build 26100 (24H2) for the virtual-desktop features."
