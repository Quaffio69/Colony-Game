// Shared tile look for terrain.gdshader and cap.gdshader (must match so the cut surface blends in).

float tile_hash(vec3 p) {
	p = fract(p * 0.1031);
	p += dot(p, p.yzx + 33.33);
	return fract((p.x + p.y) * p.z);
}

vec3 srgb_to_linear(vec3 c) {
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

// base: sRGB tile colour. cell: integer tile coords (Godot axes). world/normal: for grid lines.
vec3 tile_albedo(vec3 base, vec3 cell, vec3 world, vec3 normal, bool on_cut_level) {
	vec3 c = base + (tile_hash(cell) - 0.5) * 0.10;
	if (on_cut_level) {
		c = mix(c, vec3(1.0), 0.25); // lighter tint so the current level stands out
	}
	// Faint darkening along tile edges, in the face's plane. Faded out once a tile covers only a few
	// pixels, where the lines would alias into noise.
	vec3 f = fract(world);
	vec3 a = abs(normal);
	vec2 uv = a.x > 0.5 ? f.yz : (a.y > 0.5 ? f.xz : f.xy);
	vec2 e = min(uv, 1.0 - uv);
	float edge = smoothstep(0.0, 0.05, min(e.x, e.y));
	vec3 fw = fwidth(world);
	float pixel_size = max(fw.x, max(fw.y, fw.z)); // tiles per pixel
	float fade = 1.0 - smoothstep(0.04, 0.15, pixel_size);
	c *= mix(1.0, mix(0.86, 1.0, edge), fade);
	return srgb_to_linear(clamp(c, 0.0, 1.0));
}

// Glowing minerals (aetherite) carry alpha < 1 in their colour: the lower, the brighter.
vec3 tile_glow(vec4 base) {
	return srgb_to_linear(base.rgb) * (1.0 - base.a) * 1.4;
}
