# Developer commands and flags

Every build, run, bench, demo and debug flag.

- Build all: `dotnet build ColonyGame.sln`
- Test: `dotnet test ColonyGame.sln`
- In-game cheats (developer builds only, i.e. run from the editor or a debug export): Ctrl+Enter finishes every order at
  once (`DebugCompleteAll`); F1 opens Tests & demos. Released builds have neither.
- Headless smoke run: `<godot_console.exe> --headless --path game --quit-after 60`
- Visual check / render benchmark: `<godot_console.exe> --path game -- --screenshot=out.png` (or `--bench`)
  plus optional `--size=WxDxH --seed=N --level=N --zoom=DIST --dig-demo[=huge] --stairs-demo --ticks=N`, and
  (`--dig-demo=huge`: 240x240 round the colony at every level below it, stopped by the order limit)
  `--look=X,Y` (camera on a column), `--water-demo` (terrace spring, waterfall wheel, pond, pump, irrigated field), `--showroom[=WORKSHOP_ID]` (every workshop with every station in sizes 1-5, stocked raw goods, farms;
  the id focuses one) with `--build-menu=WORKSHOP_ID` to open the build menu, `--storage-demo[=racks]` (a racked stockpile and a ceiling dug from a scaffold; =racks frames the racks), `--house-demo[=STOREYS]` (a house built by colonists on levelled ground, a second left planned; prints what each level of it holds; `--house-zoom=N` frames it closer or further), `--machines-demo` (an excavator and a power wagon at work beside the colony: a dig box, rubble, a stockpile, charcoal; `--machines-ticks=N` how long first), `--mine-demo[=N|deep|cavern]` (a stair shaft beside the colony down to N levels below zero, default 20, or just over the bedrock, and a gallery there, or into the nearest cavern; views it: rock layers, ore and gems in the walls, fog of war; prints the rock down the shaft; `--zoom` frames it), `--ants-demo[=calm]` (a full ant nest
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
  `--no-shadows`, `--shadow-cascades` (overrides on the loaded look, never saved), `--stutter-log`, `--orbit-check`,
  `--pitch=DEG` (bench camera pitch, e.g. -20 for low views), `--no-flora` (hide plants: what they cost), `--tree-lod=0`
  (no coarse tree LOD), `--no-shadow-proxies` (trees cast their own shadows when zoomed out), `--no-vsync` (real frame
  times in the zoom sweep, which also prints world GPU and render CPU ms), `--bench-breakdown` (after each zoom, the frame
  without each view in turn), `--tree-count` (full trees in range per zoom), `--flora-hide=Plant,Tree,Marker,Shadow`,
  `--no-coarsest`, `--no-core-split`, `--no-far-shadows`, `--ids-viewport` / `--ids-check` / `--ids-sun` / `--no-ids` (the
  old ids pass, a diff of both, the old pass with the sun, none), `--shadow-filter=0..5`. Godot's `--resolution WxH` and
  `--gpu-profile` go before `--`; bench at the player's screen size (2560x1440 here), the art target scales with it. Findings: iso
  shadows must stay 4 cascades (1, or 2 split at the pivot, lost most shadows); `ShadowsUpTo` (1.5 tiles/px) turns sun shadows
  off further out; shadows are the remaining cost in iso at pixel size 1, 0.5-1 tiles/px (~11-15 ms at 1600x900).
- **UI dev flags:** `--open=colonists,stocks,...` opens windows (ids: colonists stocks build chronicle housing work inspect
  tests appearance help), `--tool=MODE` puts a tool in hand (Dig, Edge, Stockpile, ...), `--mouse=X,Y` parks the mouse (the hover card), `--tip=TEXT` shows a tooltip (`
` breaks), `--main-menu` starts on the menu, `--work-menu`/`--test-menu` (old names), `--ui-selftest` drives
  the window manager with real input events (hotkeys, Esc order, drag, resize, maximise, dock, tabs, menu, theme switch),
  prints PASS/FAIL and quits (exit code 1 on failure). The bench prints the worst update time per open window.
- **In-game test menu (F1, the Tests & demos window):** every xUnit test, run inside the game (`TestRunner`: the test DLL from
  `tests/Colony.Core.Tests/bin/Debug` loaded from bytes into its own load context sharing the game's Colony.Core;
  "Reload tests" picks up a rebuild). "Run" opens the first `ColonySim` the test built, at its end; "Watch" stops it
  at its first step (`DevHooks`, per-thread hooks in Core) and opens that world paused. The colony waits in a static
  while you look ("Back to the colony"); worlds swap by scene reload, so views must unsubscribe from the sim on
  `_ExitTree`. Also launches the dev demos into the colony. CLI: `--test=NAME [--test-watch] [--test-menu]`.
  Tests that should be watchable just build a `ColonySim` (hand-built worlds are drawn like any other).

