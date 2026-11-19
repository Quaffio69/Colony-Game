// =====================================================================
// Dwarf Render — object-id pass. Runs in a second low-res SubViewport
// (same camera, HDR 2D on) only while ASCII is visible, and writes per
// art pixel:  R = glyph group id / 255,  G = linear view depth (0 = sky).
// The present shader reads it to choose characters and edge strokes.
// =====================================================================
shader_type spatial;
render_mode unshaded, depth_draw_never, depth_test_disabled, cull_disabled, fog_disabled, shadows_disabled;

uniform sampler2D depth_tex : hint_depth_texture, filter_nearest, repeat_disable;
uniform sampler2D nr_tex : hint_normal_roughness_texture, filter_nearest, repeat_disable;
uniform bool reverse_z = true;

void vertex() {
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

void fragment() {
	float d = texture(depth_tex, SCREEN_UV).r;
	bool sky = reverse_z ? d <= 1e-7 : d >= 0.9999999;
	float a = texture(nr_tex, SCREEN_UV).a;
	if (a > 0.5) a = 1.0 - a;
	float id = floor(a / (127.0 / 255.0) * 127.0 + 0.5);
	vec4 v = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, d, 1.0);
	float lin = -v.z / v.w;
	ALBEDO = sky ? vec3(0.0) : vec3(id / 255.0, lin, 0.0);
}
