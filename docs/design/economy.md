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
  lays bare (the tile below a dug one, registered in `CompleteDesignation`) turns to grass after 0.5-1.5 days (hashed per
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
  piles with the nearest stockpile cell with room (linear scans; spatial index is the scale-up path).
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
  (stack/carry sizes, pile model, colour), 12 broad DF-style workshops (`RoomType`), their 32 stations
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
  link point, conveyors plug in here later), Output crate (haulers empty it). Workers loop: reserve batch at
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
  deepest and glowing, quartz anywhere; `WorldGenerator.DepositMineral`). Terrain colours and glow (alpha < 1)
  are in `WorldView.TileColor`; the cap palette holds 32 entries. Wild ferns/wildflowers give herbs.
- **Farms (`Nature.Farms`):** zoned plots grow one crop each (wheat -> grain and herbs on grass/soil, glowcaps
  on any floor under a roof). Every `GrowInterval` ticks all plots grow at once (one flora rebuild per
  region); empty plots get a `Sow` order and grown crops a `Gather` order (Designations labor). Crops are
  harvested whole and resown. Plots, stockpiles and rooms never overlap.
- **Gathering:** berry bushes -> Berries, reeds/shrubs -> Fiber, herbs from ferns/flowers, only when ripe (`PlantInfo.RipeGrowth`); the
  plant is cut back to `GatheredGrowth`, not removed, so regrowth can drive future yields.
