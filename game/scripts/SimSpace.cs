using Colony.Core.World;
using Godot;

namespace ColonyGame;

/// <summary>
/// Conversions between simulation space (x/y horizontal, z up) and Godot space (Y up).
/// Sim (x, y, z) maps to Godot (x, z, y). Tile (x, y, z) occupies the unit cube starting at that corner.
/// </summary>
public static class SimSpace
{
    public static Vector3 ToGodot(float x, float y, float z) => new(x, z, y);

    public static Vector3 TileCenter(TilePos p) => new(p.X + 0.5f, p.Z + 0.5f, p.Y + 0.5f);

    public static System.Numerics.Vector3 ToSim(Vector3 g) => new(g.X, g.Z, g.Y);
}
