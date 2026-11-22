using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.World;

namespace Colony.Core.Agents;

/// <summary>
/// What colonists need. Food is personal: each colonist gets hungry, walks to the nearest food and eats it
/// (<see cref="Colonist"/>). The rest is the colony's <b>living standard</b> (<see cref="Society"/>): once a day the
/// colony uses up clothing, household goods, furniture and medicine from its stores, per head, and how much of that
/// it could meet draws settlers. Which items meet which need is data ("need: X" uses in the tech tree).
/// </summary>
public static class Needs
{
    /// <summary>A game day in ticks (4 minutes at normal speed): the rhythm of eating, consumption and arrivals.</summary>
    public const int Day = Balance.Day;
    /// <summary>A full belly lasts a day.</summary>
    public const int FullSatiety = Day;
    /// <summary>Satiety one meal of nutrition gives (three a day; a berry is one, a loaf of bread four).</summary>
    public const int MealTicks = Day / 3;
    /// <summary>Below this a colonist eats when it next looks for work...</summary>
    public const int Hungry = Day / 3;
    /// <summary>...and below this it drops what it is doing to eat: early enough to walk home from far work (a tenth
    /// of a day was less than the walk back from 90 tiles out, so a playtest saw colonists starve with full stores).</summary>
    public const int Famished = Day / 6;
    public const int EatTicks = 40;
    /// <summary>A day awake on a full night's rest; sleep restores it (<see cref="Housing.RestRate"/> per tick: about a
    /// quarter of a day in a comfortable bed, twice that on the floor).</summary>
    public const int FullRest = Day;
    /// <summary>Below this a colonist goes to bed when it next looks for work; at 0 it drops where it stands.</summary>
    public const int Tired = Day / 5;

    /// <summary>Unhappy (below this) for <see cref="UnhappyDaysToLeave"/> days running, a colonist may leave: one a day at
    /// most, and never from a colony of <see cref="NeverBelow"/> or fewer (nor do the starving), so hardship thins a colony,
    /// never empties it.</summary>
    public const float Unhappy = 0.25f; // fed but homeless and in rags (0.2) counts: homes matter
    public const int UnhappyDaysToLeave = 5, NeverBelow = 7;

    /// <summary>Days from birth to grown.</summary>
    public const int ChildDays = Balance.ChildDays;
    /// <summary>Daily chance a couple marries when there is an empty home for them (settlers move in first).</summary>
    public const float MarriageChance = Balance.MarriageChance;
    /// <summary>Daily chance a couple with a free bed at home has a child.</summary>
    public const float BirthChance = Balance.BirthChance;

    /// <summary>A colonist starving (no satiety left) this long leaves the colony.</summary>
    public const int StarvingLimit = Day;

    /// <summary>
    /// The goods each colonist uses up per day (the living standard). Furniture, beds, tableware, lights and decor are
    /// not used up: they are installed in homes (<see cref="Housing"/>) and wear out there.
    /// </summary>
    public static readonly (string Need, float PerDay)[] Goods =
    [
        ("dress", 0.1f),           // a set of clothes lasts ten days
        ("health", 0.05f),
    ];

    /// <summary>Settlers come while the stores hold at least this many days of food for everyone.</summary>
    public const float SettlerFoodDays = 3f;

    public static bool IsFood(ItemType t) => t.Def.Nutrition > 0;
    public static int Nutrition(ItemType t) => t.Def.Nutrition;

    /// <summary>
    /// Whether the colony may use up goods lying here: anywhere but a factory's input crate (that is the factory's
    /// stock) or an unbuilt part's footprint (construction materials).
    /// </summary>
    public static bool Usable(ColonySim sim, TilePos p, int faction = 0) => sim.Factions.Count == 1
        ? sim.Rooms.PartAt(p) is not { } only || (only.Built && only.Role != PartRole.Input) // one colony: everything is its own
        :
        (sim.Rooms.PartAt(p) is not { } part || (part.Built && part.Role != PartRole.Input && part.Room.Faction == faction))
        && (!sim.Stockpiles.Contains(p) || sim.Stockpiles.OwnerOf(p) == faction); // never another colony's stores
}

/// <summary>
/// The colony as a whole: daily consumption of living-standard goods, the resulting standard, the food stores and
/// settlers. Runs once every <see cref="Needs.Day"/>.
/// </summary>
public sealed class Society
{
    /// <summary>The colony this is the society of.</summary>
    public int Faction { get; init; }
    private Housing Homes(ColonySim sim) => sim.Factions[Faction].Housing;
    private List<Colonist> Members(ColonySim sim) => sim.Colonists.Where(c => c.Faction == Faction).ToList();
    private void Say(ColonySim sim, string text) => sim.Announce(text, Faction);

    private readonly float[] _owed = new float[Needs.Goods.Length];

    /// <summary>Share of each living-standard need met on the last day (0..1), in <see cref="Needs.Goods"/> order.</summary>
    public float[] Met { get; } = new float[Needs.Goods.Length];

    /// <summary>The living standard: the needs met, averaged and smoothed over days (0..1).</summary>
    public float Standard { get; private set; }

    /// <summary>Days the colony has lived through.</summary>
    public int Days { get; private set; }

    public int SettlersArrived { get; private set; }
    /// <summary>Whether settlers come at all (sandbox and tests switch them off).</summary>
    public bool SettlersCome { get; set; } = true;
    public int Departed { get; private set; }

    /// <summary>Meals of nutrition lying where the colony may eat them.</summary>
    public static int FoodInStore(ColonySim sim, int faction = 0)
    {
        int meals = 0;
        foreach (var (p, pile) in sim.Items.All)
            if (Needs.IsFood(pile.Type) && Needs.Usable(sim, p, faction)) meals += pile.Available * Needs.Nutrition(pile.Type);
        return meals;
    }

    public static int Eaters(ColonySim sim, int faction = 0) => sim.Colonists.Count(c => !c.IsAutomaton && c.Faction == faction);

    /// <summary>How many days the food in store would feed everyone (infinite with nobody to feed).</summary>
    public static float FoodDays(ColonySim sim, int faction = 0)
    {
        int eaters = Eaters(sim, faction);
        return eaters == 0 ? float.PositiveInfinity : FoodInStore(sim, faction) / (eaters * (float)Needs.Day / Needs.MealTicks);
    }

    internal void Tick(ColonySim sim)
    {
        // Colonists who starved too long give up on the colony (but never empty it: see Needs.NeverBelow).
        for (int i = sim.Colonists.Count - 1; i >= 0; i--)
        {
            var c = sim.Colonists[i];
            if (c.Faction != Faction) continue;
            if (c.StarvingTicks < Needs.StarvingLimit || c.InFamily || c.IsChild || Eaters(sim, Faction) <= Needs.NeverBelow) continue; // families stay together
            sim.Depart(c);
            Departed++;
            Say(sim, $"{c.Name} has left the colony, starving");
        }
        if (sim.Tick % Needs.Day != 0) return;
        Days++;
        Consume(sim);
        Mood(sim);
        Settle(sim);
        Families(sim);
        if (Eaters(sim, Faction) > 0 && FoodDays(sim, Faction) is var days and < LowFoodDays)
            Say(sim, days <= 0
                ? "There is no food left! Gather berries, farm grain and bake bread, or colonists will leave"
                : $"Food is running low: {days:0.0} days left. Gather berries, farm grain (bread feeds four times as many)");
    }

    /// <summary>Below this many days of food the colony is warned each day.</summary>
    public const float LowFoodDays = 2f;

    /// <summary>Uses up each need's daily share from the stores (plainest goods first) and records how much was met.</summary>
    private void Consume(ColonySim sim)
    {
        int eaters = Eaters(sim, Faction);
        float sum = 0;
        for (int k = 0; k < Needs.Goods.Length; k++)
        {
            var (need, perDay) = Needs.Goods[k];
            _owed[k] += eaters * perDay;
            int due = (int)_owed[k];
            if (due == 0)
            {
                sum += Met[k];
                continue;
            }
            _owed[k] -= due;
            int left = due;
            foreach (var (p, pile) in sim.Items.All
                         .Where(e => e.Value.Type.Def.Need == need && e.Value.Available > 0 && Needs.Usable(sim, e.Key, Faction))
                         .OrderBy(e => e.Value.Type.Def.Tier).ThenBy(e => e.Key.Y).ThenBy(e => e.Key.X).ThenBy(e => e.Key.Z).ToList())
            {
                left -= sim.Items.Consume(p, left);
                if (left == 0) break;
            }
            Met[k] = (due - left) / (float)due;
            sum += Met[k];
        }
        // Smoothed: a good week lifts the standard, one bare day doesn't wreck it.
        Standard = Standard * 0.7f + sum / Needs.Goods.Length * 0.3f;
    }

    /// <summary>The colony's average happiness (as of the last day), 0..1.</summary>
    public float Happiness { get; private set; } = 0.5f;

    /// <summary>
    /// Each colonist's happiness for the day: half their home (<see cref="Housing.Score"/>; none for the homeless),
    /// a fifth their belly, the rest the living standard, plus up to 0.15 for likes met (<see cref="Colonist.LikesBonus"/>).
    /// Kept gentle on purpose: at worst work goes 15% slower
    /// (<see cref="Colonist.WorkSpeed"/>) and the unhappiest drift away one at a time, never below a founding party.
    /// </summary>
    private void Mood(ColonySim sim)
    {
        float sum = 0;
        int n = 0;
        foreach (var c in Members(sim))
        {
            if (c.IsAutomaton) continue;
            float food = c.Starving ? 0 : c.Hungry ? 0.5f : 1;
            c.Happiness = Math.Clamp(0.5f * Housing.Score(c.Home, c) + 0.2f * food + 0.3f * Standard + c.LikesBonus(sim.Tick), 0, 1);
            c.WorkTicks = c.PreferredTicks = 0; // a new day of work
            c.UnhappyDays = c.Happiness < Needs.Unhappy ? c.UnhappyDays + 1 : 0;
            sum += c.Happiness;
            n++;
        }
        Happiness = n > 0 ? sum / n : 0.5f;
        if (Eaters(sim, Faction) <= Needs.NeverBelow) return;
        var leaving = Members(sim).Where(c => !c.IsAutomaton && !c.InFamily && !c.IsChild && c.UnhappyDays >= Needs.UnhappyDaysToLeave)
            .OrderBy(c => c.Happiness).ThenByDescending(c => c.Id).FirstOrDefault(); // the newest arrivals give up first
        if (leaving == null) return;
        sim.Depart(leaving);
        Departed++;
        Say(sim, $"{leaving.Name} has left the colony, unhappy ({(leaving.Home == null ? "no home" : "a poor home")})");
    }

    /// <summary>
    /// Settlers come in waves (from <see cref="Balance.FirstSettlerDay"/>, every <see cref="Balance.SettlerWaveDays"/>)
    /// to a colony with food to spare and nobody starving: more the happier it is, as many as there are free beds (one
    /// comes regardless). They arrive at the colony's landing place.
    /// </summary>
    private void Settle(ColonySim sim)
    {
        if (Days < Balance.FirstSettlerDay || (Days - Balance.FirstSettlerDay) % Balance.SettlerWaveDays != 0) return;
        int eaters = Eaters(sim, Faction);
        if (!SettlersCome || eaters == 0 || Members(sim).Any(c => c.Starving) || FoodDays(sim, Faction) < Needs.SettlerFoodDays) return;
        float perDay = Balance.SettlerGrowth + Balance.SettlerGrowthMood * Happiness;
        int count = Math.Clamp((int)MathF.Round(eaters * perDay * Balance.SettlerWaveDays), 1, Balance.SettlerWaveMax);
        count = Math.Max(1, Math.Min(count, Homes(sim).FreeBeds));
        var spots = sim.ArrivalSpots(count, Faction);
        foreach (var at in spots)
            sim.AddColonist(NameGenerator.Generate(unchecked((int)sim.Tick * 31 + 11), sim.ColonistsEver), at, faction: Faction);
        if (spots.Count == 0) return;
        SettlersArrived += spots.Count;
        string why = Happiness >= 0.6f ? "its happy people" : Homes(sim).FreeBeds > 0 ? "its food and free beds" : "its food stores";
        Say(sim, spots.Count == 1 ? $"A settler arrives, drawn by {why}" : $"{spots.Count} settlers arrive, drawn by {why}");
    }

    public int Marriages { get; private set; }
    public int Births { get; private set; }

    /// <summary>
    /// Family life, once a day after settlers have taken their beds: children grow up (and stay at home until they
    /// marry); couples marry when there is an empty home for them (two adults of different households: never
    /// siblings) and move in as a new family; couples with a free bed at home have children while the colony has food.
    /// A family that has filled its home has no more. Rare next to settlers on purpose: outsiders grow a colony fastest.
    /// </summary>
    private void Families(ColonySim sim)
    {
        long tick = sim.Tick;
        foreach (var c in Members(sim))
            if (c.IsChild && tick - c.BornTick >= (long)Needs.ChildDays * Needs.Day)
            {
                c.IsChild = false;
                Say(sim, $"{c.Name} has grown up (and still lives at home until they marry)");
            }
        Homes(sim).AssignResidents(sim); // settlers first: they take beds before couples look for a home

        // Marriages: a couple needs a home of its own: an empty one, or one where nobody but the two of them lodges
        // (a home other settlers live in is taken). One per such home, each on a daily chance.
        var free = Members(sim).Where(c => !c.IsAutomaton && !c.IsChild && c.Spouse == null).OrderBy(c => c.Id).ToList();
        foreach (var home in Homes(sim).CoupleHomes().ToList())
        {
            if (Chance(tick, home.Room.Id, 1) >= Needs.MarriageChance) continue;
            // Who lodges here marries first (if two do, each other); then the longest in the colony.
            var lodgers = home.Residents.Where(free.Contains).ToList();
            if (lodgers.Count != home.Residents.Count) continue; // someone here can't marry (or isn't single): taken
            var a = lodgers.Count > 0 ? lodgers[0] : free.FirstOrDefault();
            var b = a == null ? null
                : lodgers.Count == 2 ? (lodgers[1].Household != a.Household ? lodgers[1] : null)
                : free.FirstOrDefault(c => c != a && c.Household != a.Household); // anyone free from another household (they move out)
            if (a == null || b == null) continue;
            free.Remove(a);
            free.Remove(b);
            int family = sim.NewFamily();
            a.Spouse = b;
            b.Spouse = a;
            a.Household = b.Household = family;
            Homes(sim).Claim(home, family);
            Marriages++;
            Say(sim, $"{a.Name} and {b.Name} marry and make {home.Name} their home");
        }
        Homes(sim).AssignResidents(sim);

        // Births: a couple at home with a bed to spare, while nobody starves.
        if (Members(sim).Any(c => c.Starving)) return;
        foreach (var c in Members(sim).Where(c => c.Spouse is { } s && c.Id < s.Id).ToList())
        {
            if (c.Home is not { } home || home.Family != c.Household) continue;
            int family = sim.Colonists.Count(m => m.Household == c.Household);
            if (home.Beds <= family || Chance(tick, c.Id, 2) >= Needs.BirthChance) continue;
            var child = sim.AddChild(c, c.Spouse!);
            Births++;
            Say(sim, $"{child.Name} is born to {c.Name} and {c.Spouse!.Name}");
        }
        Homes(sim).AssignResidents(sim);
    }

    private static float Chance(long tick, int id, int salt) => Procedural.Noise.Hash(unchecked((int)(tick / Needs.Day)), id, salt, 7717);

    // ---- Snapshot -----------------------------------------------------------------------------------------

    internal void SnapSave(BinaryWriter w)
    {
        Net.SnapIO.WriteFloats(w, _owed);
        Net.SnapIO.WriteFloats(w, Met);
        w.Write(Standard); w.Write(Days); w.Write(SettlersArrived); w.Write(SettlersCome); w.Write(Departed);
        w.Write(Happiness); w.Write(Marriages); w.Write(Births);
    }

    internal void SnapLoad(BinaryReader r)
    {
        Net.SnapIO.ReadFloatsInto(r, _owed);
        Net.SnapIO.ReadFloatsInto(r, Met);
        Standard = r.ReadSingle(); Days = r.ReadInt32(); SettlersArrived = r.ReadInt32(); SettlersCome = r.ReadBoolean();
        Departed = r.ReadInt32(); Happiness = r.ReadSingle(); Marriages = r.ReadInt32(); Births = r.ReadInt32();
    }
}
