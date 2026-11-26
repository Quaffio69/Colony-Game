# Economy: balance, industry, items, farming

Pacing knobs (Balance.cs), the industry direction, flora, items and hauling, racks and scaffolds, the production tree, the factory builder, raw resources, farms and gathering.

- **Balance (`Colony.Core.Balance`):** the pacing knobs live in one file (time, population, farming, work ticks,
  recipe labour, ants); system constants point there, so tune there and measure with `-- balance [size] [days]`
  (resources round the landing, ore within 64 columns/48 levels, nests, labour, food per tile, then days with no
  player). Target pace (1x, day = 4 min): between an RTS and DF. Personal rhythms stay brisk (three meals, a fifth of
  the day asleep); colony growth is slow enough that each step is a decision: settlers in waves from day 3, a colony
  doubling in ~1 hour, wheat 4 days (bread: 1.5 tiles a colonist), glowcaps 3 days (2.3 tiles), digging stone 90
  ticks, recipe labour 120 + 45/tier, education 2400/4800/7200 ticks a promotion, ants raiding every ~3 days sized to
  the colony. Bread chain: grain*2 -> flour, flour*2 + charcoal -> bread*2 (4 meals each). Wild small plants start
  mostly ripe (late summer) so foraging bridges the first harvest.

- **Industry over crafting (core direction):** gameplay starts dwarf-like (basic resource production) and
  grows into specialised industries, with the focus on the industrial side. No generic 3x3 crafting spots:
  production happens in player-built factories from a dedicated factory builder (in the spirit of Songs of
  Syx: rooms/buildings sized by the player, whose size and fittings set throughput and staffing). Scale is a
  core value. So every resource system (tree chopping, plant gathering, farming, mining, hauling) should be
  designed as an input to factories: bulk/area designations, throughput-oriented jobs, data that scales to
  thousands of workers and items, and no per-item micromanagement baked in.
- **Flora (`Nature.Flora`):** trees and surface plants are not voxels: a sparse per-cell registry (one plant
  per cell, standing on grass/soil) in 128-column regions with version counters, drawn as instanced
  stand-ins. They don't block movement (deliberate for now). `Plant.Growth` (0-255) and
  `PlantInfo.MatureHeight` are meant to drive future yields. Ground changes go through
  `ColonySim.CompleteDesignation`/cave-ins, which remove or carry plants; code that edits tiles elsewhere
  should call `Flora.Validate`. No growth/regrowth simulation yet. **Grass regrowth (`Nature.Regrowth`):** soil a dig
  lays bare (the tile below a dug one, registered in `CompleteDesignation`) turns to grass after 2-4 days (`Balance.GrassRegrowDays`) (hashed per
  cell) if it is open to the sky (top of its column, no floor/roof over it, not flooded: then it waits), when a
  neighbouring top is grass (so patches fill in from their edges), or anyway after `Rounds` (3) checks. A watched list
  checked every 240 ticks; nothing scans the map. In snapshots (v3).
- **Items (`Items` namespace):** counted piles, not objects: at most one `ItemPile` (type, count) per
  walkable cell, resting on a floor. Drops settle downward and spill to nearby cells when a stack is full.
  `Reserved` (promised to a hauler picking up) and `Incoming` (space promised to a delivery) keep haulers
  from colliding; every reservation must be released on completion or `Colonist.Abandon`, and carried loads
  are dropped, never lost. Item kinds are data: `ItemType` is an index into `Industry.Catalog` (well-known
  ones as `ItemType.Log` etc.). Digging stone leaves stone 50% of the time, clay/sand/minerals always; felling
  leaves logs scaled by tree size (`ColonySim.MiningYield`/`LogYield`). Piles re-settle when their ground changes (digs,
  cave-ins). `Stockpiles` are zoned floor cells (all types for now); `Hauling.Plans` pairs the nearest loose
  piles with the nearest stockpile cell with room (linear scans; spatial index is the scale-up path). A load is `Balance.CarryKg` (30) over the item's `weight` in kg (default by category: 6 logs, 10 stone, 60 herbs); picking up, a
  hauler tops it up from loose piles of the same item within 2 tiles (`Colonist.TopUp`: gathering leaves many small piles).
  Who hauls is set by work priorities (see Work priorities). `ItemPiles.UnitsOf(type)` is a running per-type tally
  (no scan). **Storage racks** (`Stockpiles.Racks`, `StructureKind.Rack`): warehouse shelving built on a stockpile cell
  (command `PlaceRacks` over a box, R turns the fronts; Build, Zones, Storage; removed by `RemoveStructures`, with its
  zone, or walled over: planks refunded via `Stockpiles.RackLost`). It rises into the open cells above, up to 3 levels
  (`RackLevelsAt`, fixed when planned: the blueprint's `Material`), costs 3 planks a level and holds
  `Capacity(levels)` = 1 + 2 x levels stacks (`ItemPiles.LimitAt` = stack x `CapacityAt`). Racks don't block movement.
  A rack blueprint's cell takes no stored goods (haulers clear it, then bring its planks). `RackView` draws them
  (uprights, two shelves a level, braced back, front rail) with a ladder per run of tall racks sharing a front, rolling
  along the rail to the colonist nearest it; `ItemView` puts one pile model per stack on the shelves.
  **Scaffolds** (`Agents/ColonistScaffold.cs`, item "scaffold", 2 logs at a joinery bench): work out of reach (a ceiling
  two levels over the floor) has `Job.HighSpots` (standable cells two below the reach with open air above:
  `HighComponents`). When the colony has a scaffold (`AnyScaffold`), `TryTakeJob` may pick such a job: the colonist
  claims it, fetches the nearest free scaffold (task `ScaffoldFetch`), carries it to a high spot, puts it up
  (`ErectTicks`), works from the cell above where it stands (`OnScaffold`, `ReachFrom`), takes it down (`StrikeTicks`)
  and leaves it as a pile (haulers return it). Abandoned at any point, the scaffold stays where it stood. Designations
  only (dig and stairs) for now. `ColonistView` raises the colonist on a pole-and-deck scaffold as it goes up.
- **Production tree is data (`data/tech-tree.json`, embedded in Core, loaded by `Industry.Catalog`):** items
  (stack size, weight, pile model, colour), 12 broad DF-style workshops (`RoomType`), their 32 stations
  (`PartType`s with role Machine; build material by size) and 79 recipes (multi-input `Ingredient`s, output
  count, labour ticks, education). Adding a product is one item + one recipe line; tiers are computed. Load
  errors throw naming the entry. Tests check the tree bootstraps from raw resources and batches fit crates.
  **Home fixtures** (category "home"): items with `fixture` (slot: bed, furniture, tableware, lighting, decor), `tier`
  (1 humble, 2 comfortable, 3 fine; `Catalog.Fixtures` per slot, validated 1..n, all makeable) and `wear` (days it
  lasts in a home: upkeep, longer for better tiers). Humble fixtures need no schooling. They are installed in homes
  (see Homes below), not used up.
- **Factory builder (`Industry`):** a `Room` is a typed set of walkable floor cells that is always
  contiguous under the movement rules (so it may span levels joined by stairs/steps). `Rooms.Paint` takes
  every walkable cell in a click-click box (all its levels), merges into touching same-type rooms and splits
  disconnected pieces; `Erase`/lost floor (`ValidateCell`, wired into digs and cave-ins) re-split, and parts
  follow their piece or are removed if cut. Rooms and stockpiles never overlap. Parts (`PartInfo`) have
  footprints + rotation and a role: Machine (a station of the room's workshop; each placed machine is set to one
  of its station's recipes, `Rooms.SetRecipe`/`CycleRecipe`, F in game; it runs it in batches), Auxiliary
  (covers N machine tiles with a bonus; per family the best are assigned first, so efficiency scales with the
  room, not raw count, and lesser extras never hurt), Input crate (one input each: `Hauling.InputDemands` fills crates
  towards two batches of every input the machines need, keeping an empty crate per input that has none, so
  a recipe needs at least one crate per input; inputs no machine needs are hauled out; sources are loose piles, stockpiles or other factories' output crates; this is the factory
  link point, conveyors plug in here later), Output crate (haulers empty it; one per product a room makes, since a crate holds one kind of item: `Rooms.Status` says when one is missing). Machines have a stock limit (`Part.Limit`, command `SetMachineLimit`, Inspect: Keep in store ‹ ›): no new batch while the colony holds that many of the product (`Rooms.AtLimit`; status "stocked up"). Workers loop: reserve batch at
  every input (one pickup per crate) + space at output crate + claim machine (biggest free machine first),
  fetch each, work
  `WorkTicks / Efficiency`, store; a partial batch is allowed when the crate isn't being refilled, so
  remainders never strand. **Education** (`Colonist.Education`, levels from the catalog) gates recipes, not
  rooms. Academy recipes promote colonists one level (students = colonists one level below, reserved per
  class); founders are 1 Scholar + 2 Literates; the first Magister arrives (`ColonySim.CheckEvents`) once an
  Academy attunement circle is built. Worker items (automatons) join as unskilled colonists that can't be
  taught. `ColonySim.Events` feeds the HUD. **Part sizes (1-5)** (same table for every station) are the core trade-off: bigger machines cost more per tile
  but need less labour per unit (batch x time); bigger tools cost exponentially more but give more bonus and
  coverage per tile. **Construction:** placed parts are blueprints (`Part.Built` false; they do nothing).
  Haulers deliver `PartInfo.Cost` onto the footprint as ordinary piles (`StorageRole.Construction`, one pile
  per cell, so costs never exceed tiles x stack limit); a builder (Designations labor) reserves them, works
  `BuildTicks`, consumes them. Station costs come from the catalog by size (size 1 is always buildable
  before that station exists: logs or stone for the first ones); tools/lanterns/crates cost logs then planks. Cancelled blueprints leave their materials as loose piles. `Rooms.InstantBuild`
  is the sandbox/test switch. Internals are deliberately not an optimisation puzzle. Parts don't block movement.
  Who builds and who runs machines is set by work priorities (see Work priorities).

- **Raw resources:** clay (lake beds, under river sand, patches of the soil layer), sand (riverbeds, beaches)
  and 8 minerals (`TileInfo.IsMineral`; copper/tin high, iron middle, silver/gold/sulfur deep, aetherite
  deepest and glowing, quartz anywhere; `WorldGenerator.DepositMineral`), plus 5 gems (`TileInfo.IsGem`: garnet from
  the middle down, sapphire, ruby and emerald deep, diamond only near the bedrock; small pockets) that leave rough gems,
  cut at the glassworks' grinding wheel (emerald and diamond need Scholars), set into jewellery and regalia at the forge.
  Deposits grow bigger with depth and never sit in the bedrock. All natural rock yields "stone" whatever its kind
  (`Procedural.Geology`, see world.md). Terrain colours and glow (alpha < 1) are in `WorldView.TileColor`; the palette
  holds 32 entries (1-28 tiles and rock looks, 30 unexplored, 31 far water; 29 free). Wild ferns/wildflowers give herbs.
- **Farms (`Nature.Farms`):** zoned plots grow one crop each (flax -> fiber; wheat -> grain and herbs on grass/soil, glowcaps
  on any floor under a roof). Every `GrowInterval` ticks all plots grow at once (one flora rebuild per
  region); empty plots get a `Sow` order and grown crops a `Gather` order (Designations labor). Crops are
  harvested whole and resown. Plots, stockpiles and rooms never overlap.
- **Gathering:** berry bushes -> Berries, reeds/shrubs -> Fiber, herbs from ferns/flowers, only when ripe (`PlantInfo.RipeGrowth`); the
  plant is cut back to `GatheredGrowth`, not removed, so regrowth can drive future yields.

- **Heavy machinery (`Agents.MachineKind`, `Agents/ColonistMachine.cs`):** dwarven steam engines built at the artificer's
  assembly frame (Literate): a steam engine (4 iron ingots, 2 steel plate, 3 cogs), then the **excavator** (engine, a steel
  bucket forged from 3 steel, 4 steel plate) or the **power wagon** (engine, 4 planks, 2 iron ingots). Items of kind
  "machine" (`ItemKind.Machine`) roll out of the workshop as machines (`ColonySim.AddMachines`/`AddMachine`): entities
  with `Machine` set and `IsAutomaton`, named "Excavator 2" etc., that **never work by themselves** (no priorities;
  `ColonySim.Machines` lists them, rebuilt when the roster changes). **Driven by colonists**, like a scaffold fetched for a
  high job: a colonist taking a dig/stair order (Mining) or a haul (Hauling) may first claim a free machine of its colony in
  its component (task `Board`, the job claimed or reserved meanwhile), walk to it, climb in (`MachineBoardTicks` 40) and
  drive it on to the job (`FinishBoarding`). Aboard (`Vehicle`; the machine's `Driver`), it steps at the machine's pace,
  the excavator digs `Balance.ExcavatorDigSpeed` (5x) as fast in any rock but crawls (`ExcavatorStepScale` 1.6), the wagon
  rolls faster (0.7) and carries `WagonLoads` (8) colonist loads; the machine follows its driver (`SyncVehicle`). A driver
  takes only the machine's trade (the excavator digging; the wagon hauling and shipping deals, in its priority order:
  whatever its other priorities, it got in for that work, and gets out when it runs out)
  and no scaffold jobs. It **parks** where it stands (`Park`) when nothing is worth driving to, or as soon as its task has
  nothing to do with the machine (a meal, bed, a fight, a draft, struck down), then leaves machines be for
  `MachineRestTicks` (300). **Is it worth fetching?** Judged on the run of work the machine would do before its driver gets
  out, not one job: the excavator against digging by hand, counting the open unclaimed dig/stair orders within
  `MachineClusterReach` (8) tiles and `ExcavatorClusterLevels` (2) levels of the job, capped at `ExcavatorSessionJobs` (40)
  (`ExcavatorDigCost` vs `HandDigCost`: walk to it, climb in, crawl it over, then each job dug fast with a crawl between);
  the wagon against carrying by hand, counting the haul's pile plus loose goods of its kind round it, capped at
  `WagonSessionLoads` (3) wagon loads, a whole wagon load only into a stockpile (`WagonHaulCost` vs `HandHaulCost`). Steps
  are measured in two passes: straight-line (|dx| + |dy| + 2|dz|) shortlists the cheapest machine that could pay at all (the
  most a full session could save is checked before the orders are counted), then only that one is measured by pathfinding
  (its trip to the work, the walk to it, and for the wagon the haul; the walk to the work by hand is the path the colonist
  planned anyway; `MeasureSteps`, `RouteSteps`, a long trip's first leg plus a straight line), at most three searches on
  the shared per-tick path budget (a decision waits a tick rather than spike a frame). A machine the measurement turns
  down is not measured again for that work for `MachineRestTicks` (`_machineNo`), so a machine behind a wall is judged by
  the way round it. So a single far tile is dug by hand and the excavator stays put, while a
  big excavation fetches it from across the map; and since a parked machine stands where its work was, it is beside the
  next dig there. The same test without the climb in (`ExcavatorStays`) decides whether a driver crawls on to the next dig
  or gets out, so the two never disagree; a wagon driver always drives on while there is hauling (it is faster than
  walking). The wagon: a haul to a stockpile reserves more of the source pile plus room in the stockpile cells round the
  destination (`WagonReach` 3, same level and colony), and at pickup gathers the same item from loose piles round it into
  more room (`ExtendLoad`, `PickUpSpill`, `DeliverSpill`; every reservation is released on abandon). Shipping a deal, a
  wagon load reserves room in the buyer's depot cells round the drop and goods from our store piles round the pickup
  (`ReserveTrade`, `_tradeGoods`, `_tradeRoom`); a trader on foot may fetch the wagon first if what is still owed of the
  line makes it pay (`TryFetchWagonForTrade`: nothing is reserved on the walk; aboard, it looks again). **Fuel:** charcoal. A
  firebox holds `FuelCharcoalLoad` (12) charcoal of `FuelPerCharcoal` (10) work; a dig costs 2, a wagon trip 3. Below a
  quarter the driver drives it to the nearest usable charcoal and stokes it (task `Stoke`, `TendVehicle`); empty with none
  to be had, it is parked and left a while, and an empty machine is only fetched while charcoal lies somewhere. Fuel only
  burns while needs run (sandboxes are free). Snapshot version 9 (kind, fuel), 12 (who drives which; a walk to a machine
  is let go on settle), `StateHash`. **Look (`ColonistView`):** the excavator on iron tracks, its house turning on a slew
  ring (cab, copper boiler, smokestack, glowing firebox, counterweight), a brass boom and stick with steam rams and a
  toothed bucket that scoops while it digs; the wagon a plank bed on four wheels with a boiler up front and its driver's
  load heaped in the bed; both stand 1.3x a person. The excavator's tracks (`BuildTracks`) drive forward or back along
  their line like a real one, turning only to move off it (at most a quarter turn, whichever end leads), while its house
  (`BuildHouse`: cab, boiler, boom, driver) slews on the ring to face the dig and keeps its facing while the tracks move;
  so digging a line towards itself it backs up a tile at a time without turning round. The wagon turns to face where it
  is driven. A driver vanishes into its
  machine: a small figure in its colour in the cab's roof hatch (on the wagon's footplate), its name on the machine's tag
  ("Excavator 1 · Edric"). Hover card: fuel and who drives it; machines are left out of the work grid. `--machines-demo`
  shows both at work.
