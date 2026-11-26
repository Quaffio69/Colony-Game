# World: movement, terrain, water, building

Movement rules, stairs, designations, the water simulation and terrain hydrology, thin walls, floors and roofs, waterworks and cave-ins.

- **Movement:** agents are 1 tile tall and occupy an open cell; standable = solid floor below, or the cell is
  itself a stair or a deck (a built upper floor, see Upper storeys). Walk 8-way (no corner cutting); climb/drop exactly one level onto an orthogonal neighbour
  through plain air above the lower end (raw climbs slow, cheap if either end is a stair cell). Legality
  must stay symmetric (connectivity needs an undirected graph); costs may differ (up vs down).
- **Stairs (DF-style):** `UpStair`, `DownStair`, `UpDownStair` are carved, non-solid, structural cells.
  Vertical move from a cell that goes up into the cell directly above that goes down. Designating a stair
  onto an existing stair adds directions (up onto down = up/down). `TileInfo`: `IsSolid` blocks movement,
  `IsStructural` carries cave-in load, `IsStair`/`GoesUp`/`GoesDown`. `Chunk.SolidCount` counts solid only;
  `NonAirCount` is for emptiness checks.
- **Designations (DF-style UI):** click to anchor a corner, click again to apply; Z/X (or Shift/Ctrl taps) while
  selecting move the free corner between levels and the view follows. Tools (their ribbon hotkeys, or Tab/Shift+Tab):
  Dig, Stairs (auto: down on top, up/down between, up at bottom), Up, Down, Up/Down, ..., Cancel (the removal box as a
  tool: designations, zone cells and every plan in the box: walls, floors, racks, unbuilt room parts), Demolish (command
  `Demolish`: takes down at once everything built in the box, leaving its materials: thin walls/doors/windows on any edge
  touching it, so a box over a home's floor clears its walls all round; floors, roofs, racks, waterworks, room parts).
  Shift+click starts a removal box; a right click aborts the box, then puts the tool down. Hovering a blueprint says
  what it waits for (materials and where they come from, or a builder). Founders bring `StartingLogs` (20): enough for
  a size-1 saw horse and joinery bench (2 logs each) and a scaffold. A colony has at most `Balance.MaxOrders` (100k)
  orders (farm sowing aside; counted per colony in `Designations.OrdersOf`): a box fills top level first and stops there,
  saying so (a whole-map box made 10M orders and hung the game and joiners). Boxes clip to the world.
  Exception: plant orders (Chop, Gather; `Designations.IsPlantOrder`) are 2D areas draped on the surface
  (`DesignatePlantArea`, any level, ignores the view level), since that work always happens on the surface;
  their marks are drawn whatever level is viewed. Dig/stairs/stockpiles/rooms stay level-based boxes.

- **Dig order and priority:** colonists take the reachable order with the most urgent priority (`Designations.PriorityOf`, 1
  first ... 5 last, default 3; stored only when not 3, in snapshots from version 7 and in `StateHash`; command
  `SetOrderPriority` over a box, sent by the dig tools after each box), then dig and carve orders on the highest level (a
  box is dug top down, never undercut; plant orders ignore level), then the nearest (`JobBoard.Candidates` packs
  priority, level, distance into one key; levels up to 1023). Cancelling or finishing an order forgets its priority.
- **Geology and depth (`Procedural.Geology`, on `VoxelWorld.Geology`):** natural rock is `TileType.Stone` in the world;
  its kind (`Rock`: sandstone and limestone in tilted 6-level bands at the top, slate, granite, basalt, bedrock) is a
  function of position from the seed: layer tops on an 8-tile grid, interpolated in integers (deterministic, no memory:
  deep chunks stay uniform). Meshes, far blocks and the cap draw rock by its kind (`Geology.Look`: rock-look palette
  entries `TileType.Sandstone`..`Bedrock`, never stored); `VoxelWorld.TileName` names it. **Level zero**
  (`VoxelWorld.ZeroLevel`) is the lowest ground on the map; the UI shows levels from it, negative below. **Bedrock:**
  every column's floor is bedrock up to `BedrockTop(x, y)` (2 to ~2 + 3.5% of the height, varying smoothly: a random
  bottom within a range; small test worlds below 96 levels keep only z = 0), which cannot be designated
  (`VoxelWorld.IsBedrock`) and holds no ore. **Harder deeper:** dig time by rock (`Balance.DigSandstone` 70 ...
  `DigBasalt` 150, gems `DigGem` 200) and, below level zero, `DigDepthHardening` (0.5%) longer per level
  (`Designations.WorkTicks(kind, tile, rock, belowZero)`). Hand-built worlds (no geology) are all slate, no bedrock.
- **Fog of war (stateless):** solid ground with no open cell on any of its six sides is unexplored
  (`VoxelWorld.IsHidden`; the map's sides and bottom count as closed). Mesh faces only exist next to open cells, so
  only the cut surface needs it: `Meshing.CutCap` reads the levels below, at and above the cut with a ring and writes
  `TileInfo.Hidden` (palette entry 30, near-black, no glyphs) for unexplored texels; the cap re-patches when chunks on
  those three levels change. Hover and Inspect call it "Unexplored rock". Orders may still be placed on it.
- **Caverns (`Geology.CavernSpan`, `WorldGenerator.CarveCaverns`):** up to three cavern layers below level zero (7%, 19%,
  30% of the height down; deep worlds only), each a band of exactly one chunk layer (`CavernLayers` holds its bottom), so
  a cavern varies one layer of chunks where it opens (on a 2048 map ~6k chunks, +34 MB world, +89 MB whole sim). Per
  column: big blobs (2D noise over `CavernThreshold`, roughly a sixth of the map a layer), a floor rolling 0-6 levels
  over the band's bottom, a roof 2-9 levels up (lower to the edges), pillars; never within `CavernRoof` (10) of the
  surface, never in the bedrock. Wild ripe glowcaps grow on cavern floors in patches (`FloraGenerator.CaveGlowcaps`).
  **Unseen until breached:** carved cells start unseen (`Chunk.Unseen`, a bit a cell, null in chunks without; in
  snapshots from version 8, the chunk marked modified when revealed; `VoxelWorld.UnseenCells` in `StateHash`). Any
  `VoxelWorld.Set` that opens a cell beside an unseen one reveals the whole connected unseen space (`Reveal`: a flood
  fill; raises `ChunkRevealed` for the chunks round it, bumps their versions); rock set into an unseen cell fills it.
  Unseen air is rock to everything the player sees or orders: meshing (`TileBox.CaptureSeen`, `IsBuried` counts it),
  the cap (`MaskUnseen`), picking (`VoxelRaycast`), flora (`FloraView` skips its plants, rebuilds on reveal), plant
  orders (`DesignatePlantArea` skips it), and connectivity (not standable until found: labelling every cavern chunk
  cost ~100 MB). `--mine-demo=cavern` sinks a shaft into the nearest cavern.
- **Water (`Fluids.Water`): shallow-water flow over column spans** (city-builder style, generalised to caves). A
  column is a list of spans: vertical runs of open cells resting on solid ground (the sky above the surface, a
  tunnel). A span holds a continuous depth of water (fixed point, `U` = 1/65536 level per unit), so surfaces are
  smooth heights. Water moves between overlapping spans of neighbouring columns through pipes driven by the
  difference in surface height clipped to the shared window (water pours over a lip into a lower neighbour:
  falls, pits, flooding a tunnel under an overhang); each pipe keeps 31/32 of last step's flow (momentum) plus
  1/5 of the head difference times the depth at the window (up to a level), scaled down if a span would give more
  than it holds. Accel must stay above 4 (sides) or alternate columns ping-pong. **Pressure** (`Pressurize`): each
  step, connected spans full to their ceilings form a flooded region acting as one pipe between its open ends (free
  spans linked to it). Its pressure = the ends' heads averaged by opening size; ends above it give, ends below take
  (half the difference a step, at most a level per opening), equal amounts, so the region stays exactly full and
  hides no water; inside it pipes carry nothing (`Confined`). Pressure is even along a still tunnel; a shaft at the
  far end fills to the lake's level, or gushes out if it opens below it (artesian). If the ends stand below the
  region's roof it isn't pressed and drains normally. (The first try, pressure as stiff squeezed-in water, hid
  water and lost head along tunnels: a 30-tile tunnel showed 37 levels at one end, 12 at the other.)
  `Water.Pressure` reports it (and the hover text). No siphons (no suction).
  Spans are rebuilt from the world on `ChunkChanged`, laying old water back in cell by cell (water
  in now-solid cells goes to the space above). Map borders drain. Springs add up to a rate until their span's
  surface reaches their limit. Public amounts are thousandths of a cell (`Full`); `MassAt` = how full a cell is.
  **Seepage:** water above its column's natural level (generated: just under a lake's shore / a river's bank, or the
  ground on land) loses `Seepage` a step (~a level per 6 game minutes). Without it, lakes whose inflow beats their
  outlet rose forever and flooded the land over hours (the books balanced; it was real accumulation). Dug-out water
  below the old ground is never touched; a fed reservoir still fills. `Total + Drained + Evaporated - Sourced` is
  constant (exact conservation).
  Generated rivers start `RiverFill` of their channel, lakes at their sill; springs at every `RiverSources` point
  (rate by catchment gain, never filling past 0.9 of the channel); `ColonySim.WaterSettleSteps` (400) run in the
  background while the first frames draw (`WaterReady` gates ticks and edits). Wheels read `ThroughFlow`.
- **Terrain hydrology** (`Terrain.Generate`, fine grid, whole levels; `-- terrain` bench checks it): rivers are
  1-2 deep with a level of bank above their water (room for their flow; flush banks flooded): each river tile's
  level is the lowest land within 2 tiles minus one, min-smoothed over the river within 2 tiles.
  `SetWaterLevels` priority-floods from the map edge (lowest first): levels never rise downstream (the river
  below is lowered, unless it runs into a higher lake: then the valley here is below that lake and joins it);
  lakes keep the level their basin fills to and spill into a lower outflow at the outlet; a river below the lake
  it enters becomes lake (backwater); side-by-side river tiles share the lower level (not if that would sink a
  river under the lake it feeds); where a river passes a lake lower down, that lake edge becomes shore unless it
  would cut water off from the edge. `MarkSpringPools` gives each stream head a pool; `BuildLakeSills` raises the
  bed at each lake outlet to a level below the lake (the lake drains only to its sill). `ShapeBanks` grades land
  within `BankReach` tiles to rise a level per tile from the bank. River/lake neighbours don't count as cliffs
  for surface materials (sand banks, not stone). The bench must show 0 water beside lower land, 0 systems not
  reaching the edge, 0 water tiles with no downhill way out, and only lake-outlet side steps.

- **Thin walls (`World.EdgeInfo`, `WallMaterials`):** walls, doors and windows stand on cell edges, one level tall,
  so rooms keep their floor. Each cell owns its east (+x) and north (+y) edge: a `ushort` per cell in `Chunk.Edges`
  (allocated on first use; low byte east, high byte north), each byte kind (2 bits: wall, door, window) + material
  (6 bits: log, rubble, plank, stone block, brick: item cost, look, quality 0-2). `VoxelWorld.SetEdge` notifies both
  cells' chunks like a tile change (so connectivity, rooms, water and views follow); `EdgeRevision` for views;
  `ITileReader.EdgesAt` (and `TileBox` copies edges) so movement reads them on the world or a snapshot.
  **Movement:** walls/windows block a move across them (doors don't); a diagonal is blocked by any of the four edges
  round its corner; a climb or drop crosses the edge at the upper of its two levels (a one-level wall can't be
  stepped over); symmetric (tested on random terrain). Rooms and homes split along walls. **Water:** each pipe has
  a `Sill`: the top of the watertight edge run from the bottom of the shared window (`Water.SillOf`), so a wall line
  is a dam that spills over its crest; a door is no dam. **Building:** `Constructions.PlaceEdge`/`PlaceOutline`
  (walls round a box)/`CancelEdge`/`Demolish` (instant, refunds); edge blueprints are delivered to whichever adjacent
  cell has floor (`Blueprint.Cell` = site, `EdgeCell`/`Side` = the edge) and built by builders like other structures;
  a door or window may be cut into a built wall (the wall's material comes back). Costs: wall 1 of its material,
  door 2 planks, window 1 glass. **Tool:** one wall tool (Build, Structures: a wall per material, doors & windows): click the edge for one piece,
  drag along it for a line, drag away from it to wall round a box; Shift removes (see ui.md). **Look:** `EdgeView` (stacked logs, rubble, plank panels,
  coursed blocks, brick; coping, corner posts, door leaves, glazed windows; blueprint ghosts). **Homes:** the shell
  (`HomeLayout.Survey`: outline closed by walls/doors/windows/rock, their tier vs expectation, windows) is a fifth of
  `Housing.Score`; wall materials count for liked materials. The old full-cell `TileType.Wall` (Tab order) stays
  for dams and embankments.
- **Floors and roofs (`World.CoverInfo`, `FloorMaterials`, `RoofMaterials`):** a byte per cell (`Chunk.Covers`): floor
  (plank, flagstone, tiles) low nibble, roof (thatch, shingle, slate, clay tile) high nibble; no effect on movement or
  water. A cell filled solid loses both; a floor whose ground is dug away goes. `Constructions.PlaceCover`/
  `PlaceCoverBox`/`ClearCoverBox` (refunds); built by builders from 1 item a cell. Tools: Floors and Roofs rows in
  the build menu (box), and Line (click an edge, drag along it: walls, doors or windows). `CoverView`: patterned floor
  plates; roofs a height field per level (vertex height = pitch x distance in from the roof's edge: hip roofs and
  ridges for any outline), drawn above their cell with `roof.gdshader` (sliced exactly at the cut, courses and
  staggered joints from world position) so a home's own level shows its interior; floor plates and stair flights are
  sliced at the view level itself (an upper storey's floor, at the top of the viewed level, would lid it). Furniture stays under one level.
  Home shell = walls .45 + floor .25 + roof .2 + windows .1 (`HomeShell.Score`); bare rock underfoot or overhead is
  humble, bare earth and open sky nothing.
- **Upper storeys (`TileType.Deck`, `StructureKind.Deck` / `Stair`):** a deck is an open cell with a built floor of its
  own (DF's constructed floor): standable whatever is below (`TileInfo.IsFooting`, like a stair), not solid, carries no
  rock (`IsStructural` false), not diggable (Demolish takes it down, leaving its boards below); its look is its floor cover
  (kept when the ground under it goes; taken with the deck). You can climb onto a deck's edge from the ground beside it
  unless an upper wall stands there. Decks cost `DeckCost` (2) of their floor material; built stairs `StairPlanks` (3).
  **Built from below:** `Blueprint.Target` is the cell built, `Cell` the site where materials go and the builder works
  (equal for every other kind): a deck is sited on the cell below it, a stair above the ground on the stair below it;
  builders stand round the target (`ReachableAround(Target)`, never in it). Upper walls, roofs and decks above may be planned
  on a cell whose deck or stair is only planned (`Constructions.WillStand`): their materials wait, since haulers only
  deliver to cells they can reach. `Blueprint.Target` is in snapshots from version 6 and in `StateHash`. **Houses
  (`Orders.Houses`, command `PlaceHouse`):** a box between ground tiles plus a storey count (1-6, sides up to 40):
  `Survey` lists every cell in the way (rock or earth, uneven ground, existing stairs or decks, a tree, a station, a
  structure or plan, no ground) and nothing is placed while anything is; `Place` plans, bottom up, per storey the stair
  (up / up-down / down, in the corner away from the door), the floor (laid on the ground floor, decks above), the door
  (middle of the first side with ground outside, skipped if the outline already has one; placed before the walls so they
  leave its edge) and the walls (`PlaceOutline`, existing walls kept); then the roof over the top storey wherever no rock
  is overhead. Homes count a deck overhead as a roof of its floor's tier. Water still passes through decks.
  `Chunk.StairCount` lets `VoxelWorld.AllStairs` skip chunks without stairs (`CoverView` draws a flight of steps in each
  stair that leads up).
- **Waterworks:** `Orders.Constructions` holds single-cell blueprints (walls from 1 stone; water wheels and
  pumps from planks) built like room parts (haulers deliver, Designations-labor builders build;
  `ColonySim.CompleteConstruction`). Walls stack on wall blueprints, so dams rise level by level; a wall is
  `TileType.Wall` (digs back out as stone). `Fluids.Waterworks`: a wheel's power = water flowing through its
  cell per step / `FlowPerPower` (hang it in a waterfall: wheels may mount on a wall); pumps lift water from
  their cell to the cell ahead and one level up. While placing one, its model stands at the cursor as a green (or red)
  ghost turned the way the click will build it (R turns it; a wheel hung on a wall takes the wall's line:
  `DesignationTool.StructurePreview` → `WaterView.ShowPreview`); power is one pool per colony (its wheels, pumps and conveyor belts; all run at the
  share the wheels cover). Farm plots within 2 tiles of water grow 1.5x as fast (`Farms.Irrigated`).
- **Waterfall mist (`Fluids.Mist`):** a coarse field (4x4-column cells: density 0..4000, `Full` = 1000, and the level
  it hangs at). Falls of 1.5+ levels (`Water.Falls`) feed the cell they land in: sqrt(strength) x drop (up to 12) x
  `Balance.MistPerFallLevel` a second, so a trickle hazes its foot and a river over a cliff raises banks. Each water
  step re-reads one of 40 bands of block rows (a fall's feed is re-measured every 8 s and stands meanwhile; ~0.13 ms
  a step on a 2048 map with 2800 awake water blocks); every 5 steps (1 s) the field spreads: feed added, `MistDecay`
  per mille lost, `MistSpread` per mille passed to the 4 neighbours weighted by `Mist.Wind` (a hash of the time, a new
  heading every 150 s), settling at their ground (up a step, down a ledge, never into rock). Only cells holding mist
  are touched (a sorted list: canonical order, deterministic). Densities add up, so many big falls make wide, thick
  banks; stopped falls thin away within a minute. Colonists in mist (`Mist.At` >= `Felt`) count its seconds toward
  the day's mood (people.md). Saved in snapshots (v11) and in `StateHash`. Drawn by `MistView` (rendering.md); a
  future beauty system can read the same field.

- **Cave-ins (DF-style):** a solid tile is supported if face-adjacent solid tiles connect it to z=0. After a
  dig (`ColonySim.CompleteDesignation` -> `World.CaveIns`), each unsupported neighbouring mass falls as one
  rigid piece until it lands; designations on it are cancelled; colonists ride it down or pop out on top.
  Masses over `CaveIns.MaxMassTiles` count as self-supporting. Any code removing solid tiles should go
  through `CompleteDesignation` or call `ColonySim.CheckSupport`. No damage yet (no health system).
