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
    public const int Day = 4800;
    /// <summary>A full belly lasts a day.</summary>
    public const int FullSatiety = Day;
    /// <summary>Satiety one meal of nutrition gives (three a day; a berry is one, a loaf of bread four).</summary>
    public const int MealTicks = Day / 3;
    /// <summary>Below this a colonist eats when it next looks for work...</summary>
    public const int Hungry = Day / 3;
    /// <summary>...and below this it drops what it is doing to eat.</summary>
    public const int Famished = Day / 10;
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
    public static bool Usable(ColonySim sim, TilePos p) =>
        sim.Rooms.PartAt(p) is not { } part || (part.Built && part.Role != PartRole.Input);
}

/// <summary>
/// The colony as a whole: daily consumption of living-standard goods, the resulting standard, the food stores and
/// settlers. Runs once every <see cref="Needs.Day"/>.
/// </summary>
public sealed class Society
{
    private readonly float[] _owed = new float[Needs.Goods.Length];

    /// <summary>Share of each living-standard need met on the last day (0..1), in <see cref="Needs.Goods"/> order.</summary>
    public float[] Met { get; } = new float[Needs.Goods.Length];

    /// <summary>The living standard: the needs met, averaged and smoothed over days (0..1).</summary>
    public float Standard { get; private set; }

    /// <summary>Days the colony has lived through.</summary>
    public int Days { get; private set; }

    public int SettlersArrived { get; private set; }
    public int Departed { get; private set; }

    /// <summary>Meals of nutrition lying where the colony may eat them.</summary>
    public static int FoodInStore(ColonySim sim)
    {
        int meals = 0;
        foreach (var (p, pile) in sim.Items.All)
            if (Needs.IsFood(pile.Type) && Needs.Usable(sim, p)) meals += pile.Available * Needs.Nutrition(pile.Type);
        return meals;
    }

    public static int Eaters(ColonySim sim) => sim.Colonists.Count(c => !c.IsAutomaton);

    /// <summary>How many days the food in store would feed everyone (infinite with nobody to feed).</summary>
    public static float FoodDays(ColonySim sim)
    {
        int eaters = Eaters(sim);
        return eaters == 0 ? float.PositiveInfinity : FoodInStore(sim) / (eaters * (float)Needs.Day / Needs.MealTicks);
    }

    internal void Tick(ColonySim sim)
    {
        // Colonists who starved too long give up on the colony (but never empty it: see Needs.NeverBelow).
        for (int i = sim.Colonists.Count - 1; i >= 0; i--)
        {
            var c = sim.Colonists[i];
            if (c.StarvingTicks < Needs.StarvingLimit || Eaters(sim) <= Needs.NeverBelow) continue;
            sim.Depart(c);
            Departed++;
            sim.Announce($"{c.Name} has left the colony, starving");
        }
        if (sim.Tick % Needs.Day != 0) return;
        Days++;
        Consume(sim);
        Mood(sim);
        Settle(sim);
        if (Eaters(sim) > 0 && FoodDays(sim) is var days and < LowFoodDays)
            sim.Announce(days <= 0
                ? "There is no food left! Gather berries, farm grain and bake bread, or colonists will leave"
                : $"Food is running low: {days:0.0} days left. Gather berries, farm grain (bread feeds four times as many)");
    }

    /// <summary>Below this many days of food the colony is warned each day.</summary>
    public const float LowFoodDays = 2f;

    /// <summary>Uses up each need's daily share from the stores (plainest goods first) and records how much was met.</summary>
    private void Consume(ColonySim sim)
    {
        int eaters = Eaters(sim);
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
                         .Where(e => e.Value.Type.Def.Need == need && e.Value.Available > 0 && Needs.Usable(sim, e.Key))
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
    /// a fifth their belly, the rest the living standard. Kept gentle on purpose: at worst work goes 15% slower
    /// (<see cref="Colonist.WorkSpeed"/>) and the unhappiest drift away one at a time, never below a founding party.
    /// </summary>
    private void Mood(ColonySim sim)
    {
        float sum = 0;
        int n = 0;
        foreach (var c in sim.Colonists)
        {
            if (c.IsAutomaton) continue;
            float food = c.Starving ? 0 : c.Hungry ? 0.5f : 1;
            c.Happiness = Math.Clamp(0.5f * Housing.Score(c.Home, c) + 0.2f * food + 0.3f * Standard, 0, 1);
            c.UnhappyDays = c.Happiness < Needs.Unhappy ? c.UnhappyDays + 1 : 0;
            sum += c.Happiness;
            n++;
        }
        Happiness = n > 0 ? sum / n : 0.5f;
        if (Eaters(sim) <= Needs.NeverBelow) return;
        var leaving = sim.Colonists.Where(c => !c.IsAutomaton && c.UnhappyDays >= Needs.UnhappyDaysToLeave)
            .OrderBy(c => c.Happiness).ThenByDescending(c => c.Id).FirstOrDefault(); // the newest arrivals give up first
        if (leaving == null) return;
        sim.Depart(leaving);
        Departed++;
        sim.Announce($"{leaving.Name} has left the colony, unhappy ({(leaving.Home == null ? "no home" : "a poor home")})");
    }

    /// <summary>
    /// Settlers come to a colony with food to spare and nobody starving: a few more each day the happier it is, as
    /// many as there are free beds (one comes regardless). They arrive at the colony's landing place.
    /// </summary>
    private void Settle(ColonySim sim)
    {
        int eaters = Eaters(sim);
        if (eaters == 0 || sim.Colonists.Any(c => c.Starving) || FoodDays(sim) < Needs.SettlerFoodDays) return;
        int count = Math.Clamp((int)MathF.Round(eaters * (0.05f + 0.2f * Happiness)), 1, 12);
        count = Math.Max(1, Math.Min(count, sim.Housing.FreeBeds));
        var spots = sim.ArrivalSpots(count);
        foreach (var at in spots)
            sim.AddColonist(NameGenerator.Generate(unchecked((int)sim.Tick * 31 + 11), sim.ColonistsEver), at);
        if (spots.Count == 0) return;
        SettlersArrived += spots.Count;
        string why = Happiness >= 0.6f ? "its happy people" : sim.Housing.FreeBeds > 0 ? "its food and free beds" : "its food stores";
        sim.Announce(spots.Count == 1 ? $"A settler arrives, drawn by {why}" : $"{spots.Count} settlers arrive, drawn by {why}");
    }
}
