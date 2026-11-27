using System;
using System.Threading.Tasks;
using Colony.Core;
using Colony.Core.Net;
using Colony.Core.Procedural;
using Godot;

namespace ColonyGame;

/// <summary>
/// Making a new world: New colony (and Host a new world) generate it on a background task while the main menu shows the
/// loading screen (<see cref="WorldGenProgress"/>: each step, a map of the world so far), then the scene reloads onto it.
/// The world options (<see cref="WorldOptions"/>) come from the menu, or --oceans= --relief= --climate= --rainfall=
/// --rivers= for the first world.
/// </summary>
public partial class Main
{
    private static WorldShape s_shape = WorldShape.Default;
    private static Task<ColonySim>? s_generating;
    private static WorldGenProgress? s_genProgress;
    private static GameSettings? s_genSettings;

    /// <summary>The world options new worlds are made with (kept for the session).</summary>
    public static WorldShape WorldOptions
    {
        get => s_shape;
        set => s_shape = value;
    }

    /// <summary>A new world is being made (the menu shows its progress).</summary>
    public bool Generating => s_generating != null;
    public WorldGenProgress? GenProgress => s_genProgress;
    public GameSettings? GenSettings => s_genSettings;

    /// <summary>Starts a new colony: a fresh world from this seed, size and the world options, made in the background.</summary>
    public void NewColony(int seed, int size, int colonies = 1)
    {
        if (s_generating != null || s_loading != null) return;
        if (Online) LeaveOnline();
        var settings = new GameSettings(seed, size, size, WorldHeight, StartingColonists, colonies) { Shape = s_shape };
        var progress = new WorldGenProgress();
        s_genSettings = settings;
        s_genProgress = progress;
        s_generating = Task.Run(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var sim = settings.Create(settleWaterInBackground: true, progress);
            GD.Print($"World {size}x{size}x{settings.Height} ({settings.Shape.Describe()}) built in {clock.ElapsedMilliseconds} ms: {sim.StartupReport}");
            return sim;
        });
    }

    private static bool s_genShotDone;
    private int _genShotWait;

    /// <summary>
    /// Dev check of the loading screen: --gen-shot=PATH[,SIZE[,SHARE]] makes a new world (SIZE², default 1024) and saves
    /// the screen once SHARE (default 0.5) of it is done, then quits.
    /// </summary>
    private void GenShot()
    {
        if (s_genShotDone || Arg("--gen-shot") is not { Length: > 0 } shot || _frames < 5) return;
        var parts = shot.Split(',');
        int size = parts.Length > 1 && int.TryParse(parts[1], out int sz) ? sz : 1024;
        float share = parts.Length > 2 && float.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0.5f;
        if (s_generating == null && s_genProgress == null)
        {
            NewColony(Seed + 1, size, 1);
            _ui.ShowMenu(true);
            return;
        }
        if (s_genProgress is not { } p || p.Fraction < share || ++_genShotWait < 4) return;
        GetViewport().GetTexture().GetImage().SavePng(parts[0]);
        GD.Print($"Saved the loading screen ({p.Title}, {p.Fraction:0.00}) to {parts[0]}");
        s_genShotDone = true;
        GetTree().Quit();
    }

    /// <summary>Every frame: opens a finished new world.</summary>
    private void GenerateFrame()
    {
        GenShot();
        if (s_generating is not { IsCompleted: true } done) return;
        s_generating = null;
        if (done.IsFaulted)
        {
            s_genProgress = null;
            UI.GlyphStatus.Say($"Couldn't make the world: {done.Exception?.InnerException?.Message}", 8);
            GD.PrintErr(done.Exception);
            return;
        }
        s_settings = s_genSettings;
        s_replayable = done.Result;
        s_player = 0;
        s_launch = done.Result;
        s_home = null;
        s_launchResult = null;
        s_devArgsDone = true; // the demos and dev flags were for the first world
        GetTree().ReloadCurrentScene();
    }

    /// <summary>World options from the command line (--oceans=islands --climate=warm ...), for the first world.</summary>
    private static void ReadWorldArgs()
    {
        T? Pick<T>(string name) where T : struct, Enum =>
            Arg(name) is { } v && Enum.TryParse<T>(v, true, out var e) ? e : null;
        if (Pick<Oceans>("--oceans") is { } o) s_shape = s_shape with { Oceans = o };
        if (Pick<Relief>("--relief") is { } r) s_shape = s_shape with { Relief = r };
        if (Pick<Climate>("--climate") is { } c) s_shape = s_shape with { Climate = c };
        if (Pick<Rainfall>("--rainfall") is { } f) s_shape = s_shape with { Rainfall = f };
        if (Pick<Rivers>("--rivers") is { } v) s_shape = s_shape with { Rivers = v };
        if (Arg("--classic-terrain") != null) s_shape = WorldShape.Legacy;
    }
}
