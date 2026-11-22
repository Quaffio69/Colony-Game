using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ColonyLauncher;

/// <summary>
/// The launcher window. On opening it asks for the newest release; then the main button is Install, Update or Play.
/// Updates download beside the installed version and switch over only when complete and verified, so the game stays
/// playable whatever happens (Play the old version while an update is offered). The launcher installs itself on the
/// first install (Start menu, optional desktop shortcut, Apps &amp; features) and replaces itself when a newer one ships.
/// </summary>
public partial class MainWindow : Window
{
    private enum Mode { Checking, Install, UpToDate, Update, Offline, NoRelease, NoRepo, Busy }

    private readonly Installation _inst;
    private readonly IFeed? _feed;
    private readonly bool _integrate; // the real install location: shortcuts and Apps & features too
    private Mode _mode = Mode.Checking;
    private Release? _release;
    private string _problem = "";
    private CancellationTokenSource? _work;
    private double _fraction;
    private Action? _dialogOk;
    private Version? _mockInstalled;

    public MainWindow()
    {
        InitializeComponent();
        var root = App.Arg("root") is { Length: > 0 } r ? Path.GetFullPath(r) : Installation.DefaultRoot;
        _integrate = App.Arg("root") is null;
        _inst = new Installation(root);
        if (App.Arg("feed") is { Length: > 0 } folder) _feed = new FolderFeed(Path.GetFullPath(folder));
        else if (App.Repo() is { Length: > 0 } repo) _feed = new GitHubFeed(repo);
        Shell.CleanOld(_inst);

        LauncherVersion.Text = App.DevBuild ? "launcher (dev build)" : $"launcher v{Versions.Show(App.OwnVersion)}";
        Loaded += OnLoaded;
        // Rounded corners: clip the content to the frame.
        Frame.SizeChanged += (_, _) => Root.Clip = new RectangleGeometry(new Rect(0, 0, Frame.ActualWidth - 2, Frame.ActualHeight - 2), 13, 13);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Map.Build(ActualWidth, ActualHeight);
        if (App.Arg("state") is { Length: > 0 } state) ShowState(state);
        else if (App.Arg("uninstall") != null) { Refresh(); OnUninstall(this, new RoutedEventArgs()); }
        else if (App.Arg("auto-uninstall") != null && !_integrate)
        {
            Shell.Uninstall(_inst, integrate: false); // testing, and only with -root: never the real install
            Application.Current.Shutdown();
            return;
        }
        else await Check();
        // -auto-install (testing): install or update straight away, as if the button were pressed.
        if (App.Arg("auto-install") != null && _mode is Mode.Install or Mode.Update) await Install();
        if (App.Arg("screenshot") is { Length: > 0 } shot)
        {
            // Let the layout, the fonts and a few map frames settle, then render the window to a PNG and quit.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (_, _) => { timer.Stop(); SaveShot(shot); Close(); };
            timer.Start();
        }
    }

    // ---- Checking --------------------------------------------------------------------------------------------

    private async Task Check()
    {
        if (_feed is null)
        {
            _mode = Mode.NoRepo;
            Refresh();
            return;
        }
        _mode = Mode.Checking;
        Refresh();
        try
        {
            _release = await _feed.LatestAsync(CancellationToken.None);
            _inst.State.LastChecked = DateTime.UtcNow.ToString("o");
            if (_inst.Installed != null) TrySave();
            if (await SelfUpdate()) return;
            var installed = Installed;
            _mode = installed is null ? Mode.Install : installed < _release.Version ? Mode.Update : Mode.UpToDate;
        }
        catch (NoReleaseException ex)
        {
            _problem = ex.Message;
            _mode = Mode.NoRelease;
        }
        catch (Exception ex)
        {
            _problem = Friendly(ex);
            _mode = Mode.Offline;
        }
        Refresh();
    }

    private Version? Installed => _mockInstalled ?? _inst.Installed;

    /// <summary>
    /// A newer launcher in the release replaces this one (installed copies only, never a dev build): downloaded beside
    /// it, swapped in, restarted. True when it is restarting.
    /// </summary>
    private async Task<bool> SelfUpdate()
    {
        if (App.DevBuild || _feed is null || _release?.Launcher is not { } asset || _release.Version <= App.OwnVersion || !Shell.RunningInstalled(_inst)) return false;
        try
        {
            _mode = Mode.Busy;
            Busy("Updating the launcher…", $"v{Versions.Show(App.OwnVersion)} → v{Versions.Show(_release.Version)}");
            var tmp = _inst.LauncherExe + ".new";
            await _feed.DownloadAsync(asset, tmp, new Progress<(long Done, long Total)>(p => SetProgress(p.Done, p.Total, "")), CancellationToken.None);
            Shell.ReplaceSelf(_inst, tmp);
            // Restart as the new launcher, with the same options.
            var args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            Process.Start(new ProcessStartInfo(_inst.LauncherExe, args) { UseShellExecute = false });
            Application.Current.Shutdown();
            return true;
        }
        catch
        {
            return false; // the old launcher still works: carry on with it
        }
    }

    // ---- The buttons ------------------------------------------------------------------------------------------

    private async void OnCheck(object sender, RoutedEventArgs e) => await Check();

    private void OnMain(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case Mode.Install:
                AskInstall();
                break;
            case Mode.Update:
                _ = Install();
                break;
            case Mode.UpToDate:
            case Mode.Checking when Installed != null:
            case Mode.Offline when Installed != null:
            case Mode.NoRelease when Installed != null:
            case Mode.NoRepo when Installed != null:
                Play();
                break;
            case Mode.Offline:
            case Mode.NoRelease:
                _ = Check();
                break;
            case Mode.NoRepo:
                Shell.Open("https://github.com/");
                break;
        }
    }

    private void OnSecond(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Busy) _work?.Cancel();
        else if (_mode == Mode.Update) Play();
    }

    private void AskInstall()
    {
        var size = _release?.Game?.Size ?? 0;
        Dialog("☼", "Install Colony Game",
            $"Colony Game v{Versions.Show(_release!.Version)} will be installed for you (no administrator rights needed):\n\n" +
            $"   {_inst.Root}\n\n" +
            $"Download: {Megabytes(size)}. The launcher keeps the game up to date; your saves live in your user folder and " +
            "are kept through updates.",
            "Install", () => _ = Install(desktop: DialogCheck.IsChecked == true),
            check: "Add a desktop shortcut", checkOn: !Shell.HasDesktopShortcut);
    }

    private async Task Install(bool desktop = false)
    {
        if (_release is null || _feed is null) return;
        if (_inst.GameRunning())
        {
            Dialog("‼", "Close the game first", "Colony Game is running. Save and quit it, then update.", "OK", null, cancel: null);
            return;
        }
        var release = _release;
        bool first = Installed is null;
        _work = new CancellationTokenSource();
        _mode = Mode.Busy;
        Busy(first ? $"Installing v{Versions.Show(release.Version)}" : $"Updating to v{Versions.Show(release.Version)}", "Starting the download…");
        var clock = Stopwatch.StartNew();
        long lastBytes = 0;
        double lastTime = 0, speed = 0;
        var progress = new Progress<(Installation.Step Step, long Done, long Total)>(p =>
        {
            switch (p.Step)
            {
                case Installation.Step.Downloading:
                    double t = clock.Elapsed.TotalSeconds;
                    if (t - lastTime >= 0.5)
                    {
                        double now = (p.Done - lastBytes) / Math.Max(0.001, t - lastTime);
                        speed = speed == 0 ? now : speed * 0.6 + now * 0.4;
                        (lastBytes, lastTime) = (p.Done, t);
                    }
                    string eta = speed > 0 && p.Total > p.Done ? $" · {Duration((p.Total - p.Done) / speed)} left" : "";
                    StatusDetail.Text = $"Downloading · {Megabytes(p.Done)} of {Megabytes(p.Total)}" + (speed > 0 ? $" · {speed / 1048576:0.0} MB/s" : "") + eta;
                    SetProgress(p.Done, p.Total, $"{Percent(p.Done, p.Total)}%");
                    break;
                case Installation.Step.Verifying:
                    StatusDetail.Text = "Checking the download is intact…";
                    SetProgress(1, 1, "");
                    break;
                case Installation.Step.Unpacking:
                    StatusDetail.Text = "Unpacking…";
                    SetProgress(p.Done, p.Total, $"{Percent(p.Done, p.Total)}%");
                    break;
                default:
                    StatusDetail.Text = "Finishing…";
                    break;
            }
        });
        try
        {
            await _inst.InstallAsync(_feed, release, progress, _work.Token);
            if (_integrate || first)
            {
                try { Shell.InstallSelf(_inst, _integrate, desktop); }
                catch { /* the game works without shortcuts */ }
            }
            if (_integrate) { try { Shell.Register(_inst); } catch { /* cosmetic */ } }
            _mockInstalled = null;
            _mode = Mode.UpToDate;
        }
        catch (OperationCanceledException)
        {
            _mode = Installed is null ? Mode.Install : Mode.Update;
            _problem = "";
        }
        catch (Exception ex)
        {
            _mode = Installed is null ? Mode.Install : Mode.Update;
            Refresh();
            Dialog("×", first ? "The install didn't finish" : "The update didn't finish",
                Friendly(ex) + (first ? "" : "\n\nThe installed version is untouched: you can still play it."), "Try again", () => _ = Install(desktop), iconBrush: "Bad");
            return;
        }
        finally
        {
            _work?.Dispose();
            _work = null;
        }
        Refresh();
    }

    private void Play()
    {
        try
        {
            _inst.Play();
            WindowState = WindowState.Minimized;
        }
        catch (Exception ex)
        {
            Dialog("×", "Couldn't start the game", Friendly(ex), "OK", null, cancel: null, iconBrush: "Bad");
        }
    }

    // ---- What the window shows ---------------------------------------------------------------------------------

    private void Refresh()
    {
        var installed = Installed;
        string? v = installed is null ? null : Versions.Show(installed);
        string latest = _release is null ? "" : Versions.Show(_release.Version);
        ProgressRow.Visibility = _mode == Mode.Busy ? Visibility.Visible : Visibility.Collapsed;
        CheckButton.IsEnabled = _mode is not (Mode.Busy or Mode.Checking) && _feed != null;
        SecondButton.Visibility = Visibility.Collapsed;
        MainButton.IsEnabled = true;
        FilesLink.IsEnabled = UninstallLink.IsEnabled = installed != null && _mode != Mode.Busy;
        string checkedAt = _inst.State.LastChecked is { } c && DateTime.TryParse(c, null, System.Globalization.DateTimeStyles.RoundtripKind, out var when)
            ? $" · checked {Ago(when)}" : "";

        switch (_mode)
        {
            case Mode.Checking:
                Status("o", "Dim", "Checking for updates…", _feed?.Describe ?? "");
                Main(installed != null ? "PLAY" : "INSTALL", installed != null ? "►" : "↓", enabled: installed != null); // play stays available while checking
                break;
            case Mode.Install:
                Status("↓", "Info", $"Ready to install · v{latest}", $"{Megabytes(_release?.Game?.Size ?? 0)} download · from {_feed?.Describe}");
                Main("INSTALL", "↓");
                break;
            case Mode.UpToDate:
                Status("•", "Good", $"Up to date · v{v}", $"{_feed?.Describe}{checkedAt}");
                Main("PLAY", "►");
                break;
            case Mode.Update:
                Status("▲", "Warn", $"Update available · v{v} → v{latest}", $"{Megabytes(_release?.Game?.Size ?? 0)} download · your saves are kept");
                Main("UPDATE", "↓");
                Second($"Play v{v}");
                break;
            case Mode.Offline:
                Status("×", installed != null ? "Dim" : "Bad", installed != null ? $"Couldn't check for updates · v{v} installed" : "Couldn't reach the update server",
                    _problem);
                Main(installed != null ? "PLAY" : "RETRY", installed != null ? "►" : "↻");
                break;
            case Mode.NoRelease:
                Status("o", "Dim", installed != null ? $"v{v} installed · nothing newer published" : "No version has been published yet",
                    $"{_feed?.Describe} · releases appear here once one is tagged");
                Main(installed != null ? "PLAY" : "RETRY", installed != null ? "►" : "↻");
                break;
            case Mode.NoRepo:
                Status("×", "Bad", "This launcher doesn't know where to find the game",
                    "A build from source: pass -repo=owner/name, or put the repo in launcher.repo beside the exe");
                Main(installed != null ? "PLAY" : "GITHUB", installed != null ? "►" : "►", enabled: installed != null);
                break;
        }
        News();
    }

    private void Busy(string text, string detail)
    {
        Status("▼", "Accent", text, detail);
        ProgressRow.Visibility = Visibility.Visible;
        CheckButton.IsEnabled = false;
        FilesLink.IsEnabled = UninstallLink.IsEnabled = false;
        Main(Installed is null ? "INSTALLING" : "UPDATING", "o", enabled: false);
        Second("Cancel");
        SetProgress(0, 1, "0%");
    }

    private void Status(string glyph, string tone, string text, string detail)
    {
        StatusIcon.Glyph = glyph;
        StatusIcon.Foreground = (Brush)FindResource(tone);
        StatusText.Text = text;
        StatusDetail.Text = detail;
    }

    private void Main(string text, string glyph, bool enabled = true)
    {
        MainText.Text = text;
        MainIcon.Glyph = glyph;
        MainButton.IsEnabled = enabled;
    }

    private void Second(string text)
    {
        SecondText.Text = text;
        SecondButton.Visibility = Visibility.Visible;
    }

    private void SetProgress(long done, long total, string label)
    {
        _fraction = total <= 0 ? 0 : Math.Min(1, done / (double)total);
        Fill.Width = Track.ActualWidth * _fraction;
        ProgressText.Text = label;
    }

    private void OnTrackSized(object sender, SizeChangedEventArgs e) => Fill.Width = Track.ActualWidth * _fraction;

    /// <summary>The newest release's notes (Markdown, shown as plain text: headings, bullets and emphasis tidied).</summary>
    private void News()
    {
        if (_release is null)
        {
            NewsTitle.Text = Installed is { } v ? $"v{Versions.Show(v)}" : "Colony Game";
            NewsDate.Text = "";
            NewsBody.Text = _mode switch
            {
                Mode.Checking => "Looking for the newest version…",
                Mode.Offline => "The release notes will show here once the launcher can reach GitHub.",
                _ => "Release notes appear here when a version is published.",
            };
            NewsLink.Visibility = Visibility.Collapsed;
            return;
        }
        NewsTitle.Text = _release.Title;
        NewsDate.Text = _release.Published is { } d ? $"v{Versions.Show(_release.Version)} · {d.ToLocalTime():d MMMM yyyy}" : $"v{Versions.Show(_release.Version)}";
        NewsBody.Text = PlainNotes(_release.Notes);
        NewsLink.Visibility = string.IsNullOrEmpty(_release.Page) ? Visibility.Collapsed : Visibility.Visible;
    }

    internal static string PlainNotes(string md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "No notes for this version.";
        // GitHub's generated "**Full Changelog**: https://..." line: the "Full release notes" link covers it.
        var kept = md.Replace("\r", "").Split('\n').Where(l => !Regex.IsMatch(l, @"^\W*Full Changelog\W*:", RegexOptions.IgnoreCase));
        var lines = kept.Select(line =>
        {
            var s = line.TrimEnd();
            s = Regex.Replace(s, @"^\s{0,3}#{1,6}\s*", "");                 // headings
            s = Regex.Replace(s, @"^(\s*)[-*+]\s+", "$1·  ");                 // bullets
            s = Regex.Replace(s, @"\*\*(.+?)\*\*|__(.+?)__", "$1$2");          // bold
            s = Regex.Replace(s, @"(?<![\w*])[*_](.+?)[*_](?![\w*])", "$1");  // emphasis
            s = Regex.Replace(s, @"`([^`]+)`", "$1");                          // code
            s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");              // links
            s = Regex.Replace(s, @"https://github\.com/[^\s)]+/pull/(\d+)", "#$1"); // auto-generated notes' PR links
            return s;
        });
        return Regex.Replace(string.Join("\n", lines).Trim(), @"\n{3,}", "\n\n");
    }

    // ---- Links ---------------------------------------------------------------------------------------------------

    private void OnSaves(object sender, RoutedEventArgs e) => Shell.Open(Installation.SavesDir);
    private void OnFiles(object sender, RoutedEventArgs e) => Shell.Open(_inst.GameRoot);
    private void OnReleasePage(object sender, RoutedEventArgs e) { if (_release?.Page is { Length: > 0 } p) Shell.Open(p); }
    private void OnReleases(object sender, RoutedEventArgs e)
    {
        if (_feed is GitHubFeed gh) Shell.Open(gh.ReleasesPage);
        else if (_feed is FolderFeed) Shell.Open(_feed.Describe);
    }

    private void OnLicenses(object sender, RoutedEventArgs e)
    {
        string Read(string name)
        {
            using var s = typeof(App).Assembly.GetManifestResourceStream(name);
            return s is null ? "" : new StreamReader(s).ReadToEnd().Trim();
        }
        Dialog("≡", "Licenses",
            "Colony Game and this launcher are open source under the MIT License:\n\n" + Read("LICENSE") +
            "\n\n────────\n\nThe launcher's typeface is IBM Plex Mono, under the SIL Open Font License 1.1:\n\n" + Read("IBMPlexMono-OFL.txt"),
            "Close", null, cancel: null);
    }

    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (_inst.GameRunning())
        {
            Dialog("‼", "Close the game first", "Colony Game is running. Save and quit it, then uninstall.", "OK", null, cancel: null);
            return;
        }
        Dialog("×", "Uninstall Colony Game?",
            $"This removes the game and the launcher from\n\n   {_inst.Root}\n\nand their shortcuts. Your saved games and settings stay in\n\n   {Installation.SavesDir}\n\n(delete that folder too if you want them gone).",
            "Uninstall", () =>
            {
                try
                {
                    Shell.Uninstall(_inst, _integrate);
                    Application.Current.Shutdown();
                }
                catch (Exception ex)
                {
                    Dialog("×", "Couldn't uninstall everything", Friendly(ex), "OK", null, cancel: null, iconBrush: "Bad");
                }
            }, iconBrush: "Bad");
    }

    // ---- The dialog ------------------------------------------------------------------------------------------------

    private void Dialog(string glyph, string title, string body, string ok, Action? onOk, string? cancel = "Cancel",
        string? check = null, bool checkOn = true, string iconBrush = "Accent")
    {
        DialogIcon.Glyph = glyph;
        DialogIcon.Foreground = (Brush)FindResource(iconBrush);
        DialogTitle.Text = title;
        DialogBody.Text = body;
        DialogOkText.Text = ok;
        DialogCancel.Visibility = cancel is null ? Visibility.Collapsed : Visibility.Visible;
        DialogCancelText.Text = cancel ?? "";
        DialogCheck.Visibility = check is null ? Visibility.Collapsed : Visibility.Visible;
        DialogCheck.Content = check;
        DialogCheck.IsChecked = checkOn;
        _dialogOk = onOk;
        Overlay.Visibility = Visibility.Visible;
    }

    private void OnDialogOk(object sender, RoutedEventArgs e)
    {
        Overlay.Visibility = Visibility.Collapsed;
        var ok = _dialogOk;
        _dialogOk = null;
        ok?.Invoke();
    }

    private void OnDialogCancel(object sender, RoutedEventArgs e)
    {
        Overlay.Visibility = Visibility.Collapsed;
        _dialogOk = null;
    }

    // ---- Window chrome -----------------------------------------------------------------------------------------------

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e)
    {
        _work?.Cancel();
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Overlay.Visibility == Visibility.Visible) OnDialogCancel(this, e);
        base.OnKeyDown(e);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private void TrySave()
    {
        try { _inst.Save(); } catch { /* not important */ }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        System.Net.Http.HttpRequestException => "Couldn't reach GitHub: check your internet connection",
        TaskCanceledException => "The server took too long to answer",
        UnauthorizedAccessException => "Windows refused access to the install folder (is the game or a file in it open?)",
        IOException io when io.Message.Contains("space") => "There isn't enough disk space",
        _ => ex.Message,
    };

    private static string Megabytes(long bytes) => bytes <= 0 ? "? MB" : $"{bytes / 1048576.0:0} MB";
    private static int Percent(long done, long total) => total <= 0 ? 0 : (int)(100 * done / total);
    private static string Duration(double s) => s < 60 ? $"{s:0} s" : $"{s / 60:0} min";

    private static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc.ToUniversalTime();
        return d.TotalMinutes < 1 ? "just now" : d.TotalHours < 1 ? $"{d.TotalMinutes:0} min ago" : d.TotalDays < 1 ? $"{d.TotalHours:0} h ago" : $"{d.TotalDays:0} days ago";
    }

    // ---- Screenshots and canned states (development) -----------------------------------------------------------------

    private void ShowState(string state)
    {
        var notes = "## Trade between colonies\n- Offer, counter and accept deals in the **Trade** window (F6)\n- Traders carry goods to the buyer's trade depot\n\n" +
                    "## Balance\n- Settlers arrive in waves; colonies grow at a steadier pace\n- Farms, digging and crafting take a little longer\n- Ant raids are sized to your colony";
        _release = new Release(new Version(0, 4, 0), "v0.4.0", "Trade and balance", notes, DateTime.UtcNow.AddDays(-1), "https://github.com/",
            new Asset("ColonyGame-0.4.0-windows.zip", "", 118 * 1048576L), null, null);
        _inst.State.LastChecked = DateTime.UtcNow.AddMinutes(-3).ToString("o");
        switch (state)
        {
            case "install" or "installask": _mode = Mode.Install; break;
            case "uninstall" or "licenses": _mockInstalled = new Version(0, 4, 0); _mode = Mode.UpToDate; break;
            case "uptodate": _mockInstalled = new Version(0, 4, 0); _mode = Mode.UpToDate; break;
            case "update": _mockInstalled = new Version(0, 3, 2); _mode = Mode.Update; break;
            case "offline": _release = null; _mockInstalled = new Version(0, 3, 2); _problem = "Couldn't reach GitHub: check your internet connection"; _mode = Mode.Offline; break;
            case "none": _release = null; _problem = ""; _mode = Mode.NoRelease; break;
            case "download":
                _mockInstalled = new Version(0, 3, 2);
                _mode = Mode.Busy;
                Refresh();
                Busy("Updating to v0.4.0", "Downloading · 51 MB of 118 MB · 12.4 MB/s · 5 s left");
                Dispatcher.BeginInvoke(() => SetProgress(51, 118, "43%"), DispatcherPriority.Loaded);
                return;
        }
        Refresh();
        if (state == "uninstall") OnUninstall(this, new RoutedEventArgs());
        if (state == "licenses") OnLicenses(this, new RoutedEventArgs());
        if (state == "installask") AskInstall();
    }

    private void SaveShot(string path)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new RenderTargetBitmap((int)(Frame.ActualWidth * dpi.DpiScaleX), (int)(Frame.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bmp.Render(Frame);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        png.Save(f);
    }
}
