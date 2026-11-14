using Colony.Core.Items;

namespace Colony.Core.Industry;

/// <summary>Kinds of factory room. Each has one recipe for now; more recipes per room come later.</summary>
public enum RoomType : byte
{
    Sawmill,
}

/// <summary>What a room part does.</summary>
public enum PartRole : byte
{
    /// <summary>Workstation: a worker processes batches of the room's recipe here.</summary>
    Machine,
    /// <summary>Raises the room's efficiency, covering a limited area of machines each.</summary>
    Auxiliary,
    /// <summary>Holds the room's input; haulers fill it.</summary>
    Input,
    /// <summary>Holds the room's products; haulers empty it.</summary>
    Output,
}

/// <summary>Part families. Sawbenches and tool racks come in <see cref="PartInfo.MaxSize"/> sizes.</summary>
public enum PartType : byte
{
    Sawbench,
    ToolRack,
    Lantern,
    InputCrate,
    OutputCrate,
}

/// <summary>
/// Consume <see cref="InCount"/> of <see cref="Input"/>, produce <see cref="OutCount"/> of <see cref="Output"/> in
/// <see cref="WorkTicks"/> of labour. A room's recipe is the per-unit base; a machine runs it in batches
/// (see <see cref="PartInfo.Batch"/>).
/// </summary>
public readonly record struct Recipe(ItemType Input, int InCount, ItemType Output, int OutCount, int WorkTicks);

public static class RoomInfo
{
    public const int TypeCount = 1;

    public static string Name(RoomType t) => t switch
    {
        RoomType.Sawmill => "Sawmill",
        _ => t.ToString(),
    };

    public static Recipe Recipe(RoomType t) => t switch
    {
        RoomType.Sawmill => new Recipe(ItemType.Log, 1, ItemType.Plank, 4, 100),
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    /// <summary>Parts that can be placed in this room type.</summary>
    public static bool Allows(RoomType room, PartType part) => part switch
    {
        PartType.Sawbench or PartType.ToolRack => room == RoomType.Sawmill,
        _ => true, // lanterns and crates fit any room
    };
}

/// <summary>
/// Part data. Sizes trade build cost against labour and space:
/// <list type="bullet">
/// <item>Machines: bigger ones cost more per tile but process bigger batches for less labour per unit
/// (a mill saw needs ~30% of a saw horse's worker time per log).</item>
/// <item>Tools: cost grows exponentially with size, but bonus and coverage per tile grow too, so big tool
/// cribs equip a large factory in little floor space.</item>
/// </list>
/// Costs are in planks and shown in the builder; building is still free until construction jobs exist.
/// </summary>
public static class PartInfo
{
    public const int TypeCount = 5;
    public const int MaxSize = 5;

    private static readonly string[] SawNames = { "Saw horse", "Saw table", "Pit saw", "Frame saw", "Mill saw" };
    private static readonly (int W, int D)[] SawFootprint = { (2, 1), (2, 2), (3, 2), (4, 2), (4, 3) };
    private static readonly int[] SawBatch = { 1, 2, 4, 6, 8 };
    /// <summary>Batch time as a multiple of the recipe's per-unit work (labour per unit: 1, .75, .55, .4, .3).</summary>
    private static readonly float[] SawTime = { 1f, 1.5f, 2.2f, 2.4f, 2.4f };
    private static readonly int[] SawCost = { 4, 12, 30, 64, 144 };

    private static readonly string[] ToolNames = { "Tool pegs", "Tool rack", "Tool wall", "Tool bench", "Tool crib" };
    private static readonly (int W, int D)[] ToolFootprint = { (1, 1), (2, 1), (3, 1), (2, 2), (3, 2) };
    private static readonly float[] ToolBonus = { 0.20f, 0.30f, 0.40f, 0.50f, 0.60f };
    private static readonly int[] ToolCoverage = { 4, 10, 18, 32, 54 };
    private static readonly int[] ToolCost = { 2, 6, 18, 54, 162 };

    /// <summary>Family name (e.g. for grouping).</summary>
    public static string Name(PartType t) => t switch
    {
        PartType.Sawbench => "Sawbench",
        PartType.ToolRack => "Tools",
        PartType.Lantern => "Lantern",
        PartType.InputCrate => "Input crate",
        PartType.OutputCrate => "Output crate",
        _ => t.ToString(),
    };

    /// <summary>Name of one size of a part.</summary>
    public static string Name(PartType t, int size) => t switch
    {
        PartType.Sawbench => SawNames[Index(t, size)],
        PartType.ToolRack => ToolNames[Index(t, size)],
        _ => Name(t),
    };

    /// <summary>How many sizes a part family comes in (sizes are 1..Sizes).</summary>
    public static int Sizes(PartType t) => t is PartType.Sawbench or PartType.ToolRack ? MaxSize : 1;

    public static int ClampSize(PartType t, int size) => Math.Clamp(size, 1, Sizes(t));

    private static int Index(PartType t, int size) => ClampSize(t, size) - 1;

    public static PartRole Role(PartType t) => t switch
    {
        PartType.Sawbench => PartRole.Machine,
        PartType.ToolRack or PartType.Lantern => PartRole.Auxiliary,
        PartType.InputCrate => PartRole.Input,
        _ => PartRole.Output,
    };

    /// <summary>Footprint (x by y) when not rotated.</summary>
    public static (int W, int D) Footprint(PartType t, int size = 1) => t switch
    {
        PartType.Sawbench => SawFootprint[Index(t, size)],
        PartType.ToolRack => ToolFootprint[Index(t, size)],
        _ => (1, 1),
    };

    public static int Tiles(PartType t, int size = 1)
    {
        var (w, d) = Footprint(t, size);
        return w * d;
    }

    /// <summary>Build cost in planks (not charged yet).</summary>
    public static int Cost(PartType t, int size = 1) => t switch
    {
        PartType.Sawbench => SawCost[Index(t, size)],
        PartType.ToolRack => ToolCost[Index(t, size)],
        PartType.Lantern => 3,
        _ => 4,
    };

    /// <summary>Recipe units a machine processes per batch.</summary>
    public static int Batch(PartType t, int size = 1) => t == PartType.Sawbench ? SawBatch[Index(t, size)] : 1;

    /// <summary>A machine's batch of <paramref name="unit"/>: counts scaled by the batch, time by the machine's speed.</summary>
    public static Recipe MachineRecipe(PartType t, int size, Recipe unit)
    {
        int batch = Batch(t, size);
        float time = t == PartType.Sawbench ? SawTime[Index(t, size)] : 1f;
        return unit with
        {
            InCount = unit.InCount * batch,
            OutCount = unit.OutCount * batch,
            WorkTicks = (int)MathF.Round(unit.WorkTicks * time),
        };
    }

    /// <summary>
    /// <paramref name="units"/> of a machine's <paramref name="batchUnits"/>-unit batch: counts and time scale
    /// down together, so labour per unit stays the machine's.
    /// </summary>
    public static Recipe PartialBatch(Recipe batch, int units, int batchUnits) => batch with
    {
        InCount = batch.InCount / batchUnits * units,
        OutCount = batch.OutCount / batchUnits * units,
        WorkTicks = Math.Max(1, (int)MathF.Round(batch.WorkTicks * units / (float)batchUnits)),
    };

    /// <summary>Efficiency bonus an auxiliary gives to the machine tiles it covers.</summary>
    public static float Bonus(PartType t, int size = 1) => t switch
    {
        PartType.ToolRack => ToolBonus[Index(t, size)],
        PartType.Lantern => 0.10f,
        _ => 0f,
    };

    /// <summary>How many machine tiles one auxiliary covers.</summary>
    public static int Coverage(PartType t, int size = 1) => t switch
    {
        PartType.ToolRack => ToolCoverage[Index(t, size)],
        PartType.Lantern => 8,
        _ => 0,
    };
}
