using Colony.Core.World;

namespace Colony.Core.Fluids;

/// <summary>A built water wheel: power from the water flowing out of its cell, for the colony that built it.</summary>
public sealed class WaterWheel
{
    public required TilePos Cell { get; init; }
    public int Facing { get; init; }
    public int Faction { get; init; }
    /// <summary>Power made during the last water step.</summary>
    public float Power { get; internal set; }
}

/// <summary>A built pump: lifts water from its cell to the cell ahead (<see cref="Facing"/>) and one level up.</summary>
public sealed class Pump
{
    public required TilePos Cell { get; init; }
    public int Facing { get; init; }
    public int Faction { get; init; }
    public TilePos Outlet => Cell.Offset(Waterworks.Dx(Facing), Waterworks.Dy(Facing), 1);
    /// <summary>Water moved during the last water step.</summary>
    public int Pumped { get; internal set; }
}

/// <summary>
/// Water wheels, pumps and the power between them. A wheel makes power in proportion to the water flowing out
/// of its cell each step (so a wheel in a fast river or under a dam's spillway makes far more than one in a
/// pond), capped per wheel. Power is one pool per colony: its wheels feed its pumps and conveyor belts, which all
/// run at the share of their demand the wheels cover. (Axles and separate networks are the natural next step.)
/// </summary>
public sealed class Waterworks
{
    /// <summary>Water flow (per water step) that makes one unit of power.</summary>
    public const int FlowPerPower = 20;
    public const float MaxWheelPower = 20f;
    public const float PumpPower = 5f;
    /// <summary>Water a fully powered pump moves per water step.</summary>
    public const int PumpRate = 150;

    private readonly List<WaterWheel> _wheels = new();
    private readonly List<Pump> _pumps = new();
    private readonly Dictionary<TilePos, object> _byCell = new();
    private float[] _generated = [], _demand = [], _supply = [];

    public IReadOnlyList<WaterWheel> Wheels => _wheels;
    public IReadOnlyList<Pump> Pumps => _pumps;
    public int Version { get; private set; }
    /// <summary>Power made, wanted, and the share covered, over every colony (see <see cref="GeneratedOf"/> for one).</summary>
    public float Generated { get; private set; }
    public float Demand { get; private set; }
    public float Supply { get; private set; }

    /// <summary>Power a colony's wheels made in the last water step.</summary>
    public float GeneratedOf(int faction) => faction >= 0 && faction < _generated.Length ? _generated[faction] : 0;
    /// <summary>Power a colony's pumps and belts want.</summary>
    public float DemandOf(int faction) => faction >= 0 && faction < _demand.Length ? _demand[faction] : 0;
    /// <summary>Share (0..1) of a colony's demand its wheels cover.</summary>
    public float SupplyOf(int faction) => faction >= 0 && faction < _supply.Length ? _supply[faction] : 0;

    /// <summary>What else draws on a colony's power (its conveyor belts; set by <see cref="ColonySim"/>).</summary>
    public Func<int, float>? OtherDemand { get; set; }

    public bool Occupied(TilePos p) => _byCell.ContainsKey(p);
    public object? At(TilePos p) => _byCell.GetValueOrDefault(p);

    public static int Dx(int facing) => facing switch { 0 => 1, 2 => -1, _ => 0 };
    public static int Dy(int facing) => facing switch { 1 => 1, 3 => -1, _ => 0 };

    public WaterWheel AddWheel(Water water, TilePos cell, int facing, int faction = 0)
    {
        var w = new WaterWheel { Cell = cell, Facing = facing, Faction = faction };
        _wheels.Add(w);
        _byCell[cell] = w;
        Version++;
        return w;
    }

    public Pump AddPump(TilePos cell, int facing, int faction = 0)
    {
        var p = new Pump { Cell = cell, Facing = facing, Faction = faction };
        _pumps.Add(p);
        _byCell[cell] = p;
        Version++;
        return p;
    }

    /// <summary>Removes whatever stands in the cell. Returns what it was (or null).</summary>
    public object? Remove(TilePos cell)
    {
        if (!_byCell.Remove(cell, out var thing)) return null;
        if (thing is WaterWheel w) _wheels.Remove(w);
        if (thing is Pump p) _pumps.Remove(p);
        Version++;
        return thing;
    }

    /// <summary>After each water step: wheels read their flow, then each colony's pumps run on the power its wheels make.</summary>
    internal void Tick(Water water, VoxelWorld world, int factions)
    {
        factions = Math.Max(1, factions);
        if (_generated.Length != factions)
        {
            _generated = new float[factions];
            _demand = new float[factions];
            _supply = new float[factions];
        }
        Array.Clear(_generated);
        for (int f = 0; f < factions; f++) _demand[f] = OtherDemand?.Invoke(f) ?? 0;
        foreach (var w in _wheels)
        {
            w.Power = Math.Min(MaxWheelPower, water.ThroughFlow(w.Cell) / (float)FlowPerPower);
            if (w.Faction < factions) _generated[w.Faction] += w.Power;
        }
        foreach (var p in _pumps)
            if (p.Faction < factions) _demand[p.Faction] += PumpPower;
        float generated = 0, demand = 0;
        for (int f = 0; f < factions; f++)
        {
            _supply[f] = _demand[f] > 0 ? Math.Min(1f, _generated[f] / _demand[f]) : 0f;
            generated += _generated[f];
            demand += _demand[f];
        }
        Generated = generated;
        Demand = demand;
        Supply = demand > 0 ? Math.Min(1f, generated / demand) : 0f;
        foreach (var p in _pumps)
            p.Pumped = water.Move(p.Cell, p.Outlet, (int)(PumpRate * SupplyOf(p.Faction)));
    }

    // ---- Snapshot -----------------------------------------------------------------------------------------

    internal void SnapSave(BinaryWriter w)
    {
        w.Write(_wheels.Count);
        foreach (var wh in _wheels) { Net.SnapIO.Write(w, wh.Cell); w.Write(wh.Facing); w.Write(wh.Power); w.Write(wh.Faction); }
        w.Write(_pumps.Count);
        foreach (var p in _pumps) { Net.SnapIO.Write(w, p.Cell); w.Write(p.Facing); w.Write(p.Pumped); w.Write(p.Faction); }
        w.Write(Version); w.Write(Generated); w.Write(Demand); w.Write(Supply);
        w.Write(_generated.Length);
        for (int f = 0; f < _generated.Length; f++) { w.Write(_generated[f]); w.Write(_demand[f]); w.Write(_supply[f]); }
    }

    internal void SnapLoad(BinaryReader r)
    {
        bool factioned = !Net.SnapIO.Before(15); // 15: power per colony
        int nw = r.ReadInt32();
        for (int i = 0; i < nw; i++)
        {
            var cell = Net.SnapIO.ReadPos(r);
            int facing = r.ReadInt32();
            float power = r.ReadSingle();
            var wh = new WaterWheel { Cell = cell, Facing = facing, Faction = factioned ? r.ReadInt32() : 0 };
            wh.Power = power;
            _wheels.Add(wh);
            _byCell[wh.Cell] = wh;
        }
        int np = r.ReadInt32();
        for (int i = 0; i < np; i++)
        {
            var cell = Net.SnapIO.ReadPos(r);
            int facing = r.ReadInt32(), pumped = r.ReadInt32();
            var p = new Pump { Cell = cell, Facing = facing, Faction = factioned ? r.ReadInt32() : 0 };
            p.Pumped = pumped;
            _pumps.Add(p);
            _byCell[p.Cell] = p;
        }
        Version = r.ReadInt32(); Generated = r.ReadSingle(); Demand = r.ReadSingle(); Supply = r.ReadSingle();
        if (!factioned)
        {
            _generated = [Generated];
            _demand = [Demand];
            _supply = [Supply];
            return;
        }
        int n = r.ReadInt32();
        _generated = new float[n];
        _demand = new float[n];
        _supply = new float[n];
        for (int f = 0; f < n; f++) { _generated[f] = r.ReadSingle(); _demand[f] = r.ReadSingle(); _supply[f] = r.ReadSingle(); }
    }
}
