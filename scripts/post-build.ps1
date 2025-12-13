Param(
    [Parameter(Mandatory=$true)][string]$InputDir,
    [Parameter(Mandatory=$false)][string]$OutDir,
    [string]$Version,
    [string]$Arch = 'x64',
    [string]$ReleaseTime # optional ISO timestamp to normalize file times (e.g. 2025-12-14T12:34:56Z)
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
# Default OutDir to InputDir (publish folder) when not provided.
# If InputDir is a framework-specific folder (e.g. net8.0-windows),
# place the archive into its parent Release folder instead.
if (-not $OutDir -or $OutDir.Trim() -eq '') {
    $leaf = Split-Path -Leaf $InputDir
    if ($leaf -match '^net') {
        # parent of the framework folder (e.g. ...\Release)
        $OutDir = Split-Path -Parent $InputDir
    } else {
        $OutDir = $InputDir
    }
}
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

$zipName = "DS4Updater_${Version}_${Arch}.zip"
$zipPath = Join-Path $OutDir $zipName

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host "Creating $zipPath from $InputDir"
try {
    # Normalize file timestamps prior to creating the ZIP so ZIP entries reflect release time.
    try {
        if ($ReleaseTime -and $ReleaseTime.Trim() -ne '') {
            $t = [datetime]::Parse($ReleaseTime).ToUniversalTime()
        } else {
            $t = (Get-Date).ToUniversalTime()
        }
        Write-Host "Normalizing file LastWriteTimeUtc to $t (UTC) for files under $InputDir"
        Get-ChildItem -Path $InputDir -Recurse -File | ForEach-Object { $_.LastWriteTimeUtc = $t }
    } catch {
        Write-Warning "Failed to normalize timestamps: $_"
    }
    Compress-Archive -Path (Join-Path $InputDir '*') -DestinationPath $zipPath -Force
    Write-Host "Created $zipPath"
} catch {
    Write-Error "Failed to create zip: $_"
    exit 1
}

Write-Output $zipPath
