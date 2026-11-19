using Colony.Core.Navigation;
using Colony.Core.World;

namespace Colony.Core.Items;

/// <summary>
/// Cells the player has zoned for storage. Haulers bring loose piles here; a pile in a stockpile cell counts
/// as stored. Cells are walkable floor cells (the open cell above the ground the player marked). Accepts every
/// item type for now; filters and room-like grouping come with the factory builder.
/// </summary>
public sealed class Stockpiles
{
    private readonly VoxelWorld _world;
    private readonly HashSet<TilePos> _cells = new();
    private readonly Owners _owners = new();

    /// <summary>The faction whose stockpile this cell is.</summary>
    public int OwnerOf(TilePos p) => _owners.Of(p);

    public Stockpiles(VoxelWorld world) => _world = world;

    public int Version { get; private set; }
    public int Count => _cells.Count;
    public IReadOnlyCollection<TilePos> Cells => _cells;
    public bool Contains(TilePos p) => _cells.Contains(p);

    /// <summary>Cells stockpiles may not claim (e.g. factory rooms). Set by <see cref="ColonySim"/>.</summary>
    public Func<TilePos, bool>? IsBlocked { get; set; }

    public bool Add(TilePos cell, int faction = 0)
    {
        if (!Movement.IsStandable(_world, cell) || IsBlocked?.Invoke(cell) == true || !_cells.Add(cell)) return false;
        _owners.Set(cell, faction);
        Version++;
        return true;
    }

    public bool Remove(TilePos cell, int faction = Owners.Any)
    {
        if (!_cells.Contains(cell) || !_owners.Matches(cell, faction)) return false;
        _cells.Remove(cell);
        _owners.Clear(cell);
        Version++;
        return true;
    }

    /// <summary>
    /// Zones the floor in the inclusive box between two ground tiles: every solid tile in the box whose cell
    /// above is walkable. Returns how many cells were added.
    /// </summary>
    public int AddFloorBox(TilePos a, TilePos b, int faction = 0)
    {
        int n = 0;
        ForBox(a, b, p =>
        {
            if (TileInfo.IsSolid(_world.Get(p)) && Add(p.Offset(0, 0, 1), faction)) n++;
        });
        return n;
    }

    /// <summary>Removes zoned cells standing on (or in) the box. Returns how many were removed.</summary>
    public int RemoveBox(TilePos a, TilePos b, int faction = Owners.Any)
    {
        int n = 0;
        ForBox(a, b, p =>
        {
            if (Remove(p.Offset(0, 0, 1), faction)) n++;
            if (Remove(p, faction)) n++;
        });
        return n;
    }

    private static void ForBox(TilePos a, TilePos b, Action<TilePos> action)
    {
        for (int z = Math.Min(a.Z, b.Z); z <= Math.Max(a.Z, b.Z); z++)
        for (int y = Math.Min(a.Y, b.Y); y <= Math.Max(a.Y, b.Y); y++)
        for (int x = Math.Min(a.X, b.X); x <= Math.Max(a.X, b.X); x++)
            action(new TilePos(x, y, z));
    }
}
