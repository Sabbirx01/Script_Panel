using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AISuitePanel;

/// <summary>
/// Repairs an install that was created from the update ZIP instead of the
/// one-file Setup.
///
/// <para><b>Why this exists.</b> The download page used to hand out the update
/// package, which carries the panel exe and .playwright and nothing else. A
/// panel installed from it opens normally and then fails every single run with
/// "Create script not found", because <c>tools\</c> and <c>runtime\</c> were
/// never there. Telling the customer to go and find Setup.exe assumes they
/// will read a log, believe it, and finish the job - so the panel now fixes
/// itself: it downloads the published Setup.exe, verifies its SHA-256, and
/// hands over to it.</para>
///
/// <para>The self-updater cannot do this job. It replaces one file; a complete
/// install is ~600 MB of tools and a Python runtime, and the release publishes
/// the encrypted setup precisely because that is the only artefact that carries
/// them.</para>
/// </summary>
internal static class SelfRepair
{
    /// <summary>Title + detail for the dashboard banner, marshalled by the view.</summary>
    public static event Action<string, string>? Status;

    private static bool _running;

    /// <summary>
    /// True when this copy cannot possibly run a tool: the archive it came from
    /// had no <c>tools\</c> folder, or no Python runtime to run them with.
    /// </summary>
    public static bool NeedsRepair =>
        !Directory.Exists(Path.Combine(AppPaths.AppRoot, "tools")) ||
        !File.Exists(Path.Combine(AppPaths.AppRoot, "runtime", "python", "python.exe"));

    /// <summary>
    /// Result of the last deep check: null = not checked yet, true = the files
    /// on disk match tools.manifest, false = something is missing or altered.
    /// Cached so the update path does not hash 216 MB a second time.
    /// </summary>
    private static bool? _toolsIntact;

    /// <summary>First few tool files that are missing or do not match the manifest.</summary>
    public static IReadOnlyList<string> BrokenTools { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// True when the panel should not spend a 596 MB download of its own: the
    /// repair owns this install. Null (unchecked) counts as "carry on".
    /// </summary>
    public static bool ToolsKnownBroken => _toolsIntact == false;

    private static string MarkerFile => Path.Combine(AppPaths.Root, "repair-attempt.json");

    private sealed class Marker
    {
        public string At { get; set; } = "";
        public string Version { get; set; } = "";
        public string Result { get; set; } = "";
    }

    /// <summary>
    /// A repair that already failed today must not run again on every start -
    /// a 600 MB download in a loop is worse than the bug. The customer still
    /// has the download page and the message in the run log.
    /// </summary>
    private static bool AttemptedRecently()
    {
        try
        {
            if (!File.Exists(MarkerFile)) return false;
            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerFile));
            if (marker is null || !DateTime.TryParse(marker.At, out var at)) return false;
            return DateTime.Now - at < TimeSpan.FromHours(12);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteMarker(string version, string result)
    {
        try
        {
            Json.Save(MarkerFile, new Marker
            {
                At = DateTime.Now.ToString("o"),
                Version = version,
                Result = result
            });
        }
        catch
        {
            // never fail a repair over bookkeeping
        }
    }

    /// <summary>
    /// Checks the files on disk against <c>tools.manifest</c>.
    ///
    /// <para><b>Why the manifest hash alone is not enough.</b> The updater
    /// compares the hash of the manifest file, which proves the manifest is
    /// intact - not that the 953 files it lists still exist. Installers get
    /// quarantined: Defender and several AV products remove Python scripts that
    /// create accounts, one file at a time, and the panel then looks complete
    /// while every run dies on a missing import. This walks the list.</para>
    ///
    /// <para>Returns the first few offending paths, or empty when the tree is
    /// intact. Records the verdict for the update path.</para>
    /// </summary>
    public static async Task<string[]> VerifyToolsAsync(CancellationToken ct = default)
    {
        var broken = new List<string>();
        try
        {
            var manifest = Path.Combine(AppPaths.AppRoot, "tools.manifest");
            var toolsDir = Path.Combine(AppPaths.AppRoot, "tools");

            // No manifest at all: an install from before 2.2.8, or from the
            // update zip. Both need the full package, but that is the bundle
            // update's call, not a "broken file" verdict.
            if (!File.Exists(manifest)) return Array.Empty<string>();

            foreach (var line in await File.ReadAllLinesAsync(manifest, ct).ConfigureAwait(false))
            {
                var text = line.Trim();
                if (text.Length <= 65) continue;

                var expected = text.Substring(0, 64);
                var rel = text.Substring(65).Replace('/', Path.DirectorySeparatorChar);
                var full = Path.Combine(toolsDir, rel);

                if (!File.Exists(full))
                {
                    broken.Add(rel);
                }
                else
                {
                    await using var stream = File.OpenRead(full);
                    var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) broken.Add(rel);
                }

                if (broken.Count >= 8) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A check that cannot run must not condemn a healthy install.
            Log.Warn("", "Tool verification skipped: " + ex.Message);
            return Array.Empty<string>();
        }

        _toolsIntact = broken.Count == 0;
        BrokenTools = broken;
        return broken.ToArray();
    }

    /// <summary>
    /// Runs once, quietly, after the window is up. Returns when there is
    /// nothing to do, when the download failed, or after the installer has been
    /// started (the caller then shuts the panel down so the files can be
    /// replaced).
    /// </summary>
    public static async Task RunAsync(CancellationToken ct = default)
    {
        if (_running) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);

            // Wait for the licence the same way the auto-updater does: a slow
            // online revalidation at boot used to make this give up instantly.
            for (var i = 0; i < 15 && !LicenseService.IsActivated; i++)
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            if (!LicenseService.IsActivated) return;

            if (!NeedsRepair)
            {
                // Folders are present - but are the files? A quarantine after a
                // clean install leaves exactly the state customers describe:
                // "the tools are not there" while the panel looks fine.
                await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                var broken = await VerifyToolsAsync(ct).ConfigureAwait(false);
                if (broken.Length == 0) return;
                Log.Warn("", "Tool files are missing or altered: " + string.Join(", ", broken));
            }

            if (AttemptedRecently())
            {
                Log.Warn("", "This copy is missing its tool files and a repair was already tried today.");
                Status?.Invoke("This copy is incomplete", Describe(false));
                return;
            }

            _running = true;

            var setupUrl = await UpdateService.LatestSetupUrlAsync(ct).ConfigureAwait(false);
            if (setupUrl is null)
            {
                Status?.Invoke("This copy is incomplete",
                    Describe(true));
                return;
            }

            var version = Path.GetFileName(new Uri(setupUrl).AbsolutePath); // file name only

            Status?.Invoke("Repairing this install",
                "This copy has no tool files. Downloading the full installer once - the panel will restart by itself.");

            var target = Path.Combine(Path.GetTempPath(), "DeveloperXPanel-Setup.exe");
            var ok = await DownloadAsync(setupUrl, target, ct).ConfigureAwait(false);
            if (!ok)
            {
                WriteMarker(version, "download failed");
                Status?.Invoke("Repair did not finish",
                    "The installer could not be downloaded. Try again later, or get it from the download page.");
                return;
            }

            if (!await VerifyAsync(target, setupUrl + ".sha256", ct).ConfigureAwait(false))
            {
                try { File.Delete(target); } catch { }
                WriteMarker(version, "hash mismatch");
                Status?.Invoke("Repair did not finish",
                    "The downloaded installer failed its hash check and was deleted. Try again, or get it from the download page.");
                return;
            }

            Log.Success("", "Full installer downloaded and verified - starting it to repair this install.");
            WriteMarker(version, "installing");

            var start = MessageBox.Show(
                "This copy of the panel is missing its tool files, which is why every run fails.\n\n" +
                "The full installer has been downloaded and verified. The panel will close, install, and reopen.\n\n" +
                "Install now?",
                Branding.AppName, MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (start != MessageBoxResult.Yes)
            {
                WriteMarker(version, "declined");
                Status?.Invoke("This copy is incomplete",
                    "Run " + target + " when you are ready - it installs the tool files this copy is missing.");
                return;
            }

            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            Application.Current?.Shutdown();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("", "Self-repair failed: " + ex.Message);
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>
    /// Says which file is missing when we know, instead of "no tools folder" -
    /// a customer can then confirm an antivirus took it.
    /// </summary>
    private static string Describe(bool installerMissing)
    {
        var what = BrokenTools.Count > 0
            ? string.Join(", ", BrokenTools.Take(3)) + (BrokenTools.Count > 3 ? ", ..." : "")
            : @"tools\";

        var text = BrokenTools.Count > 0
            ? $"These files are missing or were changed on this PC: {what}. An antivirus often removes tool scripts."
            : $"This copy has no {what} folder.";

        return installerMissing
            ? text + " The installer could not be located - get DeveloperXPanel-Setup.exe from the download page."
            : text + " Reinstalling DeveloperXPanel-Setup.exe restores them.";
    }

    private static async Task<bool> DownloadAsync(string url, string target, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(target);

            var buffer = new byte[81920];
            long done = 0;
            int read;
            var lastReport = 0;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;

                var percent = total > 0 ? (int)(done * 100 / total) : 0;
                if (percent >= lastReport + 5)
                {
                    lastReport = percent;
                    Status?.Invoke("Repairing this install",
                        $"Downloading the full installer — {percent}% of {total / 1048576} MB");
                }
            }
            return done > 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn("", "Installer download failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The release publishes the digest as "sha256:&lt;hex&gt;"; the prefix is
    /// stripped before the 64-character token is looked for.
    /// </summary>
    private static async Task<bool> VerifyAsync(string file, string hashUrl, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var text = await http.GetStringAsync(hashUrl, ct).ConfigureAwait(false);
            var expected = text
                .Replace("sha256:", "", StringComparison.OrdinalIgnoreCase)
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(x => x.Length == 64 && x.All(Uri.IsHexDigit));
            if (string.IsNullOrWhiteSpace(expected)) return false;

            await using var stream = File.OpenRead(file);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            return actual.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn("", "Installer verification failed: " + ex.Message);
            return false;
        }
    }
}
