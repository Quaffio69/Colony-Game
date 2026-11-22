using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ColonyLauncher;

/// <summary>A downloadable file of a release.</summary>
public sealed record Asset(string Name, string Url, long Size);

/// <summary>The newest published version: what it is, its notes, and the files the launcher needs.</summary>
public sealed record Release(Version Version, string Tag, string Title, string Notes, DateTime? Published, string Page,
    Asset? Game, Asset? Checksum, Asset? Launcher);

/// <summary>Thrown when there is nothing published yet (a repo with no releases).</summary>
public sealed class NoReleaseException(string message) : Exception(message);

/// <summary>Where releases come from: GitHub, or a local folder (testing the launcher without publishing).</summary>
public interface IFeed
{
    string Describe { get; }
    Task<Release> LatestAsync(CancellationToken ct);
    Task DownloadAsync(Asset asset, string path, IProgress<(long Done, long Total)>? progress, CancellationToken ct);
}

[DataContract]
internal sealed class ReleaseJson
{
    [DataMember(Name = "tag_name")] public string? Tag { get; set; }
    [DataMember(Name = "name")] public string? Name { get; set; }
    [DataMember(Name = "body")] public string? Body { get; set; }
    [DataMember(Name = "published_at")] public string? Published { get; set; }
    [DataMember(Name = "html_url")] public string? Page { get; set; }
    [DataMember(Name = "assets")] public AssetJson[]? Assets { get; set; }
}

[DataContract]
internal sealed class AssetJson
{
    [DataMember(Name = "name")] public string? Name { get; set; }
    [DataMember(Name = "browser_download_url")] public string? Url { get; set; }
    [DataMember(Name = "size")] public long Size { get; set; }
}

internal static class Json
{
    public static T Read<T>(string text)
    {
        var s = new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return (T)s.ReadObject(ms)!;
    }

    public static string Write<T>(T value)
    {
        var s = new DataContractJsonSerializer(typeof(T));
        using var ms = new MemoryStream();
        s.WriteObject(ms, value);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>The release a GitHub API (or local latest.json) answer describes; asset names follow the release workflow.</summary>
    public static Release ToRelease(ReleaseJson r, Func<string, string>? resolve = null)
    {
        var tag = r.Tag ?? throw new InvalidDataException("release has no tag");
        var assets = (r.Assets ?? []).Where(a => a.Name != null && a.Url != null)
            .Select(a => new Asset(a.Name!, resolve?.Invoke(a.Url!) ?? a.Url!, a.Size)).ToList();
        var game = assets.FirstOrDefault(a => a.Name.EndsWith("-windows.zip", StringComparison.OrdinalIgnoreCase));
        var sum = game is null ? null : assets.FirstOrDefault(a => a.Name.Equals(game.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
        var launcher = assets.FirstOrDefault(a => a.Name.Equals("ColonyLauncher.exe", StringComparison.OrdinalIgnoreCase));
        DateTime? published = DateTime.TryParse(r.Published, null, System.Globalization.DateTimeStyles.RoundtripKind, out var when) ? when : null;
        return new Release(Versions.Parse(tag), tag, string.IsNullOrWhiteSpace(r.Name) ? tag : r.Name!.Trim(), r.Body ?? "", published, r.Page ?? "",
            game, sum, launcher);
    }
}

internal static class Versions
{
    /// <summary>"v0.3.0", "0.3", "0.3.0-beta" → 0.3.0 (pre-release suffixes are ignored: /releases/latest skips pre-releases anyway).</summary>
    public static Version Parse(string tag)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s.Substring(0, cut);
        if (!Version.TryParse(s.Contains(".") ? s : s + ".0", out var v)) return new Version(0, 0, 0);
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public static string Show(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
}

/// <summary>Releases on GitHub: the newest non-draft, non-prerelease release and its assets. No account needed.</summary>
public sealed class GitHubFeed : IFeed
{
    private readonly string _repo;
    private static readonly HttpClient Http = MakeClient();

    public GitHubFeed(string repo) => _repo = repo;
    public string Describe => $"github.com/{_repo}";
    public string ReleasesPage => $"https://github.com/{_repo}/releases";

    private static HttpClient MakeClient()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // TLS 1.2 and 1.3
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"ColonyLauncher/{Versions.Show(App.OwnVersion)}"); // GitHub requires one
        return c;
    }

    public async Task<Release> LatestAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound) throw new NoReleaseException("No version has been published yet");
        if ((int)res.StatusCode == 403) throw new IOException("GitHub is limiting update checks from this network for now: try again in a while");
        res.EnsureSuccessStatusCode();
        return Json.ToRelease(Json.Read<ReleaseJson>(await res.Content.ReadAsStringAsync().ConfigureAwait(false)));
    }

    public async Task DownloadAsync(Asset asset, string path, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        using var res = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        long total = res.Content.Headers.ContentLength ?? asset.Size;
        using var src = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await Copy(src, path, total, progress, ct).ConfigureAwait(false);
    }

    internal static async Task Copy(Stream src, string path, long total, IProgress<(long, long)>? progress, CancellationToken ct)
    {
        using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 16];
        long done = 0, reported = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
            done += n;
            if (done - reported >= 1 << 18 || done == total) { progress?.Report((done, total)); reported = done; }
        }
        progress?.Report((done, Math.Max(total, done)));
    }
}

/// <summary>A folder holding latest.json (a GitHub release's JSON; asset urls are file names) and the files: for testing.</summary>
public sealed class FolderFeed(string folder) : IFeed
{
    public string Describe => folder;

    public Task<Release> LatestAsync(CancellationToken ct)
    {
        var file = Path.Combine(folder, "latest.json");
        if (!File.Exists(file)) throw new NoReleaseException("No version has been published yet");
        return Task.FromResult(Json.ToRelease(Json.Read<ReleaseJson>(File.ReadAllText(file)), url => Path.Combine(folder, url)));
    }

    public async Task DownloadAsync(Asset asset, string path, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        using var src = new FileStream(asset.Url, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        await GitHubFeed.Copy(src, path, src.Length, progress, ct).ConfigureAwait(false);
    }
}

/// <summary>What is installed where (per user, no administrator rights): %LOCALAPPDATA%\ColonyGame.</summary>
[DataContract]
public sealed class InstallState
{
    [DataMember(Name = "installed")] public string? Installed { get; set; }
    [DataMember(Name = "checked")] public string? LastChecked { get; set; }
}

public sealed class Installation
{
    public string Root { get; }
    public string GameRoot => Path.Combine(Root, "game");
    public string Downloads => Path.Combine(Root, "downloads");
    public string LauncherDir => Path.Combine(Root, "launcher");
    public string LauncherExe => Path.Combine(LauncherDir, "ColonyLauncher.exe");
    private string StateFile => Path.Combine(Root, "state.json");
    public InstallState State { get; private set; } = new();

    public Installation(string root)
    {
        Root = root;
        Load();
    }

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ColonyGame");
    /// <summary>Where the game keeps saves and settings (Godot's user:// for the project "Colony Game").</summary>
    public static string SavesDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Godot", "app_userdata", "Colony Game");

    public Version? Installed => State.Installed is { } v && GameExe(v) is not null ? Versions.Parse(v) : null;
    public string? InstalledExe => State.Installed is { } v ? GameExe(v) : null;

    private string? GameExe(string version)
    {
        var exe = Path.Combine(GameRoot, version, "ColonyGame.exe");
        return File.Exists(exe) ? exe : null;
    }

    private void Load()
    {
        try { if (File.Exists(StateFile)) State = Json.Read<InstallState>(File.ReadAllText(StateFile)); }
        catch { State = new InstallState(); } // a damaged state file: treat as not installed (a reinstall repairs it)
    }

    public void Save()
    {
        Directory.CreateDirectory(Root);
        var tmp = StateFile + ".tmp";
        File.WriteAllText(tmp, Json.Write(State));
        if (File.Exists(StateFile)) File.Delete(StateFile);
        File.Move(tmp, StateFile);
    }

    public enum Step { Downloading, Verifying, Unpacking, Finishing }

    /// <summary>
    /// Downloads a release's game zip, checks it against its published SHA-256, unpacks it beside the installed version
    /// and only then switches to it (a failed or cancelled update leaves the old version playable). Keeps the previous
    /// version, removes older ones.
    /// </summary>
    public async Task InstallAsync(IFeed feed, Release release, IProgress<(Step Step, long Done, long Total)> progress, CancellationToken ct)
    {
        var game = release.Game ?? throw new InvalidDataException($"{release.Tag} has no Windows build attached");
        Directory.CreateDirectory(Downloads);
        var zip = Path.Combine(Downloads, game.Name + ".part");
        await feed.DownloadAsync(game, zip, new Progress<(long Done, long Total)>(p => progress.Report((Step.Downloading, p.Done, p.Total))), ct).ConfigureAwait(false);

        if (release.Checksum is { } sum)
        {
            progress.Report((Step.Verifying, 0, 1));
            var sumFile = Path.Combine(Downloads, sum.Name);
            await feed.DownloadAsync(sum, sumFile, null, ct).ConfigureAwait(false);
            string expected = File.ReadAllText(sumFile).Trim().Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            File.Delete(sumFile);
            string actual = await Task.Run(() => Sha256(zip), ct).ConfigureAwait(false);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(zip);
                throw new InvalidDataException("The download was damaged (its checksum doesn't match): try again");
            }
        }

        string version = Versions.Show(release.Version);
        string target = Path.Combine(GameRoot, version), staging = target + ".tmp";
        await Task.Run(() =>
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            using (var archive = ZipFile.OpenRead(zip))
            {
                long total = archive.Entries.Sum(e => e.Length), done = 0;
                foreach (var e in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var dest = Path.GetFullPath(Path.Combine(staging, e.FullName));
                    if (!dest.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The download holds a file outside its folder"); // zip slip
                    if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\")) { Directory.CreateDirectory(dest); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest, true);
                    done += e.Length;
                    progress.Report((Step.Unpacking, done, total));
                }
            }
            // A zip of a folder (rather than its contents) is fine too.
            var dirs = Directory.GetDirectories(staging);
            if (!File.Exists(Path.Combine(staging, "ColonyGame.exe")) && dirs.Length == 1 && File.Exists(Path.Combine(dirs[0], "ColonyGame.exe")))
                staging = dirs[0];
            if (!File.Exists(Path.Combine(staging, "ColonyGame.exe"))) throw new InvalidDataException("The download doesn't contain ColonyGame.exe");
            progress.Report((Step.Finishing, 1, 1));
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(staging, target);
            if (Directory.Exists(target + ".tmp")) Directory.Delete(target + ".tmp", true);
        }, ct).ConfigureAwait(false);
        File.Delete(zip);

        string? previous = State.Installed;
        State.Installed = version;
        Save();
        // Keep this version and the one before it; older ones go.
        foreach (var dir in Directory.GetDirectories(GameRoot))
        {
            var name = Path.GetFileName(dir);
            if (name == version || name == previous) continue;
            try { Directory.Delete(dir, true); } catch { /* in use: next time */ }
        }
    }

    public static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var f = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>Whether the installed game is running (updating would fail on its open files).</summary>
    public bool GameRunning()
    {
        foreach (var p in Process.GetProcessesByName("ColonyGame"))
        {
            try { if (p.MainModule?.FileName?.StartsWith(GameRoot, StringComparison.OrdinalIgnoreCase) == true) return true; }
            catch { return true; } // can't look inside it: assume it's ours
            finally { p.Dispose(); }
        }
        return false;
    }

    public Process? Play()
    {
        var exe = InstalledExe ?? throw new FileNotFoundException("The game isn't installed");
        return Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false });
    }

    public long InstalledBytes()
    {
        try { return Directory.Exists(GameRoot) ? new DirectoryInfo(GameRoot).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0; }
        catch { return 0; }
    }

    public IEnumerable<string> Leftovers() => Directory.Exists(Downloads) ? Directory.GetFiles(Downloads) : [];
}
