Param(
    [switch]$WithV,
    [string]$Remote = 'origin',
    [switch]$DryRun,
    [switch]$NoPush
)

# Resolve repository root relative to this script
$scriptDir = Split-Path -Path $MyInvocation.MyCommand.Definition -Parent
$repoRoot = (Resolve-Path -Path (Join-Path $scriptDir ".." )).ProviderPath

# Try Updater2 csproj first, then Directory.Build.props
$version = $null
$projPath = Join-Path $repoRoot 'Updater2/DS4Updater.csproj'
if (Test-Path $projPath) {
    try {
        [xml]$xml = Get-Content -Path $projPath -ErrorAction Stop
        $node = $xml.SelectSingleNode('//Version')
        if ($node) { $version = $node.'#text' }
    } catch { }
}

if (-not $version) {
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    if (Test-Path $propsPath) {
        try {
            [xml]$xml = Get-Content -Path $propsPath -ErrorAction Stop
            $node = $xml.SelectSingleNode('//Version')
            if ($node) { $version = $node.'#text' }
        } catch { }
    }
}

if (-not $version) {
    Write-Error "No <Version> element found in Updater2/DS4Updater.csproj or Directory.Build.props"
    exit 3
}

$tag = if ($WithV) { "v$version" } else { "$version" }

Write-Host "Repository root: $repoRoot"
Write-Host "Using version: $version"
Write-Host "Tag to (re)create: $tag"

# Check for local tag
$localTagList = & git tag -l $tag 2>$null
$localTagExists = ($localTagList -ne $null) -and (($localTagList | Out-String).Trim() -ne '')
if ($DryRun) {
    Write-Host "Dry run: would delete local tag if exists: $localTagExists"
    if (-not $NoPush) { Write-Host "Dry run: would delete remote tag $tag on $Remote" }
    Write-Host "Dry run: would create annotated tag $tag and push (unless --NoPush)"
    exit 0
}

try {
    if ($localTagExists) {
        Write-Host "Deleting local tag $tag..."
        & git tag -d $tag
        if ($LASTEXITCODE -ne 0) { throw "git tag -d failed with exit $LASTEXITCODE" }
    }

    if (-not $NoPush) {
        Write-Host "Deleting remote tag $tag (if exists) on $Remote..."
        & git push $Remote --delete refs/tags/$tag 2>$null
    }

    Write-Host "Creating annotated tag $tag..."
    & git tag -a $tag -m "Release $tag" HEAD
    if ($LASTEXITCODE -ne 0) { throw "git tag creation failed with exit $LASTEXITCODE" }

    if (-not $NoPush) {
        Write-Host "Pushing tag $tag to $Remote..."
        & git push $Remote refs/tags/$tag
        if ($LASTEXITCODE -ne 0) { throw "git push failed with exit $LASTEXITCODE" }
    }

    Write-Host "Success: tag '$tag' created" -ForegroundColor Green
} catch {
    Write-Error "Operation failed: $_"
    exit 10
}
