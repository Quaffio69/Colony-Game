# Combat and wildlife

Health, gear, drafting and squads; giant ant nests (temperament, raids, swarm pathing).

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

