# Look and rendering

The Dwarf Render kit integration (pixel art, glyph ids, ASCII zoom), the graphics window, the camera, water rendering and waterfalls. Read before touching anything visual.

## Look (Dwarf Render kit, `game/addons/dwarf_render`)

- **What:** a port of the user's Three.js diorama (`reference/voxel-diorama.html` is the answer key): the 3D world is drawn
  into a low-res SubViewport sharing our World3D (art; the glyph ids come from the same render, see **Ids** below), post-processed (outlines, grade, palette LUT, ordered
  dither, foliage-aware silhouettes) and presented full-screen on CanvasLayer -100 under the UI; the main viewport's 3D is
  off. Zoomed right in, art pixels dissolve into glyphs; far out (all symbols past ~450 tiles) the view becomes DF-style
  object symbols with colonists always ☺ (`AsciiMarker`). F10 tunes everything (presets, Save to
  `user://dwarf_render_settings.tres`). Appearance F2 "Pixel-art renderer" (`GlyphOptions.PixelArt`) switches to the classic
  look by rebuilding the scene on the same world (`Main.ReloadView`); `--classic-render` forces classic. Needs Forward+ and
  `screen_space_roughness_limiter` off (project.godot): every kit material writes its **glyph group** id into ROUGHNESS.
- **Integration (`MainRender.cs`, `GameLooks.cs`):** kept our greedy `SectionMesher` and slicing (the kit's own mesher,
  rounding, AO and sky light are unused): `WorldView` adds UV.x = tile type; `terrain_dr`/`cap_dr` (`tiles_dr.gdshaderinc`)
  do the kit's toon bands, block-texture atlas (kit block ids = `TileType` values, `GameLooks.RegisterBlocks`) and glyph id.
  `GameLooks.Shader(path)` picks a `*_dr.gdshader` variant when styled (terrain, cap, water and water cap: opaque, as
  the post pass only sees opaque surfaces; flora, roofs); `GameLooks.Styled(m)` / `DwarfMaterials.RegisterTerrain` feed
  them the atlas and lighting values. Trees and bushes are the kit's `TreeBuilder` meshes (canopy + bark in one mesh for
  our MultiMeshes; the kit's foliage/object shaders gained a `cut_height` slice). Every other view keeps making plain
  `StandardMaterial3D`s: `GameLooks.Watch` converts opaque lit ones to `DwarfMaterials.CreateObject` with a glyph group by
  owning view (colonists Dwarf, CreatureView Creature, ItemView Item, HomeView furniture, Edge/Cover construction...) as
  nodes are added plus a 1 s sweep (skipping subtrees in group `GameLooks.SweepSkip`: terrain near/far roots, water surfaces, flora; walking them was a ~90 ms hitch every second at 2048; `--stutter-log` prints frames over 50 ms with sim, sweep and GC); transparent/unshaded overlays are left alone and draw after the post quad
  (`RenderPriorityMin`), so cursors, designations, ghosts and health bars stay visible, depth-tested. Label3Ds are taken out
  of the 3D render and drawn crisp in 2D by `ScreenTags` (layer 1). The renderer zooms about the rig pivot
  (`FocusOverride`). **Stable pixels:** the render scale (world units per art pixel) moves in steps of `ZoomStep` (1.4x zoom; steps only make pixels bigger than asked, never smaller, and the target shrinks when oversized: pixel size 1 once rendered 4x the screen;
  a step holds until passed or 1.15 steps away); in iso only (perspective zoom really moves the camera), between steps the present pass scales the finished picture and the art target is sized for the
  step's smallest pixels, so zooming neither resamples nor resizes every frame. The snap grid is anchored at a world point
  (re-anchored at the pivot on a turn or scale change, keeping its whole-texel count, so dither doesn't jump). The ids pass
  writes a hash of the world tile under each pixel (B channel); symbol variants, the dissolve order and water ripples use
  it, so a tile keeps its symbol through turns and zooms (a 0.25° turn used to change ~80% of pixels near the pivot, now
  ~22%). `OrbitStep` (default smooth) snaps the rig's shown yaw/pitch, camera included, so picking matches. Past the map view's start the turn snaps to quarter turns and the camera glides to each (`UpdateShownYaw`; blending towards the nearest quarter jolted up to 45° at 45° boundaries; `--orbit-check` measures the worst frame). **Map view:** past `TopDownZoom` (0.15; eased in from twice that distance) the rig looks straight down with its turn eased to the nearest 90° (quarter turns only; the camera is aimed by rotation, LookAt fails straight down), so far out the pixel grid lines up with the tiles. **Zoomed in:**
  pixels grow from `PixelGrowStart` by `PixelGrowth` of the zoom up to `MaxPixelSize`, separately from the glyph fade
  (`ZoomInStart`/`ZoomInLength`); the rig zooms in to 4 tiles (`CameraRig.MinDistance`). `--yaw=` sets the bench view's
  turn (compare frames). The sun faces
  away from the default view (yaw 215) so we see lit faces (toon shade bands are dark). Render layers 19-20 are the kit's.
- **Graphics window (F10, `UI.GraphicsWindow`):** every `DwarfRenderSettings` [Export] by group, built by reflection in
  Glyph controls (`GlyphSlider`, `GlyphCycler`, switches, colour pickers) with the diorama's labels/formats; presets,
  Save (`Main.SaveLook` -> `user://dwarf_render_settings.tres`, loaded with every world), Revert, Defaults
  (`Main.GameDefaults`: the diorama's values, zoom 1x = 45 tiles); a live readout (zoom, ASCII %, art size, pixel size, fps). The kit's
  own F10 panel is off (`CreateTuningPanel = false`). Settings the port added from the diorama: sun angle / warmth /
  ambient / shadows / day-night (`DwarfRenderer.UpdateLighting`, the diorama's updateSun; `TimeOfDay` from the game clock),
  orthographic (iso) camera (`CameraRig.Orthographic`: same framing from far back: `OrthoBack` = half-height · cot pitch + (ceiling - pivot) / sin pitch, so terrain up to the world top or the view cut never crosses the near plane; Far follows), shadow map size, profiler
  (`UI.ProfilerPanel`: fps, worst frame, sim, world GPU, draws, prims, memory, zoom/ASCII; `--profiler`). Zoom is
  `ReferenceDistance / rig distance` (`ZoomOverride`); the rig eases zoom so ASCII transitions glide.
- **Glyph ids:** Godot 4.7 stores roughness as is (or 1 - r for dynamic objects): ids decode as `round(a * 127)`, groups
  0..63 only (`GlyphAtlas.MaxGroup`). The kit first assumed the older 7-bit packing and read every id doubled (grass drew
  farm symbols). `--id-probe` on the kit demo prints the decoded ids. **Ids (`IdsEffect`):** a compositor effect on the
  art camera (after the opaque pass) reads the art render's own depth and normal-roughness buffers with a compute shader and
  writes the ids image (id, linear depth, tile hash) the present shader reads. It replaced a second SubViewport that
  rendered the whole scene again whenever any ASCII showed (every draw call twice: the prim jump at the ASCII onset), and
  that pass also redrew the sun's four shadow cascades. Raw buffer roughness is r·127/255 (1 - that for dynamic objects),
  so ids are `round(w·255)`; view positions come from the camera's near/far/size (Godot's projection with reversed,
  [0, 1] depth). `--ids-check` renders both and diffs them (ids and sky identical, depth within 0.1%, tile hash differs only
  on tile edges); `--ids-viewport` uses the old pass; the sun sits on layer 18, hidden from it (`IdsSkipSun`). **Symbols (`GameLooks.RegisterSymbols`):** DF
  world-map style: grassland ", broadleaf ♣, conifers ♠ ↑, mountains ▲ (surface stone), cut rock ▓ (the cap uses
  `dr_cap_group`), water ≈ ~, sand ░, stairs < > X, ants a, nests ∩ (nodes named `Nest_*`), beds/tables Θ Π ╥, walls #; the
  UI's bitmap glyphs are imported into the kit font. No dither on the sky (a dot grid in the far view).
- **Cost:** kit trees are ~1k triangles (no leaf cards unless that technique is on at load); full detail only below 0.34 tiles per art pixel (off above 0.42; `MainRender`: in iso the range offset otherwise made all 130k trees full detail and shadowed, 570M triangles) and within 200 (measured from the pivot: `FloraView.RangeOffset` adds the iso camera's extra distance)
  tiles, and `FloraView.Detail` drops to the distant markers once the far view is mostly symbols. **Tree LOD:** each kit
  tree mesh carries a coarse level (`TreeBuilder.Build(lodEdge:)`: 20-face blobs, 4-sided cones and trunk, no branches or
  leaf cards; one vertex buffer, two index lists) that Godot picks per MultiMesh, in view and shadows. It ends up drawn at
  every zoom and looks the same even right in (dithered shell, spherical normals): iso 7 tiles 23.8M -> 0.58M prims.
  `--tree-lod=0` turns it off. Tree shadows: full trees below 0.2 t/px, then the opaque 20-triangle markers' over the
  same range (`FloraView.ShadowProxies`, a shadows-only marker MultiMesh per region: each cascade redraws every caster, and
  the foliage's dithered cut-outs are dear per pixel there), none with the markers (`TreeShadows`). Before this, trees were
  ~90% of the prims at every zoom. **Coarsest trees** (`FloraView.Coarsest`, from 0.1 t/px: a tile 10 art pixels or
  fewer): `TreeBuilder` level 2 (8-face blobs scaled 1.25 to keep the canopy's volume, 3-sided cones and trunk) swapped into
  the tree MultiMeshes by zoom, since in iso Godot's own LOD picks the coarsest level it has at any zoom. Godot's primitive
  counter undercounts MultiMeshes: at 1440p, pitch -20, 90 tiles there were 25k full trees in range (`--tree-count`).
  **Opaque core:** the canopy's inner core is its own surface (`foliage_core.gdshader`, `DwarfMaterials.FoliageCore`: back
  faces culled, no discard, the cut flattens it onto the cut plane in the vertex shader) so it takes early depth tests;
  the shell keeps the dithered discard (`foliage.gdshaderinc` holds both). Measured and dropped: hard shadow filtering,
  unshaded foliage, near-to-far instance order (none changed the GPU time). At 1440p (2560x1440, RTX 3070, vsync off)
  every zoom at pitch -20 and -35 now runs at most 5.7 ms (90-105 tiles at -20); before this pass, 60-90 tiles took
  8-11 ms (trees) and 150 tiles 9.6 ms (8.3k draws, see **far shadows** in performance.md). Perspective still peaks at
  ~8 ms at 90 tiles. Measure at the player's resolution: `--resolution 2560x1440` before `--`.
- Not ported yet: per-vertex AO / cave sky light and edge rounding (would need mesher changes), tree variants per species.


- **Camera:** the rig's pivot follows the visible ground (highest solid tile at or below the view level), so
  zoom stays relative to what's on screen when levels are peeled away.

- **Water rendering:** sections of 2x2 water blocks (32x32 columns, all levels). Each wet span gets a surface at
  its height (at least `MinDrawnDepth`, 0.04 levels, above its floor: the thinnest film the sim counts as wet still
  shows) with corners averaged with neighbouring columns within half a level (smooth sloping rivers), and
  side faces where the water beside is lower (falls; foamy). A block's version only bumps when a span's water
  moves a sixteenth of a level from what was last drawn (hysteresis). Meshing runs on worker threads from main-thread
  span snapshots (`Capture`: a section plus a border column; every open cell is in some span, so solidity comes from
  spans); unbuilt sections first, then changed ones nearest the camera (changes beyond 256 tiles wait 3 s); uploads
  within 1.5 ms/frame (2048: ~1 ms/frame for water, was 4). UV carries the flow (`Water.FlowAt`, exaggerated for the eye) so ripples
  stream along it. No draw distance. (Remote Desktop caps rendering at ~30 fps: frame times of ~31 ms there
  are not the game.) The slice hides water above the cut, so a **water cap** (`water_cap.gdshader`, like the
  terrain cap) draws the cross-section at the cut from a map-sized R8 texture (water above the cut per column, 1/16
  levels): rebuilt over wet blocks on a level change, patched per changed block, uploaded at most 5x a second.
  **Waterfalls** (`FallView`, `waterfall(_dr).gdshader`): the sim moves water over a drop straight between columns, so the
  air between lip and landing held nothing to draw. `Water.Falls(box)` (read-only) lists pipes on awake blocks sending at
  least `SleepFlow` to a span whose surface (or floor) is 0.75+ levels lower; FallView reads it round the camera 4x a
  second (range 1.6x rig distance, 40-180 tiles, max 1500) and draws a full-cell curtain from the lip down (streaks
  running down, lacy for trickles), a splash puff and spray droplets, all animated in the shader; falls of 1.5+ levels
  also give off mist (`mist(_dr).gdshader`: 4 camera-facing puffs a fall on staggered loops, rising, swelling and
  drifting out and downwind, their density Bayer-dithered so it stays opaque pixel-art haze). `--falls-demo`.
  **Mist banks** (`MistView`, `mist_cloud(_dr).gdshader`) are drawn from the sim's mist field (`Fluids.Mist`, see
  world.md), so they build up, drift and thin with it: round the camera (range 2x rig distance, 48-256 tiles) 4x a
  second, each misty 4x4 cell gets up to 3 camera-facing puffs (more as it thickens, each fading in over its own
  density range, stacked higher), eased toward the field (no popping). Puffs loop in the shader (16-24 s: fade in, swell,
  rise, drift on the `wind` uniform, which the CPU eases when the sim's wind veers); each shifts the Bayer pattern by
  its seed so overlaps fill different pixels (thick mist accumulates), with opacity capped (0.46 a puff) so the
  thickest banks stay see-through; puffs near the camera thin away; faded-out puffs collapse to no fragments. Above
  the cut and unseen cavern cells are skipped. One MultiMesh, one bulk buffer upload a refresh, nothing while there
  is no mist (bench: within noise at every zoom at 1440p). Mist writes glyph group `GlyphAtlas.Mist` (11), which
  the post pass leaves un-inked (no outlines or edge darkening on dithered haze), and a little emission (pale in
  shade). `--mist-demo`.
