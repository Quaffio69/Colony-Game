using Colony.Core.World;

namespace Colony.Core.Transport;

/// <summary>
/// Plans a conveyor's route between two ends, across whatever lies between: an A* search over open cells by the belt's
/// own steps (flat runs, ramps up and down a level, lifts straight up or down), so it follows the ground over hills, climbs
/// cliffs and shafts on lifts, bridges rivers and gaps on trestles, goes round rock, trees, stockpiles and workshop parts
/// (or up and over other belts), and slips through walls by a hatch. It prefers belts that stay low, run straight and
/// keep out of water. Read-only on the sim, run by the game while the player points (the sim checks the route again when
/// the command comes in). A <see cref="Search"/> can run a slice at a time, so the game spreads a long one over frames.
/// </summary>
public static class ConveyorRouter
{
    /// <summary>Most cells one route search looks at before giving up.</summary>
    public const int DefaultBudget = 60_000;

    private const float Flat = 1f, Ramp = 1.35f, Lift = 1.7f, Turn = 0.45f, PerLevelUp = 0.3f, Hatch = 2.5f, Wet = 0.6f;
    private const float Greed = 1.1f; // weighted A*: a little longer than the best route, found far faster

    private static readonly (int X, int Y)[] Dirs = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    /// <summary>
    /// The cheapest route found from any of <paramref name="starts"/> to any of <paramref name="goals"/> (the cells the
    /// belt's first and last tiles may take: <see cref="Conveyors.EndCells"/>), or null if none within the budget, all at
    /// once. <paramref name="nodes"/> is how many cells were searched.
    /// </summary>
    public static List<TilePos>? Route(ColonySim sim, IReadOnlyList<TilePos> starts, IReadOnlyList<TilePos> goals, out int nodes, int budget = DefaultBudget)
    {
        var search = new Search(sim, starts, goals, budget);
        while (search.Run(budget) == null) { }
        nodes = search.Nodes;
        return search.Result;
    }

    /// <summary>One route search, run a slice of cells at a time (<see cref="Run"/>) until it finds a route or gives up.</summary>
    public sealed class Search
    {
        private readonly ColonySim _sim;
        private readonly HashSet<TilePos> _goals;
        private readonly int _budget, _gx0, _gx1, _gy0, _gy1, _gz0, _gz1;
        private readonly Dictionary<TilePos, bool> _free = new();
        private readonly Dictionary<TilePos, float> _g = new();
        private readonly Dictionary<TilePos, TilePos> _parent = new();
        private readonly Dictionary<TilePos, int> _heading = new(); // horizontal direction arrived by (-1: none yet / a lift)
        private readonly PriorityQueue<TilePos, float> _open = new();
        private readonly HashSet<TilePos> _closed = new();
        private bool? _done;

        public int Nodes { get; private set; }
        /// <summary>The route once found (null while searching, or if there is none).</summary>
        public List<TilePos>? Result { get; private set; }
        /// <summary>True once found, false once given up, null while still searching.</summary>
        public bool? Done => _done;

        public Search(ColonySim sim, IReadOnlyList<TilePos> starts, IReadOnlyList<TilePos> goals, int budget = DefaultBudget)
        {
            _sim = sim;
            _budget = budget;
            _goals = new HashSet<TilePos>(goals);
            if (starts.Count == 0 || goals.Count == 0)
            {
                _done = false;
                return;
            }
            (_gx0, _gx1, _gy0, _gy1, _gz0, _gz1) = (goals.Min(g => g.X), goals.Max(g => g.X), goals.Min(g => g.Y), goals.Max(g => g.Y), goals.Min(g => g.Z), goals.Max(g => g.Z));
            foreach (var s in starts)
            {
                if (!Ok(s) || _g.ContainsKey(s)) continue;
                _g[s] = 0;
                _heading[s] = -1;
                _open.Enqueue(s, H(s));
            }
            if (_open.Count == 0) _done = false;
        }

        private float H(TilePos c)
        {
            int dx = c.X < _gx0 ? _gx0 - c.X : c.X > _gx1 ? c.X - _gx1 : 0;
            int dy = c.Y < _gy0 ? _gy0 - c.Y : c.Y > _gy1 ? c.Y - _gy1 : 0;
            int dz = c.Z < _gz0 ? _gz0 - c.Z : c.Z > _gz1 ? c.Z - _gz1 : 0;
            int h = dx + dy;
            // Levels come cheapest as ramps riding horizontal steps; any left over need lifts.
            return Greed * (h + (dz > h ? (dz - h) * Lift + h * (Ramp - Flat) : dz * (Ramp - Flat)));
        }

        private bool Ok(TilePos c)
        {
            if (_free.TryGetValue(c, out bool ok)) return ok;
            ok = Conveyors.Free(_sim, c) is null;
            _free[c] = ok;
            return ok;
        }

        /// <summary>Searches up to <paramref name="slice"/> more cells. Returns <see cref="Done"/>.</summary>
        public bool? Run(int slice)
        {
            if (_done is not null) return _done;
            int stop = Nodes + slice;
            while (_open.TryDequeue(out var c, out _))
            {
                if (!_closed.Add(c)) continue;
                if (_goals.Contains(c))
                {
                    Result = Path(c);
                    return _done = true;
                }
                if (++Nodes > _budget) return _done = false;
                Expand(c);
                if (Nodes >= stop) return null;
            }
            return _done = false;
        }

        private void Expand(TilePos c)
        {
            float gc = _g[c];
            int came = _heading[c];
            for (int d = 0; d < 4; d++)
            {
                var (dx, dy) = Dirs[d];
                float turn = came >= 0 && came != d ? Turn : 0;
                var across = c.Offset(dx, dy, 0);
                Try(c, gc, across, Flat + turn + HatchCost(c, d), d);
                Try(c, gc, c.Offset(dx, dy, 1), Ramp + turn, d);
                Try(c, gc, c.Offset(dx, dy, -1), Ramp + turn, d);
            }
            Try(c, gc, c.Offset(0, 0, 1), Lift, -1);
            Try(c, gc, c.Offset(0, 0, -1), Lift, -1);
        }

        private void Try(TilePos c, float gc, TilePos n, float cost, int dir)
        {
            if (_closed.Contains(n) || !_sim.World.InBounds(n) || !Conveyors.CanStep(_sim.World, c, n) || !Ok(n)) return;
            if (dir >= 0) cost += Height(n);
            if (_sim.Water.MassAt(n) >= Fluids.Water.Full / 2) cost += Wet;
            float gn = gc + cost;
            if (_g.TryGetValue(n, out float known) && known <= gn) return;
            _g[n] = gn;
            _parent[n] = c;
            _heading[n] = dir;
            _open.Enqueue(n, gn + H(n));
        }

        private List<TilePos> Path(TilePos end)
        {
            var path = new List<TilePos> { end };
            while (_parent.TryGetValue(path[^1], out var p)) path.Add(p);
            path.Reverse();
            return path;
        }

        /// <summary>Levels of open air under a cell (up to 6): raised belts cost trestles, so routes keep low unless they must climb.</summary>
        private float Height(TilePos c)
        {
            int h = 0;
            for (var p = c.Offset(0, 0, -1); h < 6 && p.Z > 0 && !TileInfo.IsSolid(_sim.World.Get(p)); p = p.Offset(0, 0, -1)) h++;
            return h * PerLevelUp;
        }

        /// <summary>A thin wall (or window) across the step: the belt goes through a hatch, at a cost.</summary>
        private float HatchCost(TilePos c, int d)
        {
            var (cell, side) = d switch
            {
                0 => (c, EdgeInfo.East),
                1 => (c, EdgeInfo.North),
                2 => (c.Offset(-1, 0, 0), EdgeInfo.East),
                _ => (c.Offset(0, -1, 0), EdgeInfo.North),
            };
            return _sim.World.InBounds(cell) && EdgeInfo.Kind(_sim.World.Edge(cell, side)) is EdgeKind.Wall or EdgeKind.Window ? Hatch : 0;
        }
    }
}
