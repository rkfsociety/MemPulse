param(
    [string]$Version = '1.0.0',
    [string]$Tag = 'contracts-v1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$feedDir = Join-Path $root '.nuget-local'
New-Item -ItemType Directory -Path $feedDir -Force | Out-Null

$packageName = "PCPulse.Contracts.$Version.nupkg"
$packagePath = Join-Path $feedDir $packageName
$shaPath = "$packagePath.sha256"

$baseUrl = "https://github.com/rkfsociety/PCPulse/releases/download/$Tag"
$packageUrl = "$baseUrl/$packageName"
$shaUrl = "$baseUrl/$packageName.sha256"

function Test-PackageChecksum([string]$pkg, [string]$shaFile) {
    if (!(Test-Path $pkg) -or !(Test-Path $shaFile)) { return $false }
    $expectedLine = (Get-Content $shaFile -Raw).Trim()
    if ($expectedLine -notmatch '^([a-fA-F0-9]{64})\b') { return $false }
    $expected = $Matches[1].ToLowerInvariant()
    $actual = (Get-FileHash $pkg -Algorithm SHA256).Hash.ToLowerInvariant()
    return $actual -eq $expected
}

if (Test-PackageChecksum $packagePath $shaPath) {
    Write-Host "PCPulse.Contracts $Version already cached and verified in .nuget-local."
    exit 0
}

Write-Host "Downloading $packageUrl ..."
Invoke-WebRequest -Uri $shaUrl -OutFile $shaPath
Invoke-WebRequest -Uri $packageUrl -OutFile $packagePath

if (-not (Test-PackageChecksum $packagePath $shaPath)) {
    Remove-Item $packagePath -Force -ErrorAction SilentlyContinue
    throw "SHA-256 verification failed for $packageName."
}

Write-Host "Verified $packageName in $feedDir."
