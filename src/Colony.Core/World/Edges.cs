namespace Colony.Core.World;

/// <summary>What stands on a cell edge: a thin wall, a door (passable) or a window (not).</summary>
public enum EdgeKind : byte { None, Wall, Door, Window }

/// <summary>
/// Thin walls stand on the edges between cells instead of filling a cell, so a room keeps its floor. Each cell owns
/// two edges: its east side (+x) and its north side (+y); the west and south sides belong to the neighbours. An edge is
/// one byte: the kind in the top two bits, a material index (<see cref="WallMaterials"/>) in the rest, 0 = nothing.
/// A cell's two edges pack into a ushort (east low byte, north high byte). An edge is one level tall.
/// </summary>
public static class EdgeInfo
{
    public const int East = 0, North = 1;

    public static byte Make(EdgeKind kind, int material) => kind == EdgeKind.None ? (byte)0 : (byte)((int)kind << 6 | (material & 0x3f));
    public static EdgeKind Kind(byte info) => (EdgeKind)(info >> 6);
    public static int Material(byte info) => info & 0x3f;

    /// <summary>Walls and windows stop walkers (and water); doors let them through.</summary>
    public static bool Blocks(byte info) => Kind(info) is EdgeKind.Wall or EdgeKind.Window;

    /// <summary>Walls and closed windows hold back water; doors don't (they're never watertight).</summary>
    public static bool HoldsWater(byte info) => Kind(info) is EdgeKind.Wall or EdgeKind.Window;

    public static byte EastOf(ushort edges) => (byte)edges;
    public static byte NorthOf(ushort edges) => (byte)(edges >> 8);
}

/// <summary>
/// What thin walls are built from (the material index in <see cref="EdgeInfo"/>): the item a piece costs, how it looks
/// and what it adds to a home. Log palisades and rubble first, then planks, cut stone and brick.
/// </summary>
public static class WallMaterials
{
    public static readonly (string Name, string Item, string Material, int Quality, uint Color)[] All =
    [
        ("Log", "logs", "wood", 0, 0x7a5532),
        ("Rubble", "stone", "stone", 0, 0x8f8f94),
        ("Plank", "planks", "wood", 1, 0xc49a62),
        ("Stone block", "stone_blocks", "stone", 2, 0xc8c8cf),
        ("Brick", "bricks", "clay", 2, 0xa8503a),
    ];

    public static int Count => All.Length;
    public static int Clamp(int material) => Math.Clamp(material, 0, All.Length - 1);
}
