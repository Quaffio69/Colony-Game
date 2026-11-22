# Colony Game

A 3D grid-based colony simulation in the spirit of Dwarf Fortress. The player leads a small colony that builds
structures and hollows out mountains to make room for ever-larger, more productive factories (food, clothing, weapons,
armor, metals, crafts, then long chains of advanced products). The goal is open-ended growth: a larger, better-educated,
more sophisticated population over time. Multiplayer: each player is a colony in one shared world (trade, cooperate,
compete). Public on GitHub (`Quaffio69/Colony-Game`, MIT); players install through the launcher.

## Where things are documented (read the one a task touches; don't read them all)

| Doc | Covers |
|---|---|
| `docs/design/economy.md` | `Balance.cs` pacing knobs, industry direction, flora, items/hauling/racks/scaffolds, production tree (`data/tech-tree.json`), factory builder, raw resources, farms, gathering |
| `docs/design/world.md` | movement rules, stairs, designations, water simulation, terrain hydrology, thin walls, floors/roofs, waterworks, cave-ins |
| `docs/design/people.md` | needs (hunger, rest, mood), population and settlers, homes and fixtures, families, work priorities, likes |
| `docs/design/combat.md` | health, gear, drafting/squads, giant ants (nests, temperament, raids, swarm pathing) |
| `docs/design/multiplayer.md` | lockstep networking, `StateHash`, snapshots, saves, commands, factions/ownership, trade |
| `docs/design/rendering.md` | Dwarf Render kit integration (pixel art, glyph ids, ASCII zoom), graphics window, camera, water rendering |
| `docs/design/performance.md` | meshing, connectivity, job board, pathfinding, change tracking, far view, water stepping costs |
| `docs/design/ui.md` | Glyph UI (spec in `docs/ui/GLYPH_UI.md`), windows, hotkeys, mouse, selection, notices, main menu |
| `docs/design/distribution.md` | release workflow (tag → GitHub Release), launcher (`launcher/`), icon |
| `docs/playtest/README.md` | agent playtesting: `tools/Colony.Playtest` (turn-based text play), the playtester brief (`AGENT.md`), reports in `docs/playtest/reports/` |
| `docs/dev-commands.md` | every run/bench/demo/debug flag |
| `docs/roadmap.md` | milestones done and next |

When a change alters what one of these says, update that doc in the same commit (they are the memory of the project).

## Architecture

- **Engine:** Godot 4.7.2 .NET, C# only (no GDScript). Godot exe:
  `C:\Users\Andrew\Desktop\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe` (`_console.exe` for CLI).
- **`src/Colony.Core`**: pure .NET 8 library with *all* simulation. **Never references Godot.** Compiles optimized even
  in Debug (the editor runs Debug).
- **`game/`**: the Godot project (assembly `ColonyGame`), a thin presentation/input layer: reads Core, sends commands.
- **`tests/Colony.Core.Tests`**: xUnit. New simulation logic comes with tests.
- **`tools/Colony.Bench`**: headless benchmarks and the `-- balance` pacing report.
- **`launcher/ColonyLauncher`**: the player-facing installer/updater (WPF, .NET Framework 4.8; not in the sln).

## Rules that apply everywhere

- **Coordinates:** x/y horizontal, **z = vertical level** (z=0 bottom), DF-style. Godot is Y-up: sim (x, y, z) →
  Godot (x, z, y); convert with `SimSpace`.
- **Determinism (lockstep multiplayer depends on it):** world generation from a seed via `Colony.Core.Procedural.Noise`
  (never shared `System.Random`); random rolls are hashes of (tick, ids); no wall-clock budgets in Core (budgets count
  nodes); never enumerate string-keyed dictionaries in sim logic; `StateHash` never uses `HashCode`.
- **Every player action is a `Command`** (`Colony.Core.Commands`): names things by ids and cells, never object
  references; add it to `CommandCodec` and to `CommandTests.EveryCommand`. The game issues through `DesignationTool.Issue`.
- **Every new piece of sim state goes in its class's `SnapSave`/`SnapLoad`** (bump `Snapshot.Version` and gate reads
  with `SnapIO.Before(v)` so old saves load), and in `Settle` if it holds claims or order-dependent containers. The
  snapshot continuation test catches omissions. Bump `Lockstep.Protocol` when the sim changes incompatibly.
- **Factions:** orders, zones, rooms, blueprints and colonists belong to a colony; registries take a `faction`. With
  one colony the ownership checks are skipped (single player pays nothing).
- **Performance:** nothing scans all chunks/sections/piles per tick or frame on big maps: track changes
  (`VoxelWorld.ChunkChanged`, version counters). Avoid large per-update allocations in hot Core paths (LOH → full GCs).
- **Tile algorithms** are generic over `ITileReader` (live `VoxelWorld` or a `TileBox` snapshot).
- **Balance numbers** live in `src/Colony.Core/Balance.cs` (measure with `-- balance`), recipes in `data/tech-tree.json`.
- **Game code:** no implicit `System.Collections.Generic` using; `Noise` clashes with `Godot.Noise` (fully qualify);
  `ColonySim` reaches nodes through `Initialize(...)` from `Main`; views unsubscribe from the sim in `_ExitTree`
  (worlds swap by scene reload). UI follows the Glyph UI spec; a click that rebuilds its own window defers the rebuild.
- **Automated runs of the game or launcher must close themselves** (`--screenshot=...`, `--ui-selftest`,
  `--quit-after`; launcher `-screenshot=`): otherwise the window waits on the user's screen. Back up and restore
  `user://glyph_ui.json` (in `%APPDATA%\Godot\app_userdata\Colony Game`) around game runs.
- **Public repo:** commit as `Andrew <36288005+Quaffio69@users.noreply.github.com>` (the repo's git config); never
  commit personal details. Push `main` after each finished, verified feature (CI runs the tests). Tagging `vX.Y.Z`
  publishes to players: always ask first.

## Commands

- Build: `dotnet build ColonyGame.sln` · Test: `dotnet test ColonyGame.sln`
- Headless smoke run: `<godot_console.exe> --headless --path game --quit-after 60`
- Screenshot / render bench: `<godot_console.exe> --path game -- --screenshot=out.png [--size=1024x1024x384]`
  (`--size=1024x1024x384` for fast iteration; demos and flags in `docs/dev-commands.md`)
- Sim bench: `dotnet run -c Release --project tools/Colony.Bench -- [size] [colonists] [ticks]`; pacing:
  `-- balance [size] [days]`; also `colony`, `families`, `ants`, `water`, `snapshot` modes.
- Playtest: `dotnet run --project tools/Colony.Playtest -- new|turn|probe|report|export DIR` (see `docs/playtest/README.md`).
- UI self-test: `<godot_console.exe> --path game -- --ui-selftest` (PASS/FAIL; a few checks are timing-sensitive).
- Launcher: `dotnet build launcher/ColonyLauncher -c Release`; preview `-state=update -screenshot=out.png`.
- Release: `git tag -a vX.Y.Z -m "notes"` then `git push origin vX.Y.Z` (only when the user asks).
- GitHub CLI: `"C:\Program Files\GitHub CLI\gh.exe"` (issues, Actions runs, releases).
