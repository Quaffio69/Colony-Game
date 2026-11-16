using Colony.Core.Items;

namespace Colony.Core.Industry;

/// <summary>
/// A workshop: a kind of factory room (Carpentry, Smelter, Academy...), from the <see cref="Catalog"/>. Its
/// stations are the machines that can stand in it; its recipes are everything those stations can make.
/// </summary>
public sealed class RoomType
{
    public required int Index { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string About { get; init; } = "";
    /// <summary>sRGB floor tint as 0xRRGGBB.</summary>
    public uint Color { get; init; } = 0xffffff;
    public IReadOnlyList<string> Later { get; init; } = [];
    public List<PartType> Stations { get; } = new();
    public List<Recipe> Recipes { get; } = new();
    public override string ToString() => Id;

    public static RoomType Carpentry => Catalog.Workshop("carpentry");
}

/// <summary>What a room part does.</summary>
public enum PartRole : byte
{
    /// <summary>Workstation: a worker processes batches of the machine's selected recipe here.</summary>
    Machine,
    /// <summary>Raises the room's efficiency, covering a limited area of machines each.</summary>
    Auxiliary,
    /// <summary>Holds the room's inputs; haulers fill it.</summary>
    Input,
    /// <summary>Holds the room's products; haulers empty it.</summary>
    Output,
}

/// <summary>
/// A part family: a workshop's station (a machine, from the catalog) or one of the parts every room can take
/// (tools, lanterns, crates). Machines and tools come in <see cref="PartInfo.MaxSize"/> sizes.
/// </summary>
public sealed class PartType
{
    public required int Index { get; init; }
    /// <summary>Unique id: "workshop.station" for stations, a plain id for generic parts.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required PartRole Role { get; init; }
    /// <summary>The workshop this station belongs to (null for parts any room can take).</summary>
    public RoomType? Workshop { get; init; }
    /// <summary>What this station can make (machines only).</summary>
    public List<Recipe> Recipes { get; } = new();
    /// <summary>3D stand-in the game draws (defaults to the station id).</summary>
    public string Model { get; init; } = "";
    /// <summary>Build material by size (see <see cref="PartInfo.Cost"/>).</summary>
    public IReadOnlyList<ItemType> Build { get; init; } = [];
    /// <summary>Per-size names (optional); otherwise sizes are "Small x", "x", "Large x", "Great x", "Grand x".</summary>
    public IReadOnlyList<string> SizeNames { get; init; } = [];
    public override string ToString() => Id;

    public static PartType Sawbench => Known.Sawbench;
    public static PartType ToolRack => Known.ToolRack;
    public static PartType Lantern => Known.Lantern;
    public static PartType InputCrate => Known.InputCrate;
    public static PartType OutputCrate => Known.OutputCrate;

    // Resolved on first use, never while the catalog itself is loading.
    private static class Known
    {
        public static readonly PartType Sawbench = Catalog.Part("carpentry.saw_bench");
        public static readonly PartType ToolRack = Catalog.Part("tool_rack");
        public static readonly PartType Lantern = Catalog.Part("lantern");
        public static readonly PartType InputCrate = Catalog.Part("input_crate");
        public static readonly PartType OutputCrate = Catalog.Part("output_crate");
    }
}

/// <summary><paramref name="Count"/> units of <paramref name="Item"/> per recipe unit.</summary>
public readonly record struct Ingredient(ItemType Item, int Count);

/// <summary>
/// One unit of production at a station: consume <see cref="Inputs"/>, produce <see cref="OutCount"/> of
/// <see cref="Output"/> (or, for education and worker items, promote or add colonists) in
/// <see cref="WorkTicks"/> of labour, by a worker of at least <see cref="Education"/>. Machines run recipes
/// in batches of several units (see <see cref="PartInfo.Batch"/>).
/// </summary>
public sealed class Recipe
{
    public required int Index { get; init; }
    public required ItemType Output { get; init; }
    public required int OutCount { get; init; }
    public required IReadOnlyList<Ingredient> Inputs { get; init; }
    public required int WorkTicks { get; init; }
    /// <summary>Index into <see cref="Catalog.EducationLevels"/>.</summary>
    public required int Education { get; init; }
    public required RoomType Workshop { get; init; }
    public required PartType Station { get; init; }
    public ItemKind Kind => Output.Def.Kind;
    public string Name => Output.Name;
    public override string ToString() => Output.Id;

    /// <summary>"2 copper ore + charcoal -> copper ingot".</summary>
    public string Describe(int units = 1)
    {
        static string Amount(int n, ItemType t) => n == 1 ? t.Name.ToLowerInvariant() : $"{n} {t.Name.ToLowerInvariant()}";
        string ins = string.Join(" + ", Inputs.Select(i => Amount(i.Count * units, i.Item)));
        string outs = Kind switch
        {
            ItemKind.Education => units == 1 ? $"a {Output.Name}" : $"{units} {Output.Name}s",
            _ => Amount(OutCount * units, Output),
        };
        return $"{ins} -> {outs}";
    }
}

/// <summary>
/// <see cref="Units"/> recipe units processed together at one machine in <see cref="WorkTicks"/> (before the
/// room's efficiency).
/// </summary>
public readonly record struct Batch(Recipe Recipe, int Units, int WorkTicks)
{
    public int InCount(int ingredient) => Recipe.Inputs[ingredient].Count * Units;
    public int OutCount => Recipe.OutCount * Units;
}

/// <summary><see cref="Count"/> units of <see cref="Material"/> to build a part.</summary>
public readonly record struct BuildCost(ItemType Material, int Count);

public static class RoomInfo
{
    public static int TypeCount => Catalog.Workshops.Count;
    public static string Name(RoomType t) => t.Name;

    /// <summary>Parts that can be placed in this room type: its own stations, plus tools, lanterns and crates.</summary>
    public static bool Allows(RoomType room, PartType part) => part.Workshop is null || part.Workshop == room;
}

/// <summary>
/// Part data. Sizes trade build cost against labour and space:
/// <list type="bullet">
/// <item>Machines: bigger ones cost more per tile but process bigger batches for less labour per unit
/// (the biggest needs ~30% of the smallest's worker time per unit). Every station shares these sizes.</item>
/// <item>Tools: cost grows exponentially with size, but bonus and coverage per tile grow too, so big tool
/// cribs equip a large factory in little floor space.</item>
/// </list>
/// Placed parts are blueprints until haulers deliver their <see cref="Cost"/> and a builder puts them up.
/// </summary>
public static class PartInfo
{
    public const int MaxSize = 5;

    private static readonly string[] SizeWords = { "Small {0}", "{1}", "Large {0}", "Great {0}", "Grand {0}" };
    private static readonly (int W, int D)[] MachineFootprint = { (2, 1), (2, 2), (3, 2), (4, 2), (4, 3) };
    private static readonly int[] MachineBatch = { 1, 2, 4, 6, 8 };
    /// <summary>Batch time as a multiple of the recipe's per-unit work (labour per unit: 1, .75, .55, .4, .3).</summary>
    private static readonly float[] MachineTime = { 1f, 1.5f, 2.2f, 2.4f, 2.4f };
    private static readonly int[] MachineCost = { 2, 12, 30, 64, 144 };

    private static readonly string[] ToolNames = { "Tool pegs", "Tool rack", "Tool wall", "Tool bench", "Tool crib" };
    private static readonly (int W, int D)[] ToolFootprint = { (1, 1), (2, 1), (3, 1), (2, 2), (3, 2) };
    private static readonly float[] ToolBonus = { 0.20f, 0.30f, 0.40f, 0.50f, 0.60f };
    private static readonly int[] ToolCoverage = { 4, 10, 18, 32, 54 };
    private static readonly int[] ToolCost = { 1, 6, 18, 54, 162 }; // size 1 in logs, then planks

    /// <summary>Family name (e.g. for grouping).</summary>
    public static string Name(PartType t) => t.Name;

    /// <summary>Name of one size of a part.</summary>
    public static string Name(PartType t, int size)
    {
        if (Sizes(t) == 1) return t.Name;
        int i = Index(t, size);
        if (t == PartType.ToolRack) return ToolNames[i];
        if (t.SizeNames.Count == MaxSize) return t.SizeNames[i];
        return string.Format(SizeWords[i], t.Name.ToLowerInvariant(), t.Name);
    }

    /// <summary>How many sizes a part family comes in (sizes are 1..Sizes).</summary>
    public static int Sizes(PartType t) => t.Role == PartRole.Machine || t == PartType.ToolRack ? MaxSize : 1;

    public static int ClampSize(PartType t, int size) => Math.Clamp(size, 1, Sizes(t));

    private static int Index(PartType t, int size) => ClampSize(t, size) - 1;

    public static PartRole Role(PartType t) => t.Role;

    /// <summary>Footprint (x by y) when not rotated.</summary>
    public static (int W, int D) Footprint(PartType t, int size = 1) =>
        t.Role == PartRole.Machine ? MachineFootprint[Index(t, size)]
        : t == PartType.ToolRack ? ToolFootprint[Index(t, size)]
        : (1, 1);

    public static int Tiles(PartType t, int size = 1)
    {
        var (w, d) = Footprint(t, size);
        return w * d;
    }

    /// <summary>
    /// Materials delivered to a part's blueprint before it can be built. A station's materials come from its
    /// catalog entry, by size, so the smallest version of every station is buildable before that station
    /// exists (logs or stone for the first ones); tools, lanterns and crates take logs, then planks. Never more
    /// than the footprint can hold (one pile per cell).
    /// </summary>
    public static BuildCost Cost(PartType t, int size = 1)
    {
        size = ClampSize(t, size);
        if (t.Role == PartRole.Machine) return new BuildCost(BuildMaterial(t, size), MachineCost[size - 1]);
        if (t == PartType.ToolRack) return new BuildCost(size == 1 ? ItemType.Log : ItemType.Plank, ToolCost[size - 1]);
        if (t == PartType.Lantern) return new BuildCost(ItemType.Log, 1);
        return new BuildCost(ItemType.Log, 2);
    }

    /// <summary>One entry: all sizes. Two: size 1, then 2-5. Three: size 1, 2-3, 4-5.</summary>
    private static ItemType BuildMaterial(PartType t, int size)
    {
        var b = t.Build;
        if (b.Count == 0) return size == 1 ? ItemType.Log : ItemType.Plank;
        int i = b.Count switch
        {
            1 => 0,
            2 => size == 1 ? 0 : 1,
            _ => size == 1 ? 0 : size <= 3 ? 1 : 2,
        };
        return b[Math.Min(i, b.Count - 1)];
    }

    /// <summary>Ticks of builder work once the materials are on site.</summary>
    public static int BuildTicks(PartType t, int size = 1) => 40 + 3 * Cost(t, size).Count;

    /// <summary>Recipe units a machine processes per batch.</summary>
    public static int Batch(PartType t, int size = 1) => t.Role == PartRole.Machine ? MachineBatch[Index(t, size)] : 1;

    /// <summary>A full batch of <paramref name="recipe"/> at a machine of this size.</summary>
    public static Batch MachineBatchOf(PartType t, int size, Recipe recipe)
    {
        float time = t.Role == PartRole.Machine ? MachineTime[Index(t, size)] : 1f;
        return new Batch(recipe, Batch(t, size), (int)MathF.Round(recipe.WorkTicks * time));
    }

    /// <summary>
    /// <paramref name="units"/> of a machine's full batch: counts and time scale down together, so labour per
    /// unit stays the machine's.
    /// </summary>
    public static Batch PartialBatch(Batch full, int units) => full with
    {
        Units = units,
        WorkTicks = Math.Max(1, (int)MathF.Round(full.WorkTicks * units / (float)full.Units)),
    };

    /// <summary>Efficiency bonus an auxiliary gives to the machine tiles it covers.</summary>
    public static float Bonus(PartType t, int size = 1) =>
        t == PartType.ToolRack ? ToolBonus[Index(t, size)] : t == PartType.Lantern ? 0.10f : 0f;

    /// <summary>How many machine tiles one auxiliary covers.</summary>
    public static int Coverage(PartType t, int size = 1) =>
        t == PartType.ToolRack ? ToolCoverage[Index(t, size)] : t == PartType.Lantern ? 8 : 0;
}
