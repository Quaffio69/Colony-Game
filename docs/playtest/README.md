# Agent playtesting

An AI agent plays the real simulation a turn at a time through text (`tools/Colony.Playtest`), the way a new player
would, and writes up what it found: pacing, confusion, bugs, balance. Same rules as the game: every order is the
command a player's click issues.

## Running one

1. `dotnet build tools/Colony.Playtest` once.
2. Start a game: `dotnet run --project tools/Colony.Playtest --no-build -- new playtests/<name> [seed] [size]`
   (`playtests/` is gitignored; seed 12345 at 512 is the default).
3. Give an agent `docs/playtest/AGENT.md` as its brief, with the game folder and any focus ("rush industry",
   "neglect food", "find bugs in homes"). It plays, keeps notes, and writes its report to
   `docs/playtest/reports/<date>-<name>.md`.
4. Watch what it built: `... -- export playtests/<name>` puts the game in the saves (main menu, Load game).

## How a turn works

`turn DIR` loads `DIR/game.colony` (an ordinary save), applies `DIR/orders.txt` (each order's answer is the game's own
message), plays 12 game hours (or the hours given), saves, and writes `DIR/report-NNN.md`: colony numbers, every
colonist and what they are doing, rooms and parts, threats, events, a 64x30 map and a height map. `probe DIR X Y` lists
a column's tiles; `view X Y LEVEL` (an order) shows one level DF-style. `DIR/log.md` keeps every order and answer.

Each load settles the game as a save does (loads are put down, work is picked again), like an autosave twice a day.
