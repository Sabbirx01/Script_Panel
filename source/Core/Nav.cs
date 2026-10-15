using System.Windows.Controls;

namespace AISuitePanel;

/// <summary>
/// Lightweight view-switching bridge. <see cref="MainWindow"/> registers the
/// renderer; views just ask for a destination.
/// </summary>
public static class Nav
{
    internal static Action<UserControl>? Renderer;

    public static void Go(UserControl view) => Renderer?.Invoke(view);

    public static void GoActivation() => Go(new Views.ActivationView());

    public static void GoDashboard() => Go(new Views.DashboardView());

    /// <summary>Screen 3 - global mail settings.</summary>
    public static void GoSettings() => Go(new Views.GlobalSettingsView());

    public static void GoTool(Models.ToolInfo tool)
    {
        if (!ToolLicenseService.IsLicensed(tool.Id))
        {
            Go(new Views.ToolActivationView(tool));
            return;
        }

        // Accio keeps its own dedicated view (it has Accio.exe-specific UI
        // like the alice.json pool + .env proxy setup). The Python-driven
        // tools (Z AI, Devin AI, future) share the parameterised
        // PythonToolPanelView so the user gets the same flow on every page.
        if (string.Equals(tool.Id, "accio", System.StringComparison.OrdinalIgnoreCase))
            Go(new Views.AccioPanelView(tool));
        else if (string.Equals(tool.Id, "zai", System.StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(tool.Id, "devin", System.StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(tool.Id, "tool4", System.StringComparison.OrdinalIgnoreCase))
            Go(new Views.PythonToolPanelView(tool));
        else
            Go(new Views.ToolPanelView(tool));
    }
}
