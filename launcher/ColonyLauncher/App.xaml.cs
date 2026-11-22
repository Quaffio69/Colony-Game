using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace ColonyLauncher;

/// <summary>
/// Entry point. Command line (mostly for development and testing):
/// <list type="bullet">
/// <item><c>-repo=owner/name</c>: the GitHub repository to install from (release builds have it built in; a dev build
/// also reads <c>launcher.repo</c> beside the exe).</item>
/// <item><c>-feed=FOLDER</c>: install from a folder holding latest.json and the files instead (testing).</item>
/// <item><c>-root=FOLDER</c>: install there instead of %LOCALAPPDATA%\ColonyGame, without shortcuts or an Apps &amp;
/// features entry (testing).</item>
/// <item><c>--uninstall</c>: open on the uninstall question (Apps &amp; features runs this).</item>
/// <item><c>-screenshot=FILE.png</c>: render the window once it has settled, save it and quit (with <c>-state=...</c>
/// to show a canned state: install, uptodate, update, download, offline, none, uninstall, licenses).</item>
/// </list>
/// </summary>
public partial class App : Application
{
    public static Version OwnVersion { get; } = Versions.Parse(
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");
    /// <summary>Built by hand (not by the release workflow): never replaces itself or touches Windows integration on its own.</summary>
    public static bool DevBuild => OwnVersion == new Version(0, 0, 0);

    public static string? Arg(string name)
    {
        foreach (var a in Environment.GetCommandLineArgs().Skip(1))
        {
            var s = a.TrimStart('-');
            if (s.Equals(name, StringComparison.OrdinalIgnoreCase)) return "";
            if (s.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return s.Substring(name.Length + 1).Trim('"');
        }
        return null;
    }

    /// <summary>The repository releases come from: the command line, the built-in one, or launcher.repo beside the exe.</summary>
    public static string? Repo()
    {
        if (Arg("repo") is { Length: > 0 } r) return r;
        var built = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "GitHubRepo")?.Value;
        if (!string.IsNullOrWhiteSpace(built)) return built;
        try
        {
            var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.repo");
            if (File.Exists(file)) return File.ReadAllText(file).Trim();
        }
        catch { /* none */ }
        return null;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.Message, "Colony Game launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        new MainWindow().Show();
    }
}
