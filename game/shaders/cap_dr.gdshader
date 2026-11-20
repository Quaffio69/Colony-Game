// The cut surface (see cap.gdshader) in the Dwarf Render look: tile tops at the view level, from the R8 tile texture.
shader_type spatial;
render_mode cull_back;

#include "res://addons/dwarf_render/shaders/dr_toon.gdshaderinc"
#include "res://shaders/tiles_dr.gdshaderinc"

uniform sampler2D tiles : filter_nearest;
uniform float level;

varying vec3 v_world;

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	ivec2 cell = ivec2(floor(v_world.xz));
	int t = int(texelFetch(tiles, cell, 0).r * 255.0 + 0.5);
	if (t == 0) {
		discard;
	}
	t = clamp(t, 0, 31);
	ALBEDO = dr_tile_albedo(t, vec3(float(cell.x), level, float(cell.y)), v_world, vec3(0.0, 1.0, 0.0), true);
	EMISSION = dr_tile_glow(t, ALBEDO);
	v_sky = 1.0;
	v_transl = 0.0;
	METALLIC = 0.0;
	SPECULAR = 0.0;
	ROUGHNESS = dr_cap_group[t] / 127.0;
}
