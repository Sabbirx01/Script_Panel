<#
    Developer X Bulk Acc Panel - build script.

    Publishes the panel and drops the finished executable into ..\output
    (no build artefacts are left in the source tree).

    Usage:
        .\build.ps1                      # self-contained single file (default)
        .\build.ps1 -FrameworkDependent  # small exe, needs .NET desktop runtime
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",

    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",

    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"

$sourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir   = Split-Path -Parent $sourceDir
$project   = Join-Path $sourceDir "AISuitePanel.csproj"
$outputDir = Join-Path $rootDir "output"
$exeName   = "DeveloperXBulkAccPanel.exe"

Write-Host ""
Write-Host "  DEVELOPER X BULK ACC PANEL - BUILD" -ForegroundColor Cyan
Write-Host "  ----------------------------------" -ForegroundColor DarkCyan

if (-not (Test-Path -LiteralPath $project)) {
    throw "Project file not found: $project"
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

# A running panel would lock the executable, so close it first.
$running = Get-Process -Name "DeveloperXBulkAccPanel" -ErrorAction SilentlyContinue
if (-not $running) {
    # Older builds shipped under the previous assembly name.
    $running = Get-Process -Name "AISuitePanel" -ErrorAction SilentlyContinue
}
if ($running) {
    Write-Host "  closing $($running.Count) running panel instance(s)" -ForegroundColor DarkYellow
    $running | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) }
    Start-Sleep -Milliseconds 400
}

# Remove a previous build so output never holds a stale executable.
$previous = Join-Path $outputDir $exeName
if (Test-Path -LiteralPath $previous) {
    try {
        Remove-Item -LiteralPath $previous -Force
        Write-Host "  removed previous $exeName" -ForegroundColor DarkGray
    } catch {
        throw "Could not replace $exeName - close the running panel and try again."
    }
}

$publishArgs = @(
    "publish", $project,
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", $outputDir,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=none",
    "--nologo"
)

if ($FrameworkDependent) {
    $publishArgs += "-p:SelfContained=false"
    Write-Host "  mode  : framework dependent (needs .NET desktop runtime)" -ForegroundColor Yellow
} else {
    $publishArgs += "-p:SelfContained=true"
    Write-Host "  mode  : self-contained single file" -ForegroundColor Green
}

Write-Host "  output: $outputDir" -ForegroundColor DarkGray
Write-Host ""

& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit code $LASTEXITCODE)"
}

# Keep the output folder to just the executable.
Get-ChildItem -Path (Join-Path $outputDir "*") -Include *.pdb, *.json -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

# Carry the Playwright Node driver + browser hint alongside the EXE so the
# Login tab can spin up Chromium with cookies without needing a separate install.
$playwrightSrc = Join-Path $sourceDir "bin\Release\net10.0-windows\win-x64\.playwright"
$playwrightDst = Join-Path $outputDir ".playwright"
if (Test-Path -LiteralPath $playwrightSrc) {
    robocopy $playwrightSrc $playwrightSrc /MIR /NFL /NDL /NJH /NJS /NC /NS /NP | Out-Null
    if (Test-Path -LiteralPath $playwrightDst) { Remove-Item -LiteralPath $playwrightDst -Recurse -Force }
    Copy-Item -LiteralPath $playwrightSrc -Destination $playwrightDst -Recurse -Force
    Write-Host "  copied Playwright driver to $playwrightDst" -ForegroundColor DarkGray
}

$built = Join-Path $outputDir $exeName
if (-not (Test-Path -LiteralPath $built)) {
    throw "Build reported success but $exeName was not produced."
}

$sizeMb = [math]::Round((Get-Item -LiteralPath $built).Length / 1MB, 1)

Write-Host ""
Write-Host "  BUILD OK" -ForegroundColor Green
Write-Host "  $built  ($sizeMb MB)" -ForegroundColor White
Write-Host ""
