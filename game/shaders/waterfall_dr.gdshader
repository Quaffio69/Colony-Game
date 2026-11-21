// Falling water (FallView): where the simulation pours water over a drop it moves it between the two columns directly,
// so this draws what is in the air. One shader, three uses (mode):
//   0 sheet: a vertical curtain from the lip down to where it lands, lacy streaks running down it (thin flows are mostly
//     holes, strong ones solid), foamier towards the bottom;
//   1 splash: a churning foam puff where it lands;
//   2 spray: droplets thrown out from the foot in little arcs.
// INSTANCE_CUSTOM: r = how strong the flow is (0..1), g = the drop in levels, b = a per-instance seed.
// Dwarf Render variant: toon-lit, the water glyph in ROUGHNESS. Opaque with discard, sliced at the view level.
shader_type spatial;
render_mode cull_disabled;

#include "res://addons/dwarf_render/shaders/dr_toon.gdshaderinc"

uniform int mode = 0;
uniform float cut_height = 100000.0;
uniform vec3 water : source_color = vec3(0.25, 0.55, 0.78);
uniform vec3 foam : source_color = vec3(0.9, 0.95, 1.0);

varying vec3 v_world;
varying vec4 v_c;
varying vec2 v_uv;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

void vertex() {
	v_c = INSTANCE_CUSTOM;
	v_uv = UV;
	if (mode == 1) {
		float t = TIME * 3.0 + v_c.b * 40.0;
		VERTEX.y *= 0.7 + 0.35 * sin(t);
		VERTEX.xz *= 0.9 + 0.15 * sin(t * 1.3 + 1.0);
	} else if (mode == 2) {
		float t = fract(TIME * 1.4 + v_c.b);
		VERTEX += vec3((v_c.b - 0.5) * 0.9 * t, sin(t * 3.14159) * (0.25 + 0.3 * v_c.r) + 0.05, t * 0.6);
		VERTEX *= 1.0 - 0.6 * t;
	}
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

float foam_amount() {
	if (mode == 0) {
		float h = max(v_c.g, 0.5);
		vec2 q = vec2(v_uv.x * 6.0 + v_c.b * 13.0, v_uv.y * h * 2.2 - TIME * 3.2);
		float n = noise(q) * 0.6 + noise(q * vec2(2.0, 2.6) + 3.1) * 0.4;
		float edge = smoothstep(0.0, 0.18, v_uv.x) * smoothstep(1.0, 0.82, v_uv.x);
		float strong = clamp(v_c.r, 0.2, 1.0);
		if (n * mix(0.6, 1.0, edge) < 0.62 - 0.55 * strong) return -1.0; // a gap in the curtain
		return clamp(smoothstep(0.55, 0.85, n) + v_uv.y * 0.45, 0.0, 1.0);
	}
	return 0.8 + 0.2 * noise(v_world.xz * 7.0 + TIME * 2.0);
}

void fragment() {
	if (v_world.y > cut_height + 0.002) {
		discard;
	}
	float f = foam_amount();
	if (f < 0.0) {
		discard;
	}
	ALBEDO = mix(water, foam, f);
	ROUGHNESS = 27.0 / 127.0; // glyph group: water (GlyphAtlas.Water)
	METALLIC = 0.0;
	SPECULAR = 0.0;
	v_sky = 1.0;
	v_transl = 0.0;
}
