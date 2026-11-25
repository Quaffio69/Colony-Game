using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core;

/// <summary>
/// Dev mode (main menu, single-player games): build anything to try it. Every blueprint (room parts, walls, floors,
/// roofs, decks, stairs, racks, waterworks, houses) stands the tick it is placed, with no materials; work orders (dig,
/// carve, stairs, chop, gather) are done at once when <see cref="DevInstantOrders"/> is on; anyone runs any recipe
/// (no schooling needed); and <see cref="Commands.DevSpawn"/> drops any item, machine or colonist. Switched by
/// <see cref="Commands.SetDevMode"/>, so it replays from saves like every other order.
/// </summary>
public sealed partial class ColonySim
{
    /// <summary>Dev mode is on: construction is instant and free, recipes need no schooling.</summary>
    public bool DevMode { get; private set; }
    /// <summary>In dev mode, work orders are carried out the tick they are given (top level first).</summary>
    public bool DevInstantOrders { get; private set; }
    /// <summary>Most orders dev mode carries out in one tick (a big box finishes over a few ticks, not in one hitch).</summary>
    public const int DevOrdersPerTick = 4096;

    internal void SetDevMode(bool on, bool instantOrders)
    {
        DevMode = on;
        DevInstantOrders = on && instantOrders;
        Rooms.InstantBuild = on;
        if (!on) return;
        foreach (var room in Rooms.All)
            foreach (var part in room.Parts.ToList())
                if (!part.Built && part.BuilderId < 0) Rooms.CompleteConstruction(part);
    }

    /// <summary>Dev mode's share of a tick (after the commands, before colonists act): finish what was planned.</summary>
    private void DevStep()
    {
        if (!DevMode) return;
        if (Constructions.Count > 0)
            foreach (var bp in Constructions.All.ToList())
            {
                if (bp.BuilderId >= 0 || bp.Removed) continue; // someone is at it: let them finish
                if (bp.Kind == StructureKind.Wall && Occupied(bp.Target)) continue; // never wall anyone in
                CompleteConstruction(bp);
            }
        if (DevInstantOrders && Designations.UnclaimedCount > 0)
        {
            var due = new List<TilePos>();
            foreach (var p in Designations.All.Keys)
                if (!Designations.IsClaimed(p)) due.Add(p);
            due.Sort((p, q) => q.Z != p.Z ? q.Z.CompareTo(p.Z) : p.Y != q.Y ? p.Y.CompareTo(q.Y) : p.X.CompareTo(q.X)); // top down, like miners
            for (int i = 0; i < due.Count && i < DevOrdersPerTick; i++) CompleteDesignation(due[i]);
        }
    }

    private bool Occupied(TilePos cell)
    {
        foreach (var c in _colonists)
            if (c.Position == cell) return true;
        return false;
    }

    /// <summary>Dev mode: <paramref name="count"/> of a catalog item at a cell (machines and workers roll out, an
    /// education level is refused), or "colonist" for new settlers.</summary>
    internal string DevSpawn(TilePos at, string id, int count, int faction)
    {
        count = Math.Clamp(count, 1, 1000);
        if (id == "colonist")
        {
            int n = Math.Min(count, 20);
            for (int i = 0; i < n; i++)
                AddColonist(Agents.NameGenerator.Generate(unchecked((int)Tick * 41 + 3), ColonistsEver), at, faction: faction);
            return $"Dev: {n} {(n == 1 ? "colonist" : "colonists")}";
        }
        if (!Catalog.TryItem(id, out var type)) return $"Dev: no item '{id}'";
        var def = type.Def;
        switch (def.Kind)
        {
            case ItemKind.Education:
                return $"Dev: {def.Name} is taught, not made";
            case ItemKind.Machine:
            {
                int n = Math.Min(count, 10);
                for (int i = 0; i < n; i++) AddMachine(Agents.MachineInfo.Of(def.Id), at, faction);
                return $"Dev: {n} × {def.Name}";
            }
            case ItemKind.Worker:
            {
                int n = Math.Min(count, 20);
                for (int i = 0; i < n; i++)
                {
                    int k = _colonists.Count(c => c.IsAutomaton && !c.IsMachine) + _arriving.Count(c => c.IsAutomaton && !c.IsMachine) + 1;
                    AddColonist($"{def.Name} {k}", at, automaton: true, faction: faction);
                }
                return $"Dev: {n} × {def.Name}";
            }
        }
        Items.Drop(at, type, count);
        return $"Dev: {count} {def.Name.ToLowerInvariant()}";
    }
}
