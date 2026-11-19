using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Colony.Core;
using Colony.Core.Net;
using Godot;

namespace ColonyGame;

/// <summary>
/// Saving and loading (<see cref="SaveGame"/>: settings + command log + tick, in user://saves). Loading rebuilds the
/// world on a background task (generation plus a replay at sim speed) while the main menu shows progress, then opens
/// it. F5 quicksaves, F9 loads the newest save, and the game autosaves at the start of every game day.
/// </summary>
public partial class Main
{
    public const string SaveDir = "user://saves";
    private static Task<(ColonySim Sim, SaveGame Save)>? s_loading;
    private static float s_loadProgress;
    private static string? s_loadName;
    private long _lastAutosaveDay = -1;

    public bool Loading => s_loading != null;
    public float LoadProgress => s_loadProgress;
    public string? LoadingName => s_loadName;

    private static string SavePath(string file) => Path.Combine(ProjectSettings.GlobalizePath(SaveDir), file);

    /// <summary>Saves this game under a name (a file per name; the same name overwrites). Null on success, else why not.</summary>
    public string? SaveAs(string name)
    {
        if (!Shareable) return "This world was changed by a demo or a test, so it can't be rebuilt from a save: start a new one";
        name = string.IsNullOrWhiteSpace(name) ? $"{_sim.Factions[Player].Name}, day {_sim.Tick / Colony.Core.Agents.Needs.Day + 1}" : name.Trim();
        try
        {
            Directory.CreateDirectory(ProjectSettings.GlobalizePath(SaveDir));
            string file = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_')) + ".colony";
            _sim.Commands.Flush();
            using (var stream = File.Create(SavePath(file)))
                SaveGame.Of(_sim, s_settings!, name, Player).Write(stream);
            return null;
        }
        catch (Exception e)
        {
            return $"Couldn't save: {e.Message}";
        }
    }

    /// <summary>Every save, newest first, with its header (name, tick, settings, when).</summary>
    public List<(string File, SaveGame Header)> ListSaves()
    {
        var list = new List<(string, SaveGame)>();
        var dir = ProjectSettings.GlobalizePath(SaveDir);
        if (!Directory.Exists(dir)) return list;
        foreach (var path in Directory.GetFiles(dir, "*.colony"))
        {
            try
            {
                using var stream = File.OpenRead(path);
                list.Add((path, SaveGame.Read(stream, headerOnly: true)));
            }
            catch (Exception)
            {
                // Not a save we can read (an older version, a partial write): skip it.
            }
        }
        return list.OrderByDescending(s => s.Item2.SavedAt).ToList();
    }

    public void DeleteSave(string file)
    {
        try { File.Delete(file); } catch (Exception) { }
    }

    /// <summary>Starts rebuilding a save in the background; the scene switches to it when done (see <see cref="SaveFrame"/>).</summary>
    public string? LoadSave(string file)
    {
        if (s_loading != null) return "Already loading";
        SaveGame save;
        try
        {
            using var stream = File.OpenRead(file);
            save = SaveGame.Read(stream);
        }
        catch (Exception e)
        {
            return $"Couldn't read the save: {e.Message}";
        }
        if (Online) LeaveOnline();
        s_loadName = save.Name;
        s_loadProgress = 0;
        s_loading = Task.Run(() => (save.Load(p => s_loadProgress = p), save));
        return null;
    }

    /// <summary>Every frame: open a finished load, quick keys' work, the daily autosave.</summary>
    private void SaveFrame()
    {
        if (s_loading is { IsCompleted: true } done)
        {
            s_loading = null;
            if (done.IsFaulted)
            {
                UI.GlyphStatus.Say($"Couldn't load {s_loadName}: {done.Exception?.InnerException?.Message}", 8);
                return;
            }
            var (sim, save) = done.Result;
            if (s_justLoaded) // --load-newest check: the world exactly as loaded, before it runs a tick
            {
                GD.Print($"[save] loaded tick {sim.Tick} hash {StateHash.Compute(sim):x16}");
                GetTree().Quit();
                return;
            }
            s_settings = save.Settings;
            s_replayable = sim;
            s_player = Math.Clamp(save.Player, 0, sim.Factions.Count - 1);
            s_launch = sim;
            s_home = null;
            s_launchResult = null;
            s_devArgsDone = true;
            GetTree().ReloadCurrentScene();
            return;
        }
        // Autosave as each new day begins (a few kilobytes: no hitch), only for worlds that can be rebuilt.
        long day = _sim.Tick / Colony.Core.Agents.Needs.Day;
        if (day > 0 && day != _lastAutosaveDay && Shareable && _sim.Tick % Colony.Core.Agents.Needs.Day < 40)
        {
            if (_lastAutosaveDay >= 0) SaveAs("Autosave");
            _lastAutosaveDay = day;
        }
        else if (_lastAutosaveDay < 0) _lastAutosaveDay = day;
    }

    private static bool s_justLoaded, s_loadArgDone;

    /// <summary>
    /// Dev check: --save-at=TICKS saves as "Save check" when the sim gets there, prints its hash and quits;
    /// --load-newest loads the newest save, prints the loaded world's hash and quits. The two hashes must match.
    /// </summary>
    private void SaveDevArgs()
    {
        if (long.TryParse(Arg("--save-at"), out long at) && _sim.Tick >= at && !s_justLoaded)
        {
            var problem = SaveAs("Save check");
            GD.Print(problem ?? $"[save] saved tick {_sim.Tick} hash {StateHash.Compute(_sim):x16}");
            GetTree().Quit();
        }
        if (Arg("--load-newest") != null && !s_loadArgDone && _frames > 2)
        {
            s_loadArgDone = true;
            var newest = ListSaves().FirstOrDefault();
            if (newest.File == null || LoadSave(newest.File) is { } problem) GD.PrintErr($"[save] nothing to load");
            else s_justLoaded = true;
        }
    }

    private void QuickSave()
    {
        var problem = SaveAs("Quicksave");
        UI.GlyphStatus.Say(problem ?? "Saved (F9 loads the newest save)", problem == null ? 3 : 6);
    }

    private void QuickLoad()
    {
        var newest = ListSaves().FirstOrDefault();
        if (newest.File == null)
        {
            UI.GlyphStatus.Say("No saves yet (F5 saves)");
            return;
        }
        var problem = LoadSave(newest.File);
        UI.GlyphStatus.Say(problem ?? $"Loading {newest.Header.Name}…");
        if (problem == null) _ui.ShowMenu(true, "loading");
    }
}
