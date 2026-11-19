using Colony.Core.World;

namespace Colony.Core.Fluids;

/// <summary>A built water wheel: power from the water flowing out of its cell.</summary>
public sealed class WaterWheel
{
    public required TilePos Cell { get; init; }
    public int Facing { get; init; }
    /// <summary>Power made during the last water step.</summary>
    public float Power { get; internal set; }
}

/// <summary>A built pump: lifts water from its cell to the cell ahead (<see cref="Facing"/>) and one level up.</summary>
public sealed class Pump
{
    public required TilePos Cell { get; init; }
    public int Facing { get; init; }
    public TilePos Outlet => Cell.Offset(Waterworks.Dx(Facing), Waterworks.Dy(Facing), 1);
    /// <summary>Water moved during the last water step.</summary>
    public int Pumped { get; internal set; }
}

/// <summary>
/// Water wheels, pumps and the power between them. A wheel makes power in proportion to the water flowing out
/// of its cell each step (so a wheel in a fast river or under a dam's spillway makes far more than one in a
/// pond), capped per wheel. For now power is one colony-wide pool: every pump runs at the share of its demand
/// that the wheels cover. (Axles and separate networks are the natural next step.)
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

    public IReadOnlyList<WaterWheel> Wheels => _wheels;
    public IReadOnlyList<Pump> Pumps => _pumps;
    public int Version { get; private set; }
    public float Generated { get; private set; }
    public float Demand { get; private set; }
    /// <summary>Share of pump demand the wheels cover (0..1).</summary>
    public float Supply { get; private set; }

    public bool Occupied(TilePos p) => _byCell.ContainsKey(p);
    public object? At(TilePos p) => _byCell.GetValueOrDefault(p);

    public static int Dx(int facing) => facing switch { 0 => 1, 2 => -1, _ => 0 };
    public static int Dy(int facing) => facing switch { 1 => 1, 3 => -1, _ => 0 };

    public WaterWheel AddWheel(Water water, TilePos cell, int facing)
    {
        var w = new WaterWheel { Cell = cell, Facing = facing };
        _wheels.Add(w);
        _byCell[cell] = w;
        Version++;
        return w;
    }

    public Pump AddPump(TilePos cell, int facing)
    {
        var p = new Pump { Cell = cell, Facing = facing };
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

    /// <summary>After each water step: wheels read their flow, then pumps run on the power they make.</summary>
    internal void Tick(Water water, VoxelWorld world)
    {
        float generated = 0;
        foreach (var w in _wheels)
        {
            w.Power = Math.Min(MaxWheelPower, water.ThroughFlow(w.Cell) / (float)FlowPerPower);
            generated += w.Power;
        }
        Generated = generated;
        Demand = _pumps.Count * PumpPower;
        Supply = Demand > 0 ? Math.Min(1f, generated / Demand) : 0f;
        foreach (var p in _pumps)
            p.Pumped = water.Move(p.Cell, p.Outlet, (int)(PumpRate * Supply));
    }

    // ---- Snapshot -----------------------------------------------------------------------------------------

    internal void SnapSave(BinaryWriter w)
    {
        w.Write(_wheels.Count);
        foreach (var wh in _wheels) { Net.SnapIO.Write(w, wh.Cell); w.Write(wh.Facing); w.Write(wh.Power); }
        w.Write(_pumps.Count);
        foreach (var p in _pumps) { Net.SnapIO.Write(w, p.Cell); w.Write(p.Facing); w.Write(p.Pumped); }
        w.Write(Version); w.Write(Generated); w.Write(Demand); w.Write(Supply);
    }

    internal void SnapLoad(BinaryReader r)
    {
        int nw = r.ReadInt32();
        for (int i = 0; i < nw; i++)
        {
            var wh = new WaterWheel { Cell = Net.SnapIO.ReadPos(r), Facing = r.ReadInt32() };
            wh.Power = r.ReadSingle();
            _wheels.Add(wh);
            _byCell[wh.Cell] = wh;
        }
        int np = r.ReadInt32();
        for (int i = 0; i < np; i++)
        {
            var p = new Pump { Cell = Net.SnapIO.ReadPos(r), Facing = r.ReadInt32() };
            p.Pumped = r.ReadInt32();
            _pumps.Add(p);
            _byCell[p.Cell] = p;
        }
        Version = r.ReadInt32(); Generated = r.ReadSingle(); Demand = r.ReadSingle(); Supply = r.ReadSingle();
    }
}
