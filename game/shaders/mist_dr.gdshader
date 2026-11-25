// Mist drifting off the foot of a waterfall (FallView). Each instance is one puff on a loop: it rises from the splash,
// swells, drifts out from the cliff and with a light breeze, and fades; INSTANCE_CUSTOM.a staggers the loops. The puff
// is a camera-facing quad (built here, so skip_vertex_transform) whose soft, noisy density is dithered with a 4x4 Bayer
// pattern instead of blended: opaque, so the pixel-art pass sees it, and it reads as pixel-art haze.
// Dwarf Render variant: toon-lit, the mist glyph group in ROUGHNESS (the outline pass leaves it be).
// Instance basis: +Z = out from the cliff, +X = along it. INSTANCE_CUSTOM: r = strength, b = seed, a = loop phase.
shader_type spatial;
render_mode cull_disabled, skip_vertex_transform;

#include "res://addons/dwarf_render/shaders/dr_toon.gdshaderinc"

uniform float cut_height = 100000.0;
uniform vec3 mist : source_color = vec3(0.94, 0.97, 1.0);
uniform vec3 wind = vec3(0.35, 0.0, 0.2);

varying vec3 v_world;
varying vec2 v_uv;
varying float v_fade;
varying float v_seed;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

float bayer4(vec2 p) {
	const float m[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
	ivec2 i = ivec2(mod(p, 4.0));
	return (m[i.x + i.y * 4] + 0.5) / 16.0;
}

void vertex() {
	vec4 c = INSTANCE_CUSTOM;
	float life = fract(TIME * (0.07 + 0.05 * c.b) + c.a);
	vec3 origin = MODEL_MATRIX[3].xyz;
	vec3 out_dir = normalize(MODEL_MATRIX[2].xyz);
	vec3 along = normalize(MODEL_MATRIX[0].xyz);
	vec3 center = origin
		+ out_dir * (0.25 + life * 1.5)
		+ along * ((c.b - 0.5) * 1.4 + sin(TIME * 0.45 + c.b * 20.0) * 0.3)
		+ vec3(0.0, 0.1 + life * 1.2, 0.0)
		+ wind * life * 2.5;
	float size = (0.8 + life * 1.8) * (0.75 + 0.5 * c.r);
	vec3 world = center + (INV_VIEW_MATRIX[0].xyz * VERTEX.x + INV_VIEW_MATRIX[1].xyz * VERTEX.y) * size;
	v_world = world;
	v_uv = UV;
	v_fade = smoothstep(0.0, 0.15, life) * (1.0 - smoothstep(0.6, 1.0, life));
	v_seed = c.b;
	VERTEX = (VIEW_MATRIX * vec4(world, 1.0)).xyz;
	NORMAL = vec3(0.0, 0.0, 1.0);
}

float density() {
	float d = length(v_uv - 0.5) * 2.0;
	float n = noise(v_uv * 3.0 + vec2(v_seed * 7.0, TIME * 0.15)) * 0.6 + noise(v_uv * 6.0 - vec2(TIME * 0.1, v_seed * 3.0)) * 0.4;
	float core = clamp(1.0 - d, 0.0, 1.0);
	return clamp(sqrt(core) * (0.5 + 0.7 * n) * v_fade * 1.25, 0.0, 0.92);
}

void fragment() {
	if (v_world.y > cut_height + 0.002 || density() < bayer4(FRAGCOORD.xy)) {
		discard;
	}
	ALBEDO = mist * 0.7;
	EMISSION = dr_srgb_to_linear(mist) * 0.35; // lit from within a little: pale in shade, not dark
	ROUGHNESS = 11.0 / 127.0; // glyph group: mist (GlyphAtlas.Mist: no outlines)
	METALLIC = 0.0;
	SPECULAR = 0.0;
	v_sky = 1.0;
	v_transl = 0.0;
}
