using System.IO;
using Microsoft.Playwright;

namespace AISuitePanel;

/// <summary>
/// Opens an official tool page in an isolated, persistent Playwright profile.
/// This service intentionally does not fill forms, solve CAPTCHA, read mail,
/// extract cookies, or submit account creation/login actions.
/// </summary>
public static class ManualBrowserService
{
    private static readonly object Gate = new();
    private static readonly List<IPlaywright> PlaywrightInstances = new();
    private static readonly List<IBrowserContext> Contexts = new();

    public static async Task<bool> OpenAsync(string toolId, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        AppPaths.Ensure();
        var profile = AppPaths.WorkspaceProfilesDir(toolId);
        Directory.CreateDirectory(profile);

        try
        {
            var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            var context = await playwright.Chromium.LaunchPersistentContextAsync(
                profile,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = false,
                    Channel = "chrome",
                    ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
                    Args = new[]
                    {
                        "--disable-blink-features=AutomationControlled",
                        "--disable-infobars"
                    }
                }).ConfigureAwait(false);

            var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().ConfigureAwait(false);
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            lock (Gate)
            {
                PlaywrightInstances.Add(playwright);
                Contexts.Add(context);
            }

            Log.Info(toolId, "Manual Playwright browser opened with an isolated profile. Complete login/CAPTCHA yourself.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(toolId, "Could not start Playwright manual browser: " + ex.Message);
            return false;
        }
    }
}
