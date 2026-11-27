using Colony.Core.World;

namespace Colony.Core.Nature;

/// <summary>
/// Grass creeping back over fresh dirt: soil laid bare under the open sky (a dug-out surface, the floor of a pit or
/// cut) turns to grass after a while. Only cells a dig exposed are watched (registered by
/// <see cref="ColonySim.CompleteDesignation"/>), so nothing scans the map. A cell is due <see cref="Balance.GrassRegrowDays"/>
/// to that plus <see cref="Balance.GrassRegrowSpread"/> days after it was bared (hashed per cell, so a dug field greens unevenly); when due it greens if a
/// neighbouring top is grass (so bare patches fill in from their edges), else it waits for another round, greening
/// anyway after <see cref="Rounds"/>. Underground soil, soil under a floor, a roof or standing water, and cells no
/// longer soil drop out. Deterministic: a list in registration order, checked every <see cref="Interval"/> ticks.
/// </summary>
public sealed class Regrowth
{
    /// <summary>Ticks between checks of the watched cells.</summary>
    public const int Interval = 240;
    /// <summary>Waits for a grassy neighbour before greening on its own.</summary>
    public const int Rounds = 3;

    private readonly List<(TilePos Cell, long Due, int Round)> _cells = new();
    private readonly HashSet<TilePos> _watched = new();

    public int Count => _cells.Count;
    /// <summary>Cells that have greened since the world began (for tests and the bench).</summary>
    public int Greened { get; private set; }

    /// <summary>Starts watching a soil tile (no-op if it is already watched or not soil).</summary>
    public void Watch(VoxelWorld world, TilePos tile, long now)
    {
        if (!world.InBounds(tile) || world.Get(tile) != TileType.Soil || !_watched.Add(tile)) return;
        _cells.Add((tile, now + Delay(tile, 0), 0));
    }

    private static long Delay(TilePos p, int round) =>
        (long)(Balance.Day * (Balance.GrassRegrowDays + Procedural.Noise.Hash(p.X, p.Y, p.Z + round * 977, 0x6A55) * Balance.GrassRegrowSpread));

    internal void Tick(ColonySim sim)
    {
        if (sim.Tick % Interval != 0 || _cells.Count == 0) return;
        var world = sim.World;
        int keep = 0;
        for (int i = 0; i < _cells.Count; i++)
        {
            var (p, due, round) = _cells[i];
            if (due > sim.Tick)
            {
                _cells[keep++] = _cells[i];
                continue;
            }
            var outcome = Check(sim, p, round);
            switch (outcome)
            {
                case Outcome.Green or Outcome.GreenDry:
                    world.Set(p, outcome == Outcome.GreenDry ? TileType.DryGrass : TileType.Grass);
                    Greened++;
                    _watched.Remove(p);
                    break;
                case Outcome.Drop:
                    _watched.Remove(p);
                    break;
                default:
                    _cells[keep++] = (p, sim.Tick + Delay(p, round + 1), round + 1);
                    break;
            }
        }
        _cells.RemoveRange(keep, _cells.Count - keep);
    }

    private enum Outcome { Green, GreenDry, Wait, Drop }

    private static Outcome Check(ColonySim sim, TilePos p, int round)
    {
        var world = sim.World;
        if (world.Get(p) != TileType.Soil) return Outcome.Drop;
        var above = p.Offset(0, 0, 1);
        if (!world.InBounds(above) || world.Get(above) != TileType.Air || world.FloorAt(above) != 0 || world.RoofAt(above) != 0) return Outcome.Drop;
        if (world.SurfaceLevel(p.X, p.Y) != p.Z) return Outcome.Drop; // under an overhang or underground: no sky
        if (sim.Water.MassAt(above) >= Fluids.Water.MinMass) return Outcome.Wait; // flooded: maybe it drains
        if (round + 1 >= Rounds) return Outcome.Green;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0) continue;
            var n = p.Offset(dx, dy, dz);
            if (!world.InBounds(n) || world.IsSolid(n.X, n.Y, n.Z + 1)) continue;
            var g = world.Get(n);
            if (g == TileType.Grass) return Outcome.Green;
            if (g == TileType.DryGrass) return Outcome.GreenDry; // steppe and savanna grow back dry
        }
        return Outcome.Wait;
    }

    // ---- Snapshot -----------------------------------------------------------------------------------------

    internal void SnapSave(BinaryWriter w)
    {
        w.Write(_cells.Count);
        foreach (var (p, due, round) in _cells) { Net.SnapIO.Write(w, p); w.Write(due); w.Write(round); }
        w.Write(Greened);
    }

    internal void SnapLoad(BinaryReader r)
    {
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            var p = Net.SnapIO.ReadPos(r);
            _cells.Add((p, r.ReadInt64(), r.ReadInt32()));
            _watched.Add(p);
        }
        Greened = r.ReadInt32();
    }
}
