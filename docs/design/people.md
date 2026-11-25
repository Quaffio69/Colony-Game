# People: needs, homes, families, work

Hunger, rest, mood and population; homes, layouts and fixtures; families; work priorities; likes.

- **Needs and population (`Agents.Needs`, `Agents.Society`):** a game day is `Needs.Day` (4800 ticks, 4 min at 1x).
  Food is personal: `Colonist.Satiety` (a day when full) drains; below `Hungry` an idle colonist eats before its
  next job, below `Famished` it drops its work (only if food is in reach) and walks to the nearest food pile it may
  use (`Needs.Usable`: never a factory's input crate or construction materials), reserves enough to fill up (never
  more than fits; a loaf can overfill and simply lasts longer) and eats. Starving a full day (`StarvingLimit`) makes
  it leave the colony (`ColonySim.Depart`; ids are never reused). Automatons don't eat. Foods and need goods are data:
  'food' = meals per unit (berries/glowcaps 1, bread 4), "need: X" uses give `ItemDef.Need`. Once a day `Society`
  uses up the consumable goods (dress, health; per-head rates in `Needs.Goods`) from usable piles, plainest first;
  `Met` per need, `Standard` smoothed. Mood (`Colonist.Happiness`) moves 35% of the way each day (`Balance.MoodChange`) toward the day's: half home (`Housing.Score`), a fifth belly,
  the rest the goods standard, plus likes met and up to `Balance.MistMood` (0.06) for `MistMoodSeconds` (40 s) of the day
  spent in waterfall mist (`Colonist.MistBonus`; the hover card and colonist tooltip say so); `WorkSpeed` 0.85-1.15. Unhappy (< 0.25, i.e. homeless counts) 5 days running: the
  unhappiest, newest first, leaves, one a day; neither unhappiness nor starvation takes a colony below
  `Needs.NeverBelow` (7): hardship thins, never empties (no death spirals, by design). Settlers arrive in waves (day 3, then
  every 3 days) while there are >= 3 days of food and nobody starves: pop x (2% + 10% x mood) a day, capped by free
  beds but at least 1, max 24.
  Founders bring ~2 weeks of food; daily warnings below 2 days. `ColonySim.NeedsEnabled` switches needs off (tests).
- **Homes (`Agents.Housing`):** a home is a room of type `Catalog.Home` (build menu; not a workshop, takes no parts),
  any size and levels, min 4 cells; adjacent home floor merges into one home (separate homes need a wall or gap).
  **Space:** a quarter of the cells is walkway; beds take 2 cells (one resident each), furniture 1; the policy's
  furniture per resident sets how many fit (`Housing.Plan`: 12 cells sleep 4 as a dormitory, 3 as a cottage, 2 as a
  manor); tableware sits on furniture (4 each), lighting/decor on walls (cells/2 + 1). **Policies** (`HousingPolicy`:
  tier 0-3 and pieces per resident per slot; Dormitory, Cottage, House, Manor; H edits, F over a home switches):
  editing one refurnishes every home on it. Colonists (hauling labor, `TryFurnish`) bring fixtures of exactly the
  wanted tier, promised per home (`Pending`), and install them (`Housing.Install`); a wrong-tier piece is swapped out
  when its replacement arrives and broken down for materials (half its recipe inputs x condition), surplus beyond a
  shrunken plan is trimmed the same way, worn-out pieces (`wear` days, only while installed: stock is never tracked)
  vanish and are replaced. Fixtures follow their floor cell across room splits/merges. **Residents:** reassigned
  from scratch whenever homes, beds or colonists change (and daily): most educated into the best homes (installed
  quality, then room id). **Rest:** `Colonist.Rest` (a day awake); tired -> sleeps in its own bed (rate by bed tier:
  humble 3.2, comfortable 4, fine 4.8 per tick) or on the floor (2); exhausted -> drops where it stands. **Quality:**
  per slot, coverage (pieces per resident vs want) x tier vs expectation (education + 1: Unskilled humble, Literate
  comfortable, Scholar+ fine), weighted bed .3 furniture .2 tableware .15 lighting .15 decor .2. **Layout**
  (`HomeLayout.Arrange`, redone when a home's fixtures or floor change, `Home.Layout` of `Placement`s): seeded by the
  room, so identical homes differ but each is stable. Walls = sides where the floor ends; doorways = sides leading to
  floor outside (kept clear with the cell inside). Beds head-to-wall over two cells, deep from the door, spread apart
  (rows only when crowded); furniture becomes tables (one per four sleepers, near the door, chairs facing them), then
  chests by beds and shelves on walls; tableware on tables at the chairs, overflow on shelves/chests; items' `place`
  hint puts lights on tables (rushlights), walls (lanterns, spread) or corners (standing lamps), decor under tables /
  beside beds (rugs), on free wall (tapestries, paintings) or in corners (statues). `HomeView` draws a model per role
  and tier (pallet / framed / canopy bed, stool / chair ...). `--homes-demo` builds one walled home per policy.
  Scale-up path: `Demands` and fixture counts are linear scans per home.
- **Families (`Society.Families`, `Housing.AssignResidents`):** a colonist's `Household` is its own id while single;
  newlyweds get a family id (`ColonySim.NewFamily`), shared by their children (`InFamily`). Daily, after settlers
  arrive (so they take beds first): children grow up after `Needs.ChildDays` (16) and stay home until they marry;
  a couple marries (`MarriageChance`, two adults of different households: never siblings) only into a home of its own
  (`Housing.CoupleHomes`: no family's, not shared, 2+ beds, at most the two of them lodging; one where settlers lodge
  is taken) and claims it (`Home.Family`); couples at home with a spare bed have children (`BirthChance`, while
  nobody starves): a full home means no more. Housing: families keep their claimed home (all members, parents
  first); a family that lost its home claims an empty one; singles lodge in homes no family claimed (shared
  dormitories via `HousingPolicy.Shared`, or empty houses), the most educated first, keeping their bed unless a
  clearly better home has room. Children don't work, are drawn smaller. Only singles ever leave (starving or unhappy):
  families stay together. Settlers outpace home growth (40-day bench: ~50 settlers vs ~18 births, 5 weddings);
  `Society.SettlersCome` switches settlers off (tests). Installed fixtures' lifespans vary ±20% so they don't all
  wear out on one day. Bench: `-- families [days]`.
- **Work priorities (`Colonist.Priority`/`SetPriority`, `Work`):** seven work types (Mining = dig/stairs, Woodcutting,
  Farming = sow/harvest/gather, Construction, Hauling incl. furnishing, Industry incl. the Academy, Trading = deliveries of agreed deals), each off (0) or
  1 (first) to 4 (last), RimWorld-style. `FindWork` tries priority 1, then 2...; at one priority the broad `Labors`
  order (its `PreferredWork` first) breaks ties, and designation kinds of that priority are one nearest-first
  `JobBoard.Candidates` search (kind mask). Defaults: 2 for work in its preferred `Labor`, 3 otherwise (same order as
  before priorities existed). Changing one takes effect at the next search; the job in hand is finished.
  `CurrentWork` names what it is doing. UI: J opens the grid (`UI.WorkWindow`; left click 1-2-3-4-off, right click back,
  headings set a column). Later: job-indicative clothing and titles by proficiency once colonist pages/skills exist.
- **Likes (`Colonist`):** fixed per colonist from its id: `FavoriteFood` (walks up to 8 tiles further for it; a treat
  counts for a day), `LikedMaterial` (fixture `material` in its home: wood, stone, metal, glass, cloth, clay),
  `LikedArt` (tapestry/painting/sculpture at home), `PreferredWork` (its `Labors` order; met when most of a day's
  work was that kind). Each met like adds 0.0375 mood (max 0.15); missing one costs nothing.
