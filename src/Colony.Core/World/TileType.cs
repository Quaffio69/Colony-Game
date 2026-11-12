namespace Colony.Core.World;

/// <summary>Material occupying a single grid cell. Stored as a byte to keep large worlds compact.</summary>
public enum TileType : byte
{
    Air = 0,
    Grass,
    Soil,
    Stone,
    Ore,
}
