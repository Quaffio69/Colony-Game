using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.World;

namespace Colony.Core.Agents;

/// <summary>
/// How homes are furnished: per fixture slot, a quality tier (0 = none) and how many pieces each resident gets.
/// Homes follow a policy, so upgrading every cottage in the colony is one edit here (Songs of Syx style).
/// </summary>
public sealed class HousingPolicy
{
    public required string Name { get; set; }
    /// <summary>Per slot (see <see cref="Housing.Slots"/>): wanted tier, 0 = leave the slot empty.</summary>
    public int[] Tier { get; init; } = new int[Housing.SlotCount];
    /// <summary>Per slot: pieces per resident (beds are always one each).</summary>
    public float[] PerResident { get; init; } = new float[Housing.SlotCount];

    public HousingPolicy Clone(string name) => new() { Name = name, Tier = (int[])Tier.Clone(), PerResident = (float[])PerResident.Clone() };
}

/// <summary>An installed piece of furniture: it wears out only while installed (items in store are just counts).</summary>
public sealed class Fixture
{
    public required ItemType Item { get; init; }
    public required int Slot { get; init; }
    public required TilePos Cell { get; set; }
    /// <summary>Ticks of use left before it wears out.</summary>
    public int WearLeft { get; internal set; }
    public int Tier => Item.Def.FixtureTier;
    public float Condition => WearLeft / (float)(Item.Def.Wear * Needs.Day);
}

/// <summary>What a home's size and policy allow: how many residents, and how many pieces of which tier per slot.</summary>
public readonly record struct HomePlan(int Residents, int[] Count, int[] Tier);

/// <summary>A home room's furnishings and residents.</summary>
public sealed class Home
{
    public required Room Room { get; set; }
    public HousingPolicy Policy { get; internal set; } = null!;
    public List<Fixture> Fixtures { get; } = new();
    public List<Colonist> Residents { get; } = new();
    /// <summary>Per slot: pieces on their way (a colonist is carrying them here).</summary>
    internal readonly int[] Pending = new int[Housing.SlotCount];

    public int Beds => Fixtures.Count(f => f.Slot == Housing.Bed);
    public int Count(int slot) => Fixtures.Count(f => f.Slot == slot);
    public string Name => $"Home #{Room.Id}";

    /// <summary>Average tier of what is installed (0 when bare): which homes the most educated get first.</summary>
    public float Quality => Fixtures.Count == 0 ? 0 : (float)Fixtures.Average(f => f.Tier);
}

/// <summary>
/// Homes: rooms of type <see cref="Catalog.Home"/>, one per contiguous floor area, any size and any number of levels.
/// <para>
/// <b>Space.</b> A home of n floor cells keeps a quarter for walking; beds take two cells each and furniture one, so
/// the policy's furniture per resident decides how many fit: the same building sleeps two in comfort or six in a
/// dormitory. Tableware sits on the furniture (four per piece), lighting and decor hang on the walls (about one per
/// two cells). Each bed is one resident.
/// </para>
/// <para>
/// <b>Furnishing.</b> Colonists bring fixtures of the policy's tier and install them (<see cref="Demands"/>,
/// <see cref="Install"/>); an installed piece of the wrong tier is swapped out when its replacement arrives and
/// broken down for materials (more of them the less worn it was), so raising a policy's tiers upgrades every home on
/// it. Pieces wear out after their item's <c>wear</c> days and are replaced: upkeep, which keeps the factories busy.
/// </para>
/// <para>
/// <b>Quality.</b> Each slot does something: beds house and rest (<see cref="RestRate"/>), furniture is comfort,
/// tableware makes meals satisfying, lighting is the evening's comfort, decor is beauty. A resident judges each slot
/// by how much of it they get and its tier against what their education leads them to expect (Unskilled: humble,
/// Literate: comfortable, Scholars: fine), giving <see cref="Score"/> 0..1.
/// </para>
/// </summary>
public sealed class Housing
{
    public const int Bed = 0, Furniture = 1, Tableware = 2, Lighting = 3, Decor = 4, SlotCount = 5;
    /// <summary>Slot names, as the catalog's 'fixture' values.</summary>
    public static readonly string[] Slots = ["bed", "furniture", "tableware", "lighting", "decor"];
    /// <summary>What each slot is for, for the player.</summary>
    public static readonly string[] SlotEffects = ["sleep and residents", "comfort", "dining", "evening light", "beauty"];
    /// <summary>How much each slot counts towards a home's quality.</summary>
    private static readonly float[] Weight = [0.3f, 0.2f, 0.15f, 0.15f, 0.2f];
    /// <summary>Pieces a resident wants per slot for full marks (beds: one).</summary>
    private static readonly float[] Want = [1f, 1f, 1f, 0.5f, 0.5f];

    /// <summary>Smallest home: room for one bed, a stool and a step to turn round.</summary>
    public const int MinCells = 4;
    public const int BedCells = 2;
    public const int InstallTicks = 60;

    private readonly List<Home> _homes = new();
    private int _seenRooms = -1, _seenEver = -1, _seenCount = -1;
    private bool _plansChanged; // a policy or a home's policy changed: trim surplus pieces next tick

    public Housing()
    {
        HousingPolicy P(string name, int bed, (int T, float N) furniture, (int T, float N) tableware, (int T, float N) lighting, (int T, float N) decor) => new()
        {
            Name = name,
            Tier = [bed, furniture.T, tableware.T, lighting.T, decor.T],
            PerResident = [1, furniture.N, tableware.N, lighting.N, decor.N],
        };
        Policies =
        [
            P("Dormitory", 1, (1, 0.25f), (1, 1), (1, 0.25f), (0, 0)),
            P("Cottage", 1, (1, 1), (1, 1), (1, 0.5f), (1, 0.5f)),
            P("House", 2, (2, 1.5f), (2, 1), (2, 0.5f), (2, 0.5f)),
            P("Manor", 3, (3, 2.5f), (3, 1), (3, 1), (3, 1)),
        ];
    }

    public List<HousingPolicy> Policies { get; }
    /// <summary>The policy new homes start with.</summary>
    public HousingPolicy DefaultPolicy => Policies[1];
    public IReadOnlyList<Home> All => _homes;
    /// <summary>Bumps when homes, fixtures, residents or policies change (for views).</summary>
    public int Version { get; private set; }
    public int WornOut { get; private set; }
    public int Salvaged { get; private set; }

    public Home? HomeOf(Room room) => _homes.FirstOrDefault(h => h.Room == room);

    // ---- Policies --------------------------------------------------------------------------------------

    public void SetTier(HousingPolicy policy, int slot, int tier)
    {
        policy.Tier[slot] = Math.Clamp(tier, slot == Bed ? 1 : 0, Catalog.FixtureTiers.Length - 1);
        _plansChanged = true;
        Version++;
    }

    public void SetPerResident(HousingPolicy policy, int slot, float perResident)
    {
        if (slot == Bed) return;
        policy.PerResident[slot] = Math.Clamp(MathF.Round(perResident * 4) / 4, 0, 4);
        _plansChanged = true;
        Version++;
    }

    public void SetPolicy(Home home, HousingPolicy policy)
    {
        home.Policy = policy;
        _plansChanged = true;
        Version++;
    }

    // ---- Space -----------------------------------------------------------------------------------------

    /// <summary>What fits in a home of <paramref name="cells"/> floor cells under a policy.</summary>
    public static HomePlan Plan(int cells, HousingPolicy policy)
    {
        var count = new int[SlotCount];
        var tier = (int[])policy.Tier.Clone();
        if (cells < MinCells) return new HomePlan(0, count, tier);
        int usable = cells - Math.Max(1, cells / 4);
        float furniture = policy.Tier[Furniture] > 0 ? policy.PerResident[Furniture] : 0;
        int residents = Math.Max(1, (int)(usable / (BedCells + furniture)));
        count[Bed] = residents;
        count[Furniture] = Math.Min((int)MathF.Round(residents * furniture), usable - BedCells * residents);
        int walls = cells / 2 + 1;
        count[Tableware] = Math.Min((int)MathF.Round(residents * policy.PerResident[Tableware]), Math.Max(1, count[Furniture]) * 4);
        count[Lighting] = Math.Min((int)MathF.Round(residents * policy.PerResident[Lighting]), walls);
        count[Decor] = Math.Min((int)MathF.Round(residents * policy.PerResident[Decor]), walls);
        for (int s = 0; s < SlotCount; s++)
            if (tier[s] == 0) count[s] = 0;
        return new HomePlan(residents, count, tier);
    }

    public static HomePlan Plan(Home home) => Plan(home.Room.Cells.Count, home.Policy);

    // ---- Quality and rest ------------------------------------------------------------------------------

    /// <summary>The tier a colonist expects of a home: humble for the Unskilled, comfortable for Literates, fine above.</summary>
    public static int Expected(Colonist c) => Math.Clamp(c.Education + 1, 1, 3);

    /// <summary>How well a home serves one of its residents, 0..1 (0 for the homeless).</summary>
    public static float Score(Home? home, Colonist c)
    {
        if (home == null) return 0;
        int residents = Math.Max(1, home.Residents.Count);
        float expected = Expected(c), score = 0;
        for (int s = 0; s < SlotCount; s++)
        {
            int n = 0, tiers = 0;
            foreach (var f in home.Fixtures)
                if (f.Slot == s) { n++; tiers += f.Tier; }
            if (n == 0) continue;
            float coverage = s == Bed ? 1 : Math.Min(1f, n / (residents * Want[s]));
            float quality = Math.Min(1.25f, tiers / (float)n / expected); // better than expected pleases a little more
            score += Weight[s] * coverage * quality;
        }
        return Math.Clamp(score, 0, 1);
    }

    /// <summary>Rest regained per tick of sleep: a fine bed restores fastest, the bare floor slowest.</summary>
    public static float RestRate(Home? home, Colonist c)
    {
        if (home == null) return 2f;
        int best = 0;
        foreach (var f in home.Fixtures)
            if (f.Slot == Bed) best = Math.Max(best, f.Tier);
        return best switch { 0 => 2f, 1 => 3.2f, 2 => 4f, _ => 4.8f };
    }

    /// <summary>The cell a resident sleeps on: one of the home's beds (spread over residents), or any floor cell.</summary>
    public static TilePos SleepCell(Home home, Colonist c)
    {
        var beds = home.Fixtures.Where(f => f.Slot == Bed).ToList();
        if (beds.Count > 0) return beds[Math.Max(0, home.Residents.IndexOf(c)) % beds.Count].Cell;
        return home.Room.Cells.OrderBy(p => (p.Z, p.Y, p.X)).First();
    }

    // ---- Furnishing ------------------------------------------------------------------------------------

    /// <summary>Fixtures homes still need: per home and slot, the tier and how many (none counted twice).</summary>
    public IEnumerable<(Home Home, int Slot, int Tier, int Missing)> Demands()
    {
        foreach (var home in _homes)
        {
            var plan = Plan(home);
            for (int s = 0; s < SlotCount; s++)
            {
                if (plan.Count[s] == 0) continue;
                int matching = home.Fixtures.Count(f => f.Slot == s && f.Tier == plan.Tier[s]);
                int missing = plan.Count[s] - matching - home.Pending[s];
                if (missing > 0) yield return (home, s, plan.Tier[s], missing);
            }
        }
    }

    internal void Promise(Home home, int slot, int count) => home.Pending[slot] += count;
    internal void Unpromise(Home home, int slot, int count) => home.Pending[slot] = Math.Max(0, home.Pending[slot] - count);

    /// <summary>
    /// Installs pieces in a home (a colonist arrived with them): each replaces a piece of the wrong tier if the
    /// slot is full (that one is broken down for materials). Returns how many were installed; the rest are surplus.
    /// </summary>
    public int Install(ColonySim sim, Home home, ItemType item, int count)
    {
        int slot = SlotOf(item);
        if (slot < 0 || !_homes.Contains(home)) return 0;
        var plan = Plan(home);
        int installed = 0;
        for (int i = 0; i < count; i++)
        {
            if (home.Count(slot) >= plan.Count[slot])
            {
                var old = home.Fixtures.Where(f => f.Slot == slot && f.Tier != plan.Tier[slot])
                    .OrderBy(f => f.Tier).ThenBy(f => f.WearLeft).FirstOrDefault();
                if (old == null) break; // the slot is full of the right pieces already
                Remove(sim, home, old, salvage: true);
            }
            home.Fixtures.Add(new Fixture { Item = item, Slot = slot, Cell = Layout(home, slot), WearLeft = item.Def.Wear * Needs.Day });
            installed++;
        }
        if (installed > 0) Version++;
        if (installed > 0 && slot == Bed) AssignResidents(sim); // a new bed takes in someone at once
        return installed;
    }

    public static int SlotOf(ItemType item) => item.Def.Fixture is { } f ? Array.IndexOf(Slots, f) : -1;

    /// <summary>
    /// Where a new piece goes: beds and furniture on floor cells nobody else's bed or furniture stands on (beds
    /// first in the room's order, furniture after); tableware on the furniture; lighting and decor along the room.
    /// </summary>
    private static TilePos Layout(Home home, int slot)
    {
        var cells = home.Room.Cells.OrderBy(p => (p.Z, p.Y, p.X)).ToList();
        if (slot is Bed or Furniture)
        {
            var taken = home.Fixtures.Where(f => f.Slot is Bed or Furniture).Select(f => f.Cell).ToHashSet();
            var order = slot == Bed ? cells : Enumerable.Reverse(cells);
            foreach (var c in order)
                if (!taken.Contains(c)) return c;
            return cells[0];
        }
        if (slot == Tableware && home.Fixtures.FirstOrDefault(f => f.Slot == Furniture) is { } table)
        {
            var tables = home.Fixtures.Where(f => f.Slot == Furniture).ToList();
            return tables[home.Count(Tableware) % tables.Count].Cell;
        }
        int k = home.Count(slot) * 2 + (slot == Decor ? 1 : 0);
        return cells[k % cells.Count];
    }

    /// <summary>Takes a piece out: broken down for some of its materials (more when little worn), or simply gone if worn out.</summary>
    private void Remove(ColonySim sim, Home? home, Fixture f, bool salvage)
    {
        home?.Fixtures.Remove(f);
        if (!salvage || Catalog.RecipeFor(f.Item) is not { } recipe) return;
        float keep = 0.5f * Math.Clamp(f.Condition, 0, 1) / Math.Max(1, recipe.OutCount);
        foreach (var input in recipe.Inputs)
        {
            int n = (int)(input.Count * keep);
            if (n > 0) sim.Items.Drop(f.Cell, input.Item, n);
        }
        Salvaged++;
        Version++;
    }

    // ---- Upkeep ----------------------------------------------------------------------------------------

    internal void Tick(ColonySim sim)
    {
        if (sim.Rooms.Version != _seenRooms) Sync(sim);
        if (sim.ColonistsEver != _seenEver || sim.Colonists.Count != _seenCount) // someone came or went: house them now
        {
            (_seenEver, _seenCount) = (sim.ColonistsEver, sim.Colonists.Count);
            AssignResidents(sim);
        }
        if (_plansChanged)
        {
            _plansChanged = false;
            TrimToPlans(sim);
            AssignResidents(sim);
        }
        if (sim.Tick % Needs.Day != 0) return;
        foreach (var home in _homes)
            for (int i = home.Fixtures.Count - 1; i >= 0; i--)
            {
                var f = home.Fixtures[i];
                f.WearLeft -= Needs.Day;
                if (f.WearLeft > 0) continue;
                Remove(sim, home, f, salvage: false);
                WornOut++;
                Version++;
            }
        AssignResidents(sim);
    }

    /// <summary>
    /// Follows the rooms: a new home room gets a home (with the policy of the home its fixtures came from, if any);
    /// fixtures follow the floor they stand on, are broken down if it is no longer a home, and pieces beyond what a
    /// shrunken home or a changed policy allows are taken out. Residents of homes that are gone are re-housed.
    /// </summary>
    private void Sync(ColonySim sim)
    {
        _seenRooms = sim.Rooms.Version;
        var old = _homes.ToList();
        var fixtures = old.SelectMany(h => h.Fixtures.Select(f => (Home: h, Fixture: f))).ToList();
        _homes.Clear();
        foreach (var room in sim.Rooms.All)
        {
            if (!room.Type.IsHome) continue;
            var home = old.FirstOrDefault(h => h.Room == room);
            if (home == null)
            {
                var from = fixtures.FirstOrDefault(e => room.CellSet.Contains(e.Fixture.Cell)).Home;
                home = new Home { Room = room, Policy = from?.Policy ?? DefaultPolicy };
            }
            home.Fixtures.Clear();
            _homes.Add(home);
        }
        foreach (var (_, f) in fixtures)
        {
            if (sim.Rooms.RoomAt(f.Cell) is { Type.IsHome: true } room && HomeOf(room) is { } home) home.Fixtures.Add(f);
            else Remove(sim, null, f, salvage: true);
        }
        foreach (var h in old.Where(h => !_homes.Contains(h))) h.Residents.Clear();
        TrimToPlans(sim);
        AssignResidents(sim);
        Version++;
    }

    /// <summary>Takes out pieces beyond a home's plan (wrong tier and most worn first).</summary>
    internal void TrimToPlans(ColonySim sim)
    {
        foreach (var home in _homes)
        {
            var plan = Plan(home);
            for (int s = 0; s < SlotCount; s++)
                while (home.Count(s) > plan.Count[s])
                {
                    var worst = home.Fixtures.Where(f => f.Slot == s)
                        .OrderBy(f => f.Tier == plan.Tier[s] ? 1 : 0).ThenBy(f => f.WearLeft).First();
                    Remove(sim, home, worst, salvage: true);
                }
        }
    }

    /// <summary>
    /// Beds go to the most educated first, into the best homes (by installed quality, then room order), so a new manor
    /// draws the Scholars out of the dormitory and their cots pass to newcomers. Recomputed whenever homes, beds or the
    /// colonists change (and daily); deterministic, and stable while nothing changes. Automatons don't sleep.
    /// </summary>
    internal void AssignResidents(ColonySim sim)
    {
        var people = sim.Colonists.Where(c => !c.IsAutomaton).OrderByDescending(c => c.Education).ThenBy(c => c.Id).ToList();
        var before = _homes.ToDictionary(h => h, h => h.Residents.ToList());
        foreach (var home in _homes) home.Residents.Clear();
        foreach (var c in people) c.Home = null;
        int next = 0;
        foreach (var home in _homes.OrderByDescending(h => h.Quality).ThenBy(h => h.Room.Id))
            while (home.Residents.Count < home.Beds && next < people.Count)
            {
                var c = people[next++];
                home.Residents.Add(c);
                c.Home = home;
            }
        if (_homes.Any(h => !h.Residents.SequenceEqual(before[h]))) Version++;
    }

    public int FreeBeds => _homes.Sum(h => Math.Max(0, h.Beds - h.Residents.Count));
}
