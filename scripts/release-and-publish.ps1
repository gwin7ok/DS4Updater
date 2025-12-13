Param(
    [switch]$WithV,
    [string]$Remote = 'origin',
    [switch]$DryRun,
    [switch]$SkipBuild,
    [string]$TokenEnvVar = 'GITHUB_TOKEN',
    # Repo can be passed as 'owner/repo' or full URL. If not provided, environment variable REPO, REPO_OWNER/REPO_NAME,
    # or the hardcoded defaults below will be used.
    [string]$Repo = $env:REPO
)

Set-StrictMode -Version Latest

# repo info (can be overridden via parameters or environment variables)
$repoOwner = $env:REPO_OWNER
$repoName = $env:REPO_NAME

# If Repo parameter (owner/repo or URL) provided, parse it
if ($Repo) {
    if ($Repo -match '^https?://') {
        # URL form: https://github.com/owner/repo
        $m = [regex]::Match($Repo, 'github\.com/([^/]+)/([^/]+)')
        if ($m.Success) { $repoOwner = $m.Groups[1].Value; $repoName = $m.Groups[2].Value }
    }
    elseif ($Repo -match '^[^/]+/[^/]+$') {
        $parts = $Repo.Split('/')
        $repoOwner = $parts[0]; $repoName = $parts[1]
    }
}

# Fallback to existing hardcoded defaults if still empty
if (-not $repoOwner -or -not $repoName) {
    if (-not $repoOwner) { $repoOwner = 'gwin7ok' }
    if (-not $repoName) { $repoName = 'DS4Updater' }
}

$scriptDir = Split-Path -Path $MyInvocation.MyCommand.Definition -Parent
$repoRoot = (Resolve-Path -Path (Join-Path $scriptDir "..")).ProviderPath
Set-Location $repoRoot

# Determine version: try Updater2/DS4Updater.csproj then Directory.Build.props
$projPath = Join-Path $repoRoot 'Updater2/DS4Updater.csproj'
$version = $null
if (Test-Path $projPath) {
    $xml = [xml](Get-Content -Path $projPath -ErrorAction Stop)
    $node = $xml.SelectSingleNode('//Version')
    if ($node) { $version = $node.'#text' }
}
if (-not $version) {
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    if (Test-Path $propsPath) {
        $xml = [xml](Get-Content -Path $propsPath -ErrorAction Stop)
        $node = $xml.SelectSingleNode('//Version')
        if ($node) { $version = $node.'#text' }
    }
}
if (-not $version) { Write-Error 'No <Version> element found in Updater2/DS4Updater.csproj or Directory.Build.props'; exit 3 }

$tag = if ($WithV) { "v$version" } else { "$version" }
Write-Host "Preparing release for $tag (version $version)"

function Find-ZipsForVersion {
    param([string]$ver)
    Get-ChildItem -Path $repoRoot -Recurse -Filter "DS4Updater_${ver}_*.zip" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName
}

if (-not $SkipBuild) {
    Write-Host "Building Updater2 x64..."
    if ($DryRun) { Write-Host "[DryRun] dotnet publish ./Updater2/DS4Updater.csproj -c Release -o ./Updater2/bin/x64/Release/net8.0-windows" }
    else {
        & dotnet publish ./Updater2/DS4Updater.csproj -c Release -o ./Updater2/bin/x64/Release/net8.0-windows
        if ($LASTEXITCODE -ne 0) { Write-Error 'dotnet publish failed'; exit 4 }
    }
    Write-Host "Running post-build..."
    # Try to determine release time from GitHub Actions event payload if available.
    $releaseTime = $null
    if ($env:GITHUB_EVENT_PATH -and (Test-Path $env:GITHUB_EVENT_PATH)) {
        try {
            $ev = Get-Content -Raw $env:GITHUB_EVENT_PATH | ConvertFrom-Json
            if ($ev -and $ev.release -and $ev.release.published_at) { $releaseTime = $ev.release.published_at }
        } catch { }
    }
    if (-not $releaseTime -and $env:RELEASE_TIME) { $releaseTime = $env:RELEASE_TIME }

    if ($DryRun) { Write-Host "[DryRun] pwsh ./scripts/post-build.ps1 ./Updater2/bin/x64/Release/net8.0-windows . $version x64 '$releaseTime'" }
    else { pwsh -NoProfile -File ./scripts/post-build.ps1 ./Updater2/bin/x64/Release/net8.0-windows . $version x64 $releaseTime; if ($LASTEXITCODE -ne 0) { Write-Error 'post-build failed'; exit 5 } }
}

# locate zips
$zips = @(Find-ZipsForVersion -ver $version)
if ($zips.Count -eq 0) {
    Write-Warning "No zip artifacts found for version $version. Continuing — you can upload manually later."
} else {
    Write-Host "Found artifact(s):"; $zips | ForEach-Object { Write-Host "  $_" }
}

# Git tag handling
if ($DryRun) {
    Write-Host "[DryRun] Would recreate and push tag: $tag"
} else {
    $localTag = git tag -l $tag 2>$null
    $localExists = -not [string]::IsNullOrWhiteSpace(($localTag | Out-String).Trim())
    if ($localExists) { Write-Host "Deleting local tag $tag"; git tag -d $tag }
    Write-Host "Deleting remote tag $tag (ignore errors)"; git push $Remote --delete $tag 2>$null
    Write-Host "Creating annotated tag $tag"; git tag -a $tag -m "Release $tag" HEAD; if ($LASTEXITCODE -ne 0) { Write-Error 'git tag creation failed'; exit 8 }
    Write-Host "Pushing tag $tag to $Remote"; git push $Remote $tag; if ($LASTEXITCODE -ne 0) { Write-Error 'git push failed'; exit 9 }
}

# Prefer gh CLI when available
$useGh = $false
try { Get-Command gh -ErrorAction Stop; $useGh = $true } catch { $useGh = $false }

if ($useGh) {
    $ghAuthOk = $false
    try { gh auth status --hostname github.com 2>$null | Out-Null; $ghAuthOk = $true } catch { $ghAuthOk = $false }
    if ($DryRun) { Write-Host "[DryRun] gh release operations for $tag" }
    elseif ($ghAuthOk) {
        Write-Host "Using gh CLI to manage release"
        if ((gh release view $tag 2>$null)) { Write-Host "Deleting existing release $tag"; gh release delete $tag --yes }
        $changelogPath = Join-Path $repoRoot 'CHANGELOG.md'
        $bodyFile = $null
        if (Test-Path $changelogPath) {
            $raw = Get-Content -Raw -Path $changelogPath
            $pattern = "(?ms)^## \[${version}\].*?(?=^## \[|\z)"
            $m = [regex]::Match($raw, $pattern)
            if ($m.Success) { $bodyFile = [System.IO.Path]::GetTempFileName(); Set-Content -Path $bodyFile -Value $m.Value -Encoding UTF8 }
        }
        if ($zips.Count -gt 0) { $args = @('release','create',$tag) + $zips + @('--title',$tag); if ($bodyFile) { $args += @('--notes-file',$bodyFile) }; Write-Host "gh $($args -join ' ')"; & gh @args } else { $args = @('release','create',$tag,'--title',$tag); if ($bodyFile) { $args += @('--notes-file',$bodyFile) }; Write-Host "gh $($args -join ' ')"; & gh @args }
        if ($bodyFile -and (Test-Path $bodyFile)) { Remove-Item $bodyFile -ErrorAction SilentlyContinue }
    } else { Write-Warning "gh CLI found but not authenticated — falling back to API token mode"; $useGh = $false }
}

if (-not $useGh) {
    $token = [Environment]::GetEnvironmentVariable($TokenEnvVar)
    if (-not $token) { Write-Warning "Environment variable '$TokenEnvVar' not set. Skipping release creation." }
    else {
        $authHeaders = @{ Authorization = "token $token"; 'User-Agent' = 'release-and-publish-script' }
        $getUri = "https://api.github.com/repos/$repoOwner/$repoName/releases/tags/$tag"
        try { if ($DryRun) { Write-Host "[DryRun] GET $getUri" } else { $resp = Invoke-RestMethod -Headers $authHeaders -Uri $getUri -Method Get -ErrorAction Stop } } catch { Write-Host "No existing release or could not query: $($_.Exception.Message)" }
        if (-not $DryRun -and $resp) { $delUri = "https://api.github.com/repos/$repoOwner/$repoName/releases/$($resp.id)"; Invoke-RestMethod -Headers $authHeaders -Uri $delUri -Method Delete -ErrorAction SilentlyContinue }
        $changelogPath = Join-Path $repoRoot 'CHANGELOG.md'
        $body = ""
        if (Test-Path $changelogPath) { $raw = Get-Content -Raw -Path $changelogPath; $pattern = "(?ms)^## \[${version}\].*?(?=^## \[|\z)"; $m = [regex]::Match($raw, $pattern); if ($m.Success) { $body = $m.Value } }
        $createUri = "https://api.github.com/repos/$repoOwner/$repoName/releases"
        $payload = @{ tag_name = $tag; name = "$tag"; body = $body; draft = $false; prerelease = $false } | ConvertTo-Json
        if ($DryRun) { Write-Host "[DryRun] POST $createUri`nPayload: $payload" } else { try { $createResp = Invoke-RestMethod -Headers $authHeaders -Uri $createUri -Method Post -Body $payload -ContentType 'application/json' -ErrorAction Stop; Write-Host "Created release id $($createResp.id)" } catch { Write-Error ("Failed to create release: " + $_.Exception.Message); exit 11 } }
        if (-not $DryRun -and $createResp) {
            foreach ($zip in $zips) {
                $name = [System.IO.Path]::GetFileName($zip)
                $uploadUri = "https://uploads.github.com/repos/$repoOwner/$repoName/releases/$($createResp.id)/assets?name=$name"
                Write-Host "Uploading $name to $uploadUri"
                try { Invoke-RestMethod -Headers $authHeaders -Uri $uploadUri -Method Post -InFile $zip -ContentType 'application/zip' -ErrorAction Stop; Write-Host "Uploaded $name" } catch { Write-Warning ("Failed to upload " + $name + ": " + $_.Exception.Message) }
            }
        }
    }
}

Write-Host "Done"
