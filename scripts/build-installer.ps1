## Build installer for MemPulse
# Читаем версию из Directory.Build.props (единственный источник)
[xml]$props = Get-Content (Resolve-Path "$PSScriptRoot\..\src\Directory.Build.props") -Raw
$Version = $props.SelectSingleNode('//Version').InnerText.Trim()
Write-Host "Version extracted: [$Version]"

if (-not $Version -or $Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Invalid or missing version. Expected format X.Y.Z"
}

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$compiler = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Inno Setup 6\\ISCC.exe'

if (-not (Test-Path $compiler)) {
    Write-Host 'Installing Inno Setup via Chocolatey...'
    choco install innosetup --yes --no-progress
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup installation failed.' }
}
if (-not (Test-Path $compiler)) { throw 'ISCC.exe not found.' }

if (-not (Test-Path (Join-Path $root 'publish\\MemPulse.exe'))) {
    throw 'Publish the win‑x64 application to publish/ first.'
}

Push-Location (Join-Path $root 'installer')
try {
    & $compiler "/DAppVersion=$Version" 'MemPulse.iss'
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with code $LASTEXITCODE" }
} finally { Pop-Location }

$installer = Join-Path $root 'dist\\MemPulse-Setup-x64.exe'
if (-not (Test-Path $installer)) { throw 'Installer was not created.' }
Write-Host "Built $installer"
