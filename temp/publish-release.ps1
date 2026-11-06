<#
.SYNOPSIS
    One command: build -> encrypt -> commit -> tag -> release -> verify the feed.

.DESCRIPTION
    Before this script a release was five manual steps and the fifth one kept
    being skipped:

        1. build-enc.ps1            panel + AES-256 one-file setup + update zip
        2. commit / push source
        3. tag vX + push            <- starts .github\workflows\release.yml
        4. upload the Setup.exe to the release the workflow created
        5. update docs\update.json  <- FORGOTTEN for v2.2.3, so every installed
                                       panel answered "up to date" for ever

    Step 5 is now done by the workflow itself (job step "Publish the update feed
    and download page"), and this script waits for that run and then PROVES the
    published feed names the version that was just released. If the feed is
    stale the script fails loudly instead of letting a silent no-update release
    go out.

.PARAMETER Version
    2.2.4 style. Defaults to <Version> in source\AISuitePanel.csproj.

.PARAMETER Notes
    Release body. Defaults to a generated line.

.PARAMETER SkipBuild
    Reuse Build\ artefacts from a previous run (no rebuild, no re-encrypt).

.PARAMETER SkipSetupUpload
    Do not upload DeveloperXPanel-Setup.exe (zip + feed only).

.EXAMPLE
    .\temp\publish-release.ps1
    .\temp\publish-release.ps1 -Version 2.2.5 -Notes "Fix X"
    .\temp\publish-release.ps1 -SkipBuild          # only push tag + verify
#>
[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$Notes = "",
    [switch]$SkipBuild,
    [switch]$SkipSetupUpload
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$repo = 'Sabbirx01/Script_Panel'
$csproj = Join-Path $root 'source\AISuitePanel.csproj'
$setup = Join-Path $root 'Build\DeveloperXPanel-Setup.exe'

function Step($n, $text) {
    Write-Host ""
    Write-Host ("[{0}] {1}" -f $n, $text) -ForegroundColor Cyan
}
function Ok($text)   { Write-Host ("    " + $text) -ForegroundColor Green }
function Warn2($text){ Write-Host ("    " + $text) -ForegroundColor Yellow }

Set-Location $root

if ([string]::IsNullOrWhiteSpace($Version)) {
    $m = [regex]::Match((Get-Content -LiteralPath $csproj -Raw -Encoding UTF8),
                        '<Version>\s*([0-9]+(\.[0-9]+){1,3})\s*</Version>')
    if (-not $m.Success) { throw "Could not read <Version> from $csproj" }
    $Version = $m.Groups[1].Value
}
if ($Version -notmatch '^\d+(\.\d+){1,3}$') { throw "Version must look like 2.2.4 (got '$Version')" }
$tag = "v$Version"
$zip = Join-Path $root "Build\DeveloperXBulkAccPanel-$tag.zip"

Write-Host ""
Write-Host "  DEVELOPER X - publish $tag" -ForegroundColor White
Write-Host "  ------------------------------" -ForegroundColor DarkCyan

# ------------------------------------------------------------------ 0. clean
Step 0 'working tree'
$dirty = & git status --porcelain
if ($dirty) {
    Warn2 'uncommitted changes:'
    $dirty | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkYellow }
} else {
    Ok 'clean'
}

# -------------------------------------------------------------- 1. build+enc
if (-not $SkipBuild) {
    Step 1 "build + AES-256 encrypt (tools\build-enc.ps1 -Version $Version)"
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\build-enc.ps1') -Version $Version
    if ($LASTEXITCODE -ne 0) { throw "build-enc.ps1 failed ($LASTEXITCODE)" }
} else {
    Step 1 'skipping build (-SkipBuild)'
}

foreach ($f in @($setup, $zip)) {
    if (-not (Test-Path -LiteralPath $f)) { throw "missing artefact: $f" }
}
$exeVersion = (Get-Item -LiteralPath (Join-Path $root 'output\DeveloperXBulkAccPanel.exe')).VersionInfo.FileVersion
if ($exeVersion -notlike "$Version*") { throw "panel exe reports $exeVersion, expected $Version" }
Ok "panel exe $exeVersion"

# --------------------------------------------------------- 2. commit + tag
Step 2 "commit, tag $tag, push"
& git add -A
$staged = & git diff --cached --name-only
if ($staged) {
    & git commit -m "release: $tag" | Out-Null
    Ok ("committed " + ($staged -join ', '))
} else {
    Ok 'nothing to commit'
}
& git push origin HEAD:main
if ($LASTEXITCODE -ne 0) { throw "git push failed" }

$existing = & git tag --list $tag
if ($existing) {
    Warn2 "$tag already exists - re-running the workflow for it"
    & git push origin ":refs/tags/$tag"
}
& git tag $tag
& git push origin $tag
if ($LASTEXITCODE -ne 0) { throw "tag push failed" }

# ------------------------------------------------- 3. wait for the workflow
Step 3 'waiting for the release workflow (builds the zip, publishes the feed)'
$runId = $null
for ($i = 0; $i -lt 30 -and -not $runId; $i++) {
    Start-Sleep -Seconds 10
    $runs = & gh run list --repo $repo --workflow release.yml --limit 5 --json databaseId,headBranch,status,event 2>$null | ConvertFrom-Json
    $runId = ($runs | Where-Object { $_.event -eq 'push' } | Select-Object -First 1).databaseId
}
if (-not $runId) { throw 'the release workflow did not start within 5 minutes' }
Ok "run $runId"
& gh run watch $runId --repo $repo --exit-status
if ($LASTEXITCODE -ne 0) {
    throw "release workflow failed - see https://github.com/$repo/actions/runs/$runId"
}

# ------------------------------------------------- 4. Setup.exe as an asset
if (-not $SkipSetupUpload) {
    Step 4 'uploading the encrypted one-file setup'
    & gh release upload $tag $setup "$setup.sha256" --repo $repo --clobber
    if ($LASTEXITCODE -ne 0) { throw 'setup upload failed - the download page now offers Setup.exe first, so a missing asset is a 404 for every new customer' }

    # The page's primary button is this exact asset. Prove it is there instead
    # of assuming the upload landed.
    $setupUrl = "https://github.com/$repo/releases/download/$tag/DeveloperXPanel-Setup.exe"
    $head = Invoke-WebRequest -Uri $setupUrl -Method Head -UseBasicParsing -MaximumRedirection 5 -TimeoutSec 60
    if ($head.StatusCode -ne 200) { throw "the uploaded Setup.exe is not downloadable ($($head.StatusCode))" }
    Ok ((Split-Path $setup -Leaf) + ' attached and downloadable')
} else {
    Step 4 'skipping setup upload (-SkipSetupUpload)'
}

# ----------------------------------------------------- 5. prove the feed
Step 5 'verifying the published update feed'
$feedUrl = 'https://sabbirx01.github.io/Script_Panel/update.json?t=' + [DateTime]::UtcNow.Ticks
$feed = $null
for ($i = 0; $i -lt 12; $i++) {
    try {
        $feed = Invoke-RestMethod -Uri $feedUrl -TimeoutSec 30 -Headers @{ 'Cache-Control' = 'no-cache' }
        if ($feed.version -eq $Version) { break }
    } catch { }
    Start-Sleep -Seconds 15
}

if (-not $feed -or $feed.version -ne $Version) {
    throw ("the feed still says '" + $(if ($feed) { $feed.version } else { 'nothing' }) + "' - a panel would NOT see $tag. " +
           "The workflow updates docs\update.json; if that step failed, run it by hand before customers check.")
}

Ok ("feed: version $($feed.version)  size $([math]::Round($feed.size/1MB,1)) MB")
Ok ("sha256 " + $feed.sha256)

# the digest the feed publishes must be the digest of the asset that exists
$assetHash = ((Invoke-WebRequest -Uri ($feed.packageUrl + '.sha256') -UseBasicParsing -TimeoutSec 60).Content -split '\s+') |
    Where-Object { $_.Length -eq 64 } | Select-Object -First 1
if ($assetHash -and ($assetHash.ToLowerInvariant() -ne $feed.sha256.ToLowerInvariant())) {
    throw "feed sha256 does not match the published .sha256 asset - a panel would reject the download"
}
Ok 'feed digest matches the published asset'

Write-Host ""
Write-Host "  DONE  $tag is live and auto-update can see it" -ForegroundColor Green
Write-Host "  download page : https://sabbirx01.github.io/Script_Panel/" -ForegroundColor Gray
Write-Host "  release       : https://github.com/$repo/releases/tag/$tag" -ForegroundColor Gray
Write-Host ""
