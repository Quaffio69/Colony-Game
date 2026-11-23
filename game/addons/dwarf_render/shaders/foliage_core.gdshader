// =====================================================================
// Dwarf Render — the canopies' inner core (TreeBuilder puts it in its own surface when given a core material): opaque,
// back faces culled, no discard, so it takes early depth tests and hides the dithered shell layers behind it. Zoomed
// out over a forest the all-discard canopy was most of the frame. The view-level cut flattens it in the vertex shader.
// =====================================================================
shader_type spatial;
render_mode cull_back;

#define FOLIAGE_CORE
#include "res://addons/dwarf_render/shaders/foliage.gdshaderinc"