// =====================================================================
// Dwarf Render — foliage (port of the diorama's foliage-fs).
// Meshes from TreeBuilder.cs:
//   COLOR   = leaf colour (sRGB)
//   CUSTOM0 = (canopy-centred "spherical" normal xyz in model space, layer: 0 outer shell / 1 inner core)
//   CUSTOM1 = (sway weight 0..1, is leaf card 0/1, species + seed (int part = species 0 oak 1 pine 2 birch 3 bush), part 0 shell / 1 core / 2 card)
//   UV      = leaf-card UVs
// Techniques (fol_mode): 0 dithered shell, 1 solid, 2 leaf cards.
// Godot reuses this shader for the depth pre-pass and shadow maps, so the
// dithered holes also appear in shadows and in the outline buffers.
// =====================================================================
shader_type spatial;
render_mode cull_disabled;

#include "res://addons/dwarf_render/shaders/foliage.gdshaderinc"
