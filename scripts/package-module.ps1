param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64')]
    [string]$Rid,
    [string]$OutputDir = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

[xml]$props = Get-Content (Join-Path $root 'src\Directory.Build.props') -Raw
$Version = $props.SelectSingleNode('//Version').InnerText.Trim()
if (-not $Version -or $Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Некорректная версия в Directory.Build.props: '$Version'"
}

$manifestPath = Join-Path $root 'src\MemPulse.PulseModule\module.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($manifest.version -ne $Version) {
    throw "Версия module.json ($($manifest.version)) не совпадает с Directory.Build.props ($Version)."
}
if ($manifest.id -ne 'mempulse') {
    throw "Ожидается id=mempulse в module.json, получено '$($manifest.id)'."
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $root 'release-artifacts'
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$stageModule = Join-Path $root "obj\stage\module-$Rid"
$stageHelper = Join-Path $root "obj\stage\helper-$Rid"
$stagePack = Join-Path $root "obj\stage\pack-$Rid"
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $stageModule, $stageHelper, $stagePack
New-Item -ItemType Directory -Force -Path $stageModule, $stageHelper, $stagePack | Out-Null

& dotnet build (Join-Path $root 'src\MemPulse.PulseModule\MemPulse.PulseModule.csproj') -c Release -o $stageModule
if ($LASTEXITCODE -ne 0) { throw "Сборка MemPulse.PulseModule завершилась с ошибкой $LASTEXITCODE" }

& dotnet publish (Join-Path $root 'src\MemPulse.Helper\MemPulse.Helper.csproj') `
    -c Release -r $Rid --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false `
    -o $stageHelper
if ($LASTEXITCODE -ne 0) { throw "Публикация MemPulse.Helper ($Rid) завершилась с ошибкой $LASTEXITCODE" }

Copy-Item (Join-Path $stageModule 'module.json') (Join-Path $stagePack 'module.json')
Copy-Item (Join-Path $stageModule 'MemPulse.PulseModule.dll') (Join-Path $stagePack 'MemPulse.PulseModule.dll')
Copy-Item (Join-Path $stageModule 'MemPulse.PulseModule.deps.json') (Join-Path $stagePack 'MemPulse.PulseModule.deps.json')
Copy-Item (Join-Path $stageModule 'MemPulse.Core.dll') (Join-Path $stagePack 'MemPulse.Core.dll')

$helperExeName = if ($Rid -eq 'win-x64') { 'MemPulse.Helper.exe' } else { 'MemPulse.Helper' }
$helperSource = Join-Path $stageHelper $helperExeName
if (-not (Test-Path $helperSource)) {
    throw "Не найден собранный helper: $helperSource"
}
Copy-Item $helperSource (Join-Path $stagePack $helperExeName)

$manifestAsset = Join-Path $OutputDir "mempulse-module-$Version.manifest.json"
Copy-Item -Force $manifestPath $manifestAsset

$zipName = "mempulse-module-$Version-$Rid.zip"
$zipPath = Join-Path $OutputDir $zipName
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }

Compress-Archive -Path (Join-Path $stagePack '*') -DestinationPath $zipPath -CompressionLevel Optimal

$hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path ($zipPath + '.sha256') -Value "$hash  $zipName" -NoNewline

Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $stageModule, $stageHelper, $stagePack

Write-Host "Packaged module: $zipPath"
Write-Host "Manifest: $manifestAsset"
Write-Host "SHA256: $hash"
