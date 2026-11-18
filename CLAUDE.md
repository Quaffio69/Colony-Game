# Colony Game

A 3D grid-based colony simulation in the spirit of Dwarf Fortress. The player leads a small colony that
builds structures and hollows out mountains to make room for ever-larger, more productive factories
(food, clothing, weapons, armor, metals, crafts, then long chains of advanced products). The goal is
open-ended growth: a larger, better-educated, more sophisticated population over time. Long-term
ambitions: many interlocking systems and open-world procedural narrative storytelling.

## Architecture (decided)

- **Engine:** Godot 4.7.2 .NET edition, C# only (no GDScript).
  Godot exe: `C:\Users\Andrew\Desktop\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe`
  (use the `_console.exe` variant for headless/CLI runs).
- **`src/Colony.Core`**: pure .NET 8 class library holding *all* simulation (world, jobs, pathfinding,
  economy, population, narrative). **Must never reference Godot.** That keeps it fast, testable
  headless, and engine-independent.
- **`game/`**: the Godot project, a thin presentation/input layer that reads from Core and sends
  commands to it. Assembly name `ColonyGame`.
- **`tests/Colony.Core.Tests`**: xUnit tests for Core. New simulation logic should come with tests.
- **`tools/Colony.Bench`**: headless sim stress benchmark (reports per-phase tick costs and worst hitches).
- Performance escape hatch, if ever needed: hot paths via GDExtension (C++), but profile first.

## Conventions

- Simulation coordinates: x/y horizontal, **z = vertical level** (z=0 bottom), DF-style.
  The renderer maps sim (x, y, z) â†’ Godot (x, z, y), since Godot is Y-up.
- World generation must be deterministic from a seed (use `Colony.Core.Procedural.Noise`, not
  `System.Random` with shared state).
- The Godot SDK project has no implicit `System.Collections.Generic` using; add it explicitly.
- `ColonySim` is the root of sim state; Godot nodes receive it via `Initialize(...)` from `Main`.
- Use `SimSpace` for sim<->Godot conversions. In game code `Noise` clashes with `Godot.Noise`;
  fully qualify `Colony.Core.Procedural.Noise`.
- Terrain colours are sRGB vertex colours converted in `game/shaders/tiles.gdshaderinc`; terrain and cap
  shaders share that include so the cut surface matches.
- Algorithms that read tiles are generic over `ITileReader` so they run on the live `VoxelWorld` or a
  `TileBox` snapshot (fast, thread-safe copy). Snapshot padding: movement reads 1 tile around and 2 below.
- Sim must stay deterministic: no wall-clock budgets in Core (the path budget counts nodes, not ms).
- Avoid per-update large allocations in hot Core paths on big maps (LOH -> full GCs); reuse buffers.

## Performance architecture

- **Rendering:** 32x32x16 sections, greedy-meshed (`Meshing.SectionMesher`) on the thread pool from
  main-thread `TileBox` snapshots; uploads are budgeted per frame. The view-level slice is a shader
  discard plus a texture-driven cap quad, so changing level never re-meshes. 4x MSAA.
- **Reachability:** `Navigation.Connectivity` labels per-chunk regions, links them, and union-finds
  components; only changed chunks relabel. "Is this job reachable?" is a component compare.
- **Jobs:** `Orders.JobBoard` caches each designation's work spots and their components; colonists only
  path to jobs in their own component, nearest first.
- **Pathfinding:** hierarchical: BFS over the region graph gives a chain of regions, then A* restricted to it
  (widened by one ring; on a snapshot of its bounding box only for long corridors). Long trips go in legs
  (`Colonist.LegRegions` regions each, next leg planned on arrival), so no single search is huge and a
  cross-map trip never stalls a frame. Colonists share a per-tick node budget (`PathNodeBudgetPerTick`).
- **Deep worlds are cheap:** a `Chunk` whose tiles are all one type stores no array (`IsUniform`), so deep
  rock and sky cost almost nothing; the generator writes them uniform directly. Render sections buried in
  solid rock are skipped without snapshots. `Connectivity` iterates only chunks that have regions.
- **Change tracking, not scans:** `VoxelWorld.ChunkChanged` fires for every chunk whose version changes.
  `Connectivity` relabels exactly those chunks, `JobBoard` re-checks work spots only near them (other jobs
  just refresh component ids), and `WorldView` keeps a dirty-section queue and patches only changed cap
  chunks. Nothing scans all chunks/sections per tick or frame. Components are a BFS over the CSR region
  graph (~2 ms at 2048, 156k region edges).
- `Colony.Core` compiles with `<Optimize>true</Optimize>` even in Debug: the Godot editor runs Debug, and
  unoptimized meshing made startup ~3x slower. Keep it that way.
- Default map 2048x2048x384: gen ~1.3 s, first frame ~2 s, fully meshed ~6 s (WorldView spends up to 40 ms
  per frame and 8x the in-flight cap while >2000 sections are pending), ~650 MB managed. Use
  `--size=1024x1024x384` for faster iteration. Sim bench at 2048 (20 colonists, digging, felling, hauling):
  ~0.2-0.45 ms/tick avg; worst ticks ~5 ms per dig-driven connectivity rebuild.
- **Water** (`Fluids.Water`): per 16x16-column block (a chunk column) of spans; only awake blocks step (a block
  sleeps once its water is still; flowing rivers never are). Two passes per step (outflows, then volumes), each
  writing only its own block, so blocks run in parallel deterministically; pipes are precomputed per span
  (`BuildLinks`), rebuilt when a block or its neighbours change shape. Steps every `WaterInterval` (4) ticks. Cost
  ~1 ms/step at 1024, ~7 ms/step at 2048 (every river flows; `Apply` is the bulk). **Off the main thread:** the step
  begins at the end of a tick and joins at the start of the next (`ColonySim.AsyncWater`, set by the game; tests and
  tools step synchronously): readers meanwhile see the pre-step water, writers (`Add`, `Move`, springs, `ChunkChanged`)
  `Join` first, and the result is bit-identical to a sync step (tested). Rebuild, `Pressurize` and prep stay sync.
  Bench: `-- water [size] [steps]` (phase timings incl. rebuild and pressure), `-- upipe [size] [steps]` (a U-pipe
  dug under a real lake a cell at a time),
  `-- flood [size] [ticks]` (water on land and the water books; run 100k ticks at 1024: ~17 flooded columns,
  steady; the 512 test world never overfills a lake, so only this long run catches slow accumulation),
  `-- terrain` (hydrology checks), `-- far`.
- Known next optimizations: component rebuild is still O(regions + edges) per change (incremental or
  off-thread with deterministic apply tick); idle colonists re-search on any item/reservation change and
  hauling scans all piles and stockpile cells (spatial index); A* costs ~1-2 us/node; sim runs on the main
  thread.

## UI (Glyph UI, `game/scripts/ui`)

- **Spec:** `docs/ui/GLYPH_UI.md` (approved direction; `glyph_ui_reference.html` is the live reference, when they disagree
  follow the reference). ASCII structure (a monospace cell grid, CP437 glyphs, `·` leaders, `▁▃▇` sparklines) with a
  modern finish (frosted glass panels, pills, thin bars, switches). The only backdrop is the game itself.
- **Core:** `GlyphTheme` (tokens from `game/ui/glyph_themes.json`, user options + window layout in `user://glyph_ui.json`,
  fonts, `Cell` = advance of M x font size x density, the generated Godot `Theme`: Label tone variations `Dim`/`Faint`/
  `Hi`/`Accent`/`Good`/`Warn`/`Bad`/`Info` and `...Bold`, Button variations default/`Primary`/`Ghost`); `GlyphDraw`
  (5x7 bitmap glyphs from `glyph_glyphs.json` drawn as rects, never the font; chips; bars); `glass.gdshader` (blur via
  screen mipmaps + panel colour, rounded mask, frame, title band; a `BackBufferCopy` before the UI so every panel blurs
  the game). Fonts in `game/fonts` (OFL): JetBrains Mono (default, variable weight), IBM Plex Mono, Space Mono, VT323.
- **Controls:** `GlyphPanel`, `GlyphWindow` (title row, − □ ×, drag/resize in whole cells, double-click maximise,
  `Header`/`Content` (scrolls)/`Footer`, `Build()` on open and theme change, `Tick()` 4x a second while shown),
  `GlyphManager` (z-order = child order, front = active, hotkeys, Esc order: placement, active window, main menu;
  Ctrl+Tab; dock; status pill; layout saved), `GlyphChip`, `GlyphBar`, `GlyphSwitch`, `GlyphSegmented`, `GlyphSection`,
  `GlyphKV`, `GlyphSparkline`, `GlyphRow`, `VRule`, factory `G` (labels in cell widths, pill buttons that never take
  keyboard focus, so Space keeps pausing). Hover hints go to the status pill (`WithTip`), not tooltips.
  A click that rebuilds its own window must `AcceptEvent()` first (the control may be freed); theme changes rebuild
  deferred. Default window rects: negative X counts from the right edge, resolved on first layout.
- **Game windows (`GameUi`):** HUD pill (colony, date, people, food days, logs, planks, stone, mood, beds/homeless with
  daily ↑↓ from 8 samples a day, alert pill, view level, speed ‖ ► ►► ►►►), dock, status pill (hover hint, else the tool
  and what is under the cursor; `LastAction` shows as a message). Windows and hotkeys: Colonists C (Overview/Needs/Family,
  click to find), Stocks I, Build B (tabs Orders/Zones/Rooms/Walls/Floors/Water, category | items with costs, footer
  with size/rotate/cancel; drives `DesignationTool`'s public selection API; closing it drops build tools), Chronicle L
  (events + cave-ins, sticks to the bottom), Housing H, Work J, Inspect V (home/workshop under the cursor), Tests & demos
  F1, Appearance F2 (theme, frame, corners, font, size, spacing, UI scale, glass/shadows/chips), Help F3 (keys, debug).
  Home key looks at the colony. **Main menu** (`MainMenu`, solid `bg` backdrop for now): Continue, New colony (seed,
  map size; `Main.NewColony` reloads the scene), Multiplayer (disabled), Appearance, Tests, Quit; opened by Esc when
  nothing is left to close (the game does not start on it yet); the sim waits while it is up.
- **Multiplayer direction (not built):** freeform, each player a distinct colony (faction) in one world: cooperate
  against NPC enemies, trade, compete or go to war, with no mechanics beyond distinct colonies. Path: player actions
  as sim commands (deterministic lockstep fits the deterministic Core), a faction id on everything a colony owns, then
  networking; war needs combat and health first.

## Game design decisions

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
  should call `Flora.Validate`. No growth/regrowth simulation yet.
- **Items (`Items` namespace):** counted piles, not objects: at most one `ItemPile` (type, count) per
  walkable cell, resting on a floor. Drops settle downward and spill to nearby cells when a stack is full.
  `Reserved` (promised to a hauler picking up) and `Incoming` (space promised to a delivery) keep haulers
  from colliding; every reservation must be released on completion or `Colonist.Abandon`, and carried loads
  are dropped, never lost. Item kinds are data: `ItemType` is an index into `Industry.Catalog` (well-known
  ones as `ItemType.Log` etc.). Digging stone leaves stone 50% of the time, clay/sand/minerals always; felling
  leaves logs scaled by tree size (`ColonySim.MiningYield`/`LogYield`). Piles re-settle when their ground changes (digs,
  cave-ins). `Stockpiles` are zoned floor cells (all types for now); `Hauling.Plans` pairs the nearest loose
  piles with the nearest stockpile cell with room (linear scans; spatial index is the scale-up path).
  Who hauls is set by work priorities (see Work priorities).
- **Production tree is data (`data/tech-tree.json`, embedded in Core, loaded by `Industry.Catalog`):** items
  (stack/carry sizes, pile model, colour), 12 broad DF-style workshops (`RoomType`), their 32 stations
  (`PartType`s with role Machine; build material by size) and 71 recipes (multi-input `Ingredient`s, output
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
- **Movement:** agents are 1 tile tall and occupy an open cell; standable = solid floor below, or the cell is
  itself a stair. Walk 8-way (no corner cutting); climb/drop exactly one level onto an orthogonal neighbour
  through plain air above the lower end (raw climbs slow, cheap if either end is a stair cell). Legality
  must stay symmetric (connectivity needs an undirected graph); costs may differ (up vs down).
- **Stairs (DF-style):** `UpStair`, `DownStair`, `UpDownStair` are carved, non-solid, structural cells.
  Vertical move from a cell that goes up into the cell directly above that goes down. Designating a stair
  onto an existing stair adds directions (up onto down = up/down). `TileInfo`: `IsSolid` blocks movement,
  `IsStructural` carries cave-in load, `IsStair`/`GoesUp`/`GoesDown`. `Chunk.SolidCount` counts solid only;
  `NonAirCount` is for emptiness checks.
- **Designations (DF-style UI):** click to anchor a corner, click again to apply; Z/X while selecting move
  the free corner between levels and the view follows. Tools (Tab/Shift+Tab): Dig, Stairs (auto: down on
  top, up/down between, up at bottom), Up, Down, Up/Down. Right-click starts a cancel box or aborts.
  Exception: plant orders (Chop, Gather; `Designations.IsPlantOrder`) are 2D areas draped on the surface
  (`DesignatePlantArea`, any level, ignores the view level), since that work always happens on the surface;
  their marks are drawn whatever level is viewed. Dig/stairs/stockpiles/rooms stay level-based boxes.
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
- **Camera:** the rig's pivot follows the visible ground (highest solid tile at or below the view level), so
  zoom stays relative to what's on screen when levels are peeled away.
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
- **Water rendering:** sections of 2x2 water blocks (32x32 columns, all levels). Each wet span gets a surface at
  its height with corners averaged with neighbouring columns within half a level (smooth sloping rivers), and
  side faces where the water beside is lower (falls; foamy). A block's version only bumps when a span's water
  moves a sixteenth of a level from what was last drawn (hysteresis). Meshing runs on worker threads from main-thread
  span snapshots (`Capture`: a section plus a border column; every open cell is in some span, so solidity comes from
  spans); unbuilt sections first, then changed ones nearest the camera (changes beyond 256 tiles wait 3 s); uploads
  within 1.5 ms/frame (2048: ~1 ms/frame for water, was 4). UV carries the flow (`Water.FlowAt`, exaggerated for the eye) so ripples
  stream along it. No draw distance. (Remote Desktop caps rendering at ~30 fps: frame times of ~31 ms there
  are not the game.) The slice hides water above the cut, so a **water cap** (`water_cap.gdshader`, like the
  terrain cap) draws the cross-section at the cut from a map-sized R8 texture (water above the cut per column, 1/16
  levels): rebuilt over wet blocks on a level change, patched per changed block, uploaded at most 5x a second.
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
  door 2 planks, window 1 glass. **Tool:** build menu "Walls & doors": Outline (box), Wall/Door/Window (click the
  edge nearest the cursor; RMB removes), material row. **Look:** `EdgeView` (stacked logs, rubble, plank panels,
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
  staggered joints from world position) so a home's own level shows its interior. Furniture stays under one level.
  Home shell = walls .45 + floor .25 + roof .2 + windows .1 (`HomeShell.Score`); bare rock underfoot or overhead is
  humble, bare earth and open sky nothing.
- **Waterworks:** `Orders.Constructions` holds single-cell blueprints (walls from 1 stone; water wheels and
  pumps from planks) built like room parts (haulers deliver, Designations-labor builders build;
  `ColonySim.CompleteConstruction`). Walls stack on wall blueprints, so dams rise level by level; a wall is
  `TileType.Wall` (digs back out as stone). `Fluids.Waterworks`: a wheel's power = water flowing through its
  cell per step / `FlowPerPower` (hang it in a waterfall: wheels may mount on a wall); pumps lift water from
  their cell to the cell ahead and one level up; power is one colony-wide pool for now (pumps run at the
  share the wheels cover). Farm plots within 2 tiles of water grow twice as fast (`Farms.Irrigated`).
- **Needs and population (`Agents.Needs`, `Agents.Society`):** a game day is `Needs.Day` (4800 ticks, 4 min at 1x).
  Food is personal: `Colonist.Satiety` (a day when full) drains; below `Hungry` an idle colonist eats before its
  next job, below `Famished` it drops its work (only if food is in reach) and walks to the nearest food pile it may
  use (`Needs.Usable`: never a factory's input crate or construction materials), reserves enough to fill up (never
  more than fits; a loaf can overfill and simply lasts longer) and eats. Starving a full day (`StarvingLimit`) makes
  it leave the colony (`ColonySim.Depart`; ids are never reused). Automatons don't eat. Foods and need goods are data:
  'food' = meals per unit (berries/glowcaps 1, bread 4), "need: X" uses give `ItemDef.Need`. Once a day `Society`
  uses up the consumable goods (dress, health; per-head rates in `Needs.Goods`) from usable piles, plainest first;
  `Met` per need, `Standard` smoothed. Daily mood (`Colonist.Happiness`): half home (`Housing.Score`), a fifth belly,
  the rest the goods standard; `WorkSpeed` 0.85-1.15. Unhappy (< 0.25, i.e. homeless counts) 5 days running: the
  unhappiest, newest first, leaves, one a day; neither unhappiness nor starvation takes a colony below
  `Needs.NeverBelow` (7): hardship thins, never empties (no death spirals, by design). Settlers arrive daily while
  there are >= 3 days of food and nobody starves: pop x (5% + 20% x mood), capped by free beds but at least 1, max 12.
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
  arrive (so they take beds first): children grow up after `Needs.ChildDays` (12) and stay home until they marry;
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
- **Work priorities (`Colonist.Priority`/`SetPriority`, `Work`):** six work types (Mining = dig/stairs, Woodcutting,
  Farming = sow/harvest/gather, Construction, Hauling incl. furnishing, Industry incl. the Academy), each off (0) or
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
- **Cave-ins (DF-style):** a solid tile is supported if face-adjacent solid tiles connect it to z=0. After a
  dig (`ColonySim.CompleteDesignation` -> `World.CaveIns`), each unsupported neighbouring mass falls as one
  rigid piece until it lands; designations on it are cancelled; colonists ride it down or pop out on top.
  Masses over `CaveIns.MaxMassTiles` count as self-supporting. Any code removing solid tiles should go
  through `CompleteDesignation` or call `ColonySim.CheckSupport`. No damage yet (no health system).

## Commands

- Build all: `dotnet build ColonyGame.sln`
- Test: `dotnet test ColonyGame.sln`
- Headless smoke run: `<godot_console.exe> --headless --path game --quit-after 60`
- Visual check / render benchmark: `<godot_console.exe> --path game -- --screenshot=out.png` (or `--bench`)
  plus optional `--size=WxDxH --seed=N --level=N --zoom=DIST --dig-demo --stairs-demo --ticks=N`, and
  `--look=X,Y` (camera on a column), `--water-demo` (terrace spring, waterfall wheel, pond, pump, irrigated field), `--showroom[=WORKSHOP_ID]` (every workshop with every station in sizes 1-5, stocked raw goods, farms;
  the id focuses one) with `--build-menu=WORKSHOP_ID` to open the build menu.
  Waits until the world is fully meshed, measures 120 frames, prints `[bench]` lines (startup phases,
  frame ms, draw calls, memory), saves the PNG and quits.
- Sim benchmark: `dotnet run -c Release --project tools/Colony.Bench -- [size] [colonists] [ticks]`
  (or `-- water [size] [steps]` for the water simulation alone, whole map; `-- colony [size] [days]` for population,
  food and settlers over game days with no player)
  (prints per-phase totals, slow-tick breakdowns and GC counts; the render bench prints a startup breakdown).
- VS Code: F5 "Play Game" or "Open Godot Editor" (see `.vscode/launch.json`).
- **UI dev flags:** `--open=colonists,stocks,...` opens windows (ids: colonists stocks build chronicle housing work inspect
  tests appearance help), `--main-menu` starts on the menu, `--work-menu`/`--test-menu` (old names), `--ui-selftest` drives
  the window manager with real input events (hotkeys, Esc order, drag, resize, maximise, dock, tabs, menu, theme switch),
  prints PASS/FAIL and quits (exit code 1 on failure). The bench prints the worst update time per open window.
- **In-game test menu (F1, the Tests & demos window):** every xUnit test, run inside the game (`TestRunner`: the test DLL from
  `tests/Colony.Core.Tests/bin/Debug` loaded from bytes into its own load context sharing the game's Colony.Core;
  "Reload tests" picks up a rebuild). "Run" opens the first `ColonySim` the test built, at its end; "Watch" stops it
  at its first step (`DevHooks`, per-thread hooks in Core) and opens that world paused. The colony waits in a static
  while you look ("Back to the colony"); worlds swap by scene reload, so views must unsubscribe from the sim on
  `_ExitTree`. Also launches the dev demos into the colony. CLI: `--test=NAME [--test-watch] [--test-menu]`.
  Tests that should be watchable just build a `ColonySim` (hand-built worlds are drawn like any other).

## Status / roadmap

- [x] M0: Project scaffold, seeded mountain world gen, instanced-cube renderer with z-level slicing,
      strategy camera.
- [x] M1: Chunked world storage (16Â³ `Chunk`s with version counters) + per-chunk face-culled meshing
      (`Colony.Core.Meshing.ChunkMesher`); only chunks whose version or view-slice key changed rebuild.
- [x] M2: Voxel raycast tile picking, drag-rectangle dig designations (`Orders.DigDesignations`);
      Enter debug-digs all designations until colonists exist.
- [x] M3: Colonists (`Agents.Colonist`), fixed 20 Hz ticks (`ColonySim.Step`), A*/Dijkstra pathfinding
      (`Navigation`), designation claiming, digging, and stairs (since reworked to DF-style up/down stairs).
- [x] M3.5: Scale-up to 2048x2048x128: greedy/threaded meshing, shader slicing, connectivity regions,
      job board, hierarchical pathfinding, stair step geometry, parallel multi-range world gen.
- [x] Cave-ins: support check after digs; unsupported masses fall rigidly.
- [x] Terrain v2 (`Procedural.Terrain`): deep worlds (384 levels, surface ~140-375), domain-warped
      continents + ridged ranges + gentle hills; coarse-grid hydrology (priority flood, iterative
      least-cost basin breaching, flow accumulation) carves meandering river channels, valleys/floodplains
      and lake basins. Channels and lakes are carved dry; `Terrain.Water`/`Kind` (kept on
      `ColonySim.Terrain`) record intended water depth per column for the water milestone. Ore is
      clustered deposits, richer with depth. Spawn prefers flat land near the centre with high ground and
      a river nearby.
- [ ] Ideas: cave-in damage/injury once health exists; material-strength spans
      (overhang limits per material) as a later twist on pure connectivity support.
- [x] Water: shallow-water flow over column spans (smooth surfaces, caves and tunnels too), parallel
      deterministic two-pass steps on awake blocks, generated rivers/lakes settled at load, springs by catchment,
      `WaterView` smooth sloping surfaces with flow-streamed ripples; walls (dams, canals), water wheels, pumps,
      colony power pool, irrigation.
- [ ] Next for water: siphons, wading/swimming
      and drowning (water blocking deep cells), axles and separate power networks, powered workshops,
      floodgates/sluices, aquifers, freezing, wells, meandering river courses (they follow the 4-tile drainage).
- [x] Flora: ~130k trees (oak, birch, pine, spruce, willow) and ~100k surface plants (shrubs, berry bushes,
      ferns, wildflowers, reeds) on a 2048 map, placed by forest-patch noise, moisture (distance to water),
      altitude (broadleaf low, conifers high, treeline) and footing. `FloraView` draws per-region,
      per-kind MultiMeshes near the camera and one far-LOD marker MultiMesh per region; sliced by shader.
- [x] M4a: items as counted piles, mining/chop yields, `Chop` area designation, stockpile zones, hauling
      with reservations; HUD stock totals; `ItemView` draws piles/zones, colonists show carried loads.
      Dev flag `--haul-demo` (fells the woods around the colony and zones a stockpile).
- [x] M4b: factory builder v1: Sawmill room (logs -> planks), Sawbench machine, Tool rack/Lantern
      auxiliaries, input/output crates, demand-driven hauling between stockpiles and factories, builder UI
      (B cycles build tools, R rotates, ghost with reasons, room info panel on hover), `RoomView`.
      Dev flags `--factory-demo` (fell woods, log stockpile, equipped sawmill, plank stockpile) and
      `--parts-demo` (levelled showroom with every saw and tool size). Parts come in 5 sizes ([ ] in the builder).
- [x] Construction: blueprints, material delivery, builders (log-cost starter parts, plank-cost big ones).
- [x] Production tree: data-driven catalog, 12 workshops / 32 stations / 71 recipes with models for every
      station size, multi-input crates, recipe per machine, education and the Academy, automatons, build
      menu (B). Raw sources: clay, sand, 8 minerals by depth, wild herbs, grain/herb/glowcap farms.
- [ ] Next for industry: conveyors between output and input crates; rooms themselves costing
      (walls/floors); stockpile filters; standing orders ("keep 50 planks") instead of fixed recipes;
      wonders as aux parts (philosopher's stone, farseer orb, orrery); real ratios.
- [ ] Next for flora: wild growth and spreading/regrowth (staggered, deterministic).
- [ ] Geology: several stone types in layered/warped strata (palette now has room).
- [~] M5: Needs and homes. Done: hunger, eating, starving leaves; daily goods standard (clothing, tinctures); homes
      (sized rooms, housing policies, tiered fixtures with wear, salvage, auto layout), rest in beds, residents by
      education, mood -> work speed, gentle departures, settlers by food, mood and free beds; procedural home
      layouts; likes (food, material, art, work); thin walls, doors, windows, floors, roofs (materials, dams, shell
      quality, wall lines); families (weddings, births, children, homes per family). Next:
      schooling children, health beyond consumption, settler couples, old age.
- [x] UI: Glyph UI (theme, glass panels, window manager, HUD, dock, status pill), every menu migrated (Build, Housing,
      Work, Tests) plus Colonists, Stocks, Chronicle, Inspect, Appearance, Help windows and a main menu; work priorities.
- [ ] Next for UI / demo: colonist pages (job clothing, titles by proficiency once skills exist), player actions as sim
      commands, factions per player, title art behind the main menu, save/load.
- Later: revelry and culture (taverns, temples consuming wine and fine goods), skills, factions,
      procedural narrative/history.
