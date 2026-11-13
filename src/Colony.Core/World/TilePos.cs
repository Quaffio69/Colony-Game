namespace Colony.Core.World;

/// <summary>Integer grid position in simulation space (x/y horizontal, z = level).</summary>
public readonly record struct TilePos(int X, int Y, int Z)
{
    public TilePos Offset(int dx, int dy, int dz) => new(X + dx, Y + dy, Z + dz);
    public override string ToString() => $"({X}, {Y}, {Z})";
}

/// <summary>Chunk index (not tile units).</summary>
public readonly record struct ChunkCoord(int X, int Y, int Z);
