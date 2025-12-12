Param(
    [Parameter(Mandatory=$true)][string]$InputDir,
    [Parameter(Mandatory=$true)][string]$OutDir,
    [string]$Version,
    [string]$Arch = 'x64'
)

# If Version not provided, try to read from Updater2/DS4Updater.csproj
if (-not $Version -or $Version.Trim() -eq '') {
    $projPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'Updater2\DS4Updater.csproj'
    if (Test-Path $projPath) {
        try {
            $xml = [xml](Get-Content -Path $projPath -ErrorAction Stop)
            $node = $xml.SelectSingleNode('//Version')
            if ($node) { $Version = $node.'#text' }
        } catch {
            Write-Warning "Failed reading project file for version: $_"
        }
    }
}

if (-not $Version -or $Version.Trim() -eq '') {
    Write-Error "Version not provided and could not be determined from Updater2/DS4Updater.csproj"
    exit 2
}

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
