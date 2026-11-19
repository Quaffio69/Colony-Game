using Colony.Core.World;

namespace Colony.Core;

/// <summary>
/// Which faction (colony) owns each cell of a registry (designations, stockpiles, farm plots). Only non-zero owners
/// are stored, so a single colony costs nothing. <see cref="Any"/> (-1) as a filter means "whoever owns it".
/// </summary>
public sealed class Owners
{
    public const int Any = -1;
    private readonly Dictionary<TilePos, int> _owner = new();

    public int Of(TilePos p) => _owner.Count == 0 ? 0 : _owner.GetValueOrDefault(p);

    public void Set(TilePos p, int faction)
    {
        if (faction == 0) _owner.Remove(p);
        else _owner[p] = faction;
    }

    public void Clear(TilePos p) => _owner.Remove(p);

    /// <summary>Whether a filter (a faction, or <see cref="Any"/>) matches this cell's owner.</summary>
    public bool Matches(TilePos p, int filter) => filter == Any || Of(p) == filter;
}
