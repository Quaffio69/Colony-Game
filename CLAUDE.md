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
- **Pathfinding:** hierarchical: BFS over the region graph gives a corridor, then A* restricted to it (on a
  snapshot of its bounding box only for long corridors). Colonists share a per-tick node budget
  (`PathNodeBudgetPerTick`).
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
- Known next optimizations: component rebuild is still O(regions + edges) per change (incremental or
  off-thread with deterministic apply tick); idle colonists re-search on any item/reservation change and
  hauling scans all piles and stockpile cells (spatial index); A* costs ~1-2 us/node; sim runs on the main
  thread.

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
  are dropped, never lost. Digging stone leaves stone 50% of the time, ore always; felling leaves logs scaled
  by tree size (`ColonySim.MiningYield`/`LogYield`). Piles re-settle when their ground changes (digs,
  cave-ins). `Stockpiles` are zoned floor cells (all types for now); `Hauling.Plans` pairs the nearest loose
  piles with the nearest stockpile cell with room (linear scans; spatial index is the scale-up path).
  Colonists with `Id % 3 == 2` prefer hauling until job priorities exist.
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
  plus optional `--size=WxDxH --seed=N --level=N --zoom=DIST --dig-demo --stairs-demo --ticks=N`.
  Waits until the world is fully meshed, measures 120 frames, prints `[bench]` lines (startup phases,
  frame ms, draw calls, memory), saves the PNG and quits.
- Sim benchmark: `dotnet run -c Release --project tools/Colony.Bench -- [size] [colonists] [ticks]`
  (prints per-phase totals, slow-tick breakdowns and GC counts; the render bench prints a startup breakdown).
- VS Code: F5 "Play Game" or "Open Godot Editor" (see `.vscode/launch.json`).

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
- [ ] Ideas: job priorities; cave-in damage/injury once health exists; material-strength spans
      (overhang limits per material) as a later twist on pure connectivity support.
- [ ] Water/fluids: cellular sim on active-chunk sets (DF-like 0-7 depth per tile, with pressure for
      U-bends), possibly our own twist (e.g. flow fields, aquifers, temperature/freezing). Must stay
      deterministic and only tick chunks with moving fluid. Seed it from `Terrain.Water`.
- [x] Flora: ~130k trees (oak, birch, pine, spruce, willow) and ~100k surface plants (shrubs, berry bushes,
      ferns, wildflowers, reeds) on a 2048 map, placed by forest-patch noise, moisture (distance to water),
      altitude (broadleaf low, conifers high, treeline) and footing. `FloraView` draws per-region,
      per-kind MultiMeshes near the camera and one far-LOD marker MultiMesh per region; sliced by shader.
- [x] M4a: items as counted piles, mining/chop yields, `Chop` area designation, stockpile zones, hauling
      with reservations; HUD stock totals; `ItemView` draws piles/zones, colonists show carried loads.
      Dev flag `--haul-demo` (fells the woods around the colony and zones a stockpile).
- [ ] M4b: stockpile filters/priorities, job priorities (replace the Id%3 hauler rule), gathering
      (berries, reeds), then the factory builder: player-sized production rooms consuming stock.
- [ ] Next for flora: growth and spreading/regrowth (staggered, deterministic), farming plots as a
      factory-style input.
- [ ] Geology: rock strata by depth (several stone types, layered/warped), so deep digging varies;
      needs more `TileType`s (cap shader palette is 8 entries today).
- [ ] M4: Items, stockpiles, first production chains (e.g. ore â†’ smelter â†’ bars â†’ forge).
- [ ] M5: Needs (food, rest), farming, population growth.
- Later: education/skills, advanced product trees, factions, procedural narrative/history.
