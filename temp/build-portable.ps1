<#
    build-portable.ps1 - assemble a portable, shippable copy of the panel.

    Produces:

        dist\DeveloperX-Portable\
        ??? DeveloperXBulkAccPanel.exe     the panel
        ??? .playwright\                   the driver the panel itself needs
        ??? tools\                         the four tools' code
        ??? runtime\
        ?   ??? ms-playwright\             the browser build the tools need
        ?   ??? python\                    (optional) a bundled interpreter
        ??? data\tool-settings.json        paths written RELATIVE, so any drive works
        ??? portable.txt                   tells the panel to keep data here
        ??? README.txt                     what the customer needs to know

    WHAT IT DELIBERATELY LEAVES OUT - and must, every time:

        licence-local\   the server.  config.php holds the RSA PRIVATE KEY; anyone
                         who has it can mint keys for every tool without you.
        source\          the panel's own code.
        connector\       the tools' drop-in client, and its tests.
        _archive\ _backups\ tools\accio\source\ (not needed to run)

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

          chromium_headless_shell-*   270.8 MB   the tools are forbidden from
                                                  running headless (it is the first
                                                  thing bot detection looks at) and
                                                  the panel never asks for it
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
$toolsSrc = Join-Path $Root "tools"
if (Test-Path -LiteralPath $toolsSrc) {
    $toolsDst = Join-Path $Out "tools"
    New-Item -ItemType Directory -Force -Path $toolsDst | Out-Null

    # The operator's own credentials and per-run state must never travel inside a
    # bundle.  They used to: a plain Copy-Item put the operator's Gmail address
    # AND its password (tools\minimax\accounts.txt), the temp-mail tokens
    # (account.txt) and 150 KB of live session cookies (cookie.txt/cookies.txt)
    # into every copy handed to a customer -- which also defeated the whole point
    # of accio's obfuscated secret.py sitting right next to a plaintext .env.
    # These are written on the customer's own PC at run time, so leaving them out
    # is safe.  secret.py is deliberately NOT in this list: the tools import it.
    $secretNames = @(
        ".env", "accounts.txt", "account.txt", "cookies.txt", "cookie.txt",
        "usage_data.json", "usage.txt", "alice.json",
        # Claim bookkeeping and the archive of dead accounts were missed by the
        # first version of this list, so a bundle went out carrying claims.txt -
        # which names real accounts and when they were last claimed.
        "claims.txt", "dead_accounts.txt",
        # Devin's saved browser storage. 187 KB of it, naming 13 real accounts
        # and carrying their live auth session tokens - also missed by the first
        # list, so it travelled inside a shipped bundle. Never again: match the
        # name AND anything ending in _storage.txt / storage.txt.
        "devin_storage.txt", "storage.txt",
        # create.py's own runtime files. _live_proxies.txt is the list of
        # proxies that SURVIVED its liveness test last run (create.py:1473
        # "Write working proxies to a temp file beside the .exe"; it refetches
        # from the provider when they all die), so shipping it handed every
        # customer the operator's working proxy pool. _x_trick_debug.txt is a
        # DOM dump create.py:2982 writes on demand. Both are recreated on the
        # customer's own PC, so leaving them out is safe.
        "_live_proxies.txt", "_x_trick_debug.txt"
    )
    $skipped = 0
    Get-ChildItem -LiteralPath $toolsSrc -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\__pycache__\\|\\node_modules\\|\\screenshots\\' } |
        ForEach-Object {
            # Exact names, plus anything that looks like a saved browser store
            # (devin_storage.txt and friends), because that is where live
            # session tokens hide.
            if ($secretNames -contains $_.Name -or $_.Name -match '_storage\.txt$') { $skipped++; return }
            $rel = $_.FullName.Substring($toolsSrc.Length).TrimStart('\')
            $dst = Join-Path $toolsDst $rel
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $dst -Force
        }

    # A previous build left them behind, and Copy-Item merges rather than deletes.
    $stale = Get-ChildItem -LiteralPath $toolsDst -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $secretNames -contains $_.Name }
    if ($stale) {
        $stale | Remove-Item -Force -ErrorAction SilentlyContinue
        Step ("scrubbed         " + $stale.Count + " leftover credential/state file(s)")
    }

    $n = (Get-ChildItem $toolsDst -Recurse -File | Measure-Object).Count
    Step ("tools            $n files  ($skipped credential/state file(s) left out)")

    # Refuse to hand over a bundle that still carries someone's credentials.
    $still = Get-ChildItem -LiteralPath $Out -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $secretNames -contains $_.Name -or $_.Name -match '_storage\.txt$' }
    if ($still) {
        $still | Select-Object -First 8 | ForEach-Object { Step ("  LEAK  " + $_.FullName) }
        Fail "credential or state files reached the bundle - refusing to ship it."
    }
    Step "safety check     no credential/state files in the bundle"

    # Second net, by CONTENT rather than by filename.  The name filter above is
    # blind to a secret typed into ordinary source -- and that is exactly what
    # was hiding here: tools\minimax\create_advanced.py shipped a
    # DEFAULT_PASSWORD literal that happened to be the operator's own Gmail
    # password.  Warn instead of failing, because a tool legitimately may carry a
    # non-personal default; a human should look at whatever this lists.
    $placeholder = 'example|placeholder|changeme|your[_-]?|dummy|xxxx|\$\{|os\.environ|getenv|None|""|'''
    # Note: -Include is silently ignored next to -LiteralPath, which would make
    # this check find nothing at all.  Filter the extension by hand instead.
    $suspect = Get-ChildItem -LiteralPath $toolsDst -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.py', '.js', '.ts' } |
        Select-String -Pattern 'PASSWORD|API_KEY|SECRET|TOKEN|PASSWD' -ErrorAction SilentlyContinue |
        Where-Object { $_.Line -match '=\s*["''][^"'']{6,}["'']' -and $_.Line -notmatch $placeholder }
    if ($suspect) {
        Step ""
        Step "WARNING - hardcoded-looking secrets in shipped code:"
        $suspect | Select-Object -First 12 | ForEach-Object {
            Step ("  " + $_.Path.Replace($toolsDst + '\', '') + ":" + $_.LineNumber + "  " + $_.Line.Trim())
        }
        Step "  (review these; a real credential must come from the panel, not the source)"
        Step ""
    }
} else {
    Fail "tools\ is missing - nothing for the panel to run."
}

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
    if (-not (Test-Path (Join-Path $Out "runtime\python\python.exe"))) {
        Step "                 (no bundled python: the customer's PC needs Python installed)"
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
  tools\     the automation scripts
  runtime\   the browser they run in

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
