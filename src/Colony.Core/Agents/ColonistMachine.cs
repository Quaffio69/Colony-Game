using Colony.Core.Items;
using Colony.Core.World;

namespace Colony.Core.Agents;

/// <summary>Heavy machinery: steam-driven workers built at the artificer's assembly frame.</summary>
public enum MachineKind : byte
{
    None,
    /// <summary>A tracked steam excavator with a boom, stick and bucket: takes only dig and stair orders, and works them several times faster.</summary>
    Excavator,
    /// <summary>A steam power wagon: hauls only, carrying many loads a trip and unloading into the stockpile cells round its stop.</summary>
    PowerWagon,
}

public static class MachineInfo
{
    /// <summary>The machine a catalog item becomes (by id), or None.</summary>
    public static MachineKind Of(string itemId) => itemId switch
    {
        "excavator" => MachineKind.Excavator,
        "power_wagon" => MachineKind.PowerWagon,
        _ => MachineKind.None,
    };

    public static string Name(MachineKind k) => k switch
    {
        MachineKind.Excavator => "Excavator",
        MachineKind.PowerWagon => "Power wagon",
        _ => "",
    };

    /// <summary>The one work type a machine does.</summary>
    public static Work WorkOf(MachineKind k) => k == MachineKind.Excavator ? Work.Mining : Work.Hauling;

    /// <summary>Ticks to walk a step, as a multiple of a colonist's: the excavator crawls on its tracks, the wagon rolls.</summary>
    public static float StepScale(MachineKind k) => k switch
    {
        MachineKind.Excavator => Balance.ExcavatorStepScale,
        MachineKind.PowerWagon => Balance.WagonStepScale,
        _ => 1f,
    };
}

/// <summary>Machines: one trade each, fuel (charcoal) instead of food, and the power wagon's big loads.</summary>
public sealed partial class Colonist
{
    /// <summary>What machine this is (None for people and automatons). Machines are automatons too: no needs, homes or families.</summary>
    public MachineKind Machine { get; internal set; }

    public bool IsMachine => Machine != MachineKind.None;

    /// <summary>Fuel in the firebox, in units of work (see <see cref="Balance.FuelPerCharcoal"/>); a machine at 0 stands idle until refuelled.</summary>
    public int Fuel { get; internal set; }

    /// <summary>Fuel when full.</summary>
    public static int FuelCapacity => Balance.FuelCharcoalLoad * Balance.FuelPerCharcoal;

    public bool LowOnFuel => IsMachine && Fuel < FuelCapacity / 4;
    public bool OutOfFuel => IsMachine && Fuel <= 0;

    /// <summary>Sets a new machine up: full of fuel, its one trade at first priority, nothing else.</summary>
    internal void SetUpMachine()
    {
        Fuel = FuelCapacity;
        for (int w = 0; w < WorkTypes; w++) _priority[w] = 0;
        _priority[(int)MachineInfo.WorkOf(Machine)] = 1;
    }

    /// <summary>Burns fuel for work done (only while needs run: sandboxes and tests are free).</summary>
    private void BurnFuel(ColonySim sim, int amount)
    {
        if (IsMachine && sim.NeedsEnabled) Fuel = Math.Max(0, Fuel - amount);
    }

    private int _fuelRetry;

    /// <summary>
    /// Heads for the nearest charcoal it may use (not in a factory's input crate) and takes enough to fill the firebox; it
    /// goes as a meal does (<see cref="ColonistTask.Eat"/>), and <see cref="FinishMeal"/> stokes instead of eating.
    /// </summary>
    private bool TryRefuel(ColonySim sim)
    {
        if (!sim.NeedsEnabled || _fuelRetry > 0 || sim.PathBudgetExhausted) return false;
        int component = sim.Connectivity.ComponentOf(Position);
        _fuelRetry = 100;
        if (component < 0) return false;
        var fuel = ItemType.Charcoal;
        var options = new List<(TilePos Cell, ItemPile Pile, int Dist)>();
        foreach (var (p, pile) in sim.Items.All)
        {
            if (pile.Type != fuel || pile.Available <= 0 || !Needs.Usable(sim, p, Faction)) continue;
            if (sim.Connectivity.ComponentOf(p) != component) continue;
            options.Add((p, pile, Math.Abs(p.X - Position.X) + Math.Abs(p.Y - Position.Y) + 2 * Math.Abs(p.Z - Position.Z)));
        }
        if (options.Count == 0) return false;
        options.Sort((a, b) => a.Dist != b.Dist ? a.Dist.CompareTo(b.Dist) : CompareCells(a.Cell, b.Cell));
        foreach (var (cell, pile, _) in options.Take(CandidatesPerSearch))
        {
            var path = Route(sim, ReachableAround(sim, cell, component), cell);
            if (path is null) continue;
            int units = Math.Clamp((FuelCapacity - Fuel) / Balance.FuelPerCharcoal, 1, pile.Available);
            if (!sim.Items.Reserve(cell, units)) continue;
            _meal = (cell, fuel, units);
            _fuelRetry = 0;
            Begin(ColonistTask.Eat, cell, path);
            return true;
        }
        return false;
    }

    // ---- The power wagon's big loads ---------------------------------------------------------------------------

    /// <summary>Room the wagon reserved in stockpile cells round its stop (beyond the plan's destination).</summary>
    private readonly List<(TilePos Cell, int Amount)> _spill = new();
    /// <summary>Units of the spill still reserved at the haul's source pile (until pickup).</summary>
    private int _spillFromSource;

    /// <summary>Units a load can be: a colonist's carry, times the wagon's.</summary>
    private int CarryCapacity(ItemType type) => ItemInfo.CarryLimit(type) * (Machine == MachineKind.PowerWagon ? Balance.WagonLoads : 1);

    /// <summary>How far round its stop a wagon unloads, and round its pickup it gathers more (Chebyshev tiles).</summary>
    public const int WagonReach = 3;

    /// <summary>
    /// Reserves room for up to <paramref name="want"/> more units in the stockpile cells round the haul's destination (same
    /// level, same colony, nearest first), adding to the spill. Returns how many it found room for.
    /// </summary>
    private int ReserveSpillRoom(ColonySim sim, int want, int component)
    {
        int owner = sim.Stockpiles.OwnerOf(_haul.Destination), got = 0;
        for (int r = 1; r <= WagonReach && got < want; r++)
        for (int dy = -r; dy <= r && got < want; dy++)
        for (int dx = -r; dx <= r && got < want; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            var c = _haul.Destination.Offset(dx, dy, 0);
            if (!sim.Stockpiles.Contains(c) || sim.Stockpiles.IsDepot(c) || sim.Stockpiles.OwnerOf(c) != owner) continue;
            if (sim.Rooms.PartAt(c) != null || sim.Constructions.At(c) != null || sim.Connectivity.ComponentOf(c) != component) continue;
            int n = Math.Min(want - got, sim.Items.RoomAt(c, _haul.Type));
            if (n <= 0 || !sim.Items.ReserveIncoming(c, _haul.Type, n)) continue;
            _spill.Add((c, n));
            got += n;
        }
        return got;
    }

    /// <summary>
    /// A wagon's haul to a stockpile grows past one cell's room: more of the source pile, with room reserved in the cells
    /// round the destination, up to its capacity. (More is gathered from piles round the source at pickup.)
    /// </summary>
    private void ExtendLoad(ColonySim sim, int component)
    {
        _spill.Clear();
        _spillFromSource = 0;
        if (Machine != MachineKind.PowerWagon || !sim.Stockpiles.Contains(_haul.Destination)) return;
        if (!sim.Items.TryGet(_haul.Source, out var pile)) return;
        int want = Math.Min(CarryCapacity(_haul.Type) - _haul.Amount, pile.Available);
        if (want <= 0) return;
        int room = ReserveSpillRoom(sim, want, component);
        if (room > 0 && sim.Items.Reserve(_haul.Source, room)) _spillFromSource = room;
        else ReleaseSpill(sim, source: false);
    }

    /// <summary>Gives back the room a wagon reserved round its stop (and its source units, before pickup).</summary>
    private void ReleaseSpill(ColonySim sim, bool source)
    {
        foreach (var (c, n) in _spill) sim.Items.ReleaseIncoming(c, n);
        if (source && _spillFromSource > 0) sim.Items.Unreserve(_haul.Source, _spillFromSource);
        _spill.Clear();
        _spillFromSource = 0;
    }

    /// <summary>
    /// At pickup: takes the spill's units from the source (a shortfall gives back room, last cells first), then gathers
    /// more of the same from loose piles round it into new room, up to the wagon's capacity. Returns the units added.
    /// </summary>
    private int PickUpSpill(ColonySim sim, int carrying, int component)
    {
        if (Machine != MachineKind.PowerWagon) return 0;
        int taken = _spillFromSource > 0 ? sim.Items.TakeReserved(_haul.Source, _spillFromSource) : 0;
        for (int shortBy = _spillFromSource - taken, i = _spill.Count - 1; shortBy > 0 && i >= 0; i--)
        {
            var (c, n) = _spill[i];
            int back = Math.Min(n, shortBy);
            sim.Items.ReleaseIncoming(c, back);
            _spill[i] = (c, n - back);
            shortBy -= back;
        }
        _spill.RemoveAll(s => s.Amount == 0);
        _spillFromSource = 0;
        if (!sim.Stockpiles.Contains(_haul.Destination)) return taken;

        int capacity = CarryCapacity(_haul.Type);
        for (int dy = -WagonReach; dy <= WagonReach; dy++)
        for (int dx = -WagonReach; dx <= WagonReach; dx++)
        {
            int load = carrying + taken;
            if (load >= capacity) return taken;
            var c = _haul.Source.Offset(dx, dy, 0);
            if (c == _haul.Source || !sim.Items.TryGet(c, out var pile) || pile.Type != _haul.Type || pile.Available <= 0) continue;
            if (sim.Stockpiles.Contains(c) || sim.Rooms.PartAt(c) != null || sim.Constructions.At(c) != null || sim.Connectivity.ComponentOf(c) != component) continue;
            int room = ReserveSpillRoom(sim, Math.Min(pile.Available, capacity - load), component);
            if (room == 0) return taken; // nowhere left to put more
            if (!sim.Items.Reserve(c, room))
            {
                for (int i = _spill.Count - 1, give = room; give > 0 && i >= 0; i--)
                {
                    var (sc, n) = _spill[i];
                    int back = Math.Min(n, give);
                    sim.Items.ReleaseIncoming(sc, back);
                    _spill[i] = (sc, n - back);
                    give -= back;
                }
                _spill.RemoveAll(s => s.Amount == 0);
                continue;
            }
            taken += sim.Items.TakeReserved(c, room);
        }
        return taken;
    }

    /// <summary>At the stop: fills the spill cells from the load (what doesn't fit stays carried and goes to the destination).</summary>
    private void DeliverSpill(ColonySim sim)
    {
        foreach (var (c, n) in _spill)
        {
            int put = Math.Min(n, CarryingCount);
            if (put < n) sim.Items.ReleaseIncoming(c, n - put); // Deliver releases what it is handed; the rest of the room goes back
            CarryingCount -= sim.Items.Deliver(c, CarryingType, put);
        }
        _spill.Clear();
    }
}
