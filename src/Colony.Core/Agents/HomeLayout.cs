using Colony.Core.Items;
using Colony.Core.Navigation;
using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core.Agents;

/// <summary>What a placed piece is in a home's arrangement (a furniture item becomes a table with chairs, a chest or a shelf).</summary>
public enum FixtureRole : byte { Bed, Table, Chair, Chest, Shelf, Tableware, TableLamp, WallLamp, FloorLamp, Rug, WallHanging, Statue }

/// <summary>
/// One piece of a home's arrangement: what it is, which item it shows, the cell it stands on, the way it faces
/// (0 +x, 1 -x, 2 +y, 3 -y: a bed's foot, a chair's front, a wall piece's face, away from its wall) and, for small
/// pieces, where in the cell (offset from its centre in tiles) and how high (on a table, on a wall).
/// </summary>
public readonly record struct Placement(FixtureRole Role, ItemType Item, int Tier, TilePos Cell, int Facing, float X, float Y, float Height);

/// <summary>A home's walls (see <see cref="HomeLayout.Survey"/>): share of its outline closed, average tier, windows, wall materials.</summary>
public readonly record struct HomeShell(float Enclosure, float Tier, int Windows, IReadOnlyCollection<string> Materials,
    float Floored = 0, float FloorTier = 0, float Covered = 0, float RoofTier = 0)
{
    public static readonly HomeShell None = new(0, 0, 0, Array.Empty<string>());

    /// <summary>How well it serves someone expecting <paramref name="expected"/> (1..3): walls .45, floor .25, roof .2, windows .1.</summary>
    public float Score(float expected)
    {
        float Part(float share, float tier) => tier <= 0 ? 0 : share * Math.Min(1f, tier / expected);
        return 0.45f * Part(Enclosure, Tier) + 0.25f * Part(Floored, FloorTier) + 0.2f * Part(Covered, RoofTier) + (Windows > 0 ? 0.1f : 0);
    }
}

/// <summary>
/// Procedural home layout: arranges a home's installed fixtures the way a house would be lived in, deterministically
/// but differently for every home (seeded by the room). Doorways are found (floor leading out) and the way in is kept
/// clear. Beds stand with their heads to a wall, far from the door, spread apart (only a crowded dormitory puts them
/// side by side). Furniture becomes tables (one per four sleepers, in the living area near the door, with chairs
/// round them), then chests by the beds and shelves along the walls. Tableware is laid on the tables, overflow on
/// shelves. Lights go where their item says: rushlights on tables, lanterns on the walls spread round the room,
/// standing lamps in corners. Rugs lie under tables and beside beds; tapestries and paintings take free wall,
/// away from the lamps; sculptures stand in corners. Pieces that find no room stay installed, just not shown.
/// </summary>
public static class HomeLayout
{
    private static readonly (int X, int Y)[] Dir = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private static int Opposite(int d) => d ^ 1;

    /// <summary>The direction from one cell to a 4-neighbour.</summary>
    private static int DirTo(TilePos from, TilePos to) =>
        to.X > from.X ? 0 : to.X < from.X ? 1 : to.Y > from.Y ? 2 : 3;

    private static TilePos Step(TilePos c, int d) => new(c.X + Dir[d].X, c.Y + Dir[d].Y, c.Z);

    public static List<Placement> Arrange(Home home, VoxelWorld world)
    {
        var placed = new List<Placement>();
        var cells = home.Room.Cells.OrderBy(p => (p.Z, p.Y, p.X)).ToList();
        if (cells.Count == 0) return placed;
        var inside = home.Room.CellSet;
        int seed = home.Room.Id * 7919 + cells[0].X * 31 + cells[0].Y * 17 + cells[0].Z;
        float Jitter(TilePos c, int k) => Noise.Hash(c.X * 4 + k, c.Y, c.Z, seed);

        // Walls and doorways: a side is a wall where the floor ends (a built wall, a drop, the room's edge); a
        // doorway where it leads on to floor outside the home.
        var walls = new Dictionary<TilePos, int>();
        var entrances = new List<TilePos>();
        foreach (var c in cells)
        {
            int mask = 0;
            bool door = false;
            for (int d = 0; d < 4; d++)
            {
                var n = Step(c, d);
                if (inside.Contains(n)) continue;
                if (Movement.MoveCost(world, c, n) is not null) door = true;
                else mask |= 1 << d;
            }
            walls[c] = mask;
            if (door || TileInfo.IsStair(world.Get(c))) entrances.Add(c);
        }
        if (entrances.Count == 0) entrances.Add(cells[0]);
        bool Wall(TilePos c, int d) => (walls[c] & (1 << d)) != 0;
        int WallCount(TilePos c) => System.Numerics.BitOperations.PopCount((uint)walls[c]);

        // Steps from the way in: beds go deep, the living area near the door.
        var dist = new Dictionary<TilePos, int>();
        var queue = new Queue<TilePos>();
        foreach (var e in entrances)
        {
            dist[e] = 0;
            queue.Enqueue(e);
        }
        while (queue.TryDequeue(out var c))
            for (int d = 0; d < 4; d++)
            {
                var n = Step(c, d);
                if (inside.Contains(n) && !dist.ContainsKey(n))
                {
                    dist[n] = dist[c] + 1;
                    queue.Enqueue(n);
                }
            }
        foreach (var c in cells) dist.TryAdd(c, 0);
        int depth = Math.Max(1, dist.Values.Max());

        // The way in stays clear: doorway cells and the cell inside each.
        var keepClear = new HashSet<TilePos>(entrances);
        foreach (var e in entrances)
            for (int d = 0; d < 4; d++)
                if (!inside.Contains(Step(e, d)) && Movement.MoveCost(world, e, Step(e, d)) is not null && inside.Contains(Step(e, Opposite(d))))
                    keepClear.Add(Step(e, Opposite(d)));

        var taken = new HashSet<TilePos>();          // floor with something standing on it
        var wallUsed = new HashSet<(TilePos, int)>(); // wall faces with something against or on them
        bool Free(TilePos c) => inside.Contains(c) && !taken.Contains(c) && !keepClear.Contains(c);
        var bedCells = new List<TilePos>();

        // ---- Beds ----
        var beds = home.Fixtures.Where(f => f.Slot == Housing.Bed).OrderByDescending(f => f.Tier).ToList();
        bool crowded = beds.Count * 4 > cells.Count; // a dormitory: rows of beds
        foreach (var bed in beds)
        {
            (TilePos Head, int Wall, float Score)? best = null;
            foreach (var c in cells)
            {
                if (!Free(c)) continue;
                for (int d = 0; d < 4; d++)
                {
                    if (!Wall(c, d)) continue;
                    var foot = Step(c, Opposite(d));
                    if (!Free(foot)) continue;
                    int touching = 0;
                    foreach (var b in bedCells)
                        if (Near(b, c) || Near(b, foot)) touching++;
                    float score = dist[c] * 2f / depth + Jitter(c, d) * 1.2f + (WallCount(c) >= 2 ? 0.4f : 0) - touching * (crowded ? 0.2f : 3f);
                    if (best is null || score > best.Value.Score) best = (c, d, score);
                }
            }
            if (best is not { } b2) continue;
            var footCell = Step(b2.Head, Opposite(b2.Wall));
            taken.Add(b2.Head);
            taken.Add(footCell);
            bedCells.Add(b2.Head);
            bedCells.Add(footCell);
            wallUsed.Add((b2.Head, b2.Wall));
            bed.Cell = b2.Head;
            placed.Add(new Placement(FixtureRole.Bed, bed.Item, bed.Tier, b2.Head, Opposite(b2.Wall), 0, 0, 0));
        }

        // ---- Furniture: tables with chairs, then chests and shelves ----
        var furniture = home.Fixtures.Where(f => f.Slot == Housing.Furniture).OrderByDescending(f => f.Tier).ToList();
        int sleepers = Math.Max(1, beds.Count);
        int tableCount = furniture.Count == 0 ? 0 : Math.Clamp((sleepers + 3) / 4, 1, furniture.Count);
        var tables = new List<(TilePos Cell, Fixture F, List<int> ChairSides)>();
        var surfaces = new List<(TilePos Cell, int Facing, float Height, int Room)>(); // places small things can stand
        int chests = 0;
        for (int i = 0; i < furniture.Count; i++)
        {
            var f = furniture[i];
            if (i < tableCount)
            {
                TilePos? at = null;
                float bestScore = float.MinValue;
                foreach (var c in cells)
                {
                    if (!Free(c)) continue;
                    int open = 0;
                    for (int d = 0; d < 4; d++) if (Free(Step(c, d))) open++;
                    if (open == 0) continue;
                    float score = open * 1.5f - WallCount(c) * 1.2f - dist[c] * 1.5f / depth + Jitter(c, 7) - tables.Count(t => Near(t.Cell, c)) * 4;
                    if (score > bestScore) (at, bestScore) = (c, score);
                }
                if (at is not { } tc) continue;
                taken.Add(tc);
                int wantChairs = Math.Clamp((sleepers + tableCount - 1) / tableCount, 1, 4);
                var sides = new List<int>();
                foreach (int d in Enumerable.Range(0, 4).OrderBy(d => Jitter(tc, 10 + d)))
                {
                    if (sides.Count >= wantChairs) break;
                    var chair = Step(tc, d);
                    if (!Free(chair)) continue;
                    taken.Add(chair);
                    sides.Add(d);
                    placed.Add(new Placement(FixtureRole.Chair, f.Item, f.Tier, chair, Opposite(d), 0, 0, 0));
                }
                f.Cell = tc;
                int facing = Jitter(tc, 20) < 0.5f ? 0 : 2;
                tables.Add((tc, f, sides));
                placed.Add(new Placement(FixtureRole.Table, f.Item, f.Tier, tc, facing, 0, 0, 0));
                continue;
            }
            // A chest at a bedside while beds lack one, otherwise a shelf against a wall.
            bool chest = chests < beds.Count && Jitter(cells[0], 30 + i) < 0.75f;
            (TilePos Cell, int Wall)? spot = null;
            float spotScore = float.MinValue;
            foreach (var c in cells)
            {
                if (!Free(c)) continue;
                for (int d = 0; d < 4; d++)
                {
                    if (!Wall(c, d) || wallUsed.Contains((c, d))) continue;
                    bool byBed = bedCells.Any(b => Near(b, c));
                    float score = (chest ? (byBed ? 3 : 0) : dist[c] * 1f / depth) + Jitter(c, 40 + d) - (chest ? 0 : bedCells.Count(b => Near(b, c)));
                    if (score > spotScore) (spot, spotScore) = ((c, d), score);
                }
            }
            if (spot is not { } s) continue;
            taken.Add(s.Cell);
            wallUsed.Add((s.Cell, s.Wall));
            f.Cell = s.Cell;
            var role = chest ? FixtureRole.Chest : FixtureRole.Shelf;
            if (chest) chests++;
            placed.Add(new Placement(role, f.Item, f.Tier, s.Cell, Opposite(s.Wall), 0, 0, 0));
            surfaces.Add((s.Cell, Opposite(s.Wall), chest ? 0.42f : 0.62f, chest ? 2 : 3));
        }

        // ---- Tableware: laid at the chairs, then the table's middle, then stored on shelves and chests ----
        var tableware = home.Fixtures.Where(f => f.Slot == Housing.Tableware).ToList();
        var settings = new List<(TilePos Cell, float X, float Y, float H)>();
        foreach (var (tc, _, sides) in tables)
            foreach (int d in sides) settings.Add((tc, Dir[d].X * 0.2f, Dir[d].Y * 0.2f, 0.47f));
        foreach (var (tc, _, sides) in tables)
            if (sides.Count < 3) settings.Add((tc, -0.12f, 0.12f, 0.47f));
        foreach (var (c, facing, h, room) in surfaces)
            for (int k = 0; k < room; k++) settings.Add((c, (k - (room - 1) / 2f) * 0.22f * (facing < 2 ? 0 : 1), (k - (room - 1) / 2f) * 0.22f * (facing < 2 ? 1 : 0), h));
        for (int i = 0; i < tableware.Count && i < settings.Count; i++)
        {
            var (c, x, y, h) = settings[i];
            tableware[i].Cell = c;
            placed.Add(new Placement(FixtureRole.Tableware, tableware[i].Item, tableware[i].Tier, c, 0, x, y, h));
        }

        // ---- Lights ----
        var lamps = new List<TilePos>();
        var tableLampSpots = new Queue<(TilePos Cell, float H)>(tables.Select(t => (t.Cell, 0.47f)).Concat(surfaces.Select(s => (s.Cell, s.Height))));
        foreach (var f in home.Fixtures.Where(f => f.Slot == Housing.Lighting).OrderBy(f => f.Tier))
        {
            string place = f.Item.Def.Place ?? "wall";
            if (place == "table" && tableLampSpots.TryDequeue(out var spot))
            {
                f.Cell = spot.Cell;
                lamps.Add(spot.Cell);
                placed.Add(new Placement(FixtureRole.TableLamp, f.Item, f.Tier, spot.Cell, 0, 0.08f, -0.08f, spot.H));
                continue;
            }
            if (place == "floor" && Spread(cells, c => Free(c) && WallCount(c) >= 1, lamps, Jitter, prefer: c => WallCount(c) >= 2 ? 2 : 0) is { } corner)
            {
                taken.Add(corner);
                f.Cell = corner;
                lamps.Add(corner);
                placed.Add(new Placement(FixtureRole.FloorLamp, f.Item, f.Tier, corner, 0, 0, 0, 0));
                continue;
            }
            if (SpreadWall(cells, walls, wallUsed, lamps, Jitter) is { } w)
            {
                wallUsed.Add(w);
                f.Cell = w.Cell;
                lamps.Add(w.Cell);
                placed.Add(new Placement(FixtureRole.WallLamp, f.Item, f.Tier, w.Cell, Opposite(w.Wall), 0, 0, 0.78f));
            }
        }

        // ---- Decor ----
        var hung = new List<TilePos>(lamps);
        var rugs = new HashSet<TilePos>();
        var rugSpots = new Queue<TilePos>(tables.Select(t => t.Cell));
        foreach (var f in home.Fixtures.Where(f => f.Slot == Housing.Decor).OrderByDescending(f => f.Tier))
        {
            string place = f.Item.Def.Place ?? "wall";
            if (place == "floor")
            {
                TilePos? at = null;
                while (rugSpots.TryDequeue(out var t)) if (rugs.Add(t)) { at = t; break; }
                // Beside a bed, then any open floor away from other rugs.
                at ??= cells.Where(c => inside.Contains(c) && !rugs.Contains(c) && !bedCells.Contains(c) && !keepClear.Contains(c) && bedCells.Any(b => Near(b, c)))
                    .OrderByDescending(c => Jitter(c, 50)).Cast<TilePos?>().FirstOrDefault();
                at ??= Spread(cells, c => !rugs.Contains(c) && !bedCells.Contains(c) && !keepClear.Contains(c), rugs.ToList(), Jitter);
                if (at is not { } r) continue;
                rugs.Add(r);
                f.Cell = r;
                placed.Add(new Placement(FixtureRole.Rug, f.Item, f.Tier, r, Jitter(r, 51) < 0.5f ? 0 : 2, 0, 0, 0));
                continue;
            }
            if (place == "corner" && Spread(cells, c => Free(c) && WallCount(c) >= 1, hung, Jitter, prefer: c => WallCount(c) >= 2 ? 3 : 0) is { } nook)
            {
                taken.Add(nook);
                f.Cell = nook;
                hung.Add(nook);
                int facing = Enumerable.Range(0, 4).FirstOrDefault(d => !Wall(nook, d) && Wall(nook, Opposite(d)));
                placed.Add(new Placement(FixtureRole.Statue, f.Item, f.Tier, nook, facing, 0, 0, 0));
                continue;
            }
            if (SpreadWall(cells, walls, wallUsed, hung, Jitter) is { } w)
            {
                wallUsed.Add(w);
                f.Cell = w.Cell;
                hung.Add(w.Cell);
                placed.Add(new Placement(FixtureRole.WallHanging, f.Item, f.Tier, w.Cell, Opposite(w.Wall), 0, 0, 0.55f));
            }
        }
        return placed;
    }

    /// <summary>
    /// What a home is built of: how much of its outline is closed (thin walls, doors, windows or bare rock), the average
    /// tier of what closes it (rock and log or rubble walls humble, planks comfortable, cut stone and brick fine), its
    /// windows and the materials its walls are made of.
    /// </summary>
    public static HomeShell Survey(Home home, VoxelWorld world)
    {
        var inside = home.Room.CellSet;
        int sides = 0, closed = 0, windows = 0, tierCount = 0;
        float tiers = 0;
        var materials = new HashSet<string>();
        foreach (var c in inside)
            for (int d = 0; d < 4; d++)
            {
                var n = Step(c, d);
                if (inside.Contains(n)) continue;
                sides++;
                byte edge = world.InBounds(n) ? world.EdgeBetween(c, n) : (byte)0;
                if (edge != 0)
                {
                    var m = WallMaterials.All[WallMaterials.Clamp(EdgeInfo.Material(edge))];
                    closed++;
                    tiers += m.Quality + 1;
                    tierCount++;
                    materials.Add(m.Material);
                    if (EdgeInfo.Kind(edge) == EdgeKind.Window) windows++;
                }
                else if (world.IsSolid(n.X, n.Y, n.Z))
                {
                    closed++;
                    tiers += 1;
                    tierCount++;
                    materials.Add("stone");
                }
            }
        // Floors and roofs: a laid floor or bare rock underfoot (bare earth counts for nothing), a roof or rock overhead.
        int floored = 0, covered = 0;
        float floorTiers = 0, roofTiers = 0;
        foreach (var c in inside)
        {
            int floor = world.FloorAt(c);
            int ft = floor > 0 ? FloorMaterials.All[Math.Min(floor, FloorMaterials.Count - 1)].Tier
                : TileInfo.IsEarth(world.Get(c.X, c.Y, c.Z - 1)) || !world.IsSolid(c.X, c.Y, c.Z - 1) ? 0 : 1;
            if (ft > 0) { floored++; floorTiers += ft; }
            int roof = world.RoofAt(c);
            int rt = roof > 0 ? RoofMaterials.All[Math.Min(roof, RoofMaterials.Count - 1)].Tier : world.IsSolid(c.X, c.Y, c.Z + 1) ? 1 : 0;
            if (rt > 0) { covered++; roofTiers += rt; }
            if (floor > 0) materials.Add(FloorMaterials.All[Math.Min(floor, FloorMaterials.Count - 1)].Item == "planks" ? "wood" : "stone");
        }
        int cellCount = Math.Max(1, inside.Count);
        return new HomeShell(sides == 0 ? 1 : closed / (float)sides, tierCount == 0 ? 0 : tiers / tierCount, windows, materials,
            floored / (float)cellCount, floored == 0 ? 0 : floorTiers / floored, covered / (float)cellCount, covered == 0 ? 0 : roofTiers / covered);
    }

    /// <summary>8-neighbours on the same level.</summary>
    private static bool Near(TilePos a, TilePos b) => a.Z == b.Z && Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1 && a != b;

    /// <summary>The allowed cell farthest from <paramref name="others"/> (ties: preferred, then jitter): spreads things out.</summary>
    private static TilePos? Spread(List<TilePos> cells, Func<TilePos, bool> allowed, List<TilePos> others, Func<TilePos, int, float> jitter,
        Func<TilePos, float>? prefer = null)
    {
        TilePos? best = null;
        float bestScore = float.MinValue;
        foreach (var c in cells)
        {
            if (!allowed(c)) continue;
            float far = others.Count == 0 ? 0 : others.Min(o => MathF.Sqrt((o.X - c.X) * (o.X - c.X) + (o.Y - c.Y) * (o.Y - c.Y)) + Math.Abs(o.Z - c.Z) * 3);
            float score = Math.Min(far, 6) + (prefer?.Invoke(c) ?? 0) + jitter(c, 60) * 0.5f;
            if (score > bestScore) (best, bestScore) = (c, score);
        }
        return best;
    }

    /// <summary>A free wall face, as far as possible from <paramref name="others"/>.</summary>
    private static (TilePos Cell, int Wall)? SpreadWall(List<TilePos> cells, Dictionary<TilePos, int> walls, HashSet<(TilePos, int)> used,
        List<TilePos> others, Func<TilePos, int, float> jitter)
    {
        (TilePos, int)? best = null;
        float bestScore = float.MinValue;
        foreach (var c in cells)
            for (int d = 0; d < 4; d++)
            {
                if ((walls[c] & (1 << d)) == 0 || used.Contains((c, d))) continue;
                float far = others.Count == 0 ? 3 : others.Min(o => MathF.Sqrt((o.X - c.X) * (o.X - c.X) + (o.Y - c.Y) * (o.Y - c.Y)) + Math.Abs(o.Z - c.Z) * 3);
                float score = Math.Min(far, 8) + jitter(c, 70 + d) * 0.6f;
                if (score > bestScore) (best, bestScore) = ((c, d), score);
            }
        return best;
    }
}
