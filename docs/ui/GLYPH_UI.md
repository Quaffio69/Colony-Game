# Glyph UI — design spec & Godot implementation brief

**Status:** approved visual direction for every menu in the game (Nov 2026).
**Reference build:** `glyph_ui_reference.html` in this folder. Open it in a browser to see and use the target look: drag windows, switch themes in the Appearance window. When this spec and the reference disagree, follow the reference.
**Data files:** `glyph_themes.json` (all themes and defaults) and `glyph_glyphs.json` (5×7 bitmap glyphs, box-drawing, blocks).

---

## 1. The idea in one paragraph

Glyph UI is ASCII-inspired, but its finish is modern.

- **The ASCII part:**
  - Everything is laid out on a monospace character grid: sizes, gaps and positions are whole cells.
  - Icons are CP437 glyphs (☺ ≡ ‼ ☼ £ Θ ♥ %), drawn from the same 5×7 bitmap font as the diorama's ASCII shader.
  - Small text motifs carry the flavour: `·` leader dots, `▁▂▃▅▇` sparklines, `│` dividers, `↑↓` deltas.
- **The modern part:**
  - Panels are frosted glass with soft shadows, hairline borders and rounded corners.
  - Tabs and option groups are pills, progress bars are thin and rounded, and on/off options are toggle switches.
  - The resource bar and the window dock float as pills.
- **Rule of thumb:**
  - Structure and iconography come from the terminal.
  - Surfaces and controls come from a modern app.
  - Never use beveled or 3D frames, and never use full-cell block progress bars.

---

## 2. Grid & metrics

| Token | Value |
|---|---|
| Base font | **JetBrains Mono** 14 px (13 px on narrow screens). Alternatives: IBM Plex Mono, Space Mono, VT323 (retro, scaled 1.35×). Bold weight 600 (Space Mono 700). |
| Cell width `cw` | Advance width of `M` in the base font, rounded to whole pixels. |
| Cell height `ch` | Font size × line factor: compact **1.30**, cozy **1.55** (default), airy **1.80**. |
| Everything else | Express positions, sizes, paddings and gaps as multiples of `cw` and `ch`. Window drag and resize snap to whole cells. |

**Window anatomy** (in cells):
```
row 0        [chip] Title  K ..................  −  □  ×      ← title row (1 row)
rows 1..n-2    ··(2 cols padding)·· content ··(2 cols)·· ▌    ← scrollbar lives in the last column
row n-1        (bottom padding row)                       ⟋   ← resize grip in the corner cell
```
- **Content inset:** 2 columns left and right, 1 row top (the title row) and 1 row bottom.
- **Fixed footers** (for example, Build's item details) take rows from the bottom of the content area and do not scroll.
- **Fixed headers:** tab rows and other header rows sit above the scroll region and do not scroll.

---

## 3. Colour tokens (per theme; see `glyph_themes.json`)

| Token | Use |
|---|---|
| `bg` | Screen and map background. |
| `panel` | Window, HUD and dock surface. Drawn at 78% opacity over the blurred map (82% on light themes), or opaque when glass is off. |
| `text` / `dim` / `faint` / `hi` | Body text / secondary text / tertiary text, leaders and disabled items / emphasis and active titles. |
| `accent` / `accentInk` | Selection, focus and primary actions / text drawn on a solid accent fill. |
| `good` `warn` `bad` `info` | Status colours, bar tones and glyph tints. |
| `map.*`, `mapDim` | Map palette and how far map colours are mixed toward `bg` (0.48–0.62). |
| `dwarves[]` | Per-dwarf ☺ colours. |
| `light` | `true` for Paper. Light themes use softer shadows and slightly higher overlay alphas, as noted below. |

Themes: **Hearth** (default, matches the diorama), Glacier, Midnight, Phosphor, Amber, Paper.

**Alpha conventions:** semi-transparent fills are always a token colour plus an alpha. For example, "text @ 6%" means the `text` colour at alpha 0.06.

---

## 4. Components (exact values)

**Window panel**
- Corner radius: **8 px** by default (user option: 0 / 4 / 8 / 12 / 16).
- Shadow on dark themes:
  - inactive: `rgba(0,0,0,.55)`, blur **22**, offset y **7**.
  - active: blur **34**, offset y **12**.
- Shadow on light themes: `rgba(60,40,20,.22)`.
- Glass: blur what is behind the panel (heavy blur, about 10× downscale), then draw `panel` @ 78% (82% on light themes) on top. When glass is off, draw `panel` opaque.
- Title band: a vertical gradient over the title row from text @ 5.5% to 0, then a 1 px divider at the bottom of the title row in text @ 7% (10% on light themes).
- Border options:
  - **hairline** (default): 1 px in text @ 11% (16% on light themes). The active window uses accent @ 55%.
  - **double**: hairline plus a second line 3 px inside, in text @ 7% (accent @ 30% when active).
  - **glow**: the active border is accent @ 80% plus an outer glow in accent @ 45%, blur 16.
  - **none**.
- Dark themes add a 1 px inner highlight along the top edge in white @ 6%.

**Title row:**
1. An icon chip.
2. The title in bold: `hi` when the window is active, `dim` when it is not.
3. The hotkey letter in `faint`.
4. On the right, the controls `−` `□` `×` in `dim` (`faint` when the window is inactive). Hovering a control shows a circle in text @ 14% behind it. Hovering **close** turns the circle `bad` and the glyph white.

**Icon chip**
- One cell holding a CP437 glyph tinted with its role colour.
- Background: a rounded rect (radius 4 px) in the same colour @ 20% (16% on light themes), inset about 12% vertically and spilling about 0.15 cell horizontally.
- Chips can be turned off by the user, leaving just the tinted glyph.

**List row**
- **Selected:** rounded rect in accent @ 16% (14% on light themes) spanning the row plus half a cell on each side, inset 6% vertically.
- **Hover:** text @ 6%.
- No zebra striping.

**Tabs / segmented control**
- Track: a rounded rect in text @ 4.5% (6% on light themes) behind the whole group.
- Active segment: accent @ 20% (16% on light themes), with `hi` bold text.
- Inactive segment: `dim` text; on hover, text @ 7%.
- Each label has one space of padding on each side.
- This same control is used for window tabs, build categories, theme options and the speed control.

**Buttons**
- Default: accent @ 13% fill (10% on light themes) with a 1 px accent @ 35% outline, and `accent` text.
- Hover: solid `accent` with `accentInk` text.
- Primary: solid accent @ 90% with `accentInk` text.
- Pill shape. Labels have one space of padding on each side, for example ` Cancel `.

**Progress bar**
- Height **20% of `ch`** (minimum 2 px), fully rounded, inset a quarter cell on each side.
- Track: text @ 9% (10% on light themes).
- Fill colour, when the tone is automatic: `good` at 60% or more, `warn` at 30% or more, `bad` below that. Explicit tones are `accent`, `info` and so on.
- An optional numeric value sits to the right in `faint`.

**Switch**
- Height 50% of `ch`, width 1.8× its height, fully rounded.
- On: `accent` track with an `accentInk` knob.
- Off: text @ 16% track with a `dim` knob (white knob on light themes).
- Clicking anywhere on the row toggles it.

**Section header**
- Label in UPPERCASE, `dim`, bold.
- Then a 1 px rule in text @ 10% running to the right edge.

**Key/value rows**
- `chip + name + ····· + value`.
- The leader dots `·` are `faint`. The value is right-aligned, bold, in `hi`.

**Sparkline**
- Eighth-block characters `▁▂▃▄▅▆▇█`, drawn as real rectangles occupying 62% of the cell height.
- Tinted `good` when the trend is rising and `bad` when it is falling.
- An optional signed delta follows it.

**Scrollbar**
- Lives in the last column of the window.
- Track: width 28% of `cw`, text @ 7%.
- Thumb: text @ 35%; `accent` on hover or drag.
- No arrow buttons.

**Resize grip**
- Two short diagonal strokes in the bottom-right cell, text @ 25%; `accent` on hover.
- The right edge, bottom edge and corner are all drag handles.

**HUD (top)**
- A floating pill across the full width of row 0 (it uses the panel style).
- Left to right:
  1. the colony chip and name in bold
  2. the date and time in `dim`, hidden on narrow screens
  3. one item per resource: chip, value in bold, then `↑` (`good`) or `↓` (`bad`) for the daily change
  4. an alert pill, with a `bad` @ 20% background
- On the right, the speed segmented control `‖ ► ►► ►►►`. The active speed is solid accent with `accentInk` glyphs.
- Values below their warning level blink `bad`.
- Hovering a resource shows its details in the status pill; clicking it opens the related window.

**Dock (bottom)**
- A floating pill at the bottom-left with one item per window: glyph plus label, or glyph only on narrow screens.
- Focused window: accent @ 20% background and a 2 px `accent` underline.
- Open but unfocused: a 2 px underline in text @ 40%.
- Closed or minimised: `faint` text.
- Clicking an item focuses the window, minimises it if it is already focused, or restores it.
- **Status pill** at the bottom-right: hover hints in `dim`; messages and placement prompts in `warn`.

**Backdrop**
- The game view, or the ASCII map, is drawn with colours mixed toward `bg` by `mapDim`.
- A radial vignette darkens the edges toward `bg`: 55% at the edges on dark themes, 35% on light themes.

**Paint order**
1. Backdrop.
2. Each window in z-order: shadow, glass, panel, border, then its fills, bars and pills.
3. HUD and dock.
4. All text and glyphs.

---

## 5. Behaviour (window manager)

- **Moving and resizing**
  - Drag a window by its title row. Resize from the right edge, the bottom edge or the corner. Both snap to whole cells.
  - Enforce a minimum size per window.
  - Keep at least 4 cells of every window on screen.
- **Focus:** clicking anywhere in a window brings it to the front. The front window is the active one.
- **Window states**
  - Double-click the title to maximise to the area between the HUD and the dock.
  - `−` minimises to the dock, `□` maximises or restores, `×` closes.
- **Keyboard**
  - Hotkeys: C Colonists, S Stocks, B Build, L Log, T Appearance (Theme). In the reference build, `1`–`6` jump to Build tabs.
  - `Esc` cancels placement first; otherwise it closes the active window.
  - `Tab` cycles focus. `Space` pauses.
- **Scrolling:** the wheel scrolls the window under the pointer. Log-style windows stay pinned to the newest entry unless the user scrolls up.
- **Persistence:** save the theme, all appearance options and each window's rectangle and open/minimised state.

---

## 6. Godot implementation plan (C#)

The existing menus are Godot Control scenes. Do **not** rebuild them as a custom character renderer. Instead, ship the look as a **Theme plus a small kit of custom controls**, then migrate the menus one at a time.

### 6.1 Core
- **`GlyphTheme.cs`**
  - Loads `glyph_themes.json` and the user's saved options.
  - Builds a Godot `Theme` from them, and rebuilds it and re-applies it at the UI root when the theme or options change.
  - Exposes the tokens: `Color Tok(string name)` and `Color Tok(string name, float alpha)`.
- **`GlyphMetrics.cs`**
  - `Cell = new Vector2(font.GetCharSize('M', size).X, Mathf.Round(size * lineFactor))`.
  - Helpers: `Cells(int cols, int rows)` and `Snap(Vector2 position)`.
  - Use the cell size for every margin, separation and minimum size.
- **StyleBoxFlat mapping**
  - **Panels:** `bg_color` = panel at its alpha; `corner_radius_*` = radius; `border_width_*` = 1; `border_color` = text @ 11%.
  - **Shadows:** `shadow_color` = rgba(0,0,0,.55); `shadow_size` 22 (34 when active); `shadow_offset` (0, 7) or (0, 12); `anti_aliasing` on.
  - Make an active variation that swaps the border to accent @ 55% and enlarges the shadow.
- **Glass:** a `ColorRect` behind the panel content with a `canvas_item` shader:
  - `uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;`
  - Output `textureLod(screen_tex, SCREEN_UV, 3.0)` mixed with the panel colour at 78%, and mask it to the rounded rect (an SDF on the UV).
  - Needs the Forward+ or Mobile renderer. If glass is off or the renderer is Compatibility, use the opaque panel colour.

### 6.2 Custom controls (small, reusable)

| Control | Replaces / does |
|---|---|
| `GlyphWindow` (PanelContainer) | Title row: chip, bold title, hotkey, `− □ ×`. Title-band gradient and divider; content margins of 2 cells left/right and 1 row bottom; corner resize grip; drag and resize snapped to cells; double-click to maximise. Emits `Closed`, `Minimized` and `Focused`. Wrap every existing menu window in this. |
| `GlyphManager` (Control) | Z-order and focus, the active-window style, the maximise area, hotkeys, `Esc`/`Tab`, saving the layout. |
| `GlyphIcon` | Draws one 5×7 bitmap glyph from `glyph_glyphs.json` as rects inside a cell. Pre-bake all glyphs into one `ImageTexture` atlas and draw with `DrawTextureRectRegion`. **Never rely on the font for ☺ ♣ ≈ ☼ etc.**: fonts lack them, and some platforms render emoji. |
| `GlyphChip` | `GlyphIcon` on a 20%-alpha rounded rect in the role colour. |
| `GlyphBar` | The thin rounded bar with track and auto tone. You can also restyle `ProgressBar` instead: `background`/`fill` StyleBoxFlat with full corner radius and custom minimum height 0.2 × `ch`, vertically centred. |
| `GlyphSwitch` | Replaces CheckBox/CheckButton: track and knob as specified; the whole row is clickable. |
| `GlyphSegmented` | Replaces TabBar and option-button groups: pills on a track. Restyling TabBar/TabContainer `tab_selected`/`tab_unselected` with StyleBoxFlat (rounded, accent @ 20% when selected, transparent otherwise) gets most of the way. |
| `GlyphSection` | Uppercase dim bold label plus a 1 px rule. |
| `GlyphKV` | Chip, name, `·` leaders filling the gap, right-aligned bold value. |
| `GlyphSparkline` | Eighth-block bars from a float array. |
| `GlyphHud` / `GlyphDock` / status pill | As in section 4. |

Box-drawing characters (`─ │ ═ ║ ╔` and the rest), if you need them anywhere (for example on the map), must be drawn as lines and not as font glyphs. The arm weights are in `glyph_glyphs.json`.

### 6.3 Migration (apply across all existing menus)
1. **Inventory.** List every menu scene and popup. For each, note what it uses: windows, tabs, lists, bars, checkboxes, icons, buttons, numbers.
2. **Root theme.** Set the generated `Theme` on the UI root so every plain control picks up the look right away: fonts, colours, buttons, tabs, scrollbars.
3. **Windows.** Wrap each menu panel in `GlyphWindow` and register it with `GlyphManager` along with its hotkey.
4. **Replace controls inside each menu:**

   | From | To |
   |---|---|
   | ProgressBar | GlyphBar |
   | CheckBox | GlyphSwitch |
   | TabBar | GlyphSegmented |
   | Icons / text glyphs | GlyphChip |
   | Label pairs | GlyphKV |
   | Group headings | GlyphSection |

5. **Grid.** Convert hard-coded pixel sizes and margins to multiples of `GlyphMetrics.Cell`.
6. **Leave behaviour alone.** Keep each menu's signals and data bindings exactly as they are. This is a visual migration only.
7. **Check each menu** in Hearth (dark) and Paper (light), at 1× and 2× UI scale, before moving on.

---

## 7. Acceptance checklist (per menu)
- [ ] All window chrome comes from `GlyphWindow`: rounded corners, hairline border, shadow, title band, `− □ ×` controls.
- [ ] No beveled frames, no full-cell block bars, no default Godot checkboxes or tab looks remain.
- [ ] Icons are CP437 bitmap glyphs in chips, tinted by role.
- [ ] Numbers are right-aligned and bold; key/value rows use `·` leaders.
- [ ] Margins and sizes are whole cells, and dragging and resizing snap to the grid.
- [ ] Works in every theme; nothing is hard-coded to Hearth colours.
- [ ] Text contrast is readable on Paper, the light theme.
- [ ] Hover, selected and active states match section 4.
