using System.Collections.Generic;
using Colony.Core.World;
using DwarfRender;
using Godot;

namespace ColonyGame;

/// <summary>
/// The game's side of the Dwarf Render kit: our tile types as kit blocks (colour, texture recipe, glyph group), the
/// styled shader variants (<c>*_dr.gdshader</c>), and a converter that gives every view's plain materials the kit's toon
/// material with a glyph group by the view they belong to (colonists ☺, creatures, items, buildings), so the outline and
/// ASCII passes know what each pixel is. Views keep making ordinary materials; they are converted as they appear.
/// </summary>
public static class GameLooks
{
    // Glyph groups of our own (GlyphAtlas: 32..126 are free).
    public const int Furniture = 32, Construction = 33, Farm = 34, UpStairs = 35, DownStairs = 36, UpDownStairs = 37, Nest = 38, CutRock = 39;

    private static bool _registered;

    /// <summary>Kit block ids are our TileType values (compact: they index the texture atlas rows).</summary>
    public static void RegisterBlocks()
    {
        if (_registered) return;
        _registered = true;
        for (int i = 1; i < TileInfo.Count; i++)
        {
            var t = (TileType)i;
            BlockLooks.Register(i, Look(t));
        }
        RegisterSymbols();
    }


    /// <summary>
    /// What ASCII draws, Dwarf Fortress world-map style: each group's zoomed-in brightness ramp and its zoomed-out object
    /// symbols (eight, picked by position: repeats weight the mix). Grassland ", broadleaf ♣, conifers ♠ ↑, mountains ▲,
    /// cut rock ▓, water ≈ ~, sand ░, ore £, stairs &lt; &gt; X, colonists ☺, ants a, nests ∩, beds Θ, tables Π ╥, walls #.
    /// The kit's 5x7 font gets every glyph of the UI's bitmap font plus a few drawn here.
    /// </summary>
    private static void RegisterSymbols()
    {
        ImportUiGlyphs();
        var font = GlyphAtlas.Font;
        font.TryAdd("<", ["...#.", "..#..", ".#...", "#....", ".#...", "..#..", "...#."]);
        font.TryAdd(">", [".#...", "..#..", "...#.", "....#", "...#.", "..#..", ".#..."]);
        font.TryAdd("X", ["#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#"]);
        font.TryAdd("a", [".....", ".....", ".###.", "....#", ".####", "#...#", ".####"]);
        font.TryAdd("∩", [".....", ".###.", "#...#", "#...#", "#...#", "#...#", "....."]);
        font.TryAdd("░", ["#...#", "..#..", ".....", "#...#", "..#..", ".....", "#...#"]);
        font.TryAdd("Π", ["#####", ".#.#.", ".#.#.", ".#.#.", ".#.#.", ".#.#.", "....."]);
        font.TryAdd("╥", [".....", ".....", "#####", ".#.#.", ".#.#.", ".#.#.", ".#.#."]);
        void G(int id, string name, string ramp, string symbols) => GlyphAtlas.RegisterGroup(id, name, ramp, symbols);
        G(GlyphAtlas.Sky, "sky", "        ", "        ");           // our sky is plain: no dots over it
        G(GlyphAtlas.Oak, "oak", " .,*&%8@", "♣♣♣♣♣♣♣♣");
        G(GlyphAtlas.Pine, "pine", " .,*&%8@", "♠♠♠↑♠♠↑♠");
        G(GlyphAtlas.Birch, "birch", " .,*&%8@", "♣♣♣♣♣♣♣♣");
        G(GlyphAtlas.Bush, "bush", " .,*&%8@", "\"\"♣\"\"\"♣\"");
        G(GlyphAtlas.Creature, "ant", " .:aO0@@", "aaaaaaaa");
        G(GlyphAtlas.Grass, "grass", " .,'\"vwW", "\"\"\"\"'\"\",");
        G(GlyphAtlas.Dirt, "dirt", " .,:;+o%", ".,.`.,..");
        G(GlyphAtlas.Sand, "sand", " ..,:;+o", "░.░░.░,░");
        G(GlyphAtlas.Stone, "mountain", " .:+=#%@", "▲▲▲∩▲▲▲▲");
        G(GlyphAtlas.DeepStone, "rock", " .:+=#%@", "▓▓▓▓▓▓▓▓");
        G(CutRock, "cut rock", " .:+=#%@", "▓▓▓▓▓▓▓▓");
        G(GlyphAtlas.Water, "water", " .-~~~==", "≈≈≈~≈≈≈~");
        G(Furniture, "furniture", " .:=#HH8", "ΘΠ╥ΠΘΠ╥Π");
        G(Construction, "construction", " .:+=#%@", "########");
        G(Farm, "farm", " .,'\"vwW", "\"\"≈\"\"≈\"\"");
        G(UpStairs, "up stairs", " .:<<<<<", "<<<<<<<<");
        G(DownStairs, "down stairs", " .:>>>>>", ">>>>>>>>");
        G(UpDownStairs, "up/down stairs", " .:XXXXX", "XXXXXXXX");
        G(Nest, "nest", " .:∩∩∩∩∩", "∩∩∩∩∩∩∩∩");
    }

    /// <summary>The UI's 5x7 bitmap glyphs (game/ui/glyph_glyphs.json) into the kit's font, so ASCII matches the UI.</summary>
    private static void ImportUiGlyphs()
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(FileAccess.GetFileAsString("res://ui/glyph_glyphs.json"));
            foreach (var g in doc.RootElement.GetProperty("bitmap5x7").EnumerateObject())
            {
                var rows = new List<string>();
                foreach (var r in g.Value.EnumerateArray()) rows.Add(r.GetString() ?? ".....");
                if (rows.Count == 7) GlyphAtlas.Font.TryAdd(g.Name, rows.ToArray());
            }
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"GameLooks: couldn't import UI glyphs: {e.Message}");
        }
    }
    private static BlockLook Look(TileType t)
    {
        BlockLook B(string hex, TileRecipe recipe, int group, SurfaceClass cls = SurfaceClass.Generic, bool glow = false) =>
            new(t.ToString(), Color.FromHtml(hex), recipe, group, cls, glow);
        return t switch
        {
            TileType.Grass => B("#6f9a3a", TileRecipe.Grass, GlyphAtlas.Grass, SurfaceClass.Grass),
            TileType.Soil => B("#8a5a34", TileRecipe.Dirt, GlyphAtlas.Dirt),
            TileType.Stone => B("#77706a", TileRecipe.Stone, GlyphAtlas.Stone),
            TileType.Clay => B("#b0704c", TileRecipe.Dirt, GlyphAtlas.Dirt),
            TileType.Sand => B("#d8c08a", TileRecipe.Sand, GlyphAtlas.Sand),
            TileType.CopperOre => B("#d0783a", TileRecipe.Ore, GlyphAtlas.Ore, SurfaceClass.Ore),
            TileType.TinOre => B("#a8b2b4", TileRecipe.Ore, GlyphAtlas.Ore, SurfaceClass.Ore),
            TileType.IronOre => B("#c89a8a", TileRecipe.Ore, GlyphAtlas.Ore, SurfaceClass.Ore),
            TileType.SilverOre => B("#d8dce6", TileRecipe.Ore, GlyphAtlas.Ore, SurfaceClass.Ore),
            TileType.GoldOre => B("#f0c440", TileRecipe.Ore, GlyphAtlas.Ore, SurfaceClass.Ore),
            TileType.Quartz => B("#eeeaf6", TileRecipe.Noise, GlyphAtlas.Crystal),
            TileType.Sulfur => B("#e0d840", TileRecipe.Ore, GlyphAtlas.Ore, SurfaceClass.Ore),
            TileType.Aetherite => B("#7fe0d8", TileRecipe.Noise, GlyphAtlas.Crystal, SurfaceClass.Prop, glow: true),
            TileType.Wall => B("#8a3c2a", TileRecipe.Brick, GlyphAtlas.Brick),
            TileType.UpStair => B("#b48a58", TileRecipe.Path, UpStairs),
            TileType.DownStair => B("#b48a58", TileRecipe.Path, DownStairs),
            TileType.UpDownStair => B("#b48a58", TileRecipe.Path, UpDownStairs),
            _ => B("#77706a", TileRecipe.Stone, GlyphAtlas.Stone),
        };
    }

    /// <summary>Per tile type, for the terrain shaders: sRGB colour (alpha &lt; 1 glows) and glyph group.</summary>
    public static (Vector4[] Palette, float[] Groups, float[] CapGroups) TileTables()
    {
        RegisterBlocks();
        var palette = new Vector4[32];
        var groups = new float[32];
        var capGroups = new float[32];
        for (int i = 1; i < TileInfo.Count && i < 32; i++)
        {
            var look = BlockLooks.Get(i);
            var classic = WorldView.TileColor((TileType)i);
            palette[i] = new Vector4(look.Color.R, look.Color.G, look.Color.B, look.Emissive ? 0.2f : classic.A);
            groups[i] = look.GlyphGroup;
            capGroups[i] = look.GlyphGroup is GlyphAtlas.Stone or GlyphAtlas.Brick ? CutRock : look.GlyphGroup;
        }
        // Standing water in far blocks (FarMesher.Water): water's colour and symbols, no block texture.
        int w = (int)Colony.Core.Meshing.FarMesher.Water;
        palette[w] = new Vector4(0.16f, 0.40f, 0.60f, 1f);
        groups[w] = capGroups[w] = GlyphAtlas.Water;
        return (palette, groups, capGroups);
    }

    /// <summary>A shader by path: its <c>_dr</c> variant when the kit's look is on and one exists.</summary>
    public static Shader Shader(string path)
    {
        if (Main.StyledRender)
        {
            string dr = path.Replace(".gdshader", "_dr.gdshader");
            if (ResourceLoader.Exists(dr)) return GD.Load<Shader>(dr);
        }
        return GD.Load<Shader>(path);
    }

    /// <summary>Hands a terrain-like material (our tile shaders) the kit's atlas and lighting values, now and on every change.</summary>
    public static void Styled(ShaderMaterial m)
    {
        if (Main.StyledRender) DwarfMaterials.RegisterTerrain(m);
    }

    // ---- Converting plain materials --------------------------------------------------------------------

    private static readonly Dictionary<(Material, int), Material> _converted = new();
    private static Node? _root;
    private static ScreenTags? _tags;
    private static double _sweepIn;

    /// <summary>Converts materials under <paramref name="root"/> now, as nodes are added, and in a slow sweep (late changes).</summary>
    public static void Watch(Node root)
    {
        _root = root;
        _tags = new ScreenTags { Name = "ScreenTags" };
        root.AddChild(_tags);
        _converted.Clear();
        root.GetTree().NodeAdded += OnNodeAdded;
        root.TreeExiting += () =>
        {
            root.GetTree().NodeAdded -= OnNodeAdded;
            _root = null;
        };
        Sweep(root);
    }

    private static void OnNodeAdded(Node n)
    {
        if (_root != null && n is GeometryInstance3D g && _root.IsAncestorOf(g)) Callable.From(() => Convert(g)).CallDeferred();
    }

    /// <summary>Call every frame: a full sweep every second catches materials views swap in later.</summary>
    public static void Tick(double delta)
    {
        if (_root == null || (_sweepIn -= delta) > 0) return;
        _sweepIn = 1.0;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Sweep(_root);
        LastSweepMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>How long the last full sweep took (stutter diagnostics).</summary>
    public static double LastSweepMs;

    /// <summary>
    /// Subtrees the 1 s sweep skips: big views that build with their final (kit) materials and never swap them later
    /// (terrain sections and far blocks, water surfaces, flora). They are still converted as their nodes are added.
    /// Walking them was ~90 ms every second at 2048 (12k terrain nodes): a regular hitch in any view.
    /// </summary>
    public const string SweepSkip = "dr_sweep_skip";

    private static void Sweep(Node n)
    {
        if (n.IsInGroup(SweepSkip)) return;
        if (n is GeometryInstance3D g) Convert(g);
        foreach (var c in n.GetChildren()) Sweep(c);
    }

    private static void Convert(GeometryInstance3D g)
    {
        if (!GodotObject.IsInstanceValid(g) || g.IsInGroup("dr_keep")) return;
        if (g is Label3D label)
        {
            _tags?.Track(label); // drawn crisp in 2D instead
            return;
        }
        int group = GroupOf(g);
        if (g.MaterialOverride is { } over && Swap(over, group) is { } o) g.MaterialOverride = o;
        Mesh? mesh = g switch
        {
            MeshInstance3D mi => mi.Mesh,
            MultiMeshInstance3D mm => mm.Multimesh?.Mesh,
            _ => null,
        };
        if (g is MeshInstance3D m)
            for (int i = 0; i < m.GetSurfaceOverrideMaterialCount(); i++)
                if (m.GetSurfaceOverrideMaterial(i) is { } so && Swap(so, group) is { } s) m.SetSurfaceOverrideMaterial(i, s);
        switch (mesh)
        {
            case PrimitiveMesh pm when pm.Material is { } pmat && Swap(pmat, group) is { } p:
                pm.Material = p;
                break;
            case ArrayMesh am:
                for (int i = 0; i < am.GetSurfaceCount(); i++)
                    if (am.SurfaceGetMaterial(i) is { } sm && Swap(sm, group) is { } s) am.SurfaceSetMaterial(i, s);
                break;
        }
    }

    /// <summary>The kit's toon material for a plain opaque material (null: leave it, e.g. overlays and glass).</summary>
    private static Material? Swap(Material m, int group)
    {
        if (m is not StandardMaterial3D std) return null;
        if (std.Transparency != BaseMaterial3D.TransparencyEnum.Disabled || std.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded) return null;
        if (_converted.TryGetValue((m, group), out var done)) return done;
        var toon = DwarfMaterials.CreateObject(std.AlbedoColor, group, std.AlbedoTexture, std.VertexColorUseAsAlbedo,
            std.EmissionEnabled ? std.EmissionEnergyMultiplier : 0f);
        _converted[(m, group)] = toon;
        return toon;
    }

    /// <summary>What ASCII should draw for a node: by the view it belongs to.</summary>
    private static int GroupOf(Node n)
    {
        for (var q = n; q != null; q = q.GetParent())
            if (q.Name.ToString().StartsWith("Nest_")) return Nest; // the mound, not its ants
        for (var p = n; p != null; p = p.GetParent())
            switch (p)
            {
                case ColonistView: return GlyphAtlas.Dwarf;
                case CreatureView: return GlyphAtlas.Creature;
                case ItemView: return GlyphAtlas.Item;
                case HomeView: return Furniture;
                case RoomView: return GlyphAtlas.Prop;
                case EdgeView or CoverView: return Construction;
                case FloraView: return GlyphAtlas.Bush;
                case WaterView: return GlyphAtlas.Prop;
            }
        return GlyphAtlas.Generic;
    }
}
