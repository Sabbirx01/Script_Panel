<#
    build-portable.ps1 - assemble a portable, shippable copy of the panel.

    Produces:

        dist\DeveloperX-Portable\
        ??? DeveloperXBulkAccPanel.exe     the panel - and the tools' code inside it
        ??? .playwright\                   the driver the panel itself needs
        ??? tools.manifest                 fingerprint of the code inside the exe
        ??? runtime\
        ?   ??? ms-playwright\             the browser build the tools need
        ?   ??? python\                    (optional) a bundled interpreter
        ??? data\tool-settings.json        paths written RELATIVE, so any drive works
        ??? portable.txt                   tells the panel to keep data here
        ??? README.txt                     what the customer needs to know

    Note the missing tools\ folder. The four tools' Python source is packed into
    one AES-256 encrypted blob (source\Resources\toolbox.enc) that is embedded in
    the panel executable and handed to the interpreter in memory, so the .py
    files never reach the customer's disk.

    WHAT IT DELIBERATELY LEAVES OUT - and must, every time:

        licence-local\   the server.  config.php holds the RSA PRIVATE KEY; anyone
                         who has it can mint keys for every tool without you.
        source\          the panel's own code.
        connector\       the tools' drop-in client, and its tests.
        tools\           the tools' Python source - it belongs in the exe.
        _archive\ _backups\

    Nothing it copies contains a private key, so the result is safe to hand over.

    Usage:
        powershell -ExecutionPolicy Bypass -File build-portable.ps1
        powershell -ExecutionPolicy Bypass -File build-portable.ps1 -Clean
#>
[CmdletBinding()]
param(
    [string]$Root = "",
    [string]$Out  = "",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

# $PSScriptRoot can arrive empty when the script is launched through
# "powershell -File" from another shell, so fall back rather than binding "".
# ei script ekhon temp\-e thake, tai project root = script folder-er parent.
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = $PSScriptRoot }
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not [string]::IsNullOrWhiteSpace($Root)) { $Root = Split-Path -Parent $Root }
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = (Get-Location).Path }
if ([string]::IsNullOrWhiteSpace($Out)) { $Out = Join-Path $Root "dist\DeveloperX-Portable" }

function Step($text) { Write-Host ("  " + $text) }
function Fail($text) { Write-Host ("  " + $text) -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------- browser prune
function Prune-Browsers($msPlaywright) {
    <#
        Drop the parts of a Playwright browser set that this suite never loads.

        Measured on the shipping bundle: 700.8 MB of browsers, of which only
        426.7 MB is ever used.

          chromium_headless_shell-*   270.8 MB   a SECOND browser, used only by
                                                  Playwright's headless mode.
                                                  panel_browser.py now asks for
                                                  channel="chromium" whenever a
                                                  run is headless, which drives
                                                  the chromium-* build already
                                                  shipped - so the shell is not
                                                  needed at all (verified: the
                                                  bundled build launches both
                                                  headless and visible)
          ffmpeg-*                      3.4 MB   only used for video recording
          firefox-* / webkit-*                   no tool drives either
          locales\ (220 files)         42.7 MB   only en-US is ever rendered

        This runs on the COPY that is about to be shipped, never on the source
        tree, so an existing developer setup is left alone.
    #>
    if (-not (Test-Path -LiteralPath $msPlaywright)) { return }

    foreach ($pattern in @("chromium_headless_shell-*", "ffmpeg-*", "winldd-*",
                           "firefox-*", "webkit-*")) {
        Get-ChildItem -LiteralPath $msPlaywright -Directory -Filter $pattern -ErrorAction SilentlyContinue |
            ForEach-Object {
                $mb = [math]::Round(((Get-ChildItem $_.FullName -Recurse -File -Force -ErrorAction SilentlyContinue |
                                      Measure-Object Length -Sum).Sum / 1MB), 1)
                Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
                Step ("pruned           " + $_.Name + "  (-$mb MB)")
            }
    }

    Get-ChildItem -LiteralPath $msPlaywright -Directory -Filter "chromium-*" -ErrorAction SilentlyContinue |
        ForEach-Object {
            $locales = Join-Path $_.FullName "chrome-win64\locales"
            if (Test-Path -LiteralPath $locales) {
                $before = (Get-ChildItem -LiteralPath $locales -File).Count
                Get-ChildItem -LiteralPath $locales -File -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -ne "en-US.pak" -and $_.Name -ne "en-GB.pak" } |
                    Remove-Item -Force -ErrorAction SilentlyContinue
                $after = (Get-ChildItem -LiteralPath $locales -File).Count
                if ($before -gt $after) {
                    Step ("pruned           locales " + $before + " -> " + $after + " files")
                }
            }
        }
}

Write-Host ""
Write-Host "  DEVELOPER X - PORTABLE BUNDLE" -ForegroundColor Cyan
Write-Host "  ------------------------------" -ForegroundColor DarkCyan
Step ("output : " + $Out)
Write-Host ""

# ?? 1. the executable ????????????????????????????????????????????????????????
$exe = Join-Path $Root "output\DeveloperXBulkAccPanel.exe"
if (-not (Test-Path -LiteralPath $exe)) { Fail "No panel build found at $exe - run source\build.ps1 first." }

# ?? 2. a clean destination ???????????????????????????????????????????????????
if ($Clean -and (Test-Path -LiteralPath $Out)) {
    # SendToRecycleBin, NOT Shell.Application.Namespace(0) - namespace 0 is the
    # DESKTOP, not the Recycle Bin, so that older line quietly dumped every
    # removed folder onto the user's desktop. Namespace(10) is the Recycle Bin;
    # the VisualBasic call is the reliable way to reach it.
    Add-Type -AssemblyName Microsoft.VisualBasic
    [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory(
        (Get-Item -LiteralPath $Out -Force).FullName, 'OnlyErrorDialogs', 'SendToRecycleBin')
    Step "moved the previous bundle to the Recycle Bin"
}
New-Item -ItemType Directory -Force -Path $Out | Out-Null

Copy-Item -LiteralPath $exe -Destination $Out -Force
Step ("panel            " + [math]::Round((Get-Item $exe).Length / 1MB, 1) + " MB")

# the driver the panel runs on - without it the exe starts and then complains
$pw = Join-Path $Root "output\.playwright"
if (Test-Path -LiteralPath $pw) {
    Copy-Item -LiteralPath $pw -Destination $Out -Recurse -Force
    Step ".playwright      ok"
} else {
    Fail "output\.playwright is missing - the panel cannot run without it."
}

# ?? 3. the tools' code ???????????????????????????????????????????????????????
#
# The tools' Python source is NOT shipped as files any more. It is packed into
# one AES-256 blob (source\Resources\toolbox.enc) that lives INSIDE
# DeveloperXBulkAccPanel.exe, and the panel hands it to the bundled interpreter
# in memory. There is therefore no scripts folder to copy - which is the point:
# anybody who got the installer used to get the whole tools\ tree with it.
#
# Two things are still needed here:
#
#   tools.manifest   the identity of the payload that sits inside the exe. The
#                    panel hashes this one small file at startup (BundleUpdate)
#                    and compares it with the value published for the release,
#                    which is how a tools-side change gets noticed at all. It is
#                    written by temp\build-toolbox.ps1 from the very files that
#                    went into the archive, so it cannot drift from the exe.
#
#   a clean folder   a tools\ folder left behind by an earlier build must not
#                    survive: Copy-Item merges rather than replaces, and
#                    shipping it would hand over exactly what this removes.
$manifestSrc = Join-Path $Root "temp\tools.manifest"
if (-not (Test-Path -LiteralPath $manifestSrc)) {
    Fail "temp\tools.manifest is missing - run temp\build-toolbox.ps1 (source\build.ps1 does it) first."
}

$staleTools = Join-Path $Out "tools"
if (Test-Path -LiteralPath $staleTools) {
    Remove-Item -LiteralPath $staleTools -Recurse -Force
    Step "removed           a leftover tools\ folder from the bundle"
}

Copy-Item -LiteralPath $manifestSrc -Destination (Join-Path $Out "tools.manifest") -Force
$entries = @(Get-Content -LiteralPath $manifestSrc -ErrorAction SilentlyContinue)
Step ("tools.manifest   " + $entries.Count + " file entries (fingerprints the in-exe payload)")

# Refuse to hand over a bundle that still carries someone's credentials. They
# used to ride along inside tools\ ; the tools are gone from the bundle now, but
# the check stays because that folder is not the only thing that gets copied.
$secretNames = @(
    ".env", "accounts.txt", "account.txt", "cookies.txt", "cookie.txt",
    "usage_data.json", "usage.txt", "alice.json",
    "claims.txt", "dead_accounts.txt",
    "devin_storage.txt", "storage.txt",
    "_live_proxies.txt", "_x_trick_debug.txt"
)
$still = Get-ChildItem -LiteralPath $Out -Recurse -File -Force -ErrorAction SilentlyContinue |
    Where-Object { $secretNames -contains $_.Name -or $_.Name -match '_storage\.txt$' }
if ($still) {
    $still | Select-Object -First 8 | ForEach-Object { Step ("  LEAK  " + $_.FullName) }
    Fail "credential or state files reached the bundle - refusing to ship it."
}
Step "safety check     no credential/state files in the bundle"

# The whole point of this build, asserted rather than assumed: no tool source
# anywhere in the bundle. The interpreter and the panel's own Playwright driver
# carry .py of their own (the Python stdlib, the Node driver) and those are not
# ours, so only those two folders are excused.
$exposed = Get-ChildItem -LiteralPath $Out -Recurse -File -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in '.py', '.pyc' } |
    Where-Object { $_.FullName -notmatch '\\runtime\\|\\.playwright\\' }
if ($exposed) {
    $exposed | Select-Object -First 10 | ForEach-Object { Step ("  LEAK  " + $_.FullName) }
    Fail "tool source reached the bundle - refusing to ship it."
}
Step "safety check     no tool source in the bundle"

# ?? 4. the browser build Playwright asks for ?????????????????????????????????
$rt = Join-Path $Root "runtime"
if (Test-Path -LiteralPath $rt) {
    Copy-Item -LiteralPath $rt -Destination $Out -Recurse -Force
    $before = [math]::Round(((Get-ChildItem (Join-Path $Out "runtime") -Recurse -File |
                             Measure-Object Length -Sum).Sum / 1MB), 1)
    # Trim what nothing in this suite ever loads (see Prune-Browsers above).
    Prune-Browsers (Join-Path $Out "runtime\ms-playwright")
    $mb = [math]::Round(((Get-ChildItem (Join-Path $Out "runtime") -Recurse -File |
                          Measure-Object Length -Sum).Sum / 1MB), 1)
    Step ("runtime          $mb MB   (was $before)")
    # The panel exports PLAYWRIGHT_BROWSERS_PATH for itself and for every tool
    # process (AppPaths.RuntimeEnvironment), which is what makes the folder above
    # the browser that is actually used. Without it Playwright only ever looks at
    # %LOCALAPPDATA%\ms-playwright and the bundled copy is dead weight.
    Step "                 PLAYWRIGHT_BROWSERS_PATH -> runtime\ms-playwright (set by the panel)"

    # -- the python side of the runtime ---------------------------------------
    $py = Join-Path $Out "runtime\python\python.exe"
    if (Test-Path -LiteralPath $py) {
        # Drop every site-package the tools never import (selenium, openai,
        # mistralai, g4f, aiohttp, pip ...). temp\prune-runtime.py derives the
        # keep-set from the tools' own import statements and REFUSES to delete
        # anything it cannot prove is unused - so a wrong guess stops the build
        # instead of breaking a customer's run.
        $pruner = Join-Path $Root "temp\prune-runtime.py"
        if (Test-Path -LiteralPath $pruner) {
            & $py $pruner (Join-Path $Out "runtime\python\Lib\site-packages") `
                --tools (Join-Path $Root "tools") --apply 2>&1 |
                Where-Object { $_ -match 'freed|keep-set|REFUSING|not covered' } |
                ForEach-Object { Step ("prune            " + $_.ToString().Trim()) }
            if ($LASTEXITCODE -ne 0) {
                Fail "pruning the python runtime failed - refusing to ship a runtime the tools cannot import from."
            }

            # Prove the pruned runtime still satisfies every tool: this imports
            # the roots and fails the build on the first missing module.
            $probe = @'
import importlib
import sys

MODS = ["requests", "playwright", "curl_cffi", "cryptography", "dotenv",
        "pyautogui", "pyperclip", "pywinauto", "cloakbrowser"]

bad = []
for name in MODS:
    try:
        importlib.import_module(name)
    except Exception as exc:
        bad.append("%s (%s)" % (name, exc))

print("OK" if not bad else "MISSING " + ", ".join(bad))
sys.exit(1 if bad else 0)
'@
            $probeFile = Join-Path $Root "temp\_runtime-probe.py"
            Set-Content -LiteralPath $probeFile -Value $probe -Encoding UTF8
            $result = & $py $probeFile 2>&1
            if ($LASTEXITCODE -ne 0) {
                Fail ("the pruned runtime cannot import a module the tools use: " + ($result -join ' '))
            }
            Step "import test      every module the tools use still imports"
        }

        # pip and its console scripts are dead weight once the runtime is fixed.
        foreach ($junk in @("runtime\python\Scripts", "runtime\python\Lib\ensurepip")) {
            $path = Join-Path $Out $junk
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        }
    } else {
        Step "                 (no bundled python: the customer's PC needs Python installed)"
    }

    # One Node binary, not two. The C# driver's .playwright\node\win32_x64\node.exe
    # is byte-identical to the one the python playwright package ships (both are
    # the 1.62.0 driver), so 88 MB of duplicate used to travel in every bundle.
    # The panel points PLAYWRIGHT_NODEJS_PATH at the python copy (see
    # AppPaths.RuntimeEnvironment), which the C# driver honours.
    $dupNode = Join-Path $Out ".playwright\node"
    $sharedNode = Join-Path $Out "runtime\python\Lib\site-packages\playwright\driver\node.exe"
    if ((Test-Path -LiteralPath $dupNode) -and (Test-Path -LiteralPath $sharedNode)) {
        Remove-Item -LiteralPath $dupNode -Recurse -Force
        Step "slimmed          .playwright\node removed - one shared node.exe with the python driver"
    } elseif (Test-Path -LiteralPath $dupNode) {
        Step "                 .playwright\node kept (no python driver to share it with)"
    }
} else {
    Step "runtime\ missing - the tools will need a browser already installed on the target PC"
}

# ?? 5. portable marker + data folder ?????????????????????????????????????????
Set-Content -LiteralPath (Join-Path $Out "portable.txt") -Encoding ASCII -Value @"
Portable mode is on because this file sits next to the .exe.

The panel keeps its licence, its tools list and every tool's accounts and cookies
in the  data\  folder here, instead of in %APPDATA%. Copy the whole folder
anywhere - another drive, another PC, a USB stick - and it carries its state with
it. Delete  data\  to start a fresh, unactivated copy.
"@
Step "portable.txt     written"

# ?? 6. tool paths, written RELATIVE so any drive letter works ????????????????
$data = Join-Path $Out "data"
New-Item -ItemType Directory -Force -Path $data | Out-Null

# Mirrors %APPDATA%\AISuitePanel\tool-settings.json, but with relative paths.
# AppPaths.Resolve() turns each one into an absolute path under the folder the
# .exe actually sits in, so a customer's drive letter never matters.
$map = [ordered]@{
    zai = [ordered]@{
        delaySeconds         = 3
        threads              = 2
        pythonExePath        = ""
        createScriptPath     = "tools\zai\Zcreatev2.py"
        loginScriptPath      = "tools\zai\Zloginv2.py"
        toolWorkingDirectory = "tools\zai"
        createArgTemplate    = "--mode {mode} --count {count}"
        loginArgTemplate     = "--mode {mode}"
        headless             = $false
    }
    devin = [ordered]@{
        delaySeconds         = 3
        threads              = 2
        pythonExePath        = ""
        createScriptPath     = "tools\devin\devin_create.py"
        loginScriptPath      = "tools\devin\devin_login.py"
        toolWorkingDirectory = "tools\devin"
        createArgTemplate    = "--count {count}"
        loginArgTemplate     = ""
        headless             = $false
    }
    tool4 = [ordered]@{
        delaySeconds         = 2
        threads              = 2
        pythonExePath        = ""
        createScriptPath     = "tools\minimax\minimax_create.py"
        loginScriptPath      = "tools\minimax\minimax_login.py"
        toolWorkingDirectory = "tools\minimax"
        createArgTemplate    = "--count {count}"
        loginArgTemplate     = ""
        headless             = $false
    }
    accio = [ordered]@{
        delaySeconds         = 3
        threads              = 2
        pythonExePath        = ""
        # These used to ship blank, so a fresh customer install had no accio
        # script path at all and the tool could not run even though
        # tools\accio\source\create.py was in the bundle. Resolved relative to
        # the folder the .exe sits in, like the other three tools above.
        createScriptPath     = "tools\accio\source\create.py"
        loginScriptPath      = "tools\accio\source\login.py"
        toolWorkingDirectory = "tools\accio\source"
        createArgTemplate    = "--count {count}"
        loginArgTemplate     = ""
        headless             = $false
    }
}
$map | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $data "tool-settings.json") -Encoding UTF8
Step "data\tool-settings.json  (relative paths)"

# The licence endpoint, written out rather than left null.  null would also be
# correct (it falls back to the panel's built-in live endpoint), but spelling it
# out means anyone opening the shipped data\ can see where the bundle phones home
# without having to read the .exe.  The operator's own copy carries the same value.
$products = [ordered]@{
    panelProducts = @("panel", "dola")
    keyOverrides  = @{}
    validateUrl   = "https://license.automationservice.online/api/validate.php"
    mainProduct   = $null
    knownProducts = $null
    toolProducts  = $null
}
$products | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $data "products.json") -Encoding UTF8
Step "data\products.json       (licence endpoint = live)"

# ?? 7. the customer's own note ???????????????????????????????????????????????
Set-Content -LiteralPath (Join-Path $Out "README.txt") -Encoding UTF8 -Value @"
Developer X - Bulk Acc Panel (portable)
=======================================

1.  Double-click  DeveloperXBulkAccPanel.exe
2.  Paste the licence key you were given and press Activate.
    The key locks itself to this PC on first use, so keep it.

Everything lives in this folder.  Move the whole folder to another PC and it
comes with you - but a licence key only runs on the machine it was activated on,
so ask for a new one if you move.

  data\      your licence, your tool settings, your accounts and cookies
  runtime\   the browser the tools run in

The tools' own scripts are not files you can open - they are encrypted inside
DeveloperXBulkAccPanel.exe, which is what keeps them out of anyone else's hands.
"
Need help?  Send the folder's  data\logs\  file for the day it went wrong.
"@
Step "README.txt       written"

# ?? 8. prove nothing private went in ????????????????????????????????????????
Write-Host ""
Write-Host "  SAFETY CHECK" -ForegroundColor Cyan
$bad = @()
foreach ($pack in @("licence-local", "source", "connector", "_archive", "_backups")) {
    if (Test-Path -LiteralPath (Join-Path $Out $pack)) { $bad += $pack }
}
Get-ChildItem $Out -Recurse -File -Filter "config.php" -ErrorAction SilentlyContinue |
    ForEach-Object { $bad += $_.FullName }
Get-ChildItem $Out -Recurse -File -Filter "*.md" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like "connector*" } |
    ForEach-Object { $bad += $_.FullName }

if ($bad.Count -gt 0) {
    Write-Host "  !! the bundle contains something it must not:" -ForegroundColor Red
    $bad | ForEach-Object { Write-Host ("     " + $_) -ForegroundColor Red }
    Write-Host "     Delete these before shipping.  The server's config.php holds" -ForegroundColor Red
    Write-Host "     the RSA private key for the whole licence system." -ForegroundColor Red
    exit 1
}
Write-Host "  no server, no source, no private key  - safe to hand over" -ForegroundColor Green

$total = [math]::Round(((Get-ChildItem $Out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ""
Write-Host ("  DONE  -  {0} MB  at  {1}" -f $total, $Out) -ForegroundColor Green
Write-Host ""
