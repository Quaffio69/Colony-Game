// =====================================================================
// Dwarf Render — toon shader for anything that is not a voxel: dwarves,
// creatures, items, furniture models, tree trunks, torches.
// Create materials with DwarfMaterials.CreateObject(...) so the renderer keeps
// the shared lighting values in sync.
//
// object_id is the glyph group shown in ASCII mode (see GlyphAtlas.cs):
//   5 = generic prop, 6 = dwarf, 7 = tree trunk, 8 = torch, 9 = creature, 10 = item, ...
//   or any group id you registered for items/creatures.
// sky_exposure is an INSTANCE uniform: set it per node with
//   meshInstance.SetInstanceShaderParameter("sky_exposure", value)
// (e.g. 0.2 for a dwarf deep in a mine so it isn't lit by ambient sky light).
// =====================================================================
shader_type spatial;
render_mode cull_back;

#include "res://addons/dwarf_render/shaders/dr_toon.gdshaderinc"

uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D albedo_tex : source_color, filter_nearest, hint_default_white;
uniform bool use_vertex_color = true;           // vertex colours are treated as sRGB
uniform float object_id = 5.0;                  // glyph group (0..126)
uniform float emission_strength = 0.0;          // >0 for flames, glowing items
instance uniform float sky_exposure = 1.0;
uniform float cut_height = 100000.0;            // the game's view-level slice (Godot Y; off by default)

varying vec3 v_obj_world;

void vertex() {
	v_obj_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	if (v_obj_world.y > cut_height + 0.95) discard;
	vec3 c = albedo_color.rgb * texture(albedo_tex, UV).rgb;
	if (use_vertex_color) c *= dr_srgb_to_linear(COLOR.rgb);
	ALBEDO = c;
	EMISSION = c * emission_strength;
	AO = mix(0.12, 1.0, sky_exposure);
	AO_LIGHT_AFFECT = 0.0;
	v_sky = sky_exposure;
	v_transl = 0.0;
	METALLIC = 0.0;
	SPECULAR = 0.0;
	ROUGHNESS = object_id / 127.0;
}
