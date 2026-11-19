// Roofs (CoverView), Dwarf Render look: sliced exactly at the view level like the terrain (a roof sits above its cell, so looking into
// a home at its own level hides it), and patterned from world position: courses of shingles/tiles/slates running
// level round the slopes, a dark line under each course and staggered joints between pieces, each piece shaded a little
// differently. `course` is courses per level of height; `joints` how many pieces per tile along a course.
shader_type spatial;
render_mode cull_disabled;

#include "res://addons/dwarf_render/shaders/dr_toon.gdshaderinc"

uniform vec3 albedo : source_color = vec3(0.6, 0.3, 0.2);
uniform float cut_height = 100000.0;
uniform float course = 7.0;
uniform float joints = 3.0;
uniform float roughness = 0.85;

varying vec3 v_world;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	if (v_world.y > cut_height + 0.002) {
		discard;
	}
	float rows = v_world.y * course;
	float row = floor(rows);
	float along = (v_world.x + v_world.z) * joints + row * 0.5; // staggered from course to course
	float piece = floor(along);
	float under = smoothstep(0.0, 0.14, fract(rows));                 // shadow line along each course's lower edge
	float joint = 1.0 - 0.3 * step(0.93, fract(along));               // a thin dark joint between pieces
	float shade = 0.82 + 0.18 * hash(vec2(row, piece));
	ALBEDO = albedo * shade * mix(0.62, 1.0, under) * joint;
	ROUGHNESS = 33.0 / 127.0; // glyph group: construction (GameLooks.Construction)
	METALLIC = 0.0;
	SPECULAR = 0.0;
	v_sky = 1.0;
	v_transl = 0.0;
}
