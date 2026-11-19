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
    public const int Furniture = 32, Construction = 33, Farm = 34;

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
        GlyphAtlas.RegisterGroup(GlyphAtlas.Sky, "sky", "        ", "        "); // our sky is plain: no dots over it
        GlyphAtlas.RegisterGroup(Furniture, "furniture", " .:=#HH8", "ΠΠΠ╥ΠΠ╥Π");
        GlyphAtlas.RegisterGroup(Construction, "construction", " .:+=#%@", "########");
        GlyphAtlas.RegisterGroup(Farm, "farm", " .,'\"vwW", "\"\"τ\"\"τ\"\"");
        GlyphAtlas.Font.TryAdd("Π", ["#####", ".#.#.", ".#.#.", ".#.#.", ".#.#.", ".#.#.", "....."]);
        GlyphAtlas.Font.TryAdd("╥", [".....", ".....", "#####", ".#.#.", ".#.#.", ".#.#.", ".#.#."]);
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
            TileType.UpStair or TileType.DownStair or TileType.UpDownStair => B("#b48a58", TileRecipe.Path, GlyphAtlas.Path),
            _ => B("#77706a", TileRecipe.Stone, GlyphAtlas.Stone),
        };
    }

    /// <summary>Per tile type, for the terrain shaders: sRGB colour (alpha &lt; 1 glows) and glyph group.</summary>
    public static (Vector4[] Palette, float[] Groups) TileTables()
    {
        RegisterBlocks();
        var palette = new Vector4[32];
        var groups = new float[32];
        for (int i = 1; i < TileInfo.Count && i < 32; i++)
        {
            var look = BlockLooks.Get(i);
            var classic = WorldView.TileColor((TileType)i);
            palette[i] = new Vector4(look.Color.R, look.Color.G, look.Color.B, look.Emissive ? 0.2f : classic.A);
            groups[i] = look.GlyphGroup;
        }
        return (palette, groups);
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
        Sweep(_root);
    }

    private static void Sweep(Node n)
    {
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
