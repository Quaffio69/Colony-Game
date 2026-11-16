// Instanced trees and plants (MultiMesh stand-ins).
// - INSTANCE_CUSTOM.rgb is a per-plant tint (linear); tint_mix picks how much of its hue to apply
//   (1 = full colour, e.g. flower petals or far-LOD markers; low = brightness variation only).
// - Sliced like the terrain: anything more than ~one level above the cut is discarded, so plants standing
//   on the cut surface show as stumps instead of hiding the view.
shader_type spatial;
render_mode cull_back;

uniform vec3 albedo : source_color = vec3(0.3, 0.6, 0.25);
uniform float tint_mix = 0.35;
uniform float roughness = 0.9;
uniform float cut_height = 100000.0;
// False for plain (non-MultiMesh) meshes, which have no per-instance data: no tint.
uniform bool instanced = true;
// Self-lit share of the albedo (glowcaps, furnace mouths, aether parts).
uniform float emission = 0.0;

varying vec3 v_world;
varying vec3 v_tint;

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_tint = instanced ? INSTANCE_CUSTOM.rgb : vec3(1.0);
}

void fragment() {
	if (v_world.y > cut_height + 0.95) {
		discard;
	}
	float luma = dot(v_tint, vec3(0.333));
	ALBEDO = albedo * mix(vec3(luma), v_tint, tint_mix);
	ROUGHNESS = roughness;
	EMISSION = ALBEDO * emission;
}
