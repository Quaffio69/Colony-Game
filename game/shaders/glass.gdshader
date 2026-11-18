// Glyph UI panel surface: frosted glass (the screen behind, blurred via its mipmaps) under the panel colour,
// masked to a rounded rect, with the hairline/double frame, the top inner highlight and the title band drawn in.
// Window shadows and the glow frame are drawn by the panel control itself (StyleBoxFlat shadow), under this.
shader_type canvas_item;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
uniform vec2 size = vec2(100.0, 100.0);
uniform float radius = 8.0;
uniform vec4 panel : source_color = vec4(0.1, 0.09, 0.08, 0.78); // alpha = how much panel over the blur
uniform float glass = 1.0;
uniform float blur_lod = 3.5;
uniform vec4 border : source_color = vec4(1.0, 1.0, 1.0, 0.11);
uniform vec4 border2 : source_color = vec4(0.0);   // second (inner) line of the double frame
uniform vec4 highlight : source_color = vec4(0.0); // 1 px along the top inside edge (dark themes)
uniform float title_h = 0.0;
uniform vec4 band : source_color = vec4(0.0);      // title band, fading down to nothing
uniform vec4 divider : source_color = vec4(0.0);   // 1 px under the title row

float rounded_box(vec2 p, vec2 half_size, float r) {
	vec2 q = abs(p) - half_size + vec2(r);
	return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

void fragment() {
	vec2 p = UV * size;
	float r = min(radius, min(size.x, size.y) * 0.5);
	float d = rounded_box(p - size * 0.5, size * 0.5, r);
	float inside = clamp(0.5 - d, 0.0, 1.0);

	vec3 col = panel.rgb;
	if (glass > 0.5) col = mix(textureLod(screen_tex, SCREEN_UV, blur_lod).rgb, panel.rgb, panel.a);

	if (title_h > 0.0) {
		if (p.y < title_h) col = mix(col, band.rgb, band.a * (1.0 - p.y / title_h));
		float line = clamp(1.0 - abs(p.y - (title_h - 0.5)), 0.0, 1.0) * step(1.0, p.x) * step(p.x, size.x - 1.0);
		col = mix(col, divider.rgb, divider.a * line);
	}
	float top = step(p.y, r + 2.0);
	col = mix(col, highlight.rgb, highlight.a * clamp(1.0 - abs(d + 1.5), 0.0, 1.0) * top);
	col = mix(col, border2.rgb, border2.a * clamp(1.0 - abs(d + 3.5), 0.0, 1.0));
	col = mix(col, border.rgb, border.a * clamp(1.0 - abs(d + 0.5), 0.0, 1.0));
	COLOR = vec4(col, inside);
}
