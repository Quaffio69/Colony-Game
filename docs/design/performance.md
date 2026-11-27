# Performance architecture

How the game stays fast on big maps (2048x2048x384 default, 4096 measured): the camera window, meshing, reachability, jobs, pathfinding, change tracking, the far view, water stepping. Read before touching rendering throughput, pathfinding, connectivity or the water step.

## Performance architecture

- **The views follow the camera; their cost depends on the view, not the map.** Main projects the four screen corners
  onto the ground each frame (`MainRender.UpdateWorldWindow`) and calls `WorldView.SetView` (centre, near radius, reach,
  eye, tiles per art pixel). Terrain sections exist only on the 256² **near blocks** the near window touches (capped at
  `MaxNearRadius` 1024 tiles; kept `RetainMargin` further for panning back, freed beyond; blocks outside the window
  aren't meshed, and requeue their sections on return). A near block draws its sections once every one is meshed and its
  level-0 far block exists, so panning shows coarse ground first, never holes. Water sections (WaterView) exist only on
  shown near blocks; which water blocks there are wet is cached until `Water.WetSetVersion` or `WorldView.ShownVersion`
  changes; the water cut cap patches those blocks only. Flora regions exist only within `WorldView.FloraReach` (where
  far blocks stay level 0: under 3 tiles an art pixel; capped at 4096), nearest first. The 40 ms "loading" meshing
  budget applies only until the first view is complete; later big zooms mesh at 6 ms a frame behind the far blocks.
  At 4096, 1440p: close 2.1 ms, 60 tiles 4.6 ms, whole map 2.3 ms (were 11 ms with 26 ms p95, 12 ms, 21 ms); 6k sections
  instead of 393k; 0.5 GB VRAM instead of 2.4 GB; water meshing 0.25 ms a frame instead of 5.7.
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
- Default map 2048x2048x384: gen ~3 s (terrain 2 s, connectivity 1 s; water settles in the background ~5 s). 4096: gen
  ~11 s (terrain 6.3 s, connectivity 2.1 s, water settling 18 s in the background); 8192: terrain 31 s, connectivity
  8.8 s, settling 75 s. Generation (whole-map hydrology and settling) is now what limits map size. Use
  `--size=1024x1024x384` for faster iteration. **Core memory** (`-- mem SIZE`, after 40 steps): 4096 1.37 GB (was 3.67:
  tiles 1.1 GB -> 232 MB, connectivity 1.1 GB -> 0.2 GB, water 920 -> 412 MB); 8192 was 15 GB before these:
  - **Tiles** (`Chunk`): uniform chunks store nothing; others pack 1, 2 or 4 bits a tile against a palette of up to
    2/4/16 kinds (most chunks hold 4 or fewer: air, grass, soil, a rock), widening on a write that needs room (bytes past
    16 kinds); `Compact` narrows; readers copy runs (`CopyRun`); snapshots keep a byte a tile (old saves load).
  - **Connectivity** (`ChunkNav`) exists only for chunks with walkable regions; labels are a bit a cell for one region
    (512 bytes), a byte up to 255 regions, 16 bits beyond; cell links are packed longs (region << 48 | cell key).
  - **Water** blocks out of play drop their pipes (see world.md).
  **Sim cost:** sim bench (20 colonists, digging, felling, hauling; water stepped synchronously every 4 ticks): 0.62
  ms/tick avg at 4096 (was 10.6: nearly all of it the water step, 40 ms a step on every river; now only water within
  `WaterInterestRadius` of a colonist steps, see world.md), with the same path searches.
- **Far blocks (`WorldView`, `Meshing.FarMesher`)** draw everything outside the shown near blocks: a quadtree of
  `FarLevels` (5) levels, level L with cells of 2 << L tiles and blocks of 256 << L columns (128² cells each), picked
  every quarter second (or on a move or zoom) within the view's reach: split to level 0 wherever the near window reaches (none
  in the far view: splitting on distance 0 kept a square of level-0 blocks round the centre, without woodland or trees),
  else to the level where an art pixel covers 1.5·2^L..3·2^L tiles (iso: one level for the view; perspective: by distance
  from the camera). Blocks no longer wanted stay until what replaces them is built; a wanted block hides while an older
  coarser one still covers it. Meshes vertex-compressed (`FlagCompressAttributes` with tangents). A cell is the highest
  top, or standing water (`FarMesher.Water` = tile code 31, water's colour and symbols in `GameLooks.TileTables`) when a
  third of its columns are wet; coarse cells (over 2 tiles) read a 2x2 grid of columns (`SampleSparse`), so a block costs
  the same at every level, take the kind most of the four show (the highest of them set the height; a lone hilltop or sand
  bar painted whole cells), and count as woodland (`FarMesher.Forest` = code 29, canopy colour, oak symbols, no block
  texture: the atlas has no row for it, which drew it orange) where trees
  stand on 6% of their columns (flora's region lists; a dense wood is ~9%): far out the trees aren't drawn (586k tree markers were
  29M of 30M triangles at 4096). Greedy tops hang outward `FarMesher.Skirt`s where a level top of another rectangle meets
  them (T-junction cracks showed the sky); every block border hangs a side `2 x cell` deep (blocks of other levels meet
  there). Sampling reads the live world/water on the main thread (~2 ms a block at any level), meshing on workers; changed
  blocks rebuild at most every 2 s (a hidden level-0 block under shown sections waits), one block refreshes every 0.5 s
  (water). **Far view (`WorldView.FarView`):** once an art pixel covers 1.5+ tiles (off below 1.2; styled render only) the
  near window closes (no sections drawn or meshed) and the blocks draw the whole view; the switch waits until every block
  in view is built (selection keys on the request, not the switch, or the two sets would alternate). Each block keeps its
  highest sampled top (`TopAround`): the iso camera stands back only far enough to clear the local terrain (the world top
  put it, and its shadow cascades, hundreds of tiles into empty air). The bench waits for the blocks in view.
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
  ~1 ms/step at 1024, ~10 ms/step at 2048, ~40 ms at 4096 with every block live (`-- water`: no colonists; `Apply` is the
  bulk); in play only water near colonists steps (`SetInterest`). **Off the main thread:** the step
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

