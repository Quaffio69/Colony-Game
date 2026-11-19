using Colony.Core.Combat;
using Colony.Core.Items;
using Colony.Core.Navigation;
using Colony.Core.World;

namespace Colony.Core.Agents;

/// <summary>
/// Health and fighting. A colonist has <see cref="MaxHealth"/> health, healing slowly (faster asleep). At 0 it is
/// struck down: it lies where it fell, recovering until it can stand (<see cref="StandUpHealth"/>), and rarely
/// (<see cref="DeathChance"/>) dies of its wounds. Attacked while going about its work it fights back with whatever it
/// holds, or flees when badly hurt (children always flee). Drafted (by its player), it drops its work, fetches the best
/// gear its colony has lying about (one weapon, body armour, a shield if the weapon leaves a hand free), marches where
/// it is sent, attacks a nest it is sent against, and fights any foe that comes within <see cref="EngageRange"/>.
/// </summary>
public sealed partial class Colonist
{
    public const int MaxHealth = 100;
    public const int FleeHealth = 35;
    public const int StandUpHealth = 25;
    public const float DeathChance = 0.12f;
    public const int EngageRange = 10;
    /// <summary>Ticks after the last blow before an undrafted colonist goes back to work.</summary>
    private const int CalmAfter = 80;

    public int Health { get; internal set; } = MaxHealth;
    public bool Downed { get; private set; }
    public bool Dead { get; internal set; }
    public bool Drafted { get; private set; }
    /// <summary>Where a drafted colonist was sent (null: holds where it stands).</summary>
    public TilePos? Rally { get; private set; }
    /// <summary>The nest a drafted colonist was sent to destroy (-1: none).</summary>
    public int AttackNestId { get; private set; } = -1;
    public ItemType? Weapon { get; private set; }
    public ItemType? Armour { get; private set; }
    public ItemType? Shield { get; private set; }
    public long LastHurt { get; private set; } = long.MinValue / 2;
    public long LastStrike { get; private set; } = long.MinValue / 2;
    private int _lastAttacker = -1;
    private int _cooldown;
    private long _gearRetry;
    private (TilePos Cell, ItemType Type) _equip;
    // Caches (cleared by Settle).
    private Creature? _foe;
    private bool _chasing;
    private TilePos _chaseGoal;
    private long _chasePlanned, _chaseRetry;
    /// <summary>A free tile it is moving to so as not to fight from one another fighter holds.</summary>
    private TilePos? _fanningTo;
    private readonly List<(TilePos To, float Cost)> _combatMoves = new(12);

    public Attack Attack => Gear.Of(Weapon);
    public int Protect => Armour is { } a ? a.Def.Protect : 0;
    public bool TwoHanded => Weapon is { } w && w.Def.TwoHanded;
    public int Block => Shield is { } s && !TwoHanded ? s.Def.Block : 0;
    /// <summary>The creature it is fighting (null if none).</summary>
    public Creature? Foe => _foe;

    /// <summary>Handles the tick when the colonist is down, drafted or in a fight. False: it gets on with its life.</summary>
    private bool TickCombat(ColonySim sim)
    {
        long t = sim.Tick;
        if (Downed)
        {
            if (t % 30 == Id % 30) Health++;
            if (Health >= StandUpHealth)
            {
                Downed = false;
                sim.Announce($"{Name} is back on their feet", Faction);
            }
            return true;
        }
        int period = Task == ColonistTask.Sleep && State == ColonistState.Working ? 20 : 60;
        if (Health < MaxHealth && t % period == Id % period) Health++;
        if (_cooldown > 0) _cooldown--;
        if (Drafted) return DraftedTick(sim);

        if (t - LastHurt < CalmAfter && sim.Wildlife.NearestFoe(sim, this, Position, 3) is { } foe)
        {
            _foe = foe;
            if (Health < FleeHealth || IsChild)
            {
                BeginCombat(sim, ColonistTask.Flee);
                Flee(sim, foe.Position);
            }
            else
            {
                BeginCombat(sim, ColonistTask.Fight);
                Engage(sim, foe.Position, foe, null);
            }
            return true;
        }
        _foe = null;
        if (Task is ColonistTask.Fight or ColonistTask.Flee) EndCombat();
        return false;
    }

    private bool DraftedTick(ColonySim sim)
    {
        long t = sim.Tick;
        if (Task is ColonistTask.Equip or ColonistTask.Eat) return false; // the normal round fetches gear (or a meal, when famished)
        if (Task is not (ColonistTask.Fight or ColonistTask.Rally))
        {
            if (Task != ColonistTask.None || State != ColonistState.Idle || CarryingCount > 0) Abandon(sim);
            Task = ColonistTask.Rally;
        }
        var nest = AttackNestId >= 0 ? sim.Wildlife.NestById(AttackNestId) : null;
        if (AttackNestId >= 0 && nest is null) AttackNestId = -1; // destroyed

        if (_foe is { } f && (f.Dead || !IsFoe(sim, f))) _foe = null;
        if (_foe is null || (t + Id) % 10 == 0)
            _foe = sim.Wildlife.NearestFoe(sim, this, Position, Math.Max(EngageRange, Attack.Reach + 2), AttackNestId)
                   ?? (nest is not null ? sim.Wildlife.NearestFoe(sim, this, nest.Entrance, nest.Radius, AttackNestId) : null);

        if (_foe is { } foe)
        {
            Task = ColonistTask.Fight;
            Engage(sim, foe.Position, foe, null);
            return true;
        }
        if (nest is not null)
        {
            Task = ColonistTask.Fight;
            Engage(sim, nest.Entrance, null, nest);
            return true;
        }
        Task = ColonistTask.Rally;
        if (Rally is { } spot && Position != spot)
        {
            Chase(sim, spot, exact: true);
            return true;
        }
        if (NextStep is not null) AdvanceMove(sim); // finish the step it is on
        else if (_chasing) StopMoving();
        if (t >= _gearRetry && NextStep is null) TryEquip(sim);
        return true;
    }

    private bool IsFoe(ColonySim sim, Creature c) =>
        c.TargetId == Id || (AttackNestId >= 0 && c.NestId == AttackNestId) || ((sim.Wildlife.HostileMask(c, sim.Tick) >> Faction) & 1) != 0;

    /// <summary>Drops whatever work it had (claims released, load dropped) to fight or flee.</summary>
    private void BeginCombat(ColonySim sim, ColonistTask task)
    {
        if (Task == task) return;
        if (Task is not (ColonistTask.Fight or ColonistTask.Flee or ColonistTask.Rally)) Abandon(sim);
        else StopMoving();
        Task = task;
    }

    private void EndCombat()
    {
        StopMoving();
        _fanningTo = null;
        Finish();
    }

    private void StopMoving()
    {
        _path.Clear();
        _trip = null;
        NextStep = null;
        _chasing = false;
        State = ColonistState.Idle;
    }

    /// <summary>Strikes when in reach (and in sight, for a bow), from a tile of its own; otherwise closes in.</summary>
    private void Engage(ColonySim sim, TilePos target, Creature? foe, Nest? nest)
    {
        var a = Attack;
        if (_fanningTo is { } spot)
        {
            // On its way to a free fighting spot: keep going until it gets there (or the spot is no use).
            if (NextStep is null && (Position == spot || !Gear.InReach(spot, target, a.Reach))) _fanningTo = null;
            else
            {
                Chase(sim, spot, exact: true);
                if (!_chasing && NextStep is null) _fanningTo = null; // can't get there: fight from here
                return;
            }
        }
        if (NextStep is null && Gear.InReach(Position, target, a.Reach) && (!a.Ranged || Gear.LineOfSight(sim.World, Position, target)))
        {
            // Fighters fan out (RimWorld-style): colonists squeeze past each other anywhere, but never fight from one tile.
            if (TileHeldByFighter(sim) && FreeSpot(sim, target, a) is { } free)
            {
                _fanningTo = free;
                StopMoving();
                Chase(sim, free, exact: true);
                return;
            }
            if (_chasing) StopMoving();
            if (_cooldown == 0) Strike(sim, foe, nest, a);
            return;
        }
        Chase(sim, target, exact: false);
    }

    private bool IsFighter => !Downed && (Drafted || Task is ColonistTask.Fight);

    /// <summary>Another fighter (the lower id keeps a tile) stands on this tile.</summary>
    private bool TileHeldByFighter(ColonySim sim)
    {
        foreach (var o in sim.Colonists)
            if (o != this && o.Id < Id && o.Position == Position && o.NextStep is null && o.IsFighter) return true;
        return false;
    }

    /// <summary>Tiles other fighters stand on or are heading to.</summary>
    private HashSet<TilePos> FighterTiles(ColonySim sim)
    {
        var taken = new HashSet<TilePos>();
        foreach (var o in sim.Colonists)
        {
            if (o == this || !o.IsFighter) continue;
            taken.Add(o._fanningTo ?? (o._chasing ? o._path.LastOrDefault(o.Position) : o.Position));
            if (o._fanningTo is not null || o._chasing) taken.Add(o.Position);
        }
        return taken;
    }

    /// <summary>The nearest standable tile in reach of the target (and in sight, for a bow) that no other fighter holds.</summary>
    private TilePos? FreeSpot(ColonySim sim, TilePos target, Attack a)
    {
        int component = sim.Connectivity.ComponentOf(Position);
        if (component < 0) return null;
        var taken = FighterTiles(sim);
        int r = Math.Min(a.Reach, 3);
        TilePos? best = null;
        int bestD = int.MaxValue;
        // Ranged: look round itself (any spot in range will do); melee: round the target.
        var centre = a.Ranged ? Position : target;
        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            var p = centre.Offset(dx, dy, dz);
            if (p == Position || taken.Contains(p) || !Gear.InReach(p, target, a.Reach)) continue;
            if (sim.Connectivity.ComponentOf(p) != component) continue;
            int d = Math.Max(Math.Abs(p.X - Position.X), Math.Abs(p.Y - Position.Y)) * 4 + Math.Abs(p.Z - Position.Z) * 3 + Math.Abs(p.X - target.X) + Math.Abs(p.Y - target.Y);
            if (d >= bestD) continue;
            if (a.Ranged && !Gear.LineOfSight(sim.World, p, target)) continue;
            (best, bestD) = (p, d);
        }
        return best;
    }

    /// <summary>Walks towards a cell (next to it unless <paramref name="exact"/>), replanning as the target moves.</summary>
    private void Chase(ColonySim sim, TilePos target, bool exact)
    {
        long t = sim.Tick;
        bool stale = !_chasing || Math.Max(Math.Abs(target.X - _chaseGoal.X), Math.Abs(target.Y - _chaseGoal.Y)) + Math.Abs(target.Z - _chaseGoal.Z) > 1
                     || (!exact && t - _chasePlanned > 40);
        if (stale && NextStep is null && t >= _chaseRetry)
        {
            _chasing = false;
            _chaseRetry = t + 20;
            if (sim.PathBudgetExhausted) _chaseRetry = t + 1;
            else
            {
                int component = sim.Connectivity.ComponentOf(Position);
                var goals = exact && sim.Connectivity.ComponentOf(target) == component && component >= 0
                    ? new HashSet<TilePos> { target }
                    : component >= 0 ? ReachableAround(sim, target, component) : new HashSet<TilePos>();
                if (!exact && goals.Count > 1)
                {
                    // Spread round the target rather than piling onto one tile with the rest of the squad.
                    var taken = FighterTiles(sim);
                    var free = new HashSet<TilePos>(goals.Where(g => !taken.Contains(g)));
                    if (free.Count > 0) goals = free;
                }
                if (Route(sim, goals, target) is { } path)
                {
                    _path.Clear();
                    foreach (var p in path) _path.Enqueue(p);
                    _trip = _plannedTrip;
                    _chasing = true;
                    _chaseGoal = target;
                    _chasePlanned = t;
                    _chaseRetry = 0; // only a failed plan waits before trying again
                    State = ColonistState.Moving;
                }
            }
        }
        if (!_chasing && NextStep is null) return;
        if (AdvanceMove(sim) != MoveResult.Moving)
        {
            _chasing = false;
            State = ColonistState.Idle;
            if (exact && Position != target && Rally == target) _chaseRetry = t + 40;
        }
    }

    private void Strike(ColonySim sim, Creature? foe, Nest? nest, Attack a)
    {
        long t = sim.Tick;
        _cooldown = a.Cooldown;
        LastStrike = t;
        if (foe is not null)
        {
            Hit(sim, foe, a);
            int more = a.Cleave - 1;
            if (more > 0)
                foreach (var c in sim.Wildlife.Near(Position, a.Reach).ToList())
                {
                    if (more == 0) break;
                    if (c == foe || c.Dead || !Gear.InReach(Position, c.Position, a.Reach) || !IsFoe(sim, c)) continue;
                    Hit(sim, c, a);
                    more--;
                }
        }
        else if (nest is not null) sim.Wildlife.StrikeNest(sim, nest, a.Damage, this);
    }

    private void Hit(ColonySim sim, Creature c, Attack a)
    {
        long t = sim.Tick;
        int dmg = Gear.Resolve(a.Damage, a.Pierce, 0, 0, Gear.Roll(t, Id, c.Id, 3), Gear.Roll(t, Id, c.Id, 4));
        sim.Wildlife.Hurt(sim, c, dmg, this);
    }

    /// <summary>Steps away from the threat (to whichever neighbour is furthest from it).</summary>
    private void Flee(ColonySim sim, TilePos from)
    {
        if (NextStep is null)
        {
            Movement.GetMoves(sim.World, Position, _combatMoves);
            int Away(TilePos p) => Math.Max(Math.Abs(p.X - from.X), Math.Abs(p.Y - from.Y)) * 10 + Math.Abs(p.Z - from.Z) * 4;
            TilePos? best = null;
            int bestD = Away(Position);
            foreach (var (q, _) in _combatMoves)
                if (Away(q) > bestD) (best, bestD) = (q, Away(q));
            if (best is not { } step) return;
            _path.Clear();
            _trip = null;
            _path.Enqueue(step);
            State = ColonistState.Moving;
        }
        if (AdvanceMove(sim) != MoveResult.Moving) State = ColonistState.Idle;
    }

    /// <summary>A blow landed on it (<paramref name="from"/>: the creature, if one).</summary>
    internal void TakeHit(ColonySim sim, int damage, Creature? from)
    {
        if (Downed || Dead) return;
        LastHurt = sim.Tick;
        _lastAttacker = from?.Id ?? -1;
        Health -= damage;
        if (Health > 0) return;
        Health = 0;
        Downed = true;
        Abandon(sim);
        StopMoving();
        _foe = null;
        sim.Announce($"{Name} has been struck down{(from is not null ? $" by a {from.Kind.Name.ToLowerInvariant()}" : "")}", Faction);
        if (Gear.Roll(sim.Tick, Id, _lastAttacker, 71) < DeathChance) sim.QueueDeath(this, $"{Name} has died of their wounds");
    }

    // ---- Orders (commands) ------------------------------------------------------------------------------------------

    internal void SetDrafted(ColonySim sim, bool on)
    {
        if (on == Drafted) return;
        Drafted = on;
        _foe = null;
        _fanningTo = null;
        if (on)
        {
            if (!Downed && (Task != ColonistTask.None || State != ColonistState.Idle || CarryingCount > 0)) Abandon(sim);
            _gearRetry = 0;
        }
        else
        {
            Rally = null;
            AttackNestId = -1;
            if (Task is ColonistTask.Fight or ColonistTask.Rally) EndCombat();
        }
    }

    internal void OrderMove(TilePos spot)
    {
        Rally = spot;
        AttackNestId = -1;
        _chasing = false;
        _chaseRetry = 0;
    }

    internal void OrderAttack(int nest)
    {
        AttackNestId = nest;
        Rally = null;
        _chasing = false;
        _chaseRetry = 0;
    }

    // ---- Gear ---------------------------------------------------------------------------------------------------------

    /// <summary>Would this piece be an upgrade for its slot?</summary>
    public bool Better(ItemType t) => t.Def.Gear switch
    {
        Gear.WeaponSlot => Weapon is not { } w || Gear.Score(t) > Gear.Score(w),
        Gear.BodySlot => Armour is not { } b || Gear.Score(t) > Gear.Score(b),
        Gear.ShieldSlot => !TwoHanded && (Shield is not { } s || Gear.Score(t) > Gear.Score(s)),
        _ => false,
    };

    /// <summary>Heads for the nearest gear lying about (its colony's or loose) that beats what it has.</summary>
    private void TryEquip(ColonySim sim)
    {
        long t = sim.Tick;
        _gearRetry = t + 300;
        if (IsChild) return;
        if (sim.PathBudgetExhausted)
        {
            _gearRetry = t + 5;
            return;
        }
        int component = sim.Connectivity.ComponentOf(Position);
        if (component < 0) return;
        TilePos? best = null;
        ItemType bestType = default;
        int bestCost = int.MaxValue;
        foreach (var (p, pile) in sim.Items.All)
        {
            if (!Gear.IsGear(pile.Type) || pile.Available <= 0 || !Better(pile.Type)) continue;
            if (!Needs.Usable(sim, p, Faction) || sim.Connectivity.ComponentOf(p) != component) continue;
            int cost = Math.Abs(p.X - Position.X) + Math.Abs(p.Y - Position.Y) + 2 * Math.Abs(p.Z - Position.Z);
            if (cost < bestCost || (cost == bestCost && CompareCells(p, best!.Value) < 0)) (best, bestType, bestCost) = (p, pile.Type, cost);
        }
        if (best is not { } cell) return;
        var path = Route(sim, ReachableAround(sim, cell, component), cell);
        if (path is null || !sim.Items.Reserve(cell, 1)) return;
        _equip = (cell, bestType);
        Begin(ColonistTask.Equip, cell, path);
    }

    private void FinishEquip(ColonySim sim)
    {
        int taken = sim.Items.TakeReserved(_equip.Cell, 1);
        if (taken > 0) PutOn(sim, _equip.Type);
        Finish();
        _gearRetry = 0; // the next slot straight away
    }

    /// <summary>Wears or wields a piece; what it replaces is dropped where it stands. (Sandbox and demos; in play colonists fetch gear.)</summary>
    public void PutOn(ColonySim sim, ItemType t)
    {
        ItemType? old;
        switch (t.Def.Gear)
        {
            case Gear.WeaponSlot:
                old = Weapon;
                Weapon = t;
                if (t.Def.TwoHanded && Shield is { } shield)
                {
                    sim.Items.Drop(Position, shield, 1); // no hand free for it
                    Shield = null;
                }
                break;
            case Gear.BodySlot: old = Armour; Armour = t; break;
            case Gear.ShieldSlot: old = Shield; Shield = t; break;
            default: return;
        }
        if (old is { } o) sim.Items.Drop(Position, o, 1);
    }

    /// <summary>Drops all its gear (on death).</summary>
    internal void DropGear(ColonySim sim)
    {
        foreach (var g in new[] { Weapon, Armour, Shield })
            if (g is { } item) sim.Items.Drop(Position, item, 1);
        Weapon = Armour = Shield = null;
    }

    private string DescribeCombat() => Task switch
    {
        ColonistTask.Equip => $"fetching a {ItemInfo.Name(_equip.Type).ToLowerInvariant()}",
        ColonistTask.Fight when _foe is { } f => $"fighting a {f.Kind.Name.ToLowerInvariant()}",
        ColonistTask.Fight => "attacking a nest",
        ColonistTask.Flee => "fleeing!",
        _ => Rally is { } r && Position != r ? "marching" : "standing guard",
    };

    // ---- Snapshot -----------------------------------------------------------------------------------------------------

    private void SettleCombat()
    {
        _foe = null;
        _chasing = false;
        _chaseGoal = default;
        _chasePlanned = _chaseRetry = 0;
        _fanningTo = null;
        _equip = default;
    }

    private void SnapSaveCombat(BinaryWriter w)
    {
        w.Write(Health); w.Write(Downed); w.Write(Drafted); w.Write(Dead);
        w.Write(Rally.HasValue);
        if (Rally is { } r) Net.SnapIO.Write(w, r);
        w.Write(AttackNestId);
        foreach (var g in new[] { Weapon, Armour, Shield }) w.Write(g is { } item ? item.Index : ushort.MaxValue);
        w.Write(LastHurt); w.Write(LastStrike); w.Write(_lastAttacker); w.Write(_cooldown); w.Write(_gearRetry);
    }

    private void SnapLoadCombat(BinaryReader r)
    {
        Health = r.ReadInt32(); Downed = r.ReadBoolean(); Drafted = r.ReadBoolean(); Dead = r.ReadBoolean();
        Rally = r.ReadBoolean() ? Net.SnapIO.ReadPos(r) : null;
        AttackNestId = r.ReadInt32();
        ItemType? Gear() => r.ReadUInt16() is var i && i != ushort.MaxValue ? new ItemType(i) : null;
        Weapon = Gear(); Armour = Gear(); Shield = Gear();
        LastHurt = r.ReadInt64(); LastStrike = r.ReadInt64(); _lastAttacker = r.ReadInt32(); _cooldown = r.ReadInt32(); _gearRetry = r.ReadInt64();
    }
}
