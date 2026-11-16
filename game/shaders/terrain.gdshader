// Terrain for greedy-meshed sections. Vertex colour = base tile colour (sRGB).
// - Slices the world at the view level: everything above cut_height is discarded, so changing level never
//   re-meshes anything. The cut surface itself is drawn by cap.gdshader.
// - Per-tile brightness jitter and faint grid lines are computed from world position, so merged quads still
//   read as individual tiles.
shader_type spatial;
render_mode cull_back;

#include "res://shaders/tiles.gdshaderinc"

uniform float cut_height = 100000.0; // Godot Y of the top of the view level (level + 1)

varying vec3 v_world;
varying vec3 v_normal;

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_normal = NORMAL;
}

void fragment() {
	if (v_world.y > cut_height + 0.002) {
		discard;
	}
	// The tile this fragment belongs to: step half a tile back against the face normal.
	vec3 cell = floor(v_world - v_normal * 0.5);
	bool on_cut_level = abs(cell.y - (cut_height - 1.0)) < 0.5;
	ALBEDO = tile_albedo(COLOR.rgb, cell, v_world, v_normal, on_cut_level);
	EMISSION = tile_glow(COLOR);
	ROUGHNESS = 0.9;
}
