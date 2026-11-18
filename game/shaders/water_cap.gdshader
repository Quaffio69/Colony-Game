// Water cut by the view level (WaterView): where a column's water rises above the cut, the slice hides its surface,
// so this quad at the cut height (level + 1) shows the water's cross-section instead: you can still see a lake, a
// flooded tunnel or a river when you look at a level below its surface. An R8 texture per map cell holds how far
// the water stands above the cut, in sixteenths of a level (0 = none); deeper reads darker and more opaque.
shader_type spatial;
render_mode blend_mix, cull_back, depth_draw_opaque, specular_schlick_ggx;

uniform sampler2D cells : filter_nearest;
uniform vec3 shallow : source_color = vec3(0.22, 0.52, 0.74);
uniform vec3 deep : source_color = vec3(0.06, 0.22, 0.40);

varying vec3 v_world;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	ivec2 cell = ivec2(floor(v_world.xz));
	float v = texelFetch(cells, cell, 0).r * 255.0;
	if (v < 0.5) {
		discard;
	}
	float depth = v / 16.0; // levels of water above the cut
	float d = clamp(depth / 8.0, 0.0, 1.0);
	vec2 p = v_world.xz;
	float n = noise(p * 1.3 + vec2(TIME * 0.12, TIME * 0.08)) * 0.6 + noise(p * 2.9 - vec2(TIME * 0.1, 0.0)) * 0.4;
	// A faint grid on the cut, like the terrain's, so the cross-section reads as cells of water.
	vec2 f = fract(p);
	vec2 e = min(f, 1.0 - f);
	float edge = smoothstep(0.0, 0.06, min(e.x, e.y));
	float fade = 1.0 - smoothstep(0.04, 0.15, max(fwidth(p.x), fwidth(p.y)));
	vec3 base = mix(shallow, deep, 0.35 + 0.65 * d);
	ALBEDO = base * (0.9 + 0.2 * n) * mix(1.0, mix(0.8, 1.0, edge), fade);
	ROUGHNESS = 0.7;
	SPECULAR = 0.15;
	ALPHA = mix(0.62, 0.88, d);
}
