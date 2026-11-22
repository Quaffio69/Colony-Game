# UI (Glyph UI)

The window system, controls, game windows, hotkeys, mouse, selection, notices and the main menu. Read before touching game/scripts/ui.

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
