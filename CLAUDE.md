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
  The renderer maps sim (x, y, z) → Godot (x, z, y), since Godot is Y-up.
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
- **Pathfinding:** hierarchical: BFS over the region graph gives a corridor, then A* restricted to it on a
  snapshot of its bounding box. Colonists share a per-tick node budget (`PathNodeBudgetPerTick`).
- Default map 2048x2048x128 (~13 s to fully mesh, ~520 MB managed). Use `--size=1024x1024x128` for
  faster iteration.
- Known next optimizations: `JobBoard` rebuilds all designations on every connectivity change; component
  rebuild is O(regions + region edges) per dig (~5 ms at 2048); A* costs ~1-2 us/node (Dictionary +
  PriorityQueue); startup meshing/gen could be cached or streamed; sim runs on the main thread.

## Game design decisions

- **Movement (Minecraft-like, not DF ramps):** agents are 1 tile tall, stand in an air tile on a solid
  floor, walk 8-way (no corner cutting), and can climb or drop exactly one level onto an orthogonal
  neighbour (climbing needs headroom). Raw climbs are slow; climbs onto/off `TileType.Stairs` are cheap.
- **Stairs:** designated in one drag, footprint at least 2x2, N levels deep. One perimeter tile per level
  is carved into a stair step, spiralling around the shaft; the rest is dug out. No 1x1 DF-style shafts.

## Commands

- Build all: `dotnet build ColonyGame.sln`
- Test: `dotnet test ColonyGame.sln`
- Headless smoke run: `<godot_console.exe> --headless --path game --quit-after 60`
- Visual check / render benchmark: `<godot_console.exe> --path game -- --screenshot=out.png` (or `--bench`)
  plus optional `--size=WxDxH --seed=N --level=N --zoom=DIST --dig-demo --stairs-demo --ticks=N`.
  Waits until the world is fully meshed, measures 120 frames, prints `[bench]` lines (startup phases,
  frame ms, draw calls, memory), saves the PNG and quits.
- Sim benchmark: `dotnet run -c Release --project tools/Colony.Bench -- [size] [colonists] [ticks]`
- VS Code: F5 "Play Game" or "Open Godot Editor" (see `.vscode/launch.json`).

## Status / roadmap

- [x] M0: Project scaffold, seeded mountain world gen, instanced-cube renderer with z-level slicing,
      strategy camera.
- [x] M1: Chunked world storage (16³ `Chunk`s with version counters) + per-chunk face-culled meshing
      (`Colony.Core.Meshing.ChunkMesher`); only chunks whose version or view-slice key changed rebuild.
- [x] M2: Voxel raycast tile picking, drag-rectangle dig designations (`Orders.DigDesignations`);
      Enter debug-digs all designations until colonists exist.
- [x] M3: Colonists (`Agents.Colonist`), fixed 20 Hz ticks (`ColonySim.Step`), A*/Dijkstra pathfinding
      (`Navigation`), designation claiming, digging, and Minecraft-style spiral stairs.
- [x] M3.5: Scale-up to 2048x2048x128: greedy/threaded meshing, shader slicing, connectivity regions,
      job board, hierarchical pathfinding, stair step geometry, parallel multi-range world gen.
- [ ] Ideas: job priorities; cave-ins / structural support as gameplay (not needed for performance).
- [ ] M4: Items, stockpiles, first production chains (e.g. ore → smelter → bars → forge).
- [ ] M5: Needs (food, rest), farming, population growth.
- Later: education/skills, advanced product trees, factions, procedural narrative/history.
