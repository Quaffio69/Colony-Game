namespace Colony.Core.World;

/// <summary>
/// Floors and roofs: coverings of a cell that change how it looks and how good a home it makes, never how anything
/// moves or flows. One byte per cell: the floor material in the low four bits, the roof material in the high four
/// (0 = none). A floor is laid on the cell's ground; a roof covers the cell's top (and is drawn in the level above).
/// </summary>
public static class CoverInfo
{
    public static int Floor(byte cover) => cover & 0x0f;
    public static int Roof(byte cover) => cover >> 4;
    public static byte WithFloor(byte cover, int floor) => (byte)((cover & 0xf0) | (floor & 0x0f));
    public static byte WithRoof(byte cover, int roof) => (byte)((cover & 0x0f) | (roof & 0x0f) << 4);
}

/// <summary>Floor coverings (index 1..; 0 = bare ground): name, the item one cell costs, its tier for homes, colour.</summary>
public static class FloorMaterials
{
    public static readonly (string Name, string Item, int Tier, uint Color)[] All =
    [
        ("Bare", "", 0, 0),
        ("Plank floor", "planks", 2, 0xb8895a),
        ("Flagstone", "stone_blocks", 2, 0xa9a9b0),
        ("Tiled floor", "bricks", 3, 0xb4553e),
    ];
    public static int Count => All.Length;
}

/// <summary>Roofs (index 1..; 0 = open sky): name, the item one cell costs, its tier for homes, colour.</summary>
public static class RoofMaterials
{
    public static readonly (string Name, string Item, int Tier, uint Color)[] All =
    [
        ("Open", "", 0, 0),
        ("Thatch", "fiber", 1, 0xc9a85a),
        ("Shingle", "planks", 2, 0x7a5232),
        ("Slate", "stone_blocks", 3, 0x55606e),
        ("Clay tile", "bricks", 3, 0xa8462e),
    ];
    public static int Count => All.Length;
}
