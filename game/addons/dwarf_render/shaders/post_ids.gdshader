// =====================================================================
// Dwarf Render — object-id pass. Runs in a second low-res SubViewport
// (same camera, HDR 2D on) only while ASCII is visible, and writes per
// art pixel:  R = glyph group id / 255,  G = linear view depth (0 = sky),
//             B = a hash of the world tile under the pixel (0..1).
// The present shader reads it to choose characters and edge strokes; the
// tile hash picks symbol variants and the dissolve order, so a tile keeps
// its symbol however the camera turns or zooms.
// =====================================================================
shader_type spatial;
render_mode unshaded, depth_draw_never, depth_test_disabled, cull_disabled, fog_disabled, shadows_disabled;

uniform sampler2D depth_tex : hint_depth_texture, filter_nearest, repeat_disable;
uniform sampler2D nr_tex : hint_normal_roughness_texture, filter_nearest, repeat_disable;
uniform bool reverse_z = true;

void vertex() {
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

float tile_hash(ivec3 t) {
	uint h = uint(t.x) * 73856093u ^ uint(t.y) * 19349663u ^ uint(t.z) * 83492791u;
	h ^= h >> 13u; h *= 1274126177u; h ^= h >> 16u;
	return float(h & 1023u) / 1024.0;   // 10 bits: exact in a half float
}

void fragment() {
	float d = texture(depth_tex, SCREEN_UV).r;
	bool sky = reverse_z ? d <= 1e-7 : d >= 0.9999999;
	vec4 nr = texture(nr_tex, SCREEN_UV);
	float a = nr.a;
	if (a > 0.5) a = 1.0 - a;
	float id = floor(a * 127.0 + 0.5); // see obj_id in post_color: Godot 4.7 stores roughness as is
	vec4 v = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, d, 1.0);
	vec3 view = v.xyz / v.w;
	float lin = -view.z;
	// The tile: the surface point nudged a little into the surface (so a face belongs to the tile it bounds).
	vec3 n = normalize(nr.xyz * 2.0 - 1.0);
	vec3 world = (INV_VIEW_MATRIX * vec4(view - n * 0.05, 1.0)).xyz;
	float th = tile_hash(ivec3(floor(world)));
	ALBEDO = sky ? vec3(0.0) : vec3(id / 255.0, lin, th);
}
