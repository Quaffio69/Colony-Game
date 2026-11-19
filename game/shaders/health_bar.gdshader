// A billboarded health bar: the left `fill` of the quad in the fill colour (green to red), the rest dark.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_test_disabled, shadows_disabled;

instance uniform float fill = 1.0;

void vertex() {
	// Face the camera, keep the node's scale (the quad's size).
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
}

void fragment() {
	float edge = step(0.06, UV.y) * step(UV.y, 0.94) * step(0.02, UV.x) * step(UV.x, 0.98);
	vec3 full = mix(vec3(0.85, 0.2, 0.15), vec3(0.35, 0.85, 0.35), clamp(fill * 1.4 - 0.2, 0.0, 1.0));
	vec3 c = UV.x < fill ? full : vec3(0.12, 0.1, 0.1);
	ALBEDO = mix(vec3(0.02), c, edge);
	ALPHA = 0.92;
}
