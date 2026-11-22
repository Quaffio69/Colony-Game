# Playtester brief

You are playtesting **Colony Game**, a colony simulation in the spirit of Dwarf Fortress and RimWorld, before its
players do. You play it the way a new player would: through its orders, not its source code. Your job is to find out
where the game is confusing, slow, too fast, unfair or broken, and to write that up so the designer can act on it.

## The rules of the playtest

- **Play, don't read the code.** Read `reference.md` in your game folder once (orders, map legend, what can be built
  and made). Do not open the source (`src/`, `game/`, `tools/`) or the design docs: a player wouldn't have them.
  Confusion caused by not knowing something is a finding.
- **Each turn:** read the newest report (`report-NNN.md`, also printed by the turn command), decide, write your orders
  into `orders.txt` in the game folder (one per line), then run from the repo root:
  `dotnet run --project tools/Colony.Playtest --no-build -- turn GAMEDIR`
  (12 game hours; add an orders file and hours to change the length: `-- turn GAMEDIR GAMEDIR/orders.txt 24`).
  `-- probe GAMEDIR X Y` shows a column's tiles without time passing. Never run anything that opens a window.
- **Goal:** grow the colony over about 20 game days: keep everyone fed, house them, get industry going (planks,
  charcoal, bread, cloth...), and grow. Play sensibly, like a player who knows the genre but not this game.
- **Budget:** at most 40 turns. Use 24-hour turns when you are only waiting.
- **Keep notes as you go** in `notes.md` in the game folder: what you tried, what surprised you, what you couldn't
  find out how to do, anything that looks wrong (with the turn, coordinates and orders). Note the moment, not later.

## Your report

When done (or out of turns), write the report to the path you were given (default
`docs/playtest/reports/<date>-<game folder name>.md`), then export the game:
`dotnet run --project tools/Colony.Playtest --no-build -- export GAMEDIR`.

Report layout (short and specific; quote report lines and coordinates; no padding):

1. **Outcome:** day reached, people (arrived, born, left, died), food days, what got built, the state of industry.
2. **Timeline:** 6-12 lines, the key moments and decisions.
3. **Pacing:** per phase (first day, first week, after): what felt slow, what felt rushed, how long you waited on what.
4. **Friction:** what was hard to understand or do, and what you expected to happen instead.
5. **Bugs and oddities:** each with turn, coordinates, orders, what happened, what you expected.
6. **Balance:** numbers that seemed off (yields, costs, timings, hunger, settlers, threats).
7. **Top suggestions:** at most 7, most valuable first, each one line plus why.
