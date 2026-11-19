using Colony.Core.Agents;
using Colony.Core.World;

namespace Colony.Core.Combat;

/// <summary>How a kind of creature treats colonists: leaves them be, turns on them when provoked, or hunts them.</summary>
public enum Temperament : byte { Docile, Provokable, Hostile }

/// <summary>
/// A kind of wild creature (data, so new threats are a new entry: animals, beasts, and later other peoples' soldiers).
/// Creatures that live in nests (<see cref="Nest"/>) are counted there and only walk the world while someone is near.
/// </summary>
public sealed record CreatureKind(
    int Id, string Name, string Plural, int MaxHealth, Attack Attack, int StepTicks, Temperament Temperament,
    string? Drop, float DropChance, int NestCap, int NestHealth);

public static class CreatureKinds
{
    /// <summary>
    /// Giant ants: numerous and weak (one good blow kills one), quicker than a colonist, harmless alone, deadly as a swarm.
    /// Docile until provoked (struck, or their nest crowded by a colony's land); then they defend their ground and raid.
    /// </summary>
    public static readonly CreatureKind GiantAnt = new(
        Id: 0, Name: "Giant ant", Plural: "giant ants", MaxHealth: 8, Attack: new Attack(3, 24, 1, 1, 0), StepTicks: 6,
        Temperament: Temperament.Provokable, Drop: "chitin", DropChance: 1 / 3f, NestCap: 40, NestHealth: 600);

    public static readonly IReadOnlyList<CreatureKind> All = [GiantAnt];
}

/// <summary>What a creature is up to.</summary>
public enum CreatureMode : byte
{
    /// <summary>Foraging round its nest's territory.</summary>
    Wander,
    /// <summary>Heading home to go back underground.</summary>
    Return,
    /// <summary>Fighting: defending its ground or hunting a foe it has seen.</summary>
    Attack,
    /// <summary>On a raid: following the swarm's trail to a colony.</summary>
    Raid,
    /// <summary>Its nest is gone: it fights on, then wanders off.</summary>
    Lost,
}

/// <summary>One creature walking the world (most of a nest's population stays underground as a count).</summary>
public sealed class Creature
{
    public int Id { get; }
    public CreatureKind Kind { get; }
    public TilePos Position { get; internal set; }
    public TilePos? NextStep { get; internal set; }
    internal int StepTotal, StepDone;
    public float StepProgress => NextStep is null || StepTotal <= 0 ? 0f : StepDone / (float)StepTotal;
    public int Health { get; internal set; }
    /// <summary>The nest it belongs to (-1: lost).</summary>
    public int NestId { get; internal set; }
    public CreatureMode Mode { get; internal set; }
    /// <summary>Ticks left of foraging (wander) or of roaming before it wanders off for good (lost).</summary>
    internal int Timer;
    internal int Cooldown;
    internal int TrailIndex;
    internal int Stuck;
    /// <summary>The colonies a lost creature still fights (its nest's grudges when the nest fell).</summary>
    internal long LostMask;
    /// <summary>The colonist it is after (-1: none); the object is looked up from it.</summary>
    public int TargetId { get; internal set; } = -1;
    internal Colonist? Target;
    /// <summary>The colonist it is after (null: none).</summary>
    public Colonist? Prey => Target;
    /// <summary>When it last struck (for drawing the lunge).</summary>
    public long LastStrike { get; internal set; } = long.MinValue / 2;
    public bool Dead { get; internal set; }

    public Creature(int id, CreatureKind kind, TilePos position, int nest)
    {
        Id = id;
        Kind = kind;
        Position = position;
        NestId = nest;
        Health = kind.MaxHealth;
    }

    public bool Fighting => Mode is CreatureMode.Attack or CreatureMode.Raid || (Mode == CreatureMode.Lost && TargetId >= 0);

    internal void Settle()
    {
        NextStep = null;
        StepTotal = StepDone = 0;
        Target = null;
        TargetId = -1;
        Stuck = 0;
        TrailIndex = 0;
        if (Mode == CreatureMode.Raid) Mode = CreatureMode.Attack; // the trail is a cache; it fights on from where it is
    }

    internal void SnapSave(BinaryWriter w)
    {
        w.Write(Id); w.Write(Kind.Id); Net.SnapIO.Write(w, Position); w.Write(Health); w.Write(NestId); w.Write((byte)Mode);
        w.Write(Timer); w.Write(Cooldown); w.Write(LostMask); w.Write(LastStrike);
    }

    internal static Creature SnapLoad(BinaryReader r)
    {
        int id = r.ReadInt32();
        var kind = CreatureKinds.All[r.ReadInt32()];
        var c = new Creature(id, kind, Net.SnapIO.ReadPos(r), 0)
        {
            Health = r.ReadInt32(), NestId = r.ReadInt32(), Mode = (CreatureMode)r.ReadByte(),
            Timer = r.ReadInt32(), Cooldown = r.ReadInt32(), LostMask = r.ReadInt64(), LastStrike = r.ReadInt64(),
        };
        return c;
    }
}

/// <summary>
/// A colony of creatures (an ant nest): an entrance on the surface, a population mostly underground, a territory round
/// it that grows with its numbers, and grudges against colonies. It breeds up to its kind's cap and, when full, sends
/// founders off to start a new nest nearby (they spread). It turns hostile to a colony that strikes its creatures or
/// whose land meets its territory; it defends its ground, raids a crowding colony now and then, and calms down two
/// days after the last provocation once the land no longer meets.
/// </summary>
public sealed class Nest
{
    public const int MaxFactions = 16;

    public int Id { get; }
    public CreatureKind Kind { get; }
    public TilePos Entrance { get; }
    /// <summary>Creatures underground (those out walking are counted separately in <see cref="Out"/>).</summary>
    public int Population { get; internal set; }
    public int Out { get; internal set; }
    /// <summary>The mound itself: when struck down to 0 the nest is destroyed.</summary>
    public int Health { get; internal set; }
    public long Founded { get; }
    /// <summary>Creatures walk the world only while a colonist is near (or while it is fighting).</summary>
    public bool Awake { get; internal set; }
    /// <summary>Colonies whose land meets its territory (bit per faction).</summary>
    public long Crowded { get; internal set; }
    internal readonly long[] AngryUntil = new long[MaxFactions];
    internal long NextBirth, NextColony, NextRaid = long.MaxValue, NextForager;
    /// <summary>Creatures waiting to come out (alarm or raid) and how they come out.</summary>
    internal int ToEmerge;
    internal CreatureMode EmergeMode;
    internal long NextEmerge;
    // Caches (cleared by Settle): the swarm's trail for a raid, flow fields round the nest and towards its foes.
    internal List<TilePos>? Trail;
    internal FlowField? Home;
    internal bool HomeDirty;
    internal long HomeBuilt = long.MinValue / 2;
    internal FlowField? Threat;
    internal long ThreatBuilt = long.MinValue / 2;

    public Nest(int id, CreatureKind kind, TilePos entrance, int population, long founded)
    {
        Id = id;
        Kind = kind;
        Entrance = entrance;
        Population = population;
        Founded = founded;
        Health = kind.NestHealth;
    }

    public int Total => Population + Out;

    /// <summary>Tiles from the entrance its creatures call theirs: 12, plus one per four of them (at most 24).</summary>
    public int Radius => 12 + Math.Min(12, Total / 4);

    public bool HostileTo(int faction, long tick) =>
        faction is >= 0 and < MaxFactions && (((Crowded >> faction) & 1) != 0 || AngryUntil[faction] > tick);

    /// <summary>Every colony it is hostile to now (bit per faction).</summary>
    public long HostileMask(long tick)
    {
        long m = Crowded;
        for (int f = 0; f < MaxFactions; f++)
            if (AngryUntil[f] > tick) m |= 1L << f;
        return m;
    }

    internal void Settle()
    {
        Trail = null;
        Home = null;
        Threat = null;
        HomeDirty = false;
        HomeBuilt = ThreatBuilt = long.MinValue / 2;
    }

    internal void SnapSave(BinaryWriter w)
    {
        w.Write(Id); w.Write(Kind.Id); Net.SnapIO.Write(w, Entrance); w.Write(Population); w.Write(Out); w.Write(Health);
        w.Write(Founded); w.Write(Awake); w.Write(Crowded);
        foreach (long t in AngryUntil) w.Write(t);
        w.Write(NextBirth); w.Write(NextColony); w.Write(NextRaid); w.Write(NextForager);
        w.Write(ToEmerge); w.Write((byte)EmergeMode); w.Write(NextEmerge);
    }

    internal static Nest SnapLoad(BinaryReader r)
    {
        int id = r.ReadInt32();
        var kind = CreatureKinds.All[r.ReadInt32()];
        var entrance = Net.SnapIO.ReadPos(r);
        int pop = r.ReadInt32(), outside = r.ReadInt32(), health = r.ReadInt32();
        long founded = r.ReadInt64();
        var n = new Nest(id, kind, entrance, pop, founded) { Out = outside, Health = health, Awake = r.ReadBoolean(), Crowded = r.ReadInt64() };
        for (int f = 0; f < MaxFactions; f++) n.AngryUntil[f] = r.ReadInt64();
        n.NextBirth = r.ReadInt64(); n.NextColony = r.ReadInt64(); n.NextRaid = r.ReadInt64(); n.NextForager = r.ReadInt64();
        n.ToEmerge = r.ReadInt32(); n.EmergeMode = (CreatureMode)r.ReadByte(); n.NextEmerge = r.ReadInt64();
        return n;
    }
}
