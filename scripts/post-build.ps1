Param(
    [Parameter(Mandatory=$true)][string]$InputDir,
    [Parameter(Mandatory=$true)][string]$OutDir,
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$Arch = 'x64'
)

if (-not (Test-Path $InputDir)) { Write-Error "InputDir not found: $InputDir"; exit 1 }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

$zipName = "DS4Updater_${Version}_${Arch}.zip"
$zipPath = Join-Path $OutDir $zipName

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host "Creating $zipPath from $InputDir"
try {
    Compress-Archive -Path (Join-Path $InputDir '*') -DestinationPath $zipPath -Force
    Write-Host "Created $zipPath"
} catch {
    Write-Error "Failed to create zip: $_"
    exit 1
}

Write-Output $zipPath
