namespace Colony.Core;

/// <summary>
/// The game's pacing knobs in one place: every number here sets how fast something happens or how much of it there
/// is. The systems keep their own named constants (<see cref="Agents.Needs"/>, <see cref="Combat.Wildlife"/>...), which
/// point here, so tune here.
///
/// <para><b>Target pace</b> (at 1x, a day = 4 minutes): between an RTS and Dwarf Fortress. Personal rhythms (eating
/// about three times a day, sleeping a fifth of it) are fast so colonists feel alive; the colony's growth is slow
/// enough that each step is a decision. Milestones aimed for:</para>
/// <list type="bullet">
/// <item>Founders' food: about two weeks for seven (~50 min). First settlers on day 3, then a wave every 3 days.</item>
/// <item>Population doubles in ~12-18 days (~1 hour) when homes and food keep up; homes, not arrivals, are the limit.</item>
/// <item>Food: through bread a wheat tile feeds ~0.7 colonists (1.5 tiles each), raw glowcaps ~0.45 (~2.25 tiles each);
/// a farmer tends ~25-40 tiles. Processing pays: bread feeds more per tile than raw crops, for labour and charcoal.</item>
/// <item>Digging: a miner clears ~40-50 stone tiles a working day; a mature tree takes ~8 s to fell, gives ~3 logs.</item>
/// <item>Education: a promotion takes half a day to two days of a teacher's time plus its books and instruments.</item>
/// <item>Ants: nests 64+ tiles off; they found new ones no nearer, a week apart. Once a colony's land meets one there
/// is a day's warning, then a raid every ~3 days sized to the colony (7 founders face ~6 ants).</item>
/// </list>
/// Measure changes with the bench: <c>-- balance [size] [days]</c> (resources round the landing, food per tile,
/// growth and threats over days with a scripted farm), <c>-- colony</c>, <c>-- families</c>, <c>-- ants</c>.
/// </summary>
public static class Balance
{
    // ---- Time ------------------------------------------------------------------------------------------------

    /// <summary>A game day in ticks (20 a second: 4 minutes at 1x).</summary>
    public const int Day = 4800;

    // ---- Population --------------------------------------------------------------------------------------------

    /// <summary>The first settlers come on this day (the founders get a few days to themselves).</summary>
    public const int FirstSettlerDay = 3;
    /// <summary>Settlers come in waves this many days apart (DF-style migrant waves, more often).</summary>
    public const int SettlerWaveDays = 3;
    /// <summary>Settlers a day per colonist (summed over a wave's days): base, plus this much more at full happiness.</summary>
    public const float SettlerGrowth = 0.02f, SettlerGrowthMood = 0.10f;
    /// <summary>Most settlers in one wave.</summary>
    public const int SettlerWaveMax = 24;
    /// <summary>Days a child takes to grow up (64 minutes at 1x).</summary>
    public const int ChildDays = 16;
    /// <summary>Daily chance a couple marries when there is an empty home for them.</summary>
    public const float MarriageChance = 0.15f;
    /// <summary>Daily chance a couple with a free bed at home has a child.</summary>
    public const float BirthChance = 0.08f;
    /// <summary>Most beds a home may have for a couple to claim it as a family home (the couple and two children).</summary>
    public const int FamilyHomeMaxBeds = 4;

    // ---- Farming -----------------------------------------------------------------------------------------------

    /// <summary>Ticks from sowing to harvest.</summary>
    public const int WheatGrowTicks = Day * 4, HerbGrowTicks = Day * 5 / 2, GlowcapGrowTicks = Day * 3;
    /// <summary>Flax: the fiber crop (beds, thread, cloth, rope, paper). Before it, fiber came only from wild plants
    /// and a playtest found it the bottleneck of almost every chain.</summary>
    public const int FlaxGrowTicks = Day * 3;
    /// <summary>Units a ripe crop gives.</summary>
    public const int CropYield = 4;
    /// <summary>Growth speed of an irrigated plot (water within two tiles).</summary>
    public const float IrrigationSpeed = 1.5f;

    // ---- Yields and hauling ---------------------------------------------------------------------------------------

    /// <summary>Logs a felled tree gives: one, plus this per level of its grown height (a mature oak: ~3). Was 0.6,
    /// but a playtest piled up 1000+ logs that mostly cost hauling time.</summary>
    public const float LogsPerLevel = 0.4f;
    /// <summary>Share of dug stone tiles that leave a usable stone (the rest is rubble that vanishes).</summary>
    public const float StoneYield = 0.5f;
    /// <summary>Kg a colonist carries a trip. A load is this over the item's weight (data/tech-tree.json "weight"): 6 logs,
    /// 10 stone or ore, 15 planks, 60 herbs or grain, 3 beds. A hauler also tops a load up from loose piles of the same
    /// item beside it (Colonist.TopUp). Heavy machinery and carts are the planned way to carry more.</summary>
    public const float CarryKg = 30;

    // ---- Grass and mood -------------------------------------------------------------------------------------------

    /// <summary>Bare dug soil under the sky greens after this many days, plus up to the spread (hashed per cell; a cell
    /// with no grassy neighbour waits a few rounds). Was half a day plus up to one: players found it too quick.</summary>
    public const float GrassRegrowDays = 2, GrassRegrowSpread = 2;
    /// <summary>Share of the way a colonist's happiness moves each day toward that day's mood (home, food, living
    /// standard, likes), so good days build up and one bad day only dents it. Was 1 (mood reset daily).</summary>
    public const float MoodChange = 0.35f;

    // ---- Orders ----------------------------------------------------------------------------------------------------

    /// <summary>Most work orders (dig, carve, chop, gather) one colony may have at once; farm sowing is not counted.
    /// A box over a whole map at forty levels (10 million orders) hung the game, and joining players with it.</summary>
    public const int MaxOrders = 100_000;

    // ---- Work (ticks) --------------------------------------------------------------------------------------------

    /// <summary>Digging out a tile, by material (stairs add <see cref="StairCarveTicks"/>).</summary>
    public const int DigLoose = 40, DigClay = 55, DigStone = 90, DigOre = 120, DigAetherite = 180, DigStair = 30, DigOther = 60;
    /// <summary>Digging natural rock by kind (<see cref="Procedural.Geology"/>; slate is <see cref="DigStone"/>): soft sediment
    /// near the top, hard granite and basalt deep down. Gems are slow to work free.</summary>
    public const int DigSandstone = 70, DigLimestone = 80, DigGranite = 120, DigBasalt = 150, DigGem = 200;
    /// <summary>Below level zero every dig takes this much longer per level deeper (rock pressure): 100 levels down, half as long again.</summary>
    public const float DigDepthHardening = 0.005f;
    public const int StairCarveTicks = 45;
    /// <summary>Felling: base plus this per level of the tree's height.</summary>
    public const int ChopTicks = 60, ChopTicksPerLevel = 20;
    public const int GatherTicks = 40, SowTicks = 30;
    /// <summary>Building a room part: base plus this per unit of material.</summary>
    public const int PartBuildTicks = 60, PartBuildTicksPerItem = 4;
    /// <summary>Labour per recipe run when the tech tree gives none: base plus this per tier of the product.</summary>
    public const int RecipeTicks = 120, RecipeTicksPerTier = 45;
    /// <summary>Building waterworks, walls, floors and roofs: multiplies each structure's base build time.</summary>
    public const float StructureBuildScale = 1.5f;

    // ---- Heavy machinery ------------------------------------------------------------------------------------------

    /// <summary>The excavator digs this many times faster than a colonist (any rock), but crawls (steps take this much longer).</summary>
    public const float ExcavatorDigSpeed = 5f, ExcavatorStepScale = 1.6f;
    /// <summary>The power wagon carries this many colonist loads a trip and rolls faster than walking.</summary>
    public const int WagonLoads = 8;
    public const float WagonStepScale = 0.7f;
    /// <summary>Fuel: one charcoal is this much work; a firebox holds this many charcoal. A dig costs the excavator
    /// <see cref="ExcavatorFuelPerDig"/>, a trip costs the wagon <see cref="WagonFuelPerTrip"/>: a load of charcoal digs
    /// ~60 tiles or hauls ~40 loads.</summary>
    public const int FuelPerCharcoal = 10, FuelCharcoalLoad = 12, ExcavatorFuelPerDig = 2, WagonFuelPerTrip = 3;

    // ---- Wildlife (giant ant nests) ------------------------------------------------------------------------------

    /// <summary>A nest breeds one ant this often (a 40-ant nest refills in about two weeks).</summary>
    public const int NestBirthTicks = Day / 3;
    /// <summary>A full nest sends out founders no sooner than this after it was founded, then every
    /// <see cref="NestFoundingDays"/> plus up to <see cref="NestFoundingJitterDays"/> days.</summary>
    public const int NestFirstFoundingDays = 6, NestFoundingDays = 6, NestFoundingJitterDays = 2;
    /// <summary>New nests stay this far from any landing place, and this far from any colony's land.</summary>
    public const int NestLandingGap = 64, NestFoundingLandGap = 40;
    /// <summary>A map holds at most one nest per this many tiles squared (16 on a 512 map, 256 on a 2048 one).</summary>
    public const int NestSpacing = 128;
    /// <summary>Warning between a colony's land reaching a nest and its first raid.</summary>
    public const int FirstRaidDelay = Day;
    /// <summary>Days between raids while the land meets, plus up to half a day.</summary>
    public const int RaidIntervalDays = 3;
    /// <summary>A raiding party: a third of the nest, but at most this many plus one per two grown colonists of the
    /// raided colony (a colony of 7 meets 6 ants), and never more than <see cref="RaidMax"/>.</summary>
    public const int RaidBase = 3, RaidMax = 16, RaidMin = 4;
}
