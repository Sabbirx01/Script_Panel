<#
    Publish-Panel.ps1  -  Developer X Bulk Acc Panel release tree
    ===========================================================================
    Ek command-e puro release folder structure tidy rakhe:

        Build\      ei PC-te je build chole  ->  DeveloperXBulkAccPanel.exe
        Version\    prottek release version er folder
                    Version\v1.1.0\DeveloperXBulkAccPanel\DeveloperXBulkAccPanel.exe
        Source\     oi version-er source snapshot  ->  Source\v1.1.0\
        Encrypt\    publish-er age hash HMAC-signed  ->  v1.1.0.exe.sha256.enc
        Publish\    upload-ready update feed (manifest.json + files\)

    Usage
        .\Publish-Panel.ps1                            # output\ er exe diye tree bhore
        .\Publish-Panel.ps1 -Version 1.2.0
        .\Publish-Panel.ps1 -Version 1.2.0 -Rebuild    # age source\build.ps1 chalaibe
        .\Publish-Panel.ps1 -Version 1.2.0 -MakeFeed   # Publish\ feed-o banay
        .\Publish-Panel.ps1 -Version 1.2.0 -IncludeRuntime

    kichu delete kore na - shudhu folder bhoray o notun version jog kore.
#>

[CmdletBinding()]
param(
    # khaali rakhle: Version\ er sobcheye notun folder, na thakle csproj theke
    [string]$Version = "",

    # build script chalaibe (panel bondho korte hobe)
    [switch]$Rebuild,

    # Publish\ feed banay (publish.ps1 chaliye)
    [switch]$MakeFeed,

    # feed-e runtime\ (609 MB) o dhukabe
    [switch]$IncludeRuntime
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root      = Split-Path -Parent $scriptDir      # project root = temp\ er parent
$exeName  = 'DeveloperXBulkAccPanel.exe'
$exeBase  = 'DeveloperXBulkAccPanel'

function Head($t) { Write-Host ""; Write-Host "  $t" -ForegroundColor White }
function Step($t) { Write-Host "  $t" -ForegroundColor Cyan }
function Ok($t)   { Write-Host "  $t" -ForegroundColor Green }
function Warn2($t){ Write-Host "  $t" -ForegroundColor Yellow }
function Bad($t)  { Write-Host "  $t" -ForegroundColor Red }

Write-Host ""
Write-Host "  DEVELOPER X PANEL - RELEASE TREE" -ForegroundColor Cyan
Write-Host "  --------------------------------" -ForegroundColor DarkCyan

# ------------------------------------------------------------------ 1. folders
$folders = @{
    Build   = Join-Path $root 'Build'
    Version = Join-Path $root 'Version'
    Sources = Join-Path $root 'Sources'
    Encrypt = Join-Path $root 'Encrypt'
    Publish = Join-Path $root 'Publish'
}

Head 'Folders'
foreach ($kv in $folders.GetEnumerator()) {
    New-Item -ItemType Directory -Force -Path $kv.Value | Out-Null
    Step "$($kv.Key.PadRight(8)) -> $($kv.Value)"
}

# ------------------------------------------------------------------ 2. version
function Get-VersionFromProject {
    $csproj = Join-Path $root 'source\AISuitePanel.csproj'
    if (-not (Test-Path -LiteralPath $csproj)) { return "" }
    $m = [regex]::Match((Get-Content -LiteralPath $csproj -Raw -Encoding UTF8),
                        '<Version>\s*([0-9]+(\.[0-9]+){1,3})\s*</Version>')
    if ($m.Success) { return $m.Groups[1].Value }
    return ""
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $existing = @(Get-ChildItem -LiteralPath $folders.Version -Directory -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -match '^v\d' })
    if ($existing.Count -gt 0) {
        $Version = ($existing | Sort-Object Name | Select-Object -Last 1).Name.TrimStart('v', 'V')
    } else {
        $Version = Get-VersionFromProject
    }
}

if ($Version -notmatch '^v?\d+(\.\d+){1,3}$') {
    Bad "Version must look like 1.1.0  (got '$Version')."
    exit 1
}
$Version = $Version.TrimStart('v', 'V')
$tag = "v$Version"

Head 'Plan'
Step "version : $Version"
Step "rebuild : $(if ($Rebuild) { 'yes' } else { 'no - reuse output\' })"
Step "feed    : $(if ($MakeFeed) { 'yes' } else { 'no' })"

# --------------------------------------------------------------------- 3. build
$exeSource = Join-Path $root "output\$exeName"

if ($Rebuild) {
    Head 'Building'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'source\build.ps1')
    if ($LASTEXITCODE -ne 0) { Bad "build.ps1 failed (exit $LASTEXITCODE)."; exit 1 }
}

if (-not (Test-Path -LiteralPath $exeSource)) {
    Bad "Panel exe not found: $exeSource"
    Bad "Run source\build.ps1 first, or pass -Rebuild."
    exit 1
}

$exeSize = (Get-Item -LiteralPath $exeSource).Length
Step "source exe: $([math]::Round($exeSize / 1MB, 1)) MB"

# ------------------------------------------------------- 4. Build\ + Version\
Head 'Filling Build\ and Version\'

Copy-Item -LiteralPath $exeSource -Destination (Join-Path $folders.Build $exeName) -Force
Ok "Build\$exeName"

$versionDir = Join-Path $folders.Version $tag
$appDir     = Join-Path $versionDir $exeBase
New-Item -ItemType Directory -Force -Path $appDir | Out-Null
Copy-Item -LiteralPath $exeSource -Destination (Join-Path $appDir $exeName) -Force
Ok "Version\$tag\$exeBase\$exeName"

# browser driver, jodi output-e thake - version folder self-contained hoy
$pw = Join-Path $root 'output\.playwright'
if (Test-Path -LiteralPath $pw) {
    $pwTarget = Join-Path $appDir '.playwright'
    if (Test-Path -LiteralPath $pwTarget) { Remove-Item -LiteralPath $pwTarget -Recurse -Force }
    Copy-Item -LiteralPath $pw -Destination $pwTarget -Recurse -Force
    Ok "Version\$tag\$exeBase\.playwright"
}

# ------------------------------------------------------------- 5. Sources\ copy
Head "Snapshotting source\ -> Sources\$tag"

$sourceLive = Join-Path $root 'source'
$sourceSnap = Join-Path $folders.Sources $tag

if (Test-Path -LiteralPath $sourceSnap) { Remove-Item -LiteralPath $sourceSnap -Recurse -Force }
New-Item -ItemType Directory -Force -Path $sourceSnap | Out-Null

$skipSegments = @('bin', 'obj', '_temp')
$copied = 0

# bin\ o obj\ puropuri prune kora hoy - na hole win-x64\ er published output
# (hazar hazar file) scan korte korte minit dhore chole jay.
$snapFiles = @()
$snapFiles += Get-ChildItem -LiteralPath $sourceLive -File -ErrorAction SilentlyContinue
foreach ($dir in (Get-ChildItem -LiteralPath $sourceLive -Directory -ErrorAction SilentlyContinue)) {
    if ($skipSegments -contains $dir.Name) { continue }
    $snapFiles += Get-ChildItem -LiteralPath $dir.FullName -Recurse -File -ErrorAction SilentlyContinue
}

$snapFiles | ForEach-Object {
    $relative = $_.FullName.Substring($sourceLive.Length).TrimStart('\')
    if ($_.Name -like '*.pdb' -or $_.Name -like '*.user') { return }

    $destination = Join-Path $sourceSnap $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    $script:copied++
}
Ok "$copied file(s) -> Source\$tag"

# ------------------------------------------------------------------ 6. Encrypt
Head 'Signing the hash (Encrypt\)'

$protect = Join-Path $folders.Encrypt 'Protect-Hash.ps1'
if (-not (Test-Path -LiteralPath $protect)) {
    Warn2 "Encrypt\Protect-Hash.ps1 missing - hash was NOT signed."
} else {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $protect `
        -Path (Join-Path $folders.Build $exeName) -Version $Version -Label 'exe' -OutDir $folders.Encrypt
    if ($LASTEXITCODE -ne 0) { Warn2 "Hash signing failed - continuing." }
}

# ------------------------------------------------------------------ 7. Publish
if ($MakeFeed) {
    Head 'Building the upload-ready feed (Publish\)'

    $publishScript = Join-Path $scriptDir 'publish.ps1'
    if (-not (Test-Path -LiteralPath $publishScript)) {
        Bad "publish.ps1 not found - cannot build the feed."
        exit 1
    }

    $args = @('-Version', $Version, '-SkipBuild', '-OutDir', $folders.Publish, '-Force')
    if ($IncludeRuntime) { $args += '-IncludeRuntime' }

    & powershell -NoProfile -ExecutionPolicy Bypass -File $publishScript @args
    if ($LASTEXITCODE -ne 0) { Bad "publish.ps1 failed (exit $LASTEXITCODE)."; exit 1 }

    # server-er .htaccess ta feed folder-e thakte hoy, noile sob file 403 dey
    $htaccess = Join-Path $root '_publish\server\update-folder.htaccess'
    if (Test-Path -LiteralPath $htaccess) {
        Copy-Item -LiteralPath $htaccess -Destination (Join-Path $folders.Publish '.htaccess') -Force
        Ok "Publish\.htaccess (restored)"
    }

    # manifest-er hash-o sign kore rakhi
    $manifest = Join-Path $folders.Publish 'manifest.json'
    if ((Test-Path -LiteralPath $manifest) -and (Test-Path -LiteralPath $protect)) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $protect `
            -Path $manifest -Version $Version -Label 'manifest' -OutDir $folders.Encrypt
    }
}

# ------------------------------------------------------------------- 8. READMEs
Head 'Documenting the folders'

$notes = [ordered]@{
    'Build' = @"
Build\ - ei PC-te je build chole
------------------------------------------------------------
DeveloperXBulkAccPanel.exe   <- panel-er current build (source\build.ps1 er output)

Niyom:
  * ekhane shudhu BAAKI exe thake, notun version ele overwrite hoy
  * version-wise archive lagle Version\ folder ta dekho
  * Publish-Panel.ps1 chalaile ei folder automatic bhore jay
"@

    'Version' = @"
Version\ - prottek release version-er archive
------------------------------------------------------------
v1.1.0\
  DeveloperXBulkAccPanel\      <- exe-name folder
    DeveloperXBulkAccPanel.exe   ei version-er panel
    .playwright\                 browser driver (thakle)

Niyom:
  * notun version = notun folder. purano folder kokhono delete kora hoy na
  * ei folder khuub boro (100+ MB) - GitHub-e push kora jabe na (.gitignore-e ache)
"@

    'Sources' = @"
Sources\ - version-wise source snapshot
------------------------------------------------------------
v1.1.0\      oi version-er source (bin, obj, _temp baad)

Keno:
  * Version\v1.1.0\ er exe ta thik kon code theke banlo - seta proof hisebe thake
  * ekta version-e bug pele ei folder theke hubohu build kora jay

⚠️  Naam "Sources" keno, "Source" noy:
  Windows-e "source" ar "Source" EK-I folder. Snapshot ta source\ er vitore
  gele .NET build oi copy-gulo-ke o compile kore - duplicate class (CS0101) -
  build bhenge jay. Tai alada naam.

Niyom:
  * live source\ folder ALL time alada thake - ei snapshot shudhu copy
"@

    'Encrypt' = @"
Encrypt\ - publish-er age hash HMAC-signed hoy
------------------------------------------------------------
Protect-Hash.ps1        signing script
v1.1.0.exe.sha256.enc   Build\ exe-er sha256 + hmac
v1.1.0.manifest.sha256.enc  feed manifest-er sha256 + hmac

Keno:
  * shudhu .sha256 dile keu bodle dite pare
  * HMAC thakle server/app nijei verify korte pare

Secret:
  * licence-local\config.php -> 'signing_secret'
  * secret kokhono ei folder-e lekha hoy na
"@

    'Publish' = @"
Publish\ - upload-ready feed (server-e ei folder-er BHIITOR jaay)
------------------------------------------------------------
manifest.json   kon version, kon file, kon sha256
files\          asol file-gulo (exe + tools)
.htaccess       server-er rule file (na thakle sob 403)

Niyom:
  * ei folder automatic toiri hoy - hate file bosaben na
  * notun feed = Publish-Panel.ps1 -MakeFeed
  * tarpor .\deploy-update.ps1 chalaile server-e upload hobe
"@
}

foreach ($name in $notes.Keys) {
    $target = Join-Path $folders[$name] 'README.txt'
    Set-Content -LiteralPath $target -Value $notes[$name].Trim() -Encoding UTF8
    Ok "$name\README.txt"
}

# -------------------------------------------------------------------- 9. report
$total = @(Get-ChildItem -LiteralPath $folders.Version -Recurse -File -ErrorAction SilentlyContinue)
$versions = @(Get-ChildItem -LiteralPath $folders.Version -Directory -ErrorAction SilentlyContinue)

Write-Host ""
Write-Host "  RELEASE TREE READY" -ForegroundColor Green
Write-Host "  version : $tag" -ForegroundColor White
Write-Host "  versions archived : $($versions.Count)" -ForegroundColor Gray
Write-Host "  version files     : $($total.Count)" -ForegroundColor Gray
Write-Host ""
Write-Host "  next: .\deploy-update.ps1   (feed upload korte)" -ForegroundColor Yellow
Write-Host ""
