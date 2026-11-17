// Water surfaces (WaterView): translucent, rippling, and sliced at the view level like the terrain.
// Vertex colour: r = shallowness (deeper is darker), a = foam (falling water, or the lip of a drop).
// UV = which way and how fast the water moves (sim x, y = world x, z): ripples drift along it and stretch into
// streaks as it speeds up, so rivers visibly run while ponds and lakes only shimmer.
shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_opaque, specular_schlick_ggx;

uniform vec3 shallow : source_color = vec3(0.22, 0.52, 0.74);
uniform vec3 deep : source_color = vec3(0.08, 0.27, 0.45);
uniform float cut_height = 100000.0;

varying vec3 v_world;
varying vec4 v_color;
varying vec3 v_wn;
varying vec2 v_flow;

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
	v_color = COLOR;
	v_wn = (MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz;
	v_flow = UV;
}

void fragment() {
	if (v_world.y > cut_height + 0.002) {
		discard;
	}
	vec2 p = v_world.xz;
	float t = TIME;
	float speed = length(v_flow);
	vec2 dir = speed > 0.01 ? v_flow / speed : vec2(1.0, 0.0);
	vec2 perp = vec2(-dir.y, dir.x);
	// In the flow's frame: along the current the pattern drifts downstream and stretches with speed.
	float along = dot(p, dir), across = dot(p, perp);
	float stretch = 1.0 + 2.5 * min(speed, 1.5);
	vec2 q = vec2(along / stretch - t * (0.25 + 1.4 * speed), across * 1.3);
	float s1 = noise(q * 1.6);
	float s2 = noise(q * 3.3 + vec2(t * 0.15, 4.7));
	// Still water: two slowly drifting layers.
	float n1 = noise(p * 1.7 + vec2(t * 0.35, t * 0.2));
	float n2 = noise(p * 3.1 - vec2(t * 0.25, t * 0.4));
	float moving = clamp(speed * 2.0, 0.0, 1.0);
	float a = mix(n1, s1, moving), b = mix(n2, s2, moving);
	vec3 bump = normalize(vec3((a - 0.5) * 0.35 + (b - 0.5) * 0.2, 1.0, (b - 0.5) * 0.35 - (a - 0.5) * 0.2));
	float top = step(0.5, abs(v_wn.y));
	NORMAL = normalize(mix(NORMAL, (VIEW_MATRIX * vec4(bump, 0.0)).xyz, top * 0.6));

	vec3 base = mix(shallow, deep, clamp(1.0 - v_color.r, 0.0, 1.0));
	// Running water catches a few pale streaks; foam churns where it pours.
	float streak = top * moving * smoothstep(0.78, 0.92, s2) * 0.22;
	float foam = clamp(v_color.a * (0.55 + 0.45 * noise(q * 4.0 + vec2(0.0, t * 2.0))) + streak, 0.0, 1.0);
	ALBEDO = mix(base, vec3(0.9, 0.95, 1.0), foam);
	ROUGHNESS = mix(0.08, 0.6, foam);
	METALLIC = 0.0;
	SPECULAR = 0.6;
	ALPHA = mix(0.8, 0.92, foam);
}
