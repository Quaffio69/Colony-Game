#include "res://addons/dwarf_render/shaders/dr_toon.gdshaderinc"

uniform float fol_mode = 0.0;
uniform float fol_density = 0.7;
uniform float fol_leaf_size = 0.2;
uniform float fol_edge_fade = 0.55;
uniform float fol_screen_mix = 0.35;
uniform float fol_sphere = 0.85;
uniform float fol_transl = 0.5;
uniform float fol_inner_dark = 0.6;
uniform float fol_hue_var = 0.5;
uniform float fol_sway = 0.35;
uniform vec2 fol_screen_offset = vec2(0.0);    // whole-texel camera offset (keeps the screen pattern on the world)
uniform float cut_height = 100000.0;            // the game's view-level slice (Godot Y): canopies more than ~a level above it are cut

varying vec3 v_rest;
varying vec3 v_world;
varying vec3 v_sphere_w;
varying vec4 v_c0;
varying vec4 v_c1;

float bayer2(vec2 a) { a = floor(a); return fract(dot(a, vec2(0.5, a.y * 0.75))); }
float bayer4(vec2 a) { return bayer2(0.5 * a) * 0.25 + bayer2(a); }

void vertex() {
	vec4 wp = MODEL_MATRIX * vec4(VERTEX, 1.0);
	v_rest = wp.xyz;                                  // un-swayed: leaf cells stick to the canopy
	float s = CUSTOM1.x * fol_sway;
	float ph = CUSTOM1.z * 6.283;
	vec3 off = vec3((sin(TIME * 1.3 + wp.z * 0.35 + ph) + 0.4 * sin(TIME * 3.1 + wp.y)) * 0.12 * s,
	                sin(TIME * 2.3 + wp.x + wp.z) * 0.03 * s,
	                cos(TIME * 1.1 + wp.x * 0.35 + ph) * 0.10 * s);
	if (s > 0.0) VERTEX += (inverse(MODEL_MATRIX) * vec4(off, 0.0)).xyz;
#ifdef FOLIAGE_CORE
	// The opaque core never discards (early depth tests work, and it hides the shell layers behind it): the view-level cut
	// flattens it onto the cut plane instead (a filled cross-section), and the solid technique, which has no core, folds it
	// to a point.
	float cut = cut_height + 0.95;
	if (wp.y > cut) VERTEX += (inverse(MODEL_MATRIX) * vec4(0.0, cut - wp.y, 0.0, 0.0)).xyz;
	if (abs(fol_mode - 1.0) < 0.5) VERTEX = vec3(0.0);
#endif
	v_world = wp.xyz + off;
	v_sphere_w = normalize((MODEL_MATRIX * vec4(CUSTOM0.xyz, 0.0)).xyz);
	v_c0 = CUSTOM0;
	v_c1 = CUSTOM1;
}

void fragment() {
	float layer = v_c0.w;
	vec3 leaf_cell = floor(v_rest / fol_leaf_size);
	float seed = v_c1.z;
#ifndef FOLIAGE_CORE
	if (v_rest.y > cut_height + 0.95) discard;
	// Which parts each technique shows (part: 0 outer shell, 1 inner core, 2 leaf card).
	float part = v_c1.w;
	if (fol_mode < 0.5 && part > 1.5) discard;                        // dithered: shell + core
	if (abs(fol_mode - 1.0) < 0.5 && part > 0.5) discard;             // solid: shell only
	if (fol_mode > 1.5 && part < 0.5) discard;                        // leaf cards: cards + core
	float h = dr_hash3(leaf_cell + seed * 17.0);

	if (v_c1.y > 0.5) {
		// Leaf card: teardrop leaf silhouette from UVs.
		vec2 q = UV * 2.0 - 1.0;
		if (length(vec2(q.x / (0.62 * (1.0 - 0.4 * q.y)), q.y)) > 1.0) discard;
	} else if (fol_mode < 0.5) {
		// Dithered shell: coverage drops toward the silhouette; the inner core is denser.
		vec3 V = normalize(CAMERA_POSITION_WORLD - v_world);
		float facing = abs(dot(v_sphere_w, V));
		float a = fol_density * mix(1.0, smoothstep(0.05, 0.75, facing), fol_edge_fade);
		if (layer > 0.5) a = mix(a, 1.0, 0.55);
		float thr = mix(h, bayer4(FRAGCOORD.xy + fol_screen_offset), fol_screen_mix);
		if (thr > a) discard;
	}
#endif

	// Normal: blend the flat face normal toward the canopy-centred normal (world -> view).
	vec3 nf_w = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	if (!FRONT_FACING) nf_w = -nf_w;
	vec3 n_w = normalize(mix(nf_w, v_sphere_w, fol_sphere));
	NORMAL = normalize((VIEW_MATRIX * vec4(n_w, 0.0)).xyz);

	// Per-leaf colour variety (yellow-green / blue-green), darker inner core.
	float hv = dr_hash3(leaf_cell + 3.1) - 0.5;
	vec3 c = COLOR.rgb * (1.0 + hv * 0.35 * fol_hue_var);
	c = mix(c, c * vec3(1.15, 1.08, 0.7), max(hv, 0.0) * fol_hue_var);
	c *= mix(1.0, 0.55, layer * fol_inner_dark);
	ALBEDO = dr_srgb_to_linear(clamp(c, 0.0, 1.0));
	AO = mix(1.0, 0.7, layer);
	AO_LIGHT_AFFECT = 0.0;
	v_sky = 1.0;
	v_transl = fol_transl;
	METALLIC = 0.0;
	SPECULAR = 0.0;
	ROUGHNESS = (floor(seed) + 1.0) / 127.0;          // glyph groups 1..4 = oak / pine / birch / bush
}
