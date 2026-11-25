# Performance architecture

How the game stays fast on 2048x2048x384 maps: meshing, reachability, jobs, pathfinding, change tracking, the far view, water stepping. Read before touching rendering throughput, pathfinding, connectivity or the water step.

## Performance architecture

- **Rendering:** 32x32x16 sections, greedy-meshed (`Meshing.SectionMesher`) on the thread pool from
  main-thread `TileBox` snapshots; uploads are budgeted per frame. The view-level slice is a shader
  discard plus a texture-driven cap quad, so changing level never re-meshes. The cap texture (`Meshing.CutCap`, one byte
  per column: what the cut shows, or fog) is refilled for the whole map on each level change in 32-row bands across
  threads, in Core (optimized): 6 ms on a 2048 map, where the game-assembly loop took 110 ms a level (`--level-sweep`). 4x MSAA.
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
- **Far view (`WorldView.FarView`, `Meshing.FarMesher`):** once an art pixel covers 1.5+ tiles (off below 1.2; styled render
  only) the sections give way to 64 far blocks (256² columns each, cells of 2 tiles; meshes vertex-compressed, `FlagCompressAttributes` with tangents: ~175 MB less VRAM, no visible change): a heightfield of each cell's highest
  top, or standing water (`FarMesher.Water` = tile code 31, coloured and given water symbols in `GameLooks.TileTables`)
  when a third of its columns are wet; greedy tops, each hanging outward `FarMesher.Skirt`s where a level top of another rectangle meets it (T-junction cracks showed the sky as stray outlined pixels; overlapping tops instead z-fought), plus sides, same terrain material (sliced by the shader). Sampling reads
  the live world/water on the main thread (~2 ms a block), meshing on workers; all blocks are built in the background once
  the near world is meshed and the switch waits for them; changed blocks rebuild at most every 2 s, one block refreshes
  every 0.5 s (water), and WaterView hides and stops meshing its sections meanwhile. Full map at 2048: 30 ms / 23.6k draws
  -> 9.8 ms / 1.3k. The render bench prints visible instances per view, art size, tiles/texel and the far state.
  **In perspective** blocks are also level of detail: each is the Godot visibility parent of the sections and water
  sections on its columns, shown beyond `FarRange` (where an art pixel covers 1.5 tiles, plus half a block diagonal), so the
  horizon draws coarse (close up 44.7k -> 1.6k draws); in iso the range is off and the whole-view switch sets every block
  in range. Roots are never hidden and a section only keeps its parent while it has a mesh and is visible (`LinkFar`):
  Godot errors (`dep_instance->array_index == -1`) on dependencies of hidden or empty instances. Each block keeps its highest sampled top (`TopAround`): the iso camera stands back only far enough to clear the local terrain (the world top put it, and its shadow cascades, hundreds of tiles into empty air: shadows lost, more draws). The bench waits for them.
  **Far shadows** (`WorldView.FarShadows`, iso, from 0.5 tiles an art pixel): each block has a shadows-only copy
  (`FarShadow_*`, same mesh) that casts the terrain's shadows while sections stop casting: every cascade draws its casters
  again, and at 1440p, 1 tile a pixel, sections in four cascades were ~5k of 8.3k draws (CPU-bound, 9.9 ms -> 4.9 ms).
  Shadow edges follow the 2-tile heightfield (a pixel or two at these zooms; screenshots compare nearly identical). Dirty
  blocks rebuild every 2 s while it is on. Tried and dropped: not casting from sections roofed by solid chunks (none were:
  the 11.7k drawn sections are all surface).
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
  `-- terrain` (hydrology checks), `-- far`, `-- ants [size] [ticks] [colonists]` (nests round a colony, raids and a squad
  assault: step and wildlife tick costs, swarm sizes, the story in events).
- Known next optimizations: component rebuild is still O(regions + edges) per change (incremental or
  off-thread with deterministic apply tick); idle colonists re-search on any item/reservation change and
  hauling scans all piles and stockpile cells (spatial index); A* costs ~1-2 us/node; sim runs on the main
  thread.

