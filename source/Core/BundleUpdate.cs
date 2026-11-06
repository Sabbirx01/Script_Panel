using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AISuitePanel;

/// <summary>
/// Keeps a working install in step with the whole published package - tools and
/// runtime included - not just the panel exe.
///
/// <para><b>Why the exe swap is not enough.</b> The updater replaces
/// <c>DeveloperXBulkAccPanel.exe</c> and nothing else, so every change to a tool
/// script (a selectors fix, a new flow) stayed on the machine that built it. The
/// full package only exists inside the encrypted setup, so a full update is: read
/// the fingerprint of the tools that setup carries, compare it with ours, and if
/// it differs fetch the setup and hand over to it.</para>
///
/// <para><b>Why a fingerprint and not a version.</b> Every release publishes a
/// setup, but most releases only touch the panel. Comparing
/// <c>tools.manifest</c> - a sorted list of relative path + SHA-256 written into
/// the bundle at build time - means the 596 MB download happens exactly when the
/// tool files actually changed, and never otherwise. The panel hashes one small
/// manifest file, so the check costs nothing at startup.</para>
///
/// <para><b>Why it waits for a restart.</b> Installing closes the panel. Doing
/// that in the middle of a customer's batch is unacceptable, so the package is
/// downloaded and verified in the background and applied on the next start.</para>
/// </summary>
internal static class BundleUpdate
{
    /// <summary>Title + detail for the dashboard banner.</summary>
    public static event Action<string, string>? Status;

    private static bool _running;

    private static string ManifestPath => Path.Combine(AppPaths.AppRoot, "tools.manifest");
    private static string MarkerFile => Path.Combine(AppPaths.Root, "pending-bundle.json");
    private static string StagedSetup => Path.Combine(AppPaths.Root, "pending-setup.exe");

    private sealed class Pending
    {
        public string Version { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string At { get; set; } = "";
    }

    /// <summary>
    /// SHA-256 of the tools manifest this install was built from. Empty means the
    /// install predates the manifest, which is exactly the case that must be
    /// brought up to date once.
    /// </summary>
    public static string LocalToolsHash()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return "";
            using var stream = File.OpenRead(ManifestPath);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Compares this install with the published bundle and, when they differ,
    /// downloads and verifies the setup for the next start.
    /// </summary>
    public static async Task CheckAsync(CancellationToken ct = default)
    {
        if (_running) return;
        _running = true;
        try
        {
            // Let the window settle and the licence finish revalidating first:
            // a 596 MB download competing with the first paint is the sort of
            // thing that makes a panel feel broken.
            await Task.Delay(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
            for (var i = 0; i < 30 && !LicenseService.IsActivated; i++)
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            if (!LicenseService.IsActivated) return;

            // An incomplete copy is SelfRepair's job - it has nothing to compare.
            if (SelfRepair.NeedsRepair) return;

            var check = await UpdateService.CheckAsync(ct).ConfigureAwait(false);
            if (!check.Ok || string.IsNullOrWhiteSpace(check.BundleTools)) return;

            var local = LocalToolsHash();
            if (string.Equals(local, check.BundleTools, StringComparison.OrdinalIgnoreCase))
            {
                // Up to date: drop anything left over from an earlier attempt.
                Cleanup();
                return;
            }

            var setupUrl = string.IsNullOrWhiteSpace(check.SetupUrl)
                ? await UpdateService.LatestSetupUrlAsync(ct).ConfigureAwait(false)
                : check.SetupUrl;
            if (string.IsNullOrWhiteSpace(setupUrl)) return;

            if (AlreadyStaged(check.BundleTools)) return;

            Log.Info("", $"Tools changed (published build {check.AvailableVersion}) - fetching the full package.");
            Status?.Invoke($"Update {check.AvailableVersion} is downloading",
                "The tool files changed in this release. It installs itself when you restart the panel.");

            var temp = StagedSetup + ".part";
            if (!await DownloadAsync(setupUrl, temp, ct).ConfigureAwait(false))
            {
                Status?.Invoke("Update download failed",
                    "The package could not be downloaded. The panel will try again on the next start.");
                return;
            }

            if (!await VerifyAsync(temp, check.SetupSha256, setupUrl + ".sha256", ct).ConfigureAwait(false))
            {
                try { File.Delete(temp); } catch { }
                Log.Warn("", "Downloaded package failed its hash check - deleted.");
                Status?.Invoke("Update download failed",
                    "The package did not match its published hash and was deleted. Check your connection and restart.");
                return;
            }

            try
            {
                if (File.Exists(StagedSetup)) File.Delete(StagedSetup);
                File.Move(temp, StagedSetup);
            }
            catch (Exception ex)
            {
                Log.Warn("", "Could not stage the package: " + ex.Message);
                return;
            }

            Json.Save(MarkerFile, new Pending
            {
                Version = check.AvailableVersion,
                Sha256 = check.BundleTools,
                At = DateTime.Now.ToString("o")
            });

            Log.Success("", $"Update {check.AvailableVersion} staged - applies on the next start.");
            Status?.Invoke($"Update {check.AvailableVersion} is ready",
                "Close and reopen the panel to install it. Nothing in this session will be interrupted.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("", "Bundle update check failed: " + ex.Message);
        }
        finally
        {
            _running = false;
        }
    }

    private static bool AlreadyStaged(string bundleTools)
    {
        try
        {
            if (!File.Exists(MarkerFile) || !File.Exists(StagedSetup)) return false;
            var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(MarkerFile));
            return pending is not null
                   && string.Equals(pending.Sha256, bundleTools, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs before the window exists: applies a staged package by starting the
    /// setup, which closes the panel, unpacks over it and starts it again.
    /// Returns true when the caller must exit.
    /// </summary>
    public static bool ApplyPending()
    {
        try
        {
            if (!File.Exists(MarkerFile) || !File.Exists(StagedSetup)) return false;

            var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(MarkerFile));
            if (pending is null) { Cleanup(); return false; }

            // The marker is removed first: the installer preserves data\, so a
            // marker left behind would start the setup again on every launch.
            var version = pending.Version;
            try { File.Delete(MarkerFile); } catch { }

            Log.Info("", $"Applying staged update {version} before the window opens.");
            Process.Start(new ProcessStartInfo(StagedSetup) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("", "Could not apply the staged update: " + ex.Message);
            Cleanup();
            return false;
        }
    }

    private static void Cleanup()
    {
        try
        {
            if (File.Exists(MarkerFile)) File.Delete(MarkerFile);
            if (File.Exists(StagedSetup)) File.Delete(StagedSetup);
            var part = StagedSetup + ".part";
            if (File.Exists(part)) File.Delete(part);
        }
        catch
        {
            // a leftover file is harmless
        }
    }

    private static async Task<bool> DownloadAsync(string url, string target, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(45) };
            using var response = await http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(target);

            var buffer = new byte[81920];
            long done = 0;
            int read;
            var last = 0;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                var percent = total > 0 ? (int)(done * 100 / total) : 0;
                if (percent >= last + 10)
                {
                    last = percent;
                    Status?.Invoke("Update is downloading",
                        $"Fetching the full package — {percent}% of {total / 1048576} MB. You can keep working.");
                }
            }
            return done > 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn("", "Package download failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Prefers the digest published in the feed and falls back to the release's
    /// .sha256 asset, which is served as "sha256:&lt;hex&gt;".
    /// </summary>
    private static async Task<bool> VerifyAsync(string file, string expected, string hashUrl, CancellationToken ct)
    {
        try
        {
            var want = (expected ?? "").Trim();
            if (want.Length != 64)
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                var text = await http.GetStringAsync(hashUrl, ct).ConfigureAwait(false);
                want = text.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase)
                    .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(x => x.Length == 64 && x.All(Uri.IsHexDigit)) ?? "";
            }
            if (want.Length != 64) return false;

            await using var stream = File.OpenRead(file);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            return actual.Equals(want, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn("", "Package verification failed: " + ex.Message);
            return false;
        }
    }
}
