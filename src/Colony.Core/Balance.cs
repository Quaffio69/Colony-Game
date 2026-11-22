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
/// <item>Digging: a miner clears ~40-50 stone tiles a working day; a mature tree takes ~8 s to fell, gives ~4 logs.</item>
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

    // ---- Work (ticks) --------------------------------------------------------------------------------------------

    /// <summary>Digging out a tile, by material (stairs add <see cref="StairCarveTicks"/>).</summary>
    public const int DigLoose = 40, DigClay = 55, DigStone = 90, DigOre = 120, DigAetherite = 180, DigStair = 30, DigOther = 60;
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
