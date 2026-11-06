<#
.SYNOPSIS
    Checks that a customer's panel would actually receive the newest release.

.DESCRIPTION
    "The update does not arrive" has three separate causes, and a release only
    looks fine if all three hold. This script checks each of them against the
    LIVE endpoints - the same ones the panel calls:

      1. the CDN feed (docs\update.json -> GitHub Pages) names the newest tag
      2. the /releases/latest redirect resolves to a panel version tag
      3. the zip the feed points at downloads, matches its published
         "sha256:<hex>" digest, and contains DeveloperXBulkAccPanel.exe

    Point 2 is the check the panel now performs itself: when the feed lags
    behind the releases it uses the tag, which is how v2.2.3 was supposed to
    reach customers but never did.

.PARAMETER Expected
    Fail unless the feed names this version, e.g. -Expected 2.2.4.

.PARAMETER Download
    Also download the package and verify its digest (~124 MB).

.EXAMPLE
    .\temp\verify-update.ps1 -Expected 2.2.4 -Download
#>
[CmdletBinding()]
param(
    [string]$Expected = '',
    [switch]$Download
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$repo = 'Sabbirx01/Script_Panel'
$feedUrl = 'https://sabbirx01.github.io/Script_Panel/update.json'
$failures = New-Object System.Collections.Generic.List[string]

function Pass($t) { Write-Host ("  PASS  " + $t) -ForegroundColor Green }
function Fail($t) { Write-Host ("  FAIL  " + $t) -ForegroundColor Red; $script:failures.Add($t) }
function Note($t) { Write-Host ("        " + $t) -ForegroundColor DarkGray }

function To-Version($s) {
    $clean = ($s -replace '[^0-9.]', '')
    try { return [Version]$clean } catch { return [Version]'0.0.0' }
}

Write-Host ""
Write-Host "  update path verification - $repo" -ForegroundColor White
Write-Host "  --------------------------------" -ForegroundColor DarkCyan

# ---------------------------------------------------------------- 1. the feed
$feed = $null
try {
    $feed = Invoke-RestMethod -Uri ($feedUrl + '?t=' + [DateTime]::UtcNow.Ticks) -TimeoutSec 30
    Pass "feed answers (version $($feed.version))"
} catch {
    Fail "feed does not answer: $($_.Exception.Message)"
}

if ($feed) {
    if ($Expected -and $feed.version -ne $Expected) {
        Fail "feed names $($feed.version), expected $Expected"
    } elseif ($Expected) {
        Pass "feed names $Expected"
    }
    if (-not $feed.packageUrl) { Fail 'feed has no packageUrl' }
    if (-not $feed.sha256 -or $feed.sha256.Length -ne 64) { Fail "feed sha256 is not 64 hex chars ($($feed.sha256))" }
    if (-not $feed.size -or $feed.size -lt 1MB) { Fail "feed size looks wrong ($($feed.size))" }
    elseif ($feed.size) { Note ("package " + [math]::Round($feed.size / 1MB, 1) + " MB") }
}

# -------------------------------------------------- 2. the newest tag redirect
$latestTag = $null
try {
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('DeveloperXBulkAccPanel/verify')
    $client.Timeout = [TimeSpan]::FromSeconds(30)
    $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, "https://github.com/$repo/releases/latest")
    $response = $client.SendAsync($request).Result
    $location = $response.Headers.Location
    if ($location) {
        $path = if ($location.IsAbsoluteUri) { $location.AbsolutePath } else { $location.OriginalString }
        $latestTag = ($path.TrimEnd('/') -split '/')[-1]
    }
    if ($latestTag -match '^[vV]?\d+(\.\d+){1,3}$') {
        Pass "newest release tag is $latestTag"
    } else {
        Fail "the newest release is '$latestTag', which is not a panel build - the panel ignores it"
        $latestTag = $null
    }
} catch {
    Fail "the newest-release redirect failed: $($_.Exception.Message)"
}

if ($feed -and $latestTag) {
    $feedV = To-Version $feed.version
    $tagV = To-Version $latestTag
    if ($tagV -gt $feedV) {
        # Not fatal: the panel falls back to the tag, which is the whole point
        # of the freshness probe. Still worth saying out loud.
        Write-Host ("  WARN  the feed is behind the newest release ($($feed.version) < $($latestTag.TrimStart('v'))) " +
                    "- panels recover via the tag, but the download page shows the old version") -ForegroundColor Yellow
    } else {
        Pass "feed is not behind the newest release"
    }
}

# ---------------------------------------------------- 3. the package it points at
if ($feed -and $Download) {
    $zipUrl = $feed.packageUrl
    $tmp = Join-Path $env:TEMP ('updcheck-' + [guid]::NewGuid().ToString('N') + '.zip')
    try {
        (New-Object System.Net.WebClient).DownloadFile($zipUrl, $tmp)
        $size = (Get-Item $tmp).Length
        if ($feed.size -and $size -ne $feed.size) { Fail "downloaded $size bytes, feed said $($feed.size)" }
        else { Pass "package downloads ($([math]::Round($size / 1MB, 1)) MB)" }

        $hash = (Get-FileHash $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -eq $feed.sha256.ToLowerInvariant()) { Pass "sha256 matches the feed" }
        else { Fail "sha256 $hash does not match the feed $($feed.sha256)" }

        # the installer reads the .zip.sha256 asset when the feed carries no
        # digest - it must be parseable after "sha256:" is stripped
        $shaText = [Text.Encoding]::UTF8.GetString(
            (Invoke-WebRequest -Uri ($zipUrl + '.sha256') -UseBasicParsing -TimeoutSec 60).Content)
        $assetHash = ($shaText.Replace('sha256:', '').Split(" `t`r`n".ToCharArray()) |
                      Where-Object { $_.Length -eq 64 } | Select-Object -First 1)
        if ($assetHash -and $assetHash.ToLowerInvariant() -eq $feed.sha256.ToLowerInvariant()) {
            Pass "the .sha256 asset parses and matches"
        } else {
            Fail "the .sha256 asset does not parse to the feed digest (content: $($shaText.Trim()))"
        }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($tmp)
        try {
            $names = $zip.Entries | ForEach-Object { $_.FullName }
            if ($names -contains 'DeveloperXBulkAccPanel.exe') { Pass 'package contains DeveloperXBulkAccPanel.exe' }
            else { Fail 'package has no DeveloperXBulkAccPanel.exe - the installer would refuse it' }
            if ($names -contains 'release-info.txt') { Pass 'package has release-info.txt' }
        } finally { $zip.Dispose() }
    } catch {
        Fail "package check failed: $($_.Exception.Message)"
    } finally {
        if (Test-Path $tmp) { Remove-Item $tmp -Force }
    }
}

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "  ALL CHECKS PASSED" -ForegroundColor Green
    exit 0
}
Write-Host ("  " + $failures.Count + " CHECK(S) FAILED") -ForegroundColor Red
exit 1
