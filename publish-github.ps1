[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$Notes = "",
    [switch]$SkipBuild,
    [switch]$Prerelease
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = 'Sabbirx01/Script_Panel'
$tag = if ($Version.StartsWith('v')) { $Version } else { "v$Version" }
$build = Join-Path $root 'source\build.ps1'
$exe = Join-Path $root 'output\DeveloperXBulkAccPanel.exe'
$out = Join-Path $root '_publish\github'
$zip = Join-Path $out "DeveloperXBulkAccPanel-$tag.zip"
$sig = Join-Path $out "DeveloperXBulkAccPanel-$tag.zip.sha256"

if ($Version -notmatch '^v?\d+(\.\d+){1,3}$') { throw "Version must look like 1.1.0" }
if (-not $SkipBuild) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $build
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}
if (-not (Test-Path $exe)) { throw "Build output missing: $exe" }
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

$stage = Join-Path $out 'stage'
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item $exe (Join-Path $stage 'DeveloperXBulkAccPanel.exe') -Force
$pw = Join-Path $root 'output\.playwright'
if (Test-Path $pw) { Copy-Item $pw (Join-Path $stage '.playwright') -Recurse -Force }
Set-Content (Join-Path $stage 'release-info.txt') -Encoding UTF8 -Value @(
    "Developer X Bulk Acc Panel $tag"
    "Repository: https://github.com/$repo"
    "Licence: online RSA validation remains required"
)
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $sig -Value "sha256:$hash" -Encoding ASCII
Remove-Item $stage -Recurse -Force

$body = if ([string]::IsNullOrWhiteSpace($Notes)) { "Signed/hash-verified update package for Developer X Bulk Acc Panel $tag." } else { $Notes }
$releaseArgs = @('release','create',$tag,$zip,$sig,'--repo',$repo,'--title',"Developer X Bulk Acc Panel $tag",'--notes',$body)
if ($Prerelease) { $releaseArgs += '--prerelease' }
& gh @releaseArgs
if ($LASTEXITCODE -ne 0) { throw "GitHub release creation failed." }
Write-Host ""
Write-Host "PUBLISHED $tag -> https://github.com/$repo/releases/tag/$tag" -ForegroundColor Green
Write-Host "SHA256: $hash" -ForegroundColor Cyan
