# Status and roadmap

What is done and what is next, milestone by milestone.

- [x] M0: Project scaffold, seeded mountain world gen, instanced-cube renderer with z-level slicing,
      strategy camera.
- [x] M1: Chunked world storage (16³ `Chunk`s with version counters) + per-chunk face-culled meshing
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

## Player feedback (2026-11-22, the user and friends)

Sorted by kind; within each list roughly by how much it hurts play. Agent playtest findings are in
`docs/playtest/reports/`.

### Bugs
- [ ] Designating the whole world to dig hangs the game (too many orders at once).
- [ ] Joining a game that has hung hangs the joining player too (the snapshot or the catch-up never finishes).
- [ ] A desynced game keeps playing; it should stop and force a resync/reload from the host.
- [ ] Enter completes every order instantly in release builds (`DebugCompleteAll`, a leftover from M2): move it to dev mode.
- [ ] Tests in the game seemed broken (check the Tests window/`--ui-selftest`; the window is now dev builds only).
- [ ] Rooms cannot be shrunk once painted.

### Controls and clarity
- [ ] One wall tool for everything: click-drag a line or a box in the same tool.
- [ ] Clearer names for the wall, floor and roof tools.
- [ ] Show more clearly what material each item is made of.
- [ ] Call rooms "workshops" where they are production rooms.
- [ ] Inspect on hover, not on click; make the bottom-right inspect banner bigger.
- [ ] Simpler, clearer controls overall.
- [ ] A tutorial, at least for the basics.

### Building
- [ ] A building tool: place a whole building (walls, floor, roof) in one go, several storeys high.
- [ ] General improvements to buildings.

### Mining and orders
- [ ] Dig the highest blocks of a designation first.
- [ ] Dig priority setting (per designation).
- [ ] Mining gets harder deeper down, with better ores and gems as the reward.

### Multiplayer
- [ ] Saved multiplayer sessions that players can rejoin.
- [ ] Option to see other colonies' work orders.

### World
- [ ] Randomly generated caverns.
- [ ] Depths below zero, down to a random bottom within a range.
- [ ] Fog of war: dark tiles where no one can see or has been.
- [ ] Bigger maps, pushing how large a world can be (needs the optimisation below).

### Balance
- [ ] Grass regrows more slowly.
- [ ] Happiness lasts longer.
- [ ] Haulers carry more per trip (also the agent playtests: hauling took ~45% of colony time).

### New features
- [ ] Heavy machinery for digging, hauling etc., built in a workshop.
- [ ] Nobility: managers over aspects of production and people.
- [ ] Colonist inventory: pack meals to eat away from stores.
- [ ] Pollution.
- [ ] Simple people models with simple animations per action.
- [ ] Dev mode as a dedicated mode with cheat tools (takes over Enter-completes-all and the debug spawns).

### Direction
- [ ] Tone down the colony-sim side, lean more toward an automation game (affects needs, moods and families versus
      machines, conveyors and production chains; to decide before big work on either).

### Performance
- [ ] Optimise much further overall (frame time on big maps, sim cost with many orders).
