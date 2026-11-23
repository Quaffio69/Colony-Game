using Colony.Core.World;

namespace Colony.Core.Net;

/// <summary>
/// A fingerprint of the simulation for desync checks between peers: the tick, every colonist (position, task, needs,
/// colony), every item pile, designations, stockpiles, rooms, blueprints and the world's chunk versions. Combines
/// entries order-independently (registries may enumerate in different orders) with a fixed 64-bit mix, never
/// <see cref="HashCode"/>, whose seed differs per process.
/// </summary>
public static class StateHash
{
    private static ulong Mix(ulong h)
    {
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33;
        h *= 0xc4ceb9fe1a85ec53UL;
        h ^= h >> 33;
        return h;
    }

    private static ulong Of(params long[] values)
    {
        ulong h = 0x9e3779b97f4a7c15UL;
        foreach (long v in values) h = Mix(h ^ (ulong)v) + 0x9e3779b97f4a7c15UL;
        return h;
    }

    private static long Pos(TilePos p) => ((long)p.Z << 42) | ((long)(p.Y & 0x1fffff) << 21) | (uint)(p.X & 0x1fffff);

    public static ulong Compute(ColonySim sim)
    {
        ulong sum = Of(sim.Tick, sim.Colonists.Count, sim.Designations.Count, sim.Stockpiles.Count, sim.Rooms.All.Count, sim.Constructions.Count);
        foreach (var c in sim.Colonists)
            sum += Of(1, c.Id, Pos(c.Position), (long)c.Task, (long)c.State, c.Satiety, (long)(c.Rest * 1000), c.Faction, c.CarryingCount, c.Education,
                c.Health, c.Downed ? 1 : 0, c.Drafted ? 1 : 0, c.Weapon?.Index ?? -1, c.Armour?.Index ?? -1, c.Shield?.Index ?? -1);
        foreach (var (p, pile) in sim.Items.All)
            sum += Of(2, Pos(p), pile.Type.Index, pile.Count, pile.Reserved);
        foreach (var (p, kind) in sim.Designations.All)
            sum += Of(3, Pos(p), (long)kind, sim.Designations.OwnerOf(p));
        foreach (var p in sim.Stockpiles.Cells)
            sum += Of(4, Pos(p), sim.Stockpiles.OwnerOf(p));
        foreach (var (p, rack) in sim.Stockpiles.Racks)
            sum += Of(10, Pos(p), rack.Levels, rack.Facing);
        sum += Of(11, sim.Regrowth.Count, sim.Regrowth.Greened);
        foreach (var r in sim.Rooms.All)
            sum += Of(5, r.Id, r.Cells.Count, r.Parts.Count, r.Faction, r.Parts.Sum(p => p.Limit));
        foreach (var bp in sim.Constructions.All)
            sum += Of(6, Pos(bp.Cell), (long)bp.Kind, bp.Faction, Pos(bp.Target));
        sum += Of(7, sim.World.EdgeRevision, sim.World.CoverRevision, sim.Deaths, sim.Wildlife.Slain);
        foreach (var n in sim.Wildlife.Nests)
            sum += Of(8, n.Id, Pos(n.Entrance), n.Population, n.Out, n.Health, n.Crowded, n.Awake ? 1 : 0);
        foreach (var c in sim.Wildlife.Creatures)
            sum += Of(9, c.Id, Pos(c.Position), c.Health, (long)c.Mode, c.NestId, c.TargetId);
        foreach (var p in sim.Stockpiles.DepotCells)
            sum += Of(12, Pos(p));
        foreach (var d in sim.Trades.All)
        {
            sum += Of(13, d.Id, d.A, d.B, (long)d.State, d.Turn, d.Round);
            foreach (var l in d.Lines) sum += Of(14, d.Id, l.From, l.Type.Index, l.Count, l.Delivered, l.InTransit);
        }
        return Mix(sum);
    }
}
