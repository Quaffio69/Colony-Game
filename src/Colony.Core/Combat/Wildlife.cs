using Colony.Core.Agents;
using Colony.Core.Navigation;
using Colony.Core.World;

namespace Colony.Core.Combat;

/// <summary>
/// Wild creatures and their nests. Built to scale to many nests of numerous creatures:
/// <list type="bullet">
/// <item><b>Counted, not simulated:</b> a nest's population is a number underground; creatures only walk the world while
/// a colonist is within <see cref="WakeRange"/> of the nest (or while it fights). A sleeping nest costs a few integer
/// updates a day.</item>
/// <item><b>Swarm pathing:</b> no creature runs a path search of its own. Foragers and the homeward bound follow the
/// nest's home flow field (one flood round the entrance); fighters descend the nest's threat field (one flood from the
/// colonists they are after, refreshed every <see cref="ThreatRefresh"/> ticks) and close the last few tiles greedily;
/// raiders follow one shared trail (a single A* from the nest to the colony, like a pheromone trail).</item>
/// <item><b>Temperament:</b> provokable kinds (giant ants) leave colonists alone until a colony strikes them or its land
/// (rooms, stockpiles, farms, landing site: <see cref="Territory"/>) meets their territory. Then they defend their
/// ground, raid now and then while the land meets, and calm down <see cref="CalmTicks"/> after the last provocation.</item>
/// </list>
/// Deterministic: everything runs in list order with hashed rolls; caches (fields, trails, buckets) are rebuilt
/// identically after a snapshot (<see cref="Settle"/>).
/// </summary>
public sealed class Wildlife
{
    public const int WakeRange = 56;
    public const int AggroRange = 8;
    public const int RaidRange = 96;
    /// <summary>Tiles round a colony's claimed ground that still count as its land.</summary>
    public const int ColonyReach = 6;
    private const int BlockShift = 4;
    public const int CalmTicks = Needs.Day * 2;
    public const int BirthInterval = Needs.Day / 6;
    public const int RaidInterval = Needs.Day * 3 / 2;
    public const int MaxRaid = 16;
    public const int ThreatRefresh = 24;
    /// <summary>Nests a map holds at most (they stop founding new ones).</summary>
    public int MaxNests { get; private set; } = 8;

    private readonly List<Nest> _nests = new();
    private readonly Dictionary<int, Nest> _nestById = new();
    private readonly List<Creature> _creatures = new();
    private int _nextNest, _nextCreature;
    private readonly List<(TilePos To, float Cost)> _moves = new(12);
    private readonly Dictionary<long, List<Creature>> _buckets = new();
    private readonly List<Colonist> _prey = new();
    /// <summary>Tiles an ant bites from this tick (one biter per tile).</summary>
    private readonly HashSet<TilePos> _biting = new();
    private readonly List<HashSet<long>> _land = new();
    private (int Rooms, int Stockpiles, int Farms, int Factions) _landSeen = (-1, -1, -1, -1);

    /// <summary>Changes whenever a nest appears or disappears (views).</summary>
    public int Version { get; private set; }
    public IReadOnlyList<Nest> Nests => _nests;
    public IReadOnlyList<Creature> Creatures => _creatures;
    public int Slain { get; private set; }
    public int NestsDestroyed { get; private set; }

    public Nest? NestById(int id) => _nestById.GetValueOrDefault(id);

    // ---- Placement ------------------------------------------------------------------------------------------

    /// <summary>
    /// Scatters nests over a generated world: one chance per 128x128 cell, on dry standable ground well away from
    /// every colony's landing (they meet only as colonies grow, or when colonists go looking).
    /// </summary>
    public void Populate(ColonySim sim, int seed)
    {
        var w = sim.World;
        MaxNests = Math.Max(4, (int)((long)w.Width * w.Depth / (100 * 100)));
        const int cell = 128;
        for (int gy = 0; gy * cell < w.Depth; gy++)
        for (int gx = 0; gx * cell < w.Width; gx++)
        {
            if (Procedural.Noise.Hash(gx, gy, 1, seed ^ 0xA17) >= 0.55f) continue;
            int x = gx * cell + 16 + (int)(Procedural.Noise.Hash(gx, gy, 2, seed ^ 0xA17) * (cell - 32));
            int y = gy * cell + 16 + (int)(Procedural.Noise.Hash(gx, gy, 3, seed ^ 0xA17) * (cell - 32));
            if (Site(sim, x, y, landingGap: 64) is not { } p) continue;
            int pop = 10 + (int)(Procedural.Noise.Hash(gx, gy, 4, seed ^ 0xA17) * 20);
            AddNest(sim, CreatureKinds.GiantAnt, p, pop);
        }
    }

    /// <summary>A nest of <paramref name="kind"/> at a standable cell (tests, demos, colonizing).</summary>
    public Nest AddNest(ColonySim sim, CreatureKind kind, TilePos entrance, int population)
    {
        var n = new Nest(_nextNest++, kind, entrance, population, sim.Tick)
        {
            NextBirth = sim.Tick + BirthInterval + _nextNest * 37 % BirthInterval,
            NextColony = sim.Tick + Needs.Day * 3,
        };
        _nests.Add(n);
        _nestById[n.Id] = n;
        Version++;
        return n;
    }

    /// <summary>The standable dry surface cell at a column, if it suits a nest (away from colonies and other nests).</summary>
    private TilePos? Site(ColonySim sim, int x, int y, int landingGap)
    {
        var w = sim.World;
        if (x < 4 || y < 4 || x >= w.Width - 4 || y >= w.Depth - 4) return null;
        var p = new TilePos(x, y, w.SurfaceLevel(x, y) + 1);
        if (!Movement.IsStandable(w, p)) return null;
        // Dry ground, a little back from rivers and lakes (the generated water is still settling when nests are placed).
        if (sim.Terrain is { } terrain ? terrain.WaterDistance[y * terrain.Width + x] < 3 : sim.Water.MassAt(p) >= Fluids.Water.MinMass) return null;
        foreach (var f in sim.Factions)
            if (f.Landing is { } l && Sq(l.X - x) + Sq(l.Y - y) < landingGap * landingGap) return null;
        foreach (var n in _nests)
            if (Sq(n.Entrance.X - x) + Sq(n.Entrance.Y - y) < 40 * 40) return null;
        RefreshLand(sim);
        for (int f = 0; f < _land.Count; f++)
            if (Meets(x, y, 24, _land[f])) return null;
        return p;
    }

    private static int Sq(int v) => v * v;

    // ---- The tick -------------------------------------------------------------------------------------------

    /// <summary>Wall time of the last tick (diagnostics only; never read by the sim).</summary>
    public double LastTickMs { get; private set; }

    internal void Tick(ColonySim sim)
    {
        long t = sim.Tick;
        if (_nests.Count == 0 && _creatures.Count == 0) return;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _creatures.RemoveAll(c => c.Dead);

        _prey.Clear();
        _biting.Clear();
        foreach (var c in sim.Colonists)
            if (!c.Downed) _prey.Add(c);

        if (t % 50 == 0) Wake(sim);
        if (t % 300 == 7) Territory(sim);
        for (int i = 0; i < _nests.Count; i++) NestTick(sim, _nests[i]);
        for (int i = 0; i < _creatures.Count; i++)
            if (!_creatures[i].Dead) CreatureTick(sim, _creatures[i]);
        _creatures.RemoveAll(c => c.Dead);
        RebuildBuckets(t);
        LastTickMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>A nest wakes while a colonist is near or it has fighters out; asleep, its foragers go back underground.</summary>
    private void Wake(ColonySim sim)
    {
        foreach (var n in _nests)
        {
            bool near = n.ToEmerge > 0;
            foreach (var c in sim.Colonists)
                if (Math.Max(Math.Abs(c.Position.X - n.Entrance.X), Math.Abs(c.Position.Y - n.Entrance.Y)) <= WakeRange)
                {
                    near = true;
                    break;
                }
            if (!near)
                foreach (var c in _creatures)
                    if (c.NestId == n.Id && c.Fighting) { near = true; break; }
            if (n.Awake && !near)
            {
                foreach (var c in _creatures)
                    if (c.NestId == n.Id && !c.Dead) Burrow(c, n);
                n.Home = null;
                n.Threat = null;
            }
            n.Awake = near;
        }
    }

    private void NestTick(ColonySim sim, Nest n)
    {
        long t = sim.Tick;
        if (t >= n.NextBirth)
        {
            n.NextBirth = t + BirthInterval;
            if (n.Total < n.Kind.NestCap) n.Population++;
            else TryColonize(sim, n);
        }
        if (n.Crowded != 0 && t >= n.NextRaid) StartRaid(sim, n);
        if (!n.Awake) return;

        if (n.ToEmerge > 0 && t >= n.NextEmerge)
        {
            // Alarmed, they boil out of every hole at once; a raid files out.
            int burst = n.EmergeMode == CreatureMode.Attack ? 3 : 1;
            for (int k = 0; k < burst && n.ToEmerge > 0 && n.Population > 0; k++)
            {
                var c = Spawn(sim, n, n.EmergeMode);
                Movement.GetMoves(sim.World, n.Entrance, _moves);
                if (_moves.Count > 0) c.Position = _moves[(int)(Gear.Roll(t, c.Id, k, 12) * _moves.Count) % _moves.Count].To;
                n.ToEmerge--;
            }
            n.NextEmerge = t + 2;
            if (n.Population == 0) n.ToEmerge = 0;
        }
        if (t >= n.NextForager)
        {
            n.NextForager = t + 60;
            int foragers = 0;
            foreach (var c in _creatures)
                if (c.NestId == n.Id && c.Mode == CreatureMode.Wander) foragers++;
            if (foragers < Math.Min(n.Total, 2 + n.Total / 8) && n.Population > 1) Spawn(sim, n, CreatureMode.Wander);
        }
        if (t - n.ThreatBuilt >= ThreatRefresh) BuildThreat(sim, n);
    }

    private Creature Spawn(ColonySim sim, Nest n, CreatureMode mode)
    {
        var c = new Creature(_nextCreature++, n.Kind, n.Entrance, n.Id) { Mode = mode };
        if (mode == CreatureMode.Wander) c.Timer = 600 + (int)(Gear.Roll(sim.Tick, c.Id, n.Id, 11) * 1200);
        n.Population--;
        n.Out++;
        _creatures.Add(c);
        return c;
    }

    /// <summary>Goes back underground (it rejoins the count).</summary>
    private static void Burrow(Creature c, Nest n)
    {
        c.Dead = true;
        n.Out--;
        n.Population++;
    }

    /// <summary>Leaves the world without a trace (a lost creature wandering off, or one stuck with no nest).</summary>
    private void Vanish(Creature c)
    {
        c.Dead = true;
        if (NestById(c.NestId) is { } n) n.Out--;
    }

    /// <summary>A full nest sends founders off to start another a little way off (not near colonies or other nests).</summary>
    private void TryColonize(ColonySim sim, Nest n)
    {
        long t = sim.Tick;
        if (t < n.NextColony || n.Population < 12) return;
        n.NextColony = t + Needs.Day * 4 + (int)(Gear.Roll(t, n.Id, 0, 21) * Needs.Day);
        if (_nests.Count >= MaxNests) return;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            double angle = Gear.Roll(t, n.Id, attempt, 22) * 2 * Math.PI;
            float r = 30 + Gear.Roll(t, n.Id, attempt, 23) * 30;
            int x = n.Entrance.X + (int)(r * Math.Cos(angle)), y = n.Entrance.Y + (int)(r * Math.Sin(angle));
            if (Site(sim, x, y, landingGap: 40) is not { } p) continue;
            n.Population -= 6;
            AddNest(sim, n.Kind, p, 6);
            if (sim.Colonists.Any(c => Math.Max(Math.Abs(c.Position.X - x), Math.Abs(c.Position.Y - y)) <= WakeRange * 2))
                sim.Announce($"{Cap(n.Kind.Plural)} have founded a new nest nearby");
            return;
        }
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ---- Territory and hostility -------------------------------------------------------------------------------

    /// <summary>Which colonies' land meets each nest's territory; newly crowded nests will raid, uncrowded ones calm down.</summary>
    private void Territory(ColonySim sim)
    {
        RefreshLand(sim);
        long t = sim.Tick;
        foreach (var n in _nests)
        {
            long mask = 0;
            for (int f = 0; f < _land.Count && f < Nest.MaxFactions; f++)
                if (Meets(n.Entrance.X, n.Entrance.Y, n.Radius + ColonyReach, _land[f])) mask |= 1L << f;
            long gained = mask & ~n.Crowded, lost = n.Crowded & ~mask;
            for (int f = 0; f < Nest.MaxFactions; f++)
            {
                if (((gained >> f) & 1) != 0)
                {
                    if (!n.HostileTo(f, t))
                        sim.Announce($"{Cap(n.Kind.Plural)} grow restless: {sim.Factions[f].Name}'s land has reached their nest. Expect raids", f);
                    n.NextRaid = Math.Min(n.NextRaid, t + Needs.Day / 2);
                }
                if (((lost >> f) & 1) != 0) n.AngryUntil[f] = Math.Max(n.AngryUntil[f], t + CalmTicks);
            }
            n.Crowded = mask;
            if (mask == 0) n.NextRaid = long.MaxValue;
        }
    }

    /// <summary>Each colony's land as 16x16 column blocks: its rooms, stockpiles, farm plots and landing site.</summary>
    private void RefreshLand(ColonySim sim)
    {
        var seen = (sim.Rooms.Version, sim.Stockpiles.Version, sim.Farms.Version, sim.Factions.Count);
        if (seen == _landSeen) return;
        _landSeen = seen;
        _land.Clear();
        foreach (var f in sim.Factions)
        {
            var blocks = new HashSet<long>();
            if (f.Landing is { } l) blocks.Add(Block(l.X, l.Y));
            _land.Add(blocks);
        }
        void Mark(int faction, TilePos p)
        {
            if (faction >= 0 && faction < _land.Count) _land[faction].Add(Block(p.X, p.Y));
        }
        foreach (var room in sim.Rooms.All)
            foreach (var p in room.Cells) Mark(room.Faction, p);
        foreach (var p in sim.Stockpiles.Cells) Mark(sim.Stockpiles.OwnerOf(p), p);
        foreach (var p in sim.Farms.Cells) Mark(sim.Farms.OwnerOf(p), p);
    }

    private static long Block(int x, int y) => ((long)(x >> BlockShift) << 32) | (uint)(y >> BlockShift);

    /// <summary>Any block of <paramref name="blocks"/> within <paramref name="r"/> tiles of (x, y)?</summary>
    private static bool Meets(int x, int y, int r, HashSet<long> blocks)
    {
        if (blocks.Count == 0) return false;
        for (int by = (y - r) >> BlockShift; by <= (y + r) >> BlockShift; by++)
        for (int bx = (x - r) >> BlockShift; bx <= (x + r) >> BlockShift; bx++)
        {
            if (!blocks.Contains(((long)bx << 32) | (uint)by)) continue;
            int x0 = bx << BlockShift, y0 = by << BlockShift;
            int dx = x < x0 ? x0 - x : x > x0 + 15 ? x - x0 - 15 : 0;
            int dy = y < y0 ? y0 - y : y > y0 + 15 ? y - y0 - 15 : 0;
            if (dx * dx + dy * dy <= r * r) return true;
        }
        return false;
    }

    /// <summary>Is any colony's land within this nest's territory (the land a colony may not take without a fight)?</summary>
    public bool TerritoryMeets(ColonySim sim, Nest n, int faction)
    {
        RefreshLand(sim);
        return faction < _land.Count && Meets(n.Entrance.X, n.Entrance.Y, n.Radius + ColonyReach, _land[faction]);
    }

    /// <summary>A colony struck the nest or its creatures: it is angry with that colony for a while and sends defenders out.</summary>
    private void Alarm(ColonySim sim, Nest n, int faction, int defenders)
    {
        long t = sim.Tick;
        if (faction is < 0 or >= Nest.MaxFactions) return;
        if (!n.HostileTo(faction, t))
            sim.Announce($"{Cap(n.Kind.Plural)} swarm out to defend their nest against {sim.Factions[faction].Name}!", faction);
        n.AngryUntil[faction] = t + CalmTicks;
        if (n.ToEmerge < defenders || n.EmergeMode != CreatureMode.Attack)
        {
            n.ToEmerge = Math.Min(n.Population, Math.Max(n.ToEmerge, defenders));
            n.EmergeMode = CreatureMode.Attack;
        }
        n.Awake = true;
    }

    /// <summary>Angers a nest at a colony now (as if struck): defenders come out (debug, scripted events).</summary>
    public void Provoke(ColonySim sim, Nest n, int faction) => Alarm(sim, n, faction, Math.Min(n.Population, 16));

    /// <summary>A crowded nest raids: most of its creatures follow one trail to the nearest colonist of the crowding colony.</summary>
    private void StartRaid(ColonySim sim, Nest n)
    {
        long t = sim.Tick;
        n.NextRaid = t + RaidInterval + (int)(Gear.Roll(t, n.Id, 0, 31) * Needs.Day / 2);
        if (n.Population < 4) return;
        Colonist? target = null;
        int best = int.MaxValue;
        foreach (var c in _prey)
        {
            if (((n.Crowded >> c.Faction) & 1) == 0) continue;
            int d = Math.Max(Math.Abs(c.Position.X - n.Entrance.X), Math.Abs(c.Position.Y - n.Entrance.Y));
            if (d <= RaidRange && d < best) (best, target) = (d, c);
        }
        if (target is null) return;
        sim.Connectivity.Update();
        int component = sim.Connectivity.ComponentOf(n.Entrance);
        if (component < 0) return;
        var goals = new HashSet<TilePos>();
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
        {
            var g = target.Position.Offset(dx, dy, dz);
            if (sim.Connectivity.ComponentOf(g) == component) goals.Add(g);
        }
        if (goals.Count == 0) return; // the colony is out of the ants' reach (walled off, across water)
        var corridor = sim.Connectivity.Corridor(n.Entrance, goals);
        if (corridor is null) return;
        var trail = Pathfinder.FindPathToAny(sim.World, n.Entrance, goals, target.Position, 40_000,
            p => corridor.Contains(sim.Connectivity.RegionOf(p)));
        if (trail is null) return;
        n.Trail = trail;
        n.ToEmerge = Math.Min(MaxRaid, Math.Max(4, n.Population / 3)); // a raiding party, not the whole nest
        n.EmergeMode = CreatureMode.Raid;
        n.Awake = true;
        sim.Announce($"A swarm of {n.Kind.Plural} pours out of their nest to raid {sim.Factions[target.Faction].Name}!", target.Faction);
    }

    // ---- Fighting ---------------------------------------------------------------------------------------------

    /// <summary>A blow from a colonist (or anything else, <paramref name="by"/> null). Provokes its nest.</summary>
    public void Hurt(ColonySim sim, Creature c, int damage, Colonist? by)
    {
        if (c.Dead) return;
        c.Health -= damage;
        if (by != null)
        {
            if (NestById(c.NestId) is { } n) Alarm(sim, n, by.Faction, 8);
            else if (by.Faction is >= 0 and < Nest.MaxFactions) c.LostMask |= 1L << by.Faction;
            if (c.Target is null && !by.Downed)
            {
                c.Target = by;
                c.TargetId = by.Id;
            }
            if (c.Mode is CreatureMode.Wander or CreatureMode.Return) c.Mode = CreatureMode.Attack;
        }
        if (c.Health > 0) return;
        c.Dead = true;
        Slain++;
        if (NestById(c.NestId) is { } home) home.Out--;
        if (c.Kind.Drop is { } drop && Gear.Roll(sim.Tick, c.Id, 0, 41) < c.Kind.DropChance)
            sim.Items.Drop(c.Position, Industry.Catalog.Item(drop), 1);
    }

    /// <summary>A blow against a nest's mound: every one brings defenders out; at 0 the nest is destroyed.</summary>
    public void StrikeNest(ColonySim sim, Nest n, int damage, Colonist by)
    {
        if (!_nestById.ContainsKey(n.Id)) return;
        n.Health -= Math.Max(1, damage);
        Alarm(sim, n, by.Faction, n.Population);
        if (n.Health <= 0) DestroyNest(sim, n, by.Faction);
    }

    private void DestroyNest(ColonySim sim, Nest n, int faction)
    {
        long t = sim.Tick;
        long mask = n.HostileMask(t) | (faction is >= 0 and < Nest.MaxFactions ? 1L << faction : 0);
        // Those still underground boil out (a last swarm, at most 20); the rest perish in the collapse.
        int last = Math.Min(20, n.Population);
        for (int i = 0; i < last; i++) Spawn(sim, n, CreatureMode.Lost);
        foreach (var c in _creatures)
        {
            if (c.NestId != n.Id || c.Dead) continue;
            c.NestId = -1;
            c.Mode = CreatureMode.Lost;
            c.LostMask = mask;
            c.Timer = Needs.Day / 2 + (int)(Gear.Roll(t, c.Id, 0, 51) * Needs.Day / 4);
        }
        _nests.Remove(n);
        _nestById.Remove(n.Id);
        NestsDestroyed++;
        Version++;
        sim.Announce($"{(faction >= 0 && faction < sim.Factions.Count ? sim.Factions[faction].Name : "Someone")} destroyed a nest of {n.Kind.Plural}", faction);
    }

    private void Bite(ColonySim sim, Creature c, Colonist target)
    {
        long t = sim.Tick;
        c.Cooldown = c.Kind.Attack.Cooldown;
        c.LastStrike = t;
        int dmg = Gear.Resolve(c.Kind.Attack.Damage, c.Kind.Attack.Pierce, target.Protect, target.Block,
            Gear.Roll(t, c.Id, target.Id, 1), Gear.Roll(t, c.Id, target.Id, 2));
        target.TakeHit(sim, dmg, c);
    }

    /// <summary>The colonies a creature fights now (its nest's grudges, or a lost one's own).</summary>
    public long HostileMask(Creature c, long tick) => NestById(c.NestId) is { } n ? n.HostileMask(tick) : c.LostMask;

    // ---- Creatures ----------------------------------------------------------------------------------------------

    private void CreatureTick(ColonySim sim, Creature c)
    {
        long t = sim.Tick;
        var w = sim.World;
        if (c.Cooldown > 0) c.Cooldown--;
        var nest = NestById(c.NestId);
        long mask = nest?.HostileMask(t) ?? c.LostMask;

        if (c.Mode == CreatureMode.Lost && --c.Timer <= 0)
        {
            Vanish(c); // wanders off for good
            return;
        }
        if (c.Mode == CreatureMode.Wander && --c.Timer <= 0) c.Mode = CreatureMode.Return;

        // Lose a target that is down, gone, no longer an enemy, or (for defenders) has left the nest's ground.
        if (c.Target is { } old && (old.Downed || old.Dead || ((mask >> old.Faction) & 1) == 0 || TooFar(c, nest, old.Position)))
        {
            c.Target = null;
            c.TargetId = -1;
        }
        if (c.Target is null && mask != 0 && (t + c.Id) % 8 == 0) Acquire(c, nest, mask);

        if (c.Target is { } tg && c.NextStep is null && Gear.InReach(c.Position, tg.Position, 1))
        {
            // One biter per tile: a later ant on a held tile steps to a free one beside its prey (a swarm surrounds it).
            if (!_biting.Add(c.Position))
            {
                Movement.GetMoves(w, c.Position, _moves);
                foreach (var m in _moves)
                    if (!_biting.Contains(m.To) && Gear.InReach(m.To, tg.Position, 1))
                    {
                        _biting.Add(m.To);
                        c.NextStep = m.To;
                        c.StepTotal = Math.Max(1, (int)MathF.Round(m.Cost * c.Kind.StepTicks));
                        c.StepDone = 0;
                        return;
                    }
            }
            if (c.Cooldown == 0) Bite(sim, c, tg);
            return;
        }

        if (c.NextStep is { } step)
        {
            if (++c.StepDone >= c.StepTotal)
            {
                c.Position = step;
                c.NextStep = null;
            }
            return;
        }

        (TilePos To, float Cost)? next = null;
        if (c.Target is { } target)
        {
            int d = Dist(c.Position, target.Position);
            if (d > 4 && nest?.Threat is { } field && field.Downhill(w, c.Position, _moves) is { } q) next = Pick(q);
            next ??= Greedy(sim, c, target.Position);
        }
        else switch (c.Mode)
        {
            case CreatureMode.Raid:
                next = FollowTrail(sim, c, nest);
                break;
            case CreatureMode.Attack:
                c.Mode = CreatureMode.Return; // no foe in sight: home
                break;
            case CreatureMode.Wander:
                next = Forage(sim, c, nest!);
                break;
            case CreatureMode.Return:
                if (nest is null) { Vanish(c); return; }
                if (Gear.InReach(c.Position, nest.Entrance, 1))
                {
                    Burrow(c, nest);
                    return;
                }
                if (HomeField(sim, nest).Downhill(w, c.Position, _moves) is { } h) next = Pick(h);
                next ??= Greedy(sim, c, nest.Entrance);
                break;
            case CreatureMode.Lost:
                next = Random(sim, c);
                break;
        }

        if (next is { } n)
        {
            c.Stuck = 0;
            c.NextStep = n.To;
            c.StepTotal = Math.Max(1, (int)MathF.Round(n.Cost * c.Kind.StepTicks));
            c.StepDone = 0;
        }
        else if (++c.Stuck > 120)
        {
            // Can't get anywhere (walled in, cut off by water): it digs down and is gone from here.
            if (nest is not null) Burrow(c, nest);
            else Vanish(c);
        }
    }

    private bool TooFar(Creature c, Nest? nest, TilePos p) => c.Mode switch
    {
        CreatureMode.Raid => Dist(c.Position, p) > 24,
        CreatureMode.Lost => Dist(c.Position, p) > AggroRange + 6,
        _ => nest is not null && Dist(nest.Entrance, p) > nest.Radius + AggroRange + 8,
    };

    /// <summary>The nearest colonist of a colony it is hostile to that it would go for (defenders stay near their nest).</summary>
    private void Acquire(Creature c, Nest? nest, long mask)
    {
        int range = c.Mode switch { CreatureMode.Raid => 20, CreatureMode.Lost => AggroRange + 4, _ => AggroRange };
        Colonist? best = null;
        int bestD = int.MaxValue;
        foreach (var p in _prey)
        {
            if (((mask >> p.Faction) & 1) == 0) continue;
            int d = Dist(c.Position, p.Position);
            if (d > range || d >= bestD) continue;
            if (c.Mode is not (CreatureMode.Raid or CreatureMode.Lost) && nest is not null && Dist(nest.Entrance, p.Position) > nest.Radius + AggroRange) continue;
            (best, bestD) = (p, d);
        }
        if (best is null) return;
        c.Target = best;
        c.TargetId = best.Id;
        if (c.Mode is CreatureMode.Wander or CreatureMode.Return) c.Mode = CreatureMode.Attack;
    }

    private static int Dist(TilePos a, TilePos b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)) + Math.Abs(a.Z - b.Z);

    private (TilePos, float)? Pick(TilePos q)
    {
        foreach (var m in _moves)
            if (m.To == q) return m;
        return null;
    }

    /// <summary>The legal step that gets closest to <paramref name="goal"/> (only if it is closer than here).</summary>
    private (TilePos, float)? Greedy(ColonySim sim, Creature c, TilePos goal)
    {
        Movement.GetMoves(sim.World, c.Position, _moves);
        int here = 10 * Math.Max(Math.Abs(c.Position.X - goal.X), Math.Abs(c.Position.Y - goal.Y)) + 12 * Math.Abs(c.Position.Z - goal.Z);
        (TilePos, float)? best = null;
        int bestD = here;
        foreach (var m in _moves)
        {
            int d = 10 * Math.Max(Math.Abs(m.To.X - goal.X), Math.Abs(m.To.Y - goal.Y)) + 12 * Math.Abs(m.To.Z - goal.Z);
            if (d < bestD) (best, bestD) = (m, d);
        }
        // Blocked: a random sidestep now and then gets round small obstacles.
        if (best is null && c.Stuck > 10 && c.Stuck % 4 == 0) return Random(sim, c);
        return best;
    }

    private (TilePos, float)? Random(ColonySim sim, Creature c)
    {
        Movement.GetMoves(sim.World, c.Position, _moves);
        if (_moves.Count == 0) return null;
        return _moves[(int)(Gear.Roll(sim.Tick, c.Id, c.Stuck, 61) * _moves.Count) % _moves.Count];
    }

    /// <summary>A random step that stays within the nest's territory (by the home field).</summary>
    private (TilePos, float)? Forage(ColonySim sim, Creature c, Nest nest)
    {
        var field = HomeField(sim, nest);
        Movement.GetMoves(sim.World, c.Position, _moves);
        int limit = nest.Radius * 10;
        int count = 0;
        foreach (var m in _moves)
            if (field.TryGet(m.To, out int d) && d <= limit) count++;
        if (count == 0) return Greedy(sim, c, nest.Entrance);
        int pick = (int)(Gear.Roll(sim.Tick, c.Id, 0, 62) * count) % count;
        foreach (var m in _moves)
            if (field.TryGet(m.To, out int d) && d <= limit && pick-- == 0) return m;
        return null;
    }

    /// <summary>Raiders walk the nest's trail; at its end they look about for colonists to fight.</summary>
    private (TilePos, float)? FollowTrail(ColonySim sim, Creature c, Nest? nest)
    {
        if (nest?.Trail is not { Count: > 0 } trail)
        {
            c.Mode = CreatureMode.Attack;
            return null;
        }
        while (c.TrailIndex < trail.Count && Dist(c.Position, trail[c.TrailIndex]) <= 1 && c.Position == trail[c.TrailIndex]) c.TrailIndex++;
        if (c.TrailIndex >= trail.Count)
        {
            c.Mode = CreatureMode.Attack; // arrived: nobody near (Acquire found no one) means home again next tick
            Acquire(c, nest, nest.HostileMask(sim.Tick));
            return null;
        }
        var goal = trail[c.TrailIndex];
        Movement.GetMoves(sim.World, c.Position, _moves);
        foreach (var m in _moves)
            if (m.To == goal) return m;
        // Pushed off the trail (chasing, or the ground changed): find the nearest trail cell ahead and head for it.
        int bestI = c.TrailIndex, bestD = int.MaxValue;
        for (int i = c.TrailIndex; i < Math.Min(trail.Count, c.TrailIndex + 40); i++)
        {
            int d = Dist(c.Position, trail[i]);
            if (d < bestD) (bestI, bestD) = (i, d);
        }
        c.TrailIndex = bestI;
        return Greedy(sim, c, trail[bestI]);
    }

    /// <summary>The flood round a nest's entrance (built on first need; rebuilt when the ground there changes).</summary>
    private FlowField HomeField(ColonySim sim, Nest n)
    {
        if (n.Home is null || (n.HomeDirty && sim.Tick - n.HomeBuilt >= 100))
        {
            int r = n.Radius + 10;
            var e = n.Entrance;
            n.Home = FlowField.Build(sim.World, [e], e.X - r, e.Y - r, e.X + r, e.Y + r, 8000, _moves);
            n.HomeBuilt = sim.Tick;
            n.HomeDirty = false;
        }
        return n.Home;
    }

    /// <summary>The flood from the colonists a nest's fighters are after, over a box round the fight.</summary>
    private void BuildThreat(ColonySim sim, Nest n)
    {
        n.ThreatBuilt = sim.Tick;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        var sources = new List<TilePos>();
        foreach (var c in _creatures)
        {
            if (c.NestId != n.Id || c.Dead || c.Target is not { } tg) continue;
            Grow(c.Position);
            Grow(tg.Position);
            if (!sources.Contains(tg.Position)) sources.Add(tg.Position);
        }
        void Grow(TilePos p)
        {
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        if (sources.Count == 0)
        {
            n.Threat = null;
            return;
        }
        const int pad = 6, span = 72;
        int cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        minX = Math.Max(minX - pad, cx - span / 2); maxX = Math.Min(maxX + pad, cx + span / 2);
        minY = Math.Max(minY - pad, cy - span / 2); maxY = Math.Min(maxY + pad, cy + span / 2);
        n.Threat = FlowField.Build(sim.World, sources, minX, minY, maxX, maxY, 6000, _moves);
    }

    /// <summary>The ground changed: home fields over it are rebuilt (at most every 100 ticks).</summary>
    internal void OnChunkChanged(VoxelWorld world, int index)
    {
        if (_nests.Count == 0) return;
        var cc = world.Chunks[index].Coord;
        int x0 = cc.X << Chunk.Shift, y0 = cc.Y << Chunk.Shift;
        foreach (var n in _nests)
        {
            if (n.Home is null) continue;
            int r = n.Radius + 10;
            if (x0 + Chunk.Size > n.Entrance.X - r && x0 <= n.Entrance.X + r && y0 + Chunk.Size > n.Entrance.Y - r && y0 <= n.Entrance.Y + r)
                n.HomeDirty = true;
        }
    }

    // ---- Lookups for colonists and views -------------------------------------------------------------------------

    private static long BucketKey(int x, int y) => ((long)(x >> 3) << 32) | (uint)(y >> 3);

    private void RebuildBuckets(long tick)
    {
        if (tick % 1000 == 0) _buckets.Clear();
        else foreach (var list in _buckets.Values) list.Clear();
        foreach (var c in _creatures)
        {
            long k = BucketKey(c.Position.X, c.Position.Y);
            if (!_buckets.TryGetValue(k, out var list)) _buckets[k] = list = new List<Creature>();
            list.Add(c);
        }
    }

    /// <summary>Living creatures within <paramref name="range"/> tiles (horizontally; a few levels) of a cell, by list order.</summary>
    public IEnumerable<Creature> Near(TilePos p, int range)
    {
        for (int by = (p.Y - range) >> 3; by <= (p.Y + range) >> 3; by++)
        for (int bx = (p.X - range) >> 3; bx <= (p.X + range) >> 3; bx++)
        {
            if (!_buckets.TryGetValue(((long)bx << 32) | (uint)by, out var list)) continue;
            foreach (var c in list)
                if (!c.Dead && Math.Max(Math.Abs(c.Position.X - p.X), Math.Abs(c.Position.Y - p.Y)) <= range && Math.Abs(c.Position.Z - p.Z) <= 4)
                    yield return c;
        }
    }

    /// <summary>
    /// The nearest creature a colonist should fight near <paramref name="center"/>: one after it, one whose nest (or
    /// grudge) is against its colony, or (<paramref name="nestId"/>) any of a nest it was sent to destroy.
    /// </summary>
    public Creature? NearestFoe(ColonySim sim, Colonist me, TilePos center, int range, int nestId = -1)
    {
        Creature? best = null;
        int bestD = int.MaxValue;
        foreach (var c in Near(center, range))
        {
            bool foe = c.TargetId == me.Id || (nestId >= 0 && c.NestId == nestId) || ((HostileMask(c, sim.Tick) >> me.Faction) & 1) != 0;
            if (!foe) continue;
            int d = Dist(me.Position, c.Position);
            if (d < bestD || (d == bestD && c.Id < best!.Id)) (best, bestD) = (c, d);
        }
        return best;
    }

    /// <summary>The nest whose entrance is nearest a cell, within <paramref name="range"/> tiles.</summary>
    public Nest? NestNear(TilePos p, int range)
    {
        Nest? best = null;
        int bestD = int.MaxValue;
        foreach (var n in _nests)
        {
            int d = Dist(p, n.Entrance);
            if (d <= range && d < bestD) (best, bestD) = (n, d);
        }
        return best;
    }

    // ---- Snapshot ---------------------------------------------------------------------------------------------

    internal void Settle(ColonySim sim)
    {
        _creatures.RemoveAll(c => c.Dead);
        foreach (var n in _nests) n.Settle();
        foreach (var c in _creatures) c.Settle();
        _landSeen = (-1, -1, -1, -1);
        _buckets.Clear();
        RebuildBuckets(1);
    }

    internal void SnapSave(BinaryWriter w)
    {
        w.Write(_nextNest); w.Write(_nextCreature); w.Write(Slain); w.Write(NestsDestroyed); w.Write(Version); w.Write(MaxNests);
        w.Write(_nests.Count);
        foreach (var n in _nests) n.SnapSave(w);
        w.Write(_creatures.Count);
        foreach (var c in _creatures) c.SnapSave(w);
    }

    internal void SnapLoad(BinaryReader r)
    {
        _nextNest = r.ReadInt32(); _nextCreature = r.ReadInt32(); Slain = r.ReadInt32(); NestsDestroyed = r.ReadInt32();
        Version = r.ReadInt32(); MaxNests = r.ReadInt32();
        int nn = r.ReadInt32();
        for (int i = 0; i < nn; i++)
        {
            var n = Nest.SnapLoad(r);
            _nests.Add(n);
            _nestById[n.Id] = n;
        }
        int nc = r.ReadInt32();
        for (int i = 0; i < nc; i++) _creatures.Add(Creature.SnapLoad(r));
        RebuildBuckets(1);
    }

    /// <summary>Links creatures' targets after a load (none: targets are dropped when settling).</summary>
    internal void AfterLoad() { }
}
