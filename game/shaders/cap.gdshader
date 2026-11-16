// The cut surface of the view level: one big quad at height level + 1 that draws the top of every solid
// tile on the view level whose top is covered by the (sliced-away) tile above. Tile types come from a
// world-sized R8 texture, so changing level only re-uploads that texture.
shader_type spatial;
render_mode cull_back;

#include "res://shaders/tiles.gdshaderinc"

uniform sampler2D tiles : filter_nearest;
uniform vec4 palette[32]; // rgb: sRGB tile colour; a < 1 glows (aetherite)
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
	ALBEDO = tile_albedo(palette[t].rgb, vec3(float(cell.x), level, float(cell.y)), v_world, vec3(0.0, 1.0, 0.0), true);
	EMISSION = tile_glow(palette[t]);
	ROUGHNESS = 0.9;
}
