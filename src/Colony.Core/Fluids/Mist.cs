using Colony.Core.World;

namespace Colony.Core.Fluids;

/// <summary>
/// Waterfall mist: a coarse field (cells of <see cref="Cell"/> x <see cref="Cell"/> columns, each with a density and the
/// level its mist hangs at) that the taller falls (<see cref="Water.Falls"/>, <see cref="MinDrop"/>+ levels) feed where
/// they land. Every <see cref="SpreadTicks"/> ticks (a second) it takes in what the falls feed, decays a little and
/// spreads to the four neighbouring cells, more of it downwind (<see cref="Wind"/>, veering slowly), hugging the ground:
/// it climbs a step, sinks off a ledge and never passes into rock. Densities add up, so big falls and many falls make
/// thick, wide banks of mist; when the falls stop it thins away within a minute or so. Colonists who spend time in it
/// are lifted (<see cref="Balance.MistMood"/>), and the game draws its clouds from the same field, so what the player
/// sees is what the colonists feel.
/// Cost: each water step reads the falls of one of <see cref="Bands"/> bands of block rows (only awake water blocks are
/// looked at; a fall's feed is re-measured every few seconds and stands meanwhile), and a spread touches only cells that
/// hold mist (a sorted list, so the order, and the outcome, are canonical). Integer arithmetic; saved in snapshots.
/// </summary>
public sealed class Mist
{
    /// <summary>Columns per mist cell, each way.</summary>
    public const int Cell = 4;
    /// <summary>Density of thick mist (the field holds up to <see cref="Max"/>).</summary>
    public const int Full = 1000;
    public const int Max = 4 * Full;
    /// <summary>Falls at least this tall (levels) throw up mist.</summary>
    public const float MinDrop = 1.5f;
    /// <summary>Water steps per spread (a second).</summary>
    public const int StepsPerSpread = 5;
    public const int SpreadTicks = StepsPerSpread * ColonySim.WaterInterval;
    /// <summary>The map's block rows are read for falls in this many bands, one per water step (each fall re-measured every
    /// 8 s: ~0.13 ms a step on a 2048 map with 2800 awake water blocks).</summary>
    public const int Bands = 40;
    /// <summary>Density a colonist has to stand in to enjoy it.</summary>
    public const int Felt = Full / 6;

    private readonly VoxelWorld _world;
    private readonly int _cw, _cd;
    private readonly int[] _density, _next;
    private readonly short[] _level, _nextLevel;
    /// <summary>Per cell: mist the falls landing in it feed each second, the strongest one's share and where it lands.</summary>
    private readonly int[] _feed, _feedTop;
    private readonly short[] _feedLevel;
    /// <summary>What each band's falls feed (cell, amount), taken back when the band is read again.</summary>
    private readonly List<(int Cell, int Amount)>[] _fed = new List<(int, int)>[Bands];
    private List<int> _active = new(), _spare = new(); // cells holding or fed mist, ascending after each spread
    private readonly bool[] _listed;
    private readonly List<Fall> _falls = new();
    private int _steps;

    public Mist(VoxelWorld world)
    {
        _world = world;
        _cw = (world.Width + Cell - 1) / Cell;
        _cd = (world.Depth + Cell - 1) / Cell;
        int n = _cw * _cd;
        _density = new int[n];
        _next = new int[n];
        _level = new short[n];
        _nextLevel = new short[n];
        _feed = new int[n];
        _feedTop = new int[n];
        _feedLevel = new short[n];
        _listed = new bool[n];
        for (int b = 0; b < Bands; b++) _fed[b] = new();
    }

    public int CellsWide => _cw;
    public int CellsDeep => _cd;
    /// <summary>Cells holding (or being fed) mist.</summary>
    public int ActiveCells => _active.Count;
    /// <summary>The sum of all mist (for tests and the state hash).</summary>
    public long Total { get { long t = 0; foreach (int i in _active) t += _density[i]; return t; } }

    /// <summary>Mist density (0..<see cref="Max"/>) of cell (cx, cy), and the level it hangs at (its floor).</summary>
    public (int Density, int Level) CellAt(int cx, int cy)
    {
        if ((uint)cx >= (uint)_cw || (uint)cy >= (uint)_cd) return (0, 0);
        int i = cy * _cw + cx;
        return (_density[i], _level[i]);
    }

    /// <summary>Mist at a tile: the density of its cell if the tile is within a few levels of where that mist hangs, else 0.</summary>
    public int At(TilePos p)
    {
        var (d, level) = CellAt(p.X / Cell, p.Y / Cell);
        return d > 0 && p.Z >= level - 1 && p.Z <= level + 3 ? d : 0;
    }

    /// <summary>
    /// The breeze the mist drifts on, (x, y) each -2..2: veers to a new heading every couple of minutes, by a hash of
    /// the time, so every peer has the same wind.
    /// </summary>
    public static (int X, int Y) Wind(long tick)
    {
        long period = tick / (ColonySim.TicksPerSecond * 150);
        float a = Procedural.Noise.Hash((int)period, 0x3157, 7, 0xB1EE) * 8f;
        return ((int)a) switch
        {
            0 => (2, 0), 1 => (1, 1), 2 => (0, 2), 3 => (-1, 1), 4 => (-2, 0), 5 => (-1, -1), 6 => (0, -2), _ => (1, -1),
        };
    }

    /// <summary>After each water step: re-read one band's falls; every <see cref="StepsPerSpread"/>th step, spread.</summary>
    internal void Sample(ColonySim sim)
    {
        int band = _steps % Bands;
        var fed = _fed[band];
        foreach (var (i, amount) in fed) { _feed[i] -= amount; _feedTop[i] = 0; }
        fed.Clear();
        int rows = (_world.Depth + Chunk.Size - 1) >> Chunk.Shift;
        int r0 = band * rows / Bands, r1 = (band + 1) * rows / Bands;
        if (r1 > r0)
        {
            _falls.Clear();
            sim.Water.Falls(0, r0 << Chunk.Shift, _world.Width - 1, (r1 << Chunk.Shift) - 1, _falls, MinDrop, int.MaxValue);
            foreach (var f in _falls) Feed(f, fed);
        }
        if (++_steps % StepsPerSpread != 0) return;
        Spread(sim.Tick);
        Enjoy(sim);
    }

    /// <summary>A fall feeds the cell it lands in, by how hard and how far it falls.</summary>
    private void Feed(Fall f, List<(int, int)> fed)
    {
        int x = f.X + (f.Side == 0 ? 1 : f.Side == 1 ? -1 : 0), y = f.Y + (f.Side == 2 ? 1 : f.Side == 3 ? -1 : 0);
        if ((uint)x >= (uint)_world.Width || (uint)y >= (uint)_world.Depth) return;
        int i = y / Cell * _cw + x / Cell;
        float strength = Math.Clamp(f.Rate * 12f, 0.05f, 1f);
        // Square root: a trickle still hazes its foot a little, a river over a cliff raises banks of it.
        int amount = (int)(MathF.Sqrt(strength) * Math.Min(f.Top - f.Bottom, 12f) * Balance.MistPerFallLevel);
        if (amount <= 0) return;
        if (amount > _feedTop[i])
        {
            _feedTop[i] = amount; // the cell's mist hangs where its strongest fall lands
            _feedLevel[i] = (short)Math.Max(0, (int)f.Bottom);
        }
        _feed[i] += amount;
        fed.Add((i, amount));
        List(i, _active);
    }

    private void List(int i, List<int> into)
    {
        if (_listed[i]) return;
        _listed[i] = true;
        into.Add(i);
    }

    private static readonly (int Dx, int Dy)[] Sides = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>
    /// One second of mist: the feed added, a <see cref="Balance.MistDecay"/> share lost, <see cref="Balance.MistSpread"/>
    /// of the rest passed to the neighbours (weighted by the wind), settling at their ground.
    /// </summary>
    private void Spread(long tick)
    {
        if (_active.Count == 0) return;
        _active.Sort(); // newly fed cells were appended
        var (wx, wy) = Wind(tick);
        var next = _spare;
        next.Clear();
        foreach (int i in _active) _listed[i] = false;
        foreach (int i in _active)
        {
            int d = Math.Min(Max, _density[i] + _feed[i]);
            d -= d * Balance.MistDecay / 1000 + 2; // the +2 lets thin wisps die out
            int level = _feed[i] > 0 ? _feedLevel[i] : _level[i];
            if (d <= 0)
            {
                if (_feed[i] > 0) { _nextLevel[i] = (short)level; List(i, next); } // still fed: stays listed
                continue;
            }
            int cx = i % _cw, cy = i / _cw;
            int given = d * Balance.MistSpread / 1000, kept = d - given;
            foreach (var (dx, dy) in Sides)
            {
                int share = given * (4 + wx * dx + wy * dy) / 16; // the four weights sum to 16
                int nx = cx + dx, ny = cy + dy;
                int to = share > 0 && (uint)nx < (uint)_cw && (uint)ny < (uint)_cd ? GroundAt(nx, ny, level) : -1;
                if (to < 0) { kept += share; continue; }
                int j = ny * _cw + nx;
                if (_next[j] == 0) _nextLevel[j] = (short)to;
                _next[j] += share;
                List(j, next);
            }
            if (_next[i] == 0 || _feed[i] > 0) _nextLevel[i] = (short)level;
            _next[i] += kept;
            List(i, next);
        }
        foreach (int i in _active) _density[i] = 0;
        int keep = 0;
        for (int k = 0; k < next.Count; k++)
        {
            int i = next[k];
            int d = Math.Min(Max, _next[i]);
            _next[i] = 0;
            if (d <= 0 && _feed[i] <= 0) { _listed[i] = false; continue; }
            _density[i] = Math.Max(0, d);
            _level[i] = _nextLevel[i];
            next[keep++] = i;
        }
        next.RemoveRange(keep, next.Count - keep);
        next.Sort();
        _spare = _active;
        _active = next;
    }

    /// <summary>
    /// The level mist moving at <paramref name="level"/> settles at over cell (cx, cy), by its middle column: up one if
    /// that is rock (a step), down one if the level below is open (a ledge); -1 where it can't go (a wall).
    /// </summary>
    private int GroundAt(int cx, int cy, int level)
    {
        int x = Math.Min(cx * Cell + Cell / 2, _world.Width - 1), y = Math.Min(cy * Cell + Cell / 2, _world.Depth - 1);
        if (level >= _world.Height) return -1;
        if (_world.IsSolid(x, y, level))
            return level + 1 < _world.Height && !_world.IsSolid(x, y, level + 1) ? level + 1 : -1;
        return level > 0 && !_world.IsSolid(x, y, level - 1) ? level - 1 : level;
    }

    /// <summary>Colonists standing in mist count the time (<see cref="Colonist.MistBonus"/>).</summary>
    private void Enjoy(ColonySim sim)
    {
        if (_active.Count == 0) return;
        foreach (var c in sim.Colonists)
            if (!c.IsAutomaton && At(c.Position) >= Felt) c.MistSweeps++;
    }

    internal void SnapSave(BinaryWriter w)
    {
        w.Write(_steps);
        w.Write(_active.Count);
        foreach (int i in _active) { w.Write(i); w.Write(_density[i]); w.Write(_level[i]); w.Write(_feedTop[i]); w.Write(_feedLevel[i]); }
        foreach (var fed in _fed)
        {
            w.Write(fed.Count);
            foreach (var (i, amount) in fed) { w.Write(i); w.Write(amount); }
        }
    }

    internal void SnapLoad(BinaryReader r)
    {
        _steps = r.ReadInt32();
        int n = r.ReadInt32();
        for (int k = 0; k < n; k++)
        {
            int i = r.ReadInt32();
            _density[i] = r.ReadInt32(); _level[i] = r.ReadInt16(); _feedTop[i] = r.ReadInt32(); _feedLevel[i] = r.ReadInt16();
            List(i, _active);
        }
        foreach (var fed in _fed)
        {
            int m = r.ReadInt32();
            for (int k = 0; k < m; k++)
            {
                int i = r.ReadInt32(), amount = r.ReadInt32();
                fed.Add((i, amount));
                _feed[i] += amount;
            }
        }
    }
}
