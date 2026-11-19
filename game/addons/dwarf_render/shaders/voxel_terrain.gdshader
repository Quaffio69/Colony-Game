// =====================================================================
// Dwarf Render — voxel terrain (port of the diorama's toon-fs).
// Expects meshes built by VoxelMesher.cs (or a mesher writing the same data):
//   COLOR    = block base colour (sRGB)
//   CUSTOM0  = (vertex AO 0..1, sky exposure 0..1, emissive 0/1, material class)
//              class: 0 generic, 1 water, 2 prop/no jitter, 3 grass, 4 ore
//   CUSTOM1  = (texture position xyz in chunk-local voxel space, block type = atlas row)
//   CUSTOM2  = (lookup axis normal xyz, glyph group id 0..126)
//   CUSTOM3  = (owning voxel centre xyz chunk-local, 1 if valid else 0)
// The glyph group id is written to ROUGHNESS so the post / ASCII passes can
// read "what object is under this pixel" from the normal-roughness buffer.
// =====================================================================
shader_type spatial;
render_mode cull_back;

#include "res://addons/dwarf_render/shaders/dr_toon.gdshaderinc"

uniform sampler2D dr_atlas : filter_nearest, repeat_disable;   // block texture atlas (3 columns x block types), raw sRGB
uniform float dr_tile_px = 8.0;          // tile resolution of the atlas (4, 8, 16)
uniform float dr_tex_strength = 0.85;    // 0 = flat colour
uniform vec3 dr_dirt_col = vec3(0.541, 0.353, 0.204);   // grass-side dirt (used when textures are off)
uniform vec3 dr_ore_host = vec3(0.435, 0.416, 0.400);   // stone around ore flecks (textures off)

varying vec4 v_aux;
varying vec3 v_tex_pos;
varying float v_block;
varying vec3 v_look;
varying float v_group;
varying vec3 v_cell;
varying float v_has_cell;

void vertex() {
	v_aux = CUSTOM0;
	v_tex_pos = (MODEL_MATRIX * vec4(CUSTOM1.xyz, 1.0)).xyz;     // world-space so chunks tile seamlessly
	v_block = CUSTOM1.w;
	v_look = CUSTOM2.xyz;
	v_group = CUSTOM2.w;
	v_cell = (MODEL_MATRIX * vec4(CUSTOM3.xyz, 1.0)).xyz;
	v_has_cell = CUSTOM3.w;
}

void fragment() {
	vec3 Lk = normalize(v_look);
	vec3 tp = v_tex_pos;
	vec3 cell = v_has_cell > 0.5 ? floor(v_cell) : floor(tp - Lk * 0.5);
	float cls = v_aux.w;
	vec3 albedo = COLOR.rgb;                         // authored in sRGB; converted at the end
	float jitter = 0.93 + 0.14 * dr_hash3(cell);
	if (cls < 0.5 || cls > 2.5) albedo *= jitter;

	// Grass: dirt sides with a ragged green lip, dirt underneath.
	if (abs(cls - 3.0) < 0.5 && Lk.y < 0.5) {
		vec2 fc = abs(Lk.x) > 0.5 ? tp.zy : tp.xy;
		float lip = 0.70 - 0.14 * step(0.5, dr_hash3(vec3(floor(fc.x * 4.0), cell.y, 3.0)));
		if (Lk.y < -0.5 || fract(tp.y) < lip) albedo = dr_dirt_col * jitter;
	}
	// Ore: 4x4 flecks over host stone.
	if (abs(cls - 4.0) < 0.5) {
		vec2 fc = abs(Lk.x) > 0.5 ? tp.zy : (abs(Lk.y) > 0.5 ? tp.xz : tp.xy);
		vec2 g = floor(fract(fc) * 4.0);
		if (dr_hash3(vec3(g, dot(cell, vec3(1.0, 7.0, 13.0)))) < 0.6) albedo = dr_ore_host * jitter;
	}
	// Block texture atlas. Top/bottom tiles get a random 90 degree turn per voxel.
	if (v_block > 0.5 && dr_tex_strength > 0.0) {
		vec3 an = abs(Lk);
		vec2 fuv; float face = 1.0;
		if (an.y >= an.x && an.y >= an.z) { fuv = tp.xz; face = Lk.y > 0.0 ? 0.0 : 2.0; }
		else if (an.x >= an.z) { fuv = tp.zy; }
		else { fuv = tp.xy; }
		vec2 f = fract(fuv);
		if (face != 1.0) {
			float r = floor(dr_hash3(cell + 5.3) * 4.0);
			if (r == 1.0) f = vec2(f.y, 1.0 - f.x); else if (r == 2.0) f = 1.0 - f; else if (r == 3.0) f = vec2(1.0 - f.y, f.x);
		}
		f.y = 1.0 - f.y;                              // atlas rows are stored top-down
		ivec2 texel = ivec2(min(floor(f * dr_tile_px), vec2(dr_tile_px - 1.0)));
		ivec2 apx = ivec2(int(face * dr_tile_px), int(floor(v_block + 0.5) * dr_tile_px)) + texel;
		vec3 tex = texelFetch(dr_atlas, apx, 0).rgb;
		albedo = mix(albedo, tex * mix(1.0, jitter, 0.5), dr_tex_strength);
	}
	// Water: blocky animated glints.
	if (abs(cls - 1.0) < 0.5 && Lk.y > 0.5) {
		float w = sin(tp.x * 2.1 + TIME * 1.4) * sin(tp.z * 1.7 - TIME * 1.1) + 0.5 * sin((tp.x + tp.z) * 3.3 + TIME * 2.0);
		albedo *= 0.92 + 0.08 * step(0.0, w);
		albedo = mix(albedo, albedo * 1.35 + 0.08, step(0.95, w));
	}

	ALBEDO = dr_srgb_to_linear(clamp(albedo, 0.0, 1.0));
	if (v_aux.z > 0.5) {                              // emissive (forge embers, crystals)
		float fl = 0.92 + 0.08 * sin(TIME * 11.0 + cell.x * 1.7 + cell.z) * sin(TIME * 7.3 + cell.y);
		EMISSION = ALBEDO * 1.2 * fl;
	}
	// Ambient: vertex AO times sky exposure (caves get dark). Direct light is not affected.
	AO = v_aux.x * mix(0.12, 1.0, v_aux.y);
	AO_LIGHT_AFFECT = 0.0;
	v_sky = v_aux.y;
	v_transl = 0.0;
	METALLIC = 0.0;
	SPECULAR = 0.0;
	ROUGHNESS = v_group / 127.0;                      // glyph group id for post / ASCII
}
