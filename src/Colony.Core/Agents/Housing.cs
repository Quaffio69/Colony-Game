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

    /// <summary>Shared lodging (a dormitory): singles board here; it never becomes a family's home.</summary>
    public bool Shared { get; set; }

    public HousingPolicy Clone(string name) => new() { Name = name, Tier = (int[])Tier.Clone(), PerResident = (float[])PerResident.Clone(), Shared = Shared };
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
    /// <summary>The family (<see cref="Colonist.Household"/>) whose home this is; 0 while it lodges singles (or stands empty).</summary>
    public int Family { get; internal set; }
    /// <summary>Per slot: pieces on their way (a colonist is carrying them here).</summary>
    internal readonly int[] Pending = new int[Housing.SlotCount];

    public int Beds => Fixtures.Count(f => f.Slot == Housing.Bed);
    public int Count(int slot) => Fixtures.Count(f => f.Slot == slot);
    public string Name => $"Home #{Room.Id}";

    /// <summary>Average tier of what is installed (0 when bare): which homes the most educated get first.</summary>
    public float Quality => Fixtures.Count == 0 ? 0 : (float)Fixtures.Average(f => f.Tier);

    /// <summary>How its fixtures are arranged (see <see cref="HomeLayout"/>): redone whenever they or the room change.</summary>
    public IReadOnlyList<Placement> Layout { get; internal set; } = [];
    internal bool LayoutDirty = true;
    /// <summary>What it is built of (walls, windows), surveyed with its layout.</summary>
    public HomeShell Shell { get; internal set; } = HomeShell.None;
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
    /// <summary>The colony these homes belong to (it houses only its own people, in its own home rooms).</summary>
    public int Faction { get; init; }

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
    private long _seenEdges = -1, _seenCovers = -1;
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
        Policies[0].Shared = true; // the dormitory lodges singles
    }

    public List<HousingPolicy> Policies { get; }
    /// <summary>The policy new homes start with.</summary>
    public HousingPolicy DefaultPolicy => Policies[1];
    public IReadOnlyList<Home> All => _homes;
    /// <summary>Bumps when homes, fixtures, residents or policies change (for views).</summary>
    public int Version { get; private set; }
    /// <summary>Marks the policies changed (a policy added or renamed outside the setters).</summary>
    public void Touch() => Version++;
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

    /// <summary>Makes a policy shared lodging or not; families in homes that become shared move out (and look for another).</summary>
    public void SetShared(HousingPolicy policy, bool shared)
    {
        policy.Shared = shared;
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
        // The shell: walls all round, a laid floor, a roof, all at the tier expected, and a window or two for light.
        var shell = home.Shell;
        float walls = shell.Score(expected);
        return Math.Clamp(0.8f * score + 0.2f * Math.Min(1f, walls), 0, 1);
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
            // Lifespans vary a little (±20%), so pieces put in together don't all wear out on the same day.
            float spread = 0.8f + 0.4f * Procedural.Noise.Hash(home.Room.Id, home.Fixtures.Count, (int)(sim.Tick % 100_000), 3301);
            home.Fixtures.Add(new Fixture { Item = item, Slot = slot, Cell = home.Room.Cells.First(), WearLeft = (int)(item.Def.Wear * Needs.Day * spread) });
            home.LayoutDirty = true;
            installed++;
        }
        if (installed > 0) Version++;
        ArrangeDirty(sim);
        if (installed > 0 && slot == Bed) AssignResidents(sim); // a new bed takes in someone at once
        return installed;
    }

    public static int SlotOf(ItemType item) => item.Def.Fixture is { } f ? Array.IndexOf(Slots, f) : -1;


    /// <summary>Takes a piece out: broken down for some of its materials (more when little worn), or simply gone if worn out.</summary>
    private void Remove(ColonySim sim, Home? home, Fixture f, bool salvage)
    {
        home?.Fixtures.Remove(f);
        if (home != null) home.LayoutDirty = true;
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
        if (sim.World.EdgeRevision != _seenEdges || sim.World.CoverRevision != _seenCovers) // walls, floors or roofs changed: re-survey homes
        {
            (_seenEdges, _seenCovers) = (sim.World.EdgeRevision, sim.World.CoverRevision);
            foreach (var h in _homes) h.LayoutDirty = true;
        }
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
        if (sim.Tick % Needs.Day == 0)
        {
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
        ArrangeDirty(sim);
    }

    /// <summary>Re-arranges homes whose fixtures or floor changed (<see cref="HomeLayout"/>).</summary>
    private void ArrangeDirty(ColonySim sim)
    {
        foreach (var home in _homes)
        {
            if (!home.LayoutDirty) continue;
            home.LayoutDirty = false;
            home.Layout = HomeLayout.Arrange(home, sim.World);
            home.Shell = HomeLayout.Survey(home, sim.World);
            Version++;
        }
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
            if (!room.Type.IsHome || room.Faction != Faction) continue;
            var home = old.FirstOrDefault(h => h.Room == room);
            if (home == null)
            {
                var from = fixtures.FirstOrDefault(e => room.CellSet.Contains(e.Fixture.Cell)).Home;
                home = new Home { Room = room, Policy = from?.Policy ?? DefaultPolicy };
            }
            home.Fixtures.Clear();
            home.LayoutDirty = true; // its floor may have changed
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
    /// Who sleeps where, recomputed whenever homes, beds or the colonists change (and daily); deterministic.
    /// <list type="number">
    /// <item>A family (a married couple, their children grown or not) keeps the home it claimed: all of them live there,
    /// parents first, as far as its beds go.</item>
    /// <item>A family that lost its home claims an empty one (no family, nobody lodging, not shared).</item>
    /// <item>Singles (settlers, the unmarried who came alone) lodge in free beds of homes no family claimed, the most
    /// educated first; each keeps its bed unless a clearly better home has room, so a new manor draws the Scholars
    /// out of the dormitory. A home where a single lodges is taken: newlyweds need an empty one.</item>
    /// </list>
    /// Automatons don't sleep.
    /// </summary>
    internal void AssignResidents(ColonySim sim)
    {
        var people = sim.Colonists.Where(c => !c.IsAutomaton && c.Faction == Faction).ToList();
        var before = _homes.ToDictionary(h => h, h => h.Residents.ToList());
        var lodgedIn = new Dictionary<Colonist, Home>();
        foreach (var (home, list) in before)
            foreach (var c in list)
                if (home.Family == 0) lodgedIn[c] = home;
        foreach (var home in _homes) home.Residents.Clear();
        foreach (var c in people) c.Home = null;

        var families = people.Where(c => c.InFamily).GroupBy(c => c.Household)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.IsChild).ThenBy(c => c.BornTick).ThenBy(c => c.Id).ToList());
        void Move(Home home, List<Colonist> members)
        {
            foreach (var c in members)
            {
                if (home.Residents.Count >= home.Beds) break;
                home.Residents.Add(c);
                c.Home = home;
            }
        }
        // 1. Families stay in their homes.
        foreach (var home in _homes)
        {
            if (home.Family == 0) continue;
            if (home.Policy.Shared || !families.TryGetValue(home.Family, out var members)) home.Family = 0;
            else Move(home, members);
        }
        // 2. A family without a home takes an empty one (nobody lodging there before).
        foreach (var (id, members) in families.OrderBy(f => f.Key))
        {
            if (members.Any(c => c.Home != null) || _homes.Any(h => h.Family == id)) continue;
            var free = _homes.Where(h => h.Family == 0 && !h.Policy.Shared && before[h].Count == 0 && h.Beds >= 2)
                .OrderByDescending(h => h.Quality).ThenBy(h => h.Room.Id).FirstOrDefault();
            if (free == null) continue;
            free.Family = id;
            Move(free, members);
        }
        // 3. Singles lodge in homes no family claimed.
        foreach (var c in people.Where(c => !c.InFamily).OrderByDescending(c => c.Education).ThenBy(c => c.Id))
        {
            var open = _homes.Where(h => h.Family == 0 && h.Residents.Count < h.Beds).ToList();
            if (open.Count == 0) break;
            var best = open.OrderByDescending(h => h.Quality).ThenBy(h => h.Room.Id).First();
            var home = lodgedIn.TryGetValue(c, out var had) && open.Contains(had) && best.Quality < had.Quality + 0.5f ? had : best;
            home.Residents.Add(c);
            c.Home = home;
        }
        if (_homes.Any(h => !h.Residents.SequenceEqual(before[h]))) Version++;
    }

    /// <summary>Homes a couple could make theirs: claimed by no family, not shared, two beds or more, two lodgers at most.</summary>
    public IEnumerable<Home> CoupleHomes() =>
        _homes.Where(h => h.Family == 0 && !h.Policy.Shared && h.Residents.Count <= 2 && h.Beds >= 2).OrderByDescending(h => h.Quality).ThenBy(h => h.Room.Id);

    /// <summary>Gives a family a home (newlyweds); its members move in at the next assignment.</summary>
    internal void Claim(Home home, int family)
    {
        home.Family = family;
        Version++;
    }

    /// <summary>Beds settlers could take: free beds in homes no family has claimed.</summary>
    public int FreeBeds => _homes.Where(h => h.Family == 0).Sum(h => Math.Max(0, h.Beds - h.Residents.Count));
}
