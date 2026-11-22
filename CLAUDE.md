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

## Look (Dwarf Render kit, `game/addons/dwarf_render`)

- **What:** a port of the user's Three.js diorama (`reference/voxel-diorama.html` is the answer key): the 3D world is drawn
  into two low-res SubViewports sharing our World3D (art + ids), post-processed (outlines, grade, palette LUT, ordered
  dither, foliage-aware silhouettes) and presented full-screen on CanvasLayer -100 under the UI; the main viewport's 3D is
  off. Zoomed right in, art pixels dissolve into glyphs; far out (all symbols past ~450 tiles) the view becomes DF-style
  object symbols with colonists always ☺ (`AsciiMarker`). F10 tunes everything (presets, Save to
  `user://dwarf_render_settings.tres`). Appearance F2 "Pixel-art renderer" (`GlyphOptions.PixelArt`) switches to the classic
  look by rebuilding the scene on the same world (`Main.ReloadView`); `--classic-render` forces classic. Needs Forward+ and
  `screen_space_roughness_limiter` off (project.godot): every kit material writes its **glyph group** id into ROUGHNESS.
- **Integration (`MainRender.cs`, `GameLooks.cs`):** kept our greedy `SectionMesher` and slicing (the kit's own mesher,
  rounding, AO and sky light are unused): `WorldView` adds UV.x = tile type; `terrain_dr`/`cap_dr` (`tiles_dr.gdshaderinc`)
  do the kit's toon bands, block-texture atlas (kit block ids = `TileType` values, `GameLooks.RegisterBlocks`) and glyph id.
  `GameLooks.Shader(path)` picks a `*_dr.gdshader` variant when styled (terrain, cap, water and water cap: opaque, as
  the post pass only sees opaque surfaces; flora, roofs); `GameLooks.Styled(m)` / `DwarfMaterials.RegisterTerrain` feed
  them the atlas and lighting values. Trees and bushes are the kit's `TreeBuilder` meshes (canopy + bark in one mesh for
  our MultiMeshes; the kit's foliage/object shaders gained a `cut_height` slice). Every other view keeps making plain
  `StandardMaterial3D`s: `GameLooks.Watch` converts opaque lit ones to `DwarfMaterials.CreateObject` with a glyph group by
  owning view (colonists Dwarf, CreatureView Creature, ItemView Item, HomeView furniture, Edge/Cover construction...) as
  nodes are added plus a 1 s sweep (skipping subtrees in group `GameLooks.SweepSkip`: terrain near/far roots, water surfaces, flora; walking them was a ~90 ms hitch every second at 2048; `--stutter-log` prints frames over 50 ms with sim, sweep and GC); transparent/unshaded overlays are left alone and draw after the post quad
  (`RenderPriorityMin`), so cursors, designations, ghosts and health bars stay visible, depth-tested. Label3Ds are taken out
  of the 3D render and drawn crisp in 2D by `ScreenTags` (layer 1). The renderer zooms about the rig pivot
  (`FocusOverride`). **Stable pixels:** the render scale (world units per art pixel) moves in steps of `ZoomStep` (1.4x zoom; steps only make pixels bigger than asked, never smaller, and the target shrinks when oversized: pixel size 1 once rendered 4x the screen;
  a step holds until passed or 1.15 steps away); in iso only (perspective zoom really moves the camera), between steps the present pass scales the finished picture and the art target is sized for the
  step's smallest pixels, so zooming neither resamples nor resizes every frame. The snap grid is anchored at a world point
  (re-anchored at the pivot on a turn or scale change, keeping its whole-texel count, so dither doesn't jump). The ids pass
  writes a hash of the world tile under each pixel (B channel); symbol variants, the dissolve order and water ripples use
  it, so a tile keeps its symbol through turns and zooms (a 0.25° turn used to change ~80% of pixels near the pivot, now
  ~22%). `OrbitStep` (default smooth) snaps the rig's shown yaw/pitch, camera included, so picking matches. Past the map view's start the turn snaps to quarter turns and the camera glides to each (`UpdateShownYaw`; blending towards the nearest quarter jolted up to 45° at 45° boundaries; `--orbit-check` measures the worst frame). **Map view:** past `TopDownZoom` (0.15; eased in from twice that distance) the rig looks straight down with its turn eased to the nearest 90° (quarter turns only; the camera is aimed by rotation, LookAt fails straight down), so far out the pixel grid lines up with the tiles. **Zoomed in:**
  pixels grow from `PixelGrowStart` by `PixelGrowth` of the zoom up to `MaxPixelSize`, separately from the glyph fade
  (`ZoomInStart`/`ZoomInLength`); the rig zooms in to 4 tiles (`CameraRig.MinDistance`). `--yaw=` sets the bench view's
  turn (compare frames). The sun faces
  away from the default view (yaw 215) so we see lit faces (toon shade bands are dark). Render layers 19-20 are the kit's.
- **Graphics window (F10, `UI.GraphicsWindow`):** every `DwarfRenderSettings` [Export] by group, built by reflection in
  Glyph controls (`GlyphSlider`, `GlyphCycler`, switches, colour pickers) with the diorama's labels/formats; presets,
  Save (`Main.SaveLook` -> `user://dwarf_render_settings.tres`, loaded with every world), Revert, Defaults
  (`Main.GameDefaults`: the diorama's values, zoom 1x = 45 tiles); a live readout (zoom, ASCII %, art size, pixel size, fps). The kit's
  own F10 panel is off (`CreateTuningPanel = false`). Settings the port added from the diorama: sun angle / warmth /
  ambient / shadows / day-night (`DwarfRenderer.UpdateLighting`, the diorama's updateSun; `TimeOfDay` from the game clock),
  orthographic (iso) camera (`CameraRig.Orthographic`: same framing from far back: `OrthoBack` = half-height · cot pitch + (ceiling - pivot) / sin pitch, so terrain up to the world top or the view cut never crosses the near plane; Far follows), shadow map size, profiler
  (`UI.ProfilerPanel`: fps, worst frame, sim, world GPU, draws, prims, memory, zoom/ASCII; `--profiler`). Zoom is
  `ReferenceDistance / rig distance` (`ZoomOverride`); the rig eases zoom so ASCII transitions glide.
- **Glyph ids:** Godot 4.7 stores roughness as is (or 1 - r for dynamic objects): ids decode as `round(a * 127)`, groups
  0..63 only (`GlyphAtlas.MaxGroup`). The kit first assumed the older 7-bit packing and read every id doubled (grass drew
  farm symbols). `--id-probe` on the kit demo prints the decoded ids. **Symbols (`GameLooks.RegisterSymbols`):** DF
  world-map style: grassland ", broadleaf ♣, conifers ♠ ↑, mountains ▲ (surface stone), cut rock ▓ (the cap uses
  `dr_cap_group`), water ≈ ~, sand ░, stairs < > X, ants a, nests ∩ (nodes named `Nest_*`), beds/tables Θ Π ╥, walls #; the
  UI's bitmap glyphs are imported into the kit font. No dither on the sky (a dot grid in the far view).
- **Cost:** kit trees are ~1k triangles (no leaf cards unless that technique is on at load); full detail only below 0.6 tiles per art pixel (`MainRender`: in iso the range offset otherwise made all 130k trees full detail and shadowed, 570M triangles) and within 200 (measured from the pivot: `FloraView.RangeOffset` adds the iso camera's extra distance)
  tiles, and `FloraView.Detail` drops to the distant markers once the far view is mostly symbols.
- Not ported yet: per-vertex AO / cave sky light and edge rounding (would need mesher changes), tree variants per species.

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
  `GlyphManager` (z-order = child order, front = active, windows never narrower than their content (`NeededCols`, grown
  on tick; the body clips), hotkeys, Esc order: placement, active window, main menu;
  Ctrl+Tab; dock; status pill; layout saved), `GlyphChip`, `GlyphBar`, `GlyphSwitch`, `GlyphSegmented`, `GlyphSection`,
  `GlyphKV`, `GlyphSparkline`, `GlyphRow`, `VRule`, factory `G` (labels in cell widths, pill buttons that never take
  keyboard focus, so Space keeps pausing). Hover hints go to the status pill (`WithTip`), not tooltips; HUD items also show
  an instant themed banner at the cursor (`GlyphStatus.Banner`, `WithBanner`, `GlyphSegmented.BannerTips`).
  A click that rebuilds its own window must `AcceptEvent()` first (the control may be freed); theme changes rebuild
  deferred. Default window rects: negative X counts from the right edge, resolved on first layout.
- **Game windows (`GameUi`):** HUD pill (colony, date, people, food days, logs, planks, stone, mood, beds/homeless with
  daily ↑↓ from 8 samples a day, alert pill, view level, speed ‖ ► ►► ►►►), dock, status pill (hover hint, else the tool
  and what is under the cursor; `LastAction` shows as a message). Windows and hotkeys: Colonists C (Overview/Needs/Family,
  click to find), Stocks I, Build B (tabs Orders/Zones/Rooms/Walls/Floors/Water, category | items with costs, footer
  with size/rotate/cancel; drives `DesignationTool`'s public selection API; closing it drops build tools), Chronicle L
  (events + cave-ins, sticks to the bottom), Housing H, Work J, Inspect V (home/workshop under the cursor), Military M
  (fighters: health, gear, draft switches; nearby nests with Look/Attack), Trade F6 (`UI.TradeWindow`: offers and deals
  with Accept/Counter/Decline/Withdraw and delivery progress; an offer composer: give from your stores, ask from
  theirs, click to add, − + (Shift: tens)), Tests & demos
  F1, Appearance F2 (theme, frame, corners, font, size, spacing, UI scale, glass/shadows/chips), Help F3 (keys, debug).
  Home key looks at the colony; G drafts everyone and turns on squad orders (`DesignationTool.SquadOrders`: click ground
  to march, a nest to attack; G/RMB/Esc: done), Shift+G stands everyone down. **Orders ribbon** (`UI.OrdersRibbon`): a
  pill row above the dock with every order's icon, name and hotkey (DF-style display: lowercase = the key, uppercase =
  Shift+key): n dig, y stairs, u up, k down, o up/down, t chop, p gather, P stockpile, F farm plot (again: next crop),
  B dam wall, O demolish box (DF o: remove construction), X (or Del) cancel box. DF letters where free (d/j/i are pan right, Work, Stocks). It shows while an order
  is in hand (any order hotkey brings it up) or while pinned by its own "Orders" pill (bottom left); clicking picks.
  Window hotkeys ignore Shift (shifted letters are orders). Ribbon and pill are `GlyphManager.Blockers` (no clicks
  through to the world). **Mouse:** no tool by default (`ToolMode.None`): LMB drag moves the view level (14 px a level,
  up = up), RMB drag pans (`CameraRig.Pan`: the ground follows the mouse), tapping Shift / Ctrl alone goes a level up /
  down (`DesignationTool._Input`; any key or click while held cancels the tap; while boxing it moves the box's corner),
  RMB click (≤ 6 px) steps back: aborts the box, then drops the tool (`CancelPlacement`, also Esc). Shift+LMB starts
  the removal box (or removes an edge piece) for any tool. `--orders` opens the ribbon pinned. **Selection (`SelectionView`, `Main.Selection`):** a click (≤ 6 px, level
  unchanged) with no tool picks what is under it (`Pick` records: colonist in the cell, nest, room, stockpile or farm
  field (`ZoneCells`: zoned cells joined 8-way or a level up/down), blueprint, pile, a colonist beside it, else the tile)
  and opens Inspect; accent plates over a room/zone, a ring under a colonist, a box otherwise; with no tool the room or
  zone under the cursor gets faint plates (what a click would select). Esc / RMB click clear it (`Tool.NothingToCancel`,
  the last step of `CancelPlacement`). Colonists and Military rows select too. **Inspect** (V) shows the selection, else
  whatever is under the cursor: colonists (doing, health/food/rest/mood bars, home, gear, work priorities, likes),
  homes and workshops as before, stockpiles (cells, racks, stacks used/capacity, contents), farm fields (crops sown,
  ripe), piles (role), blueprints (what they wait for), nests, tiles; Go to, Follow (camera tracks the colonist until
  WASD/RMB), clear. **Notices** (`UI.Notices`): the tool card (bottom centre, accent pill: the tool in hand, what to do,
  the box size or why it can't be placed), the box size at the cursor while dragging, a Paused pill, and toasts for the
  colony's events (alerts red; click: Chronicle). Window shadows are a tight contact shadow (the old 34 px one smeared
  a dark band under windows). **Main menu** (`MainMenu`, solid `bg` backdrop for now): Continue, New colony (seed,
  map size; `Main.NewColony` reloads the scene), Multiplayer (disabled), Appearance, Tests, Quit; opened by Esc when
  nothing is left to close (the game does not start on it yet); the sim waits while it is up.
- **Multiplayer direction:** freeform, each player a distinct colony (faction) in one world: cooperate against NPC
  enemies, trade, compete or go to war, with no mechanics beyond distinct colonies. Built: commands and factions
  (below), hot-seat. Next: networking (lockstep: peers exchange commands stamped with the tick they apply before);
  war needs combat and health first.

## Networking (`Colony.Core.Net`, game `MainNet.cs`)

- **Host-relayed lockstep over TCP** (plain .NET sockets in Core, no Godot: tested headless over loopback). The host's
  sim is the reference and never waits. `LockstepHost`: accepts clients (`Peer`: length-prefixed frames, a reader
  thread filling an inbox drained on the main thread), gives each a free colony (refuses when full or on a protocol
  mismatch), sends a Welcome (colony, `GameSettings`, the tick, a `Snapshot` of the game), stamps the client's colony on
  every command it sends (`with { Player = faction }`: nobody acts for another colony) and submits it; each frame
  `Broadcast` flushes (so commands apply even while paused) and sends each client a Turn: newly logged commands with
  their ticks and the host's tick. `AfterTick` (call after every host tick) takes a `StateHash` every
  `HashInterval` (100) ticks, sent with the next turn. `LockstepClient`: `Connect` (waits for the welcome),
  `BuildWorld` (loads the snapshot), `Poll`, `Advance`
  (applies each command when the sim reaches its tick, never runs past `ConfirmedTick`, compares hashes:
  `DesyncTick`), `Send`. Guests trail the host by their latency and catch up.
- **`StateHash`:** colonists, piles, designations, stockpiles, rooms, blueprints, edge/cover revisions, combined
  order-independently with a fixed 64-bit mix (never `HashCode`: seeded per process; also never enumerate
  string-keyed dictionaries in sim logic, their order differs between processes).
- **Game:** sessions live in statics across scene reloads. `HostGame` (this world, if `Shareable`: generated from
  settings, or loaded or joined; test worlds can't be; demos are disabled online),
  `HostNewGame`, `JoinGame` (builds the world, reloads onto it), `LeaveOnline`. Host frames: `Poll`, ticks with
  `AfterTick`, `Broadcast`; the menu doesn't pause an online game. Client frames: `Poll`, `Advance` (all confirmed
  ticks, a third at a time when far behind); its tool sends commands (`DesignationTool.Remote`) and shows their results
  when they come back (`Commands.Applied`); no pause/speed keys. HUD shows `NetStatus` (DESYNC/Disconnected in bad).
  Main menu Multiplayer: name, port, Host this world, Host a new world (2-4 players), Join by address, players, Leave.
  Dev: `--host[=PORT]`, `--join=ADDR[:PORT]`, `--name=`, `--speed=0..2`, `--net-orders` (orders own colony through the
  command path; with `--net-ants` also an angry ant nest beside it), `--net-check=TICKS` (prints the state hash at that tick; client quits, host 15 s later). End-to-end:
  start a headless host with `--colonies=2 --host=7781 --net-orders --net-check=1500`, then a client with
  `--join=127.0.0.1:7781 --net-orders --net-check=1500`; the two `[net] ... hash` lines must match (they did).
- Limits: demos edit the world outside commands, so they're off online (a world a demo changed can still be hosted:
  snapshots hold its edits).
- **Snapshots (`Net.Snapshot`, version 4: trade depots, deals, seventh work type; 3: racks and regrowth; versions 2-3 still load, `SnapIO.Before(v)` skips newer sections):** the whole state in a gzip blob: the `GameSettings` (the terrain is regenerated, a
  second at 2048), the chunks changed since generation (`VoxelWorld.Seal` after generation starts tracking them), then
  flora, piles, orders, stockpiles, farms, rooms and parts, blueprints, waterworks, water (spans, volumes, flow
  momentum, sleep state, springs; natural levels from the terrain; pipes rebuilt), colonists, colonies (society,
  housing: policies, homes, fixtures, residents) and sim counters/events, in named sections (`SnapIO.Section`: a
  mismatch throws naming it). `Take` first applies a `SettleGame` command (`ColonySim.Settle`: water joined, queued
  commands applied, every colonist `Settle`s: puts down its task (claims released, loads dropped) and clears its
  caches; containers compacted so their enumeration order no longer depends on history (`SnapIO.Compact`); layouts
  redone; connectivity and job board rebuilt from scratch). Because it is a command, online every peer settles before
  the same tick. A loaded snapshot is in exactly the settled state, so both play on alike: tested in Core (a busy
  two-colony game: identical after load and every 250 ticks for 3000; a snapshot of a loaded snapshot matches) and at
  2048 (`-- snapshot [size] [ticks]` bench: take ~0.6 s, ~8 MB, load ~2 s, matches after 400 more ticks). Every new
  piece of sim state must be added to its class's `SnapSave`/`SnapLoad` (and `Settle` if it holds claims or
  order-dependent containers); the continuation test catches omissions. Arrays are written in bulk (`MemoryMarshal`):
  element by element through gzip, water took 11 s to load.
- **Saves (`Net.SaveGame`, game `MainSave.cs`):** version 2 holds a snapshot (version 1, the command log, still loads by
  replay), with the `GameSettings`, the tick, a name, the local player and when it was saved (`Read(headerOnly)` lists
  saves fast). `Load` runs on a background task with progress; loaded games are bit-identical (tested, and across two
  processes: `--save-at=TICKS` then `--load-newest` print matching hashes). Files in `user://saves/*.colony` (name ->
  file; same name overwrites). Main menu: Save game (name, overwrite list; not for test worlds or guests: the host's
  save holds every colony), Load game (newest first, day, map, colonies, age, delete), a Loading page with a progress
  bar; F5 quicksave, F9 loads the newest, autosave ("Autosave") every second game day. A loaded multi-colony save can
  be hosted again.

## Commands and factions (multiplayer foundation)

- **Commands (`Colony.Core.Commands`):** every player action is a `Command` record (dig box, stairs, plant areas,
  zones, farms, dams, paint/erase rooms, place/remove parts, recipes, waterworks, edge pieces and lines, outlines,
  floors/roofs, work priorities, home policy, housing policy edits, drafting and squad orders, trade depots, offers, counters, accept, reject, debug complete / spawn nest). Commands name things by stable ids
  and cells (part id, room type id, policy index, a cell of the home), never object references, and `Apply` returns
  the player's message. `sim.Commands` (`CommandQueue`): `Submit` queues, `Flush` applies before the next tick (Step
  flushes first thing), `Execute` applies now (single player; it is still "before the next tick"); every applied
  command is logged with its tick, so `CommandQueue.Replay` on a world generated the same way reproduces the game
  exactly (tested on an encoded log). `CommandCodec` writes them in binary (tag, player, fields; every type round-trips).
  The game issues everything through `DesignationTool.Issue` (stamps the local player); dev demos still edit directly.
- **Factions (`Agents.Faction`, `sim.Factions`):** a colony: id, name, landing spot, its own `Society` (settlers, mood,
  goods, families, departures) and `Housing` (only its own home rooms and people). `sim.Society`/`sim.Housing` are
  shorthand for faction 0. Ownership: designations, stockpile cells and farm plots (`Owners`: cell -> faction, only
  non-zero stored), `Room.Faction`, `Blueprint.Faction`, `Colonist.Faction`; registries take an optional `faction`
  (place: owner, default 0; cancel/remove: filter, default `Owners.Any`). A player can't redesignate another colony's
  order, cancel its things, place parts in its rooms, or command its colonists (commands refuse). Colonists only take
  their colony's orders (`JobBoard.Candidates` faction filter), rooms, blueprints, homes and students; haulers fill only
  their colony's stockpiles, crates and sites, from loose piles or their own stores (`Hauling.Plans` faction); food and
  goods come from loose piles or their own stores (`Needs.Usable(sim, p, faction)`); a Magister comes to each colony's
  own Academy. Loose piles are anyone's. Built walls/floors/roofs and waterworks have no owner. `SimEvent` carries the
  faction (-1: everyone). `ColonySim.Generate(..., factions: N)`: colony 0 at the spawn, others on dry land evenly
  round it a quarter of the map away, each with founders and (when N > 1) its provisions in a small stockpile of its
  own. With one colony the ownership checks are skipped (`Colonist.Filter`, `Needs.Usable`), so single player pays
  nothing for them (benched). Game: `Main.Player` is the local colony (HUD, windows, commands follow it), `--colonies=N`, `--play-as=N`, F4
  hot-seat switches colony, New colony offers 1-4 colonies; colonists show a ring and name tag in their colony's colour.
- **Trade (`Trade.Trades`, `sim.Trades`; commands `AddTradeDepot`, `ProposeTrade`, `CounterTrade`, `AcceptTrade`, `RejectTrade`):**
  deals between colonies brokered in turns: a colony offers terms (`TradeLine`s: who gives how many of which item;
  commands carry `TradeTerms` of item ids, value-equal), the colony whose `Turn` it is accepts, counters (new terms,
  the other's turn, `Round`++) or declines; the offerer may withdraw. Accepting is binding and needs a trade depot on
  every receiving side. A **trade depot** is a stockpile zone flagged depot (`Stockpiles.IsDepot`, Build, Zones): goods
  there are its owner's (`Needs.Usable`, hauling `Ours`), haulers never store into it and carry deliveries on to the
  stockpiles (`RoleOf` = Loose). **Traders** (work type `Trading`, labor Hauling, tried before hauling at the same
  priority; `Colonist.TryTrade`, tasks `TradePickup`/`TradeDeliver`): the nearest pile of an owed line in their own
  stores (`Trades.Sendable`: not factory inputs, building materials or another colony's stores) to the nearest buyer
  depot cell with room, a load at a time; `Claim`/`Unclaim`/`Delivered` keep `InTransit` and `Delivered` per line, so
  traders share the work and an abandoned load is owed again. Goods aren't set aside on acceptance: traders send
  what the stores hold and wait for the rest. Done when every line is delivered; events go to both colonies. Snapshot
  section `trades` (v4), `Settle` zeroes `InTransit`, `StateHash` covers depots and deals. `--trade-demo` (with
  `--colonies=2`): depots, an agreed deal under way and an offer waiting; opens Trade.

## Game design decisions

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
- **Movement:** agents are 1 tile tall and occupy an open cell; standable = solid floor below, or the cell is
  itself a stair. Walk 8-way (no corner cutting); climb/drop exactly one level onto an orthogonal neighbour
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
  a size-1 saw horse and joinery bench (2 logs each) and a scaffold.
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
  **Waterfalls** (`FallView`, `waterfall(_dr).gdshader`): the sim moves water over a drop straight between columns, so the
  air between lip and landing held nothing to draw. `Water.Falls(box)` (read-only) lists pipes on awake blocks sending at
  least `SleepFlow` to a span whose surface (or floor) is 0.75+ levels lower; FallView reads it round the camera 4x a
  second (range 1.6x rig distance, 40-180 tiles, max 1500) and draws a full-cell curtain from the lip down (streaks
  running down, lacy for trickles), a splash puff and spray droplets, all animated in the shader; falls of 1.5+ levels
  also give off mist (`mist(_dr).gdshader`: 4 camera-facing puffs a fall on staggered loops, rising, swelling and
  drifting out and downwind, their density Bayer-dithered so it stays opaque pixel-art haze). `--falls-demo`.
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
  share the wheels cover). Farm plots within 2 tiles of water grow 1.5x as fast (`Farms.Irrigated`).
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
- **Cave-ins (DF-style):** a solid tile is supported if face-adjacent solid tiles connect it to z=0. After a
  dig (`ColonySim.CompleteDesignation` -> `World.CaveIns`), each unsupported neighbouring mass falls as one
  rigid piece until it lands; designations on it are cancelled; colonists ride it down or pop out on top.
  Masses over `CaveIns.MaxMassTiles` count as self-supporting. Any code removing solid tiles should go
  through `CompleteDesignation` or call `ColonySim.CheckSupport`. No damage yet (no health system).
- **Health and combat (`Agents/ColonistCombat.cs`, `Combat.Gear`):** light and fast. A colonist has 100 health and heals
  1 per 60 ticks (per 20 asleep). At 0 it is struck down (`Downed`: lies where it fell, heals 1 per 30 ticks, stands at 25)
  and rarely dies (`DeathChance` 12%, a hashed roll; `ColonySim.QueueDeath` -> `Bury` after the wildlife tick: load and
  gear dropped, spouse unlinked, `Deaths`). Departing colonists leave their gear. Blows are `Gear.Resolve`: a shield
  blocks `block`% outright, else damage minus body armour's `protect` (less the weapon's `pierce`); a fully soaked blow
  still chips 1 a quarter of the time. Rolls hash (tick, attacker, target). **Gear is catalog data** (`gear`: weapon /
  body / shield, with damage, cooldown, reach, cleave, pierce, hands, protect, block): spear (reach 2, pierce), bow
  (reach 9, needs `Gear.LineOfSight`: solids and thin walls block), war axe (two-handed, cleaves 3: swarms), sword (fast,
  takes a shield); padded jerkin 1, chitin armour 2 (from ant chitin), plate 4, power armour 7; wooden shield 25%, iron
  40%. Bare hands: 4 dmg / 20 ticks. A two-handed weapon drops the shield. **Drafting** (commands `SetDrafted` (id or -1:
  all grown), `SquadMove` (a formation round the spot), `SquadAttack(nestId)`): drafted colonists drop work, fetch the
  best usable gear lying about (`TryEquip`, task `Equip`, one slot at a time), march to `Rally`, fight any foe within
  `EngageRange` (10) and strike the nest they were sent against; they eat when famished between fights and never
  collapse from tiredness; squadmates spread round a target. Undrafted, a bitten colonist fights back (task `Fight`), or
  flees below 35 health (children always flee), and goes back to work 80 ticks after the last blow. `Colonist.AdvanceMove`
  is the shared walking step (normal tasks and combat chases). Fighters fan out (RimWorld-style: colonists squeeze past
  each other anywhere, but never fight from one tile): a fighter on a tile a lower-id fighter holds walks to the nearest
  free tile in reach (`FreeSpot`, `_fanningTo`); ants keep one biter per tile too. Every new combat field is in
  `SnapSaveCombat`/`Settle`.
- **Wildlife (`Combat.Wildlife`, `Nest`, `Creature`, `CreatureKind`, `FlowField`):** creature kinds are data
  (`CreatureKinds`: health, `Attack`, speed, `Temperament` Docile/Provokable/Hostile, drop, nest cap and mound health),
  so animals and other peoples' soldiers slot in later. **Giant ants** (the first): 8 health (one weapon blow; two
  punches), bite 3 per 24 ticks, quicker than colonists; a third drop chitin. **Nests** are counted, not simulated: a
  population underground breeds to 40 (one per `BirthInterval`, a third of a day), a full nest founds another 30-60 tiles off
  every 6-8 days (64+ from landings, 40+ from colony land, 40+ from nests; `MaxNests` = map area / 128²), `Populate` scatters them at generation (a 55% chance
  per 128² cell, 64+ tiles from every landing). A nest is **awake** (foragers out, `2 + total/8`) only while a colonist
  is within `WakeRange` (56) or it is fighting; asleep, its ants go back underground. **Temperament:** ants leave
  colonists alone until provoked: struck (`Hurt`/`StrikeNest` -> `Alarm`: angry at that colony for `CalmTicks` (2 days),
  defenders boil out 3 every 2 ticks), or a colony's land (16² blocks with its rooms, stockpiles, farms, landing;
  `ColonyReach` 6) meets the nest's territory (radius 12 + total/4, max 24): `Crowded`, announced, a day later the first raid, then one every
  ~3 days (a third of the nest, 4-16, but at most 3 + half the colony's grown colonists, filing out along one shared A* `Trail` to the nearest colonist of that colony
  within `RaidRange` 96; nothing if unreachable). Defenders fight only near their nest; raiders roam 20 tiles off the
  trail's end; nobody bites a downed colonist. The mound has 600 health; destroyed, up to 20 survivors fight on as
  `Lost` and wander off within a day. **Swarm pathing:** no ant searches: foragers and the homeward follow the nest's
  home flow field (Dijkstra flood round the entrance, rebuilt when its chunks change, at most every 100 ticks), fighters
  descend the nest's threat field (a flood from the colonists they are after over a box round the fight, every 24
  ticks) and close the last 4 tiles greedily, raiders walk the trail; stuck 120 ticks, an ant burrows home. Colonists
  find foes through `Wildlife.Near`/`NearestFoe` (8x8 column buckets rebuilt each tick). Cost at 2048 with ~100 ants out
  and 40 fighting: ~0.03 ms/tick (`-- ants` bench). Snapshots hold nests and creatures; `Settle` drops the caches
  (fields, trails, targets, buckets rebuilt) identically on both sides. `DebugSpawnNest` (command) makes an angry nest.

## Commands

- Build all: `dotnet build ColonyGame.sln`
- Test: `dotnet test ColonyGame.sln`
- Headless smoke run: `<godot_console.exe> --headless --path game --quit-after 60`
- Visual check / render benchmark: `<godot_console.exe> --path game -- --screenshot=out.png` (or `--bench`)
  plus optional `--size=WxDxH --seed=N --level=N --zoom=DIST --dig-demo --stairs-demo --ticks=N`, and
  `--look=X,Y` (camera on a column), `--water-demo` (terrace spring, waterfall wheel, pond, pump, irrigated field), `--showroom[=WORKSHOP_ID]` (every workshop with every station in sizes 1-5, stocked raw goods, farms;
  the id focuses one) with `--build-menu=WORKSHOP_ID` to open the build menu, `--storage-demo[=racks]` (a racked stockpile and a ceiling dug from a scaffold; =racks frames the racks), `--ants-demo[=calm]` (a full ant nest
  beside the colony; everyone armed, drafted and sent against it; =calm: just the nest).
  Waits until the world is fully meshed, measures 120 frames, prints `[bench]` lines (startup phases,
  frame ms, draw calls, memory), saves the PNG and quits.
- Sim benchmark: `dotnet run -c Release --project tools/Colony.Bench -- [size] [colonists] [ticks]`
  (or `-- water [size] [steps]` for the water simulation alone, whole map; `-- colony [size] [days]` for population,
  food and settlers over game days with no player)
  (prints per-phase totals, slow-tick breakdowns and GC counts; the render bench prints a startup breakdown).
- VS Code: F5 "Play Game" or "Open Godot Editor" (see `.vscode/launch.json`).
- **Render perf flags:** `--bench-zooms=4,60,450,...` (after the world is built, per rig distance: avg/p95/max ms, draws, primitives,
  art size, tiles per pixel, far view, shadows; the tool for frame-rate work), `--ortho`, `--persp`, `--default-look`, `--pixel=N`,
  `--no-shadows`, `--shadow-cascades` (overrides on the loaded look, never saved), `--stutter-log`, `--orbit-check`. Findings: iso
  shadows must stay 4 cascades (1, or 2 split at the pivot, lost most shadows); `ShadowsUpTo` (1.5 tiles/px) turns sun shadows
  off further out; shadows are the remaining cost in iso at pixel size 1, 0.5-1 tiles/px (~11-15 ms at 1600x900).
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
- [x] Production tree: data-driven catalog, 12 workshops / 32 stations / 78 recipes with models for every
      station size, multi-input crates, recipe per machine, education and the Academy, automatons, build
      menu (B). Raw sources: clay, sand, 8 minerals by depth, wild herbs, grain/herb/glowcap farms.
- [ ] Next for industry: conveyors between output and input crates; rooms themselves costing
      (walls/floors); stockpile filters; standing orders ("keep 50 planks") instead of fixed recipes;
      wonders as aux parts (philosopher's stone, farseer orb, orrery); real ratios.
- [~] Flora: grass grows back over dug dirt (`Regrowth`). Next: wild growth and spreading/regrowth of plants (staggered, deterministic).
- [ ] Geology: several stone types in layered/warped strata (palette now has room).
- [~] M5: Needs and homes. Done: hunger, eating, starving leaves; daily goods standard (clothing, tinctures); homes
      (sized rooms, housing policies, tiered fixtures with wear, salvage, auto layout), rest in beds, residents by
      education, mood -> work speed, gentle departures, settlers by food, mood and free beds; procedural home
      layouts; likes (food, material, art, work); thin walls, doors, windows, floors, roofs (materials, dams, shell
      quality, wall lines); families (weddings, births, children, homes per family). Next:
      schooling children, health beyond consumption, settler couples, old age.
- [x] UI: Glyph UI (theme, glass panels, window manager, HUD, dock, status pill), every menu migrated (Build, Housing,
      Work, Tests) plus Colonists, Stocks, Chronicle, Inspect, Appearance, Help windows and a main menu; work priorities.
- [x] Multiplayer foundation: player commands (queue, log, exact replay, binary codec), factions (ownership of orders,
      zones, rooms, blueprints, colonists; per-colony society and housing), multi-colony world generation, hot-seat.
- [x] Networking: host-relayed lockstep over TCP (join mid-game by snapshot, desync hashes, guests act only for their
      colony), Multiplayer page in the main menu, HUD status; verified across two processes (identical hashes).
- [x] Save/load: settings + command log saves, background load with progress, quicksave/quickload, daily autosave.
- [x] Snapshots: whole-state saves and joins (settle as a command, exact; ~2 s load at 2048).
- [x] Combat and the first threat: health, struck down / rare death, four weapons and two-part armour from the catalog,
      drafting and squad orders (G, Military window M), giant ant nests (docile until provoked or crowded; defend, raid,
      spread; swarm flow fields), verified in tests, the `ants` bench and two processes (`--net-ants`).
- [x] Look: the Dwarf Render kit (pixel art, outlines, palettes, dithered trees, ASCII zoom) on our terrain, water, flora
      and every view; fighters fan out (one striker per tile); windows grow to fit; instant HUD hover banners.
- [ ] Next for combat: war between colonies (colonists attacking colonists), more threats (wild animals, hostile and
      provokable peoples), medicine/bandages and carrying the downed to bed, ranged arrows drawn in flight, armour tiers.
- [ ] Next for the demo: chat, NPC trading partners and caravans, colonist pages (job clothing, titles by
      proficiency once skills exist), title art behind the main menu, NAT/relay for internet play without port forwarding.
- Later: revelry and culture (taverns, temples consuming wine and fine goods), skills, factions,
      procedural narrative/history.
