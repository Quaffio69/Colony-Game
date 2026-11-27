// Terrain in the Dwarf Render look (see terrain.gdshader for the classic one): greedy sections from SectionMesher,
// sliced at the view level, toon-lit, textured from the kit's block atlas, glyph group written to ROUGHNESS for the
// outline and ASCII passes. UV.x = tile type.
shader_type spatial;
render_mode cull_back;

#include "res://addons/dwarf_render/shaders/dr_toon.gdshaderinc"
#include "res://shaders/tiles_dr.gdshaderinc"

uniform float cut_height = 100000.0; // Godot Y of the top of the view level (level + 1)

varying vec3 v_world;
varying vec3 v_normal;
varying float v_tile;

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_normal = NORMAL;
	v_tile = UV.x;
}

void fragment() {
	if (v_world.y > cut_height + 0.002) {
		discard;
	}
	int t = clamp(int(v_tile + 0.5), 0, 63);
	vec3 cell = floor(v_world - v_normal * 0.5);
	bool on_cut = abs(cell.y - (cut_height - 1.0)) < 0.5;
	ALBEDO = dr_tile_albedo(t, cell, v_world, v_normal, on_cut);
	EMISSION = dr_tile_glow(t, ALBEDO);
	v_sky = 1.0;
	v_transl = 0.0;
	METALLIC = 0.0;
	SPECULAR = 0.0;
	ROUGHNESS = dr_group[t] / 127.0;
}
