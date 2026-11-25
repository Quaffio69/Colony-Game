// Banks of waterfall mist (MistView): each instance is one cloud puff over a cell of the sim's mist field. It loops:
// fades in near the cell's middle, swells and drifts downwind (the `wind` uniform, eased on the CPU when it veers),
// rising a little, and fades out as the next puff of the cell fades in, so a cell's mist is steady but never still.
// A camera-facing quad (built here, so skip_vertex_transform) whose soft noisy density is dithered with a 4x4 Bayer
// pattern instead of blended: opaque, so the pixel-art pass sees it. Each puff shifts the pattern by its seed, so
// overlapping puffs cover different pixels and thicken where the field is dense; `opacity` (capped on the CPU) keeps
// even the thickest banks see-through. Puffs right in front of the camera thin away.
// INSTANCE_CUSTOM: r = opacity 0..1, g = size (tiles), b = seed, a = loop phase.
shader_type spatial;
render_mode cull_disabled, skip_vertex_transform;

uniform float cut_height = 100000.0;
uniform vec3 mist : source_color = vec3(0.93, 0.96, 1.0);
uniform vec2 wind = vec2(0.5, 0.0); // tiles a second (Godot x, z)

varying vec3 v_world;
varying vec2 v_uv;
varying float v_alpha;
varying float v_seed;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

float bayer4(vec2 p) {
	const float m[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
	ivec2 i = ivec2(mod(p, 4.0));
	return (m[i.x + i.y * 4] + 0.5) / 16.0;
}

void vertex() {
	vec4 c = INSTANCE_CUSTOM;
	float period = 16.0 + 8.0 * c.b; // seconds a puff lives
	float life = fract(TIME / period + c.a);
	vec3 origin = MODEL_MATRIX[3].xyz;
	float sway = TIME * 0.21 + c.b * 40.0;
	vec3 center = origin
		+ vec3(wind.x, 0.0, wind.y) * (life - 0.5) * period * 0.5
		+ vec3(sin(sway) * 0.35, life * 1.4, cos(sway * 0.8) * 0.35); // rising as it goes
	float fade = smoothstep(0.0, 0.3, life) * (1.0 - smoothstep(0.65, 1.0, life));
	float near = smoothstep(2.0, 9.0, distance(center, INV_VIEW_MATRIX[3].xyz)); // don't fog the lens
	v_alpha = c.r * fade * near;
	float size = c.g * (0.75 + 0.45 * life) * step(0.01, v_alpha); // unseen puffs collapse: no fragments
	vec3 world = center + (INV_VIEW_MATRIX[0].xyz * VERTEX.x + INV_VIEW_MATRIX[1].xyz * VERTEX.y) * size;
	v_world = world;
	v_uv = UV;
	v_seed = c.b;
	VERTEX = (VIEW_MATRIX * vec4(world, 1.0)).xyz;
	NORMAL = vec3(0.0, 0.0, 1.0);
}

float density() {
	vec2 p = v_uv - 0.5;
	float d = length(p) * 2.0;
	float n = noise(v_uv * 2.5 + vec2(v_seed * 9.0, TIME * 0.05)) * 0.65 + noise(v_uv * 5.0 - vec2(TIME * 0.04, v_seed * 5.0)) * 0.35;
	float core = clamp((1.0 - d * (0.75 + 0.45 * n)) * 1.8, 0.0, 1.0); // a soft rim round a fuller middle
	return core * core * (3.0 - 2.0 * core) * v_alpha;
}

void fragment() {
	// Each puff its own shift of the pattern: overlaps fill in rather than land on the same pixels.
	float s = floor(v_seed * 16.0);
	vec2 shift = vec2(mod(s, 4.0), floor(s / 4.0));
	if (v_world.y > cut_height + 0.002 || density() < bayer4(FRAGCOORD.xy + shift)) {
		discard;
	}
	ALBEDO = mist;
	ROUGHNESS = 0.9;
}
