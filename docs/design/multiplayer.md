# Multiplayer, networking, saves, commands, factions, trade

Lockstep networking, state hashes, snapshots and saves, the command system, factions/ownership, and trade between colonies. Read before adding sim state (snapshot rule), player actions (command rule) or anything online.

- **Multiplayer direction:** freeform, each player a distinct colony (faction) in one world: cooperate against NPC
  enemies, trade, compete or go to war, with no mechanics beyond distinct colonies. Built: commands and factions
  (below), hot-seat. Next: networking (lockstep: peers exchange commands stamped with the tick they apply before);
  war needs combat and health first.

## Networking (`Colony.Core.Net`, game `MainNet.cs`)

- **Host-relayed lockstep over TCP** (plain .NET sockets in Core, no Godot: tested headless over loopback). The host's
  sim is the reference and never waits. `LockstepHost`: accepts clients (`Peer`: length-prefixed frames, a reader
  thread filling an inbox drained on the main thread), seats each in a colony (see **Coming back** below; refuses when none is free or on a protocol
  mismatch), sends a Welcome (colony, `GameSettings`, the tick, a `Snapshot` of the game), stamps the client's colony on
  every command it sends (`with { Player = faction }`: nobody acts for another colony) and submits it; each frame
  `Broadcast` flushes (so commands apply even while paused) and sends each client a Turn: newly logged commands with
  their ticks and the host's tick. `AfterTick` (call after every host tick) takes a `StateHash` every
  `HashInterval` (100) ticks, sent just before the next turn (a client must hold a tick's hash before it can step past it, or the check was skipped: a race a fast loopback client lost). `LockstepClient`: `Connect` (waits for the welcome),
  `BuildWorld` (loads the snapshot), `Poll`, `Advance`
  (applies each command when the sim reaches its tick, never runs past `ConfirmedTick`, compares hashes:
  `DesyncTick`), `Send`. Guests trail the host by their latency and catch up. On a desync the client stops (no more
  ticks) and sends `Msg.Resync`; the host answers `Msg.State` (its tick and a fresh snapshot, like a join; turns go on
  from there), the client drops what it had queued, and the game opens it (`ResyncReady`, `TakeResync`, a scene reload).
- **Coming back (rejoining, saved sessions):** each colony records who plays it (`Faction.Player`, `PlayerKey`: a
  24-hex SHA-256 digest of the player's key, `NetPlayer.KeyHash`; the key never leaves the host), set by the host's
  `ClaimColony` command (guests' copies are refused), so every peer, snapshot and save agrees (snapshot 14, `StateHash`).
  Each install makes a random key once (game `MultiplayerPrefs`, `user://multiplayer.json`: name, key, last address and
  port) and sends it in the Hello. `LockstepHost.Seat`: the colony whose key matches (an absent player: theirs, under
  any name), else one recorded with a name but no key (a save from before keys) matching the name, else a colony nobody
  has played; colonies held for absent players wait for them ("held for Ann until they come back"). A key matching a
  connected player is a second copy of the game on one computer: it joins as a keyless player of its own. The host
  plays colony 0 and claims it at start; `Free(colony)` releases an absent player's colony to the next newcomer. So a
  saved online game, loaded and hosted again another day, seats everyone in their own colony. Main menu Multiplayer:
  name, port and address remembered, Rejoin (last game), and online every colony with "here" / "away" / "free" and Free
  for the host. Dev: `--key=` (a key for this run; with `--name`, nothing is saved). Tested over loopback (rejoining by
  key under a new name, a save hosted again with players returning in another order, freeing, keyless saves by name,
  two copies on one computer, a guest's claim refused) and across two processes (`--key`, matching hashes).
- **`StateHash`:** colonists, piles, designations, stockpiles, rooms, blueprints, edge/cover revisions, combined
  order-independently with a fixed 64-bit mix (never `HashCode`: seeded per process; also never enumerate
  string-keyed dictionaries in sim logic, their order differs between processes).
- **Game:** sessions live in statics across scene reloads. `HostGame` (this world, if `Shareable`: generated from
  settings, or loaded or joined; test worlds can't be; demos are disabled online),
  `HostNewGame`, `JoinGame` (builds the world, reloads onto it), `LeaveOnline`. Host frames: `Poll`, ticks with
  `AfterTick`, `Broadcast`; the menu doesn't pause an online game. Client frames: `Poll`, `Advance` (all confirmed
  ticks, a third at a time when far behind); its tool sends commands (`DesignationTool.Remote`) and shows their results
  when they come back (`Commands.Applied`); no pause/speed keys. HUD shows `NetStatus` ("Out of step: reloading from the host"/Disconnected in bad).
  Main menu Multiplayer: name, port, Host this world, Host a new world (2-4 players), Join by address, players, Leave.
  Dev: `--host[=PORT]`, `--join=ADDR[:PORT]`, `--name=`, `--speed=0..2`, `--net-orders` (orders own colony through the
  command path; with `--net-ants` also an angry ant nest beside it), `--net-check=TICKS` (prints the state hash at that tick; client quits, host 15 s later), `--net-desync=TICK` (a client puts a stone in its world alone: it must resync, and its hash line says "resynced 1x"). End-to-end:
  start a headless host with `--colonies=2 --host=7781 --net-orders --net-check=1500`, then a client with
  `--join=127.0.0.1:7781 --net-orders --net-check=1500`; the two `[net] ... hash` lines must match (they did).
- Limits: demos edit the world outside commands, so they're off online (a world a demo changed can still be hosted:
  snapshots hold its edits).
- **Snapshots (`Net.Snapshot`, version 17: the settings carry the world shape (`GameSettings.Shape`, `WorldShape`; older snapshots rebuild with the first generator); 16: conveyor ends work with their 3x3; 15: conveyor belts and the goods on them, power per colony (wheels and pumps carry their colony); version 6: blueprints built from another cell (`Blueprint.Target`: decks, stairs); 5: machine limits; 4: trade depots, deals, seventh work type; 3: racks and regrowth; versions 2-3 still load, `SnapIO.Before(v)` skips newer sections):** the whole state in a gzip blob: the `GameSettings` (the terrain is regenerated, a
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
- **Saves (`Net.SaveGame`, game `MainSave.cs`):** version 3 adds the world shape to the settings and a flag for the body (snapshot or log); version 2 holds a snapshot (version 1, the command log, still loads by
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

