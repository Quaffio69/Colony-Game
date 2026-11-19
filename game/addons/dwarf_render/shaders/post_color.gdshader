// =====================================================================
// Dwarf Render — low-res post pass (port of the diorama's post-fs).
// Lives on a full-screen QuadMesh that DwarfRenderer puts in front of the
// render camera inside the low-res SubViewport. Runs once per ART pixel:
//   outlines -> fog -> watercolour edges -> grade -> duotone -> hatching
//   -> grain -> ordered dither -> palette snap (3D lookup texture).
// Colours are processed in display (sRGB) space, like the prototype, and
// converted back to linear at the end (keep the camera's tonemap = Linear).
// Requires the Forward+ renderer (normal-roughness buffer).
// =====================================================================
shader_type spatial;
render_mode unshaded, depth_draw_never, depth_test_disabled, cull_disabled, fog_disabled, shadows_disabled;

uniform sampler2D screen_tex : hint_screen_texture, filter_nearest, repeat_disable;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest, repeat_disable;
uniform sampler2D nr_tex : hint_normal_roughness_texture, filter_nearest, repeat_disable;
uniform sampler3D palette_lut : filter_nearest, repeat_disable;

uniform bool reverse_z = true;          // Godot 4.3+ uses reverse Z (sky depth = 0). Set false on 4.2.
uniform float texel_world = 0.1;        // ortho: world units per art pixel; perspective: per pixel per unit depth
uniform float is_ortho = 1.0;
uniform vec2 dither_offset = vec2(0.0); // whole-texel camera offset (keeps dither/hatching locked to the world)

uniform bool outline_on = true;
uniform float outline_outer = 0.75;
uniform float outline_inner = -0.35;    // <0 highlight crease, >0 ink crease
uniform float depth_threshold = 0.6;
uniform float normal_threshold = 0.35;
uniform float outline_width = 1.0;      // 1 or 2 art pixels
uniform vec3 outline_color = vec3(0.169, 0.102, 0.086);
uniform float foliage_outline = 1.0;    // 0 none, 1 canopy silhouette only, 2 every clump

uniform float sat = 1.15;
uniform float contrast = 1.05;
uniform float bright = 1.0;
uniform float fog_amount = 0.0;
uniform float fog_ref = 90.0;           // distance camera -> focus point
uniform vec3 fog_color = vec3(0.9, 0.82, 0.64);   // sRGB
uniform float wobble = 0.0;
uniform float edge_darken = 0.0;
uniform float hatch = 0.0;
uniform vec3 paper = vec3(0.937, 0.894, 0.784);
uniform float tone_mix = 0.0;
uniform vec3 tone_a = vec3(0.16, 0.12, 0.10);
uniform vec3 tone_b = vec3(0.94, 0.88, 0.75);
uniform float grain = 0.0;
uniform float grain_anim = 0.0;
uniform bool palette_on = true;
uniform float dither_amount = 0.35;
uniform float bayer_size = 4.0;
uniform float dither_spread = 0.19;

void vertex() {
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);           // full-screen quad (QuadMesh size 2x2)
}

float hash2(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vnoise(vec2 p) {
	vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash2(i), hash2(i + vec2(1, 0)), f.x), mix(hash2(i + vec2(0, 1)), hash2(i + 1.0), f.x), f.y);
}
float bayer2(vec2 a) { a = floor(a); return fract(dot(a, vec2(0.5, a.y * 0.75))); }
float bayer4(vec2 a) { return bayer2(0.5 * a) * 0.25 + bayer2(a); }
float bayer8(vec2 a) { return bayer4(0.5 * a) * 0.25 + bayer2(a); }
vec3 lin_to_srgb(vec3 c) { c = max(c, vec3(0.0)); return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }
vec3 srgb_to_lin(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }

float raw_depth(vec2 uv) { return texture(depth_tex, uv).r; }
bool is_sky(float d) { return reverse_z ? d <= 1e-7 : d >= 0.9999999; }
float lin_depth(float d, vec2 uv, mat4 inv_proj) {
	vec4 v = inv_proj * vec4(uv * 2.0 - 1.0, d, 1.0);
	return -v.z / v.w;
}
// Object id from the roughness channel (Godot packs roughness into 7 bits).
float obj_id(float a) { if (a > 0.5) a = 1.0 - a; return floor(a / (127.0 / 255.0) * 127.0 + 0.5); }
bool is_fol(float a) { float id = obj_id(a); return id >= 0.5 && id <= 4.5; }

float outer_axis(float d, float da, float db, float thr) {
	bool farther = (da - d > thr) || (db - d > thr);
	return (farther && (da + db - 2.0 * d) > thr) ? 1.0 : 0.0;
}
float crease(float d, vec3 n, float dn, vec3 nn, float thr) {
	if (abs(dn - d) > thr || dot(n - nn, vec3(0.31, 1.0, 0.17)) <= 0.02) return 0.0;
	return 1.0 - dot(n, nn);
}

void fragment() {
	vec2 px = 1.0 / VIEWPORT_SIZE;
	vec2 uv = SCREEN_UV;
	vec2 wp = floor(uv * VIEWPORT_SIZE) + dither_offset;
	vec2 cuv = uv;
	if (wobble > 0.0) {
		vec2 w = vec2(vnoise(wp * 0.21), vnoise(wp * 0.21 + 17.3)) * 2.0 - 1.0;
		cuv += w * wobble * px;
	}
	vec3 col = lin_to_srgb(texture(screen_tex, cuv).rgb);
	float dC = raw_depth(uv);
	bool sky = is_sky(dC);
	mat4 ip = INV_PROJECTION_MATRIX;
	mat3 v2w = mat3(INV_VIEW_MATRIX);

	if (fog_amount > 0.0 && !sky) {
		float f = smoothstep(-32.0, 56.0, lin_depth(dC, uv, ip) - fog_ref);
		col = mix(col, fog_color, f * fog_amount);
	}
	if (edge_darken > 0.0) {
		vec3 avg = (lin_to_srgb(texture(screen_tex, cuv + vec2(px.x, 0.0)).rgb) + lin_to_srgb(texture(screen_tex, cuv - vec2(px.x, 0.0)).rgb) +
		            lin_to_srgb(texture(screen_tex, cuv + vec2(0.0, px.y)).rgb) + lin_to_srgb(texture(screen_tex, cuv - vec2(0.0, px.y)).rgb)) * 0.25;
		col *= 1.0 - edge_darken * 0.45 * smoothstep(0.04, 0.25, length(col - avg));
	}

	// --- Outlines (second-difference depth test; world normals; foliage-aware) ---
	if (outline_on && !sky) {
		float d = lin_depth(dC, uv, ip);
		vec4 nC = texture(nr_tex, uv);
		vec3 n = normalize(v2w * (nC.xyz * 2.0 - 1.0));
		float tw = is_ortho > 0.5 ? texel_world : d * texel_world;
		float thr = depth_threshold * max(1.0, tw * 6.0);
		vec2 ox = vec2(px.x, 0.0), oy = vec2(0.0, px.y);
		bool fol = is_fol(nC.a);
		float d1R = lin_depth(raw_depth(uv + ox), uv + ox, ip), d1L = lin_depth(raw_depth(uv - ox), uv - ox, ip);
		float d1U = lin_depth(raw_depth(uv - oy), uv - oy, ip), d1D = lin_depth(raw_depth(uv + oy), uv + oy, ip);
		bool fol_sil = fol && foliage_outline < 1.5;
		bool want_crease = !fol && outline_inner != 0.0;
		vec4 qR = vec4(0.5), qL = vec4(0.5), qU = vec4(0.5), qD = vec4(0.5);
		if (want_crease || fol_sil) {
			qR = texture(nr_tex, uv + ox); qL = texture(nr_tex, uv - ox);
			qU = texture(nr_tex, uv - oy); qD = texture(nr_tex, uv + oy);
		}
		float inner = 0.0;
		if (want_crease) {
			inner = max(max(crease(d, n, d1R, normalize(v2w * (qR.xyz * 2.0 - 1.0)), thr), crease(d, n, d1L, normalize(v2w * (qL.xyz * 2.0 - 1.0)), thr)),
			            max(crease(d, n, d1U, normalize(v2w * (qU.xyz * 2.0 - 1.0)), thr), crease(d, n, d1D, normalize(v2w * (qD.xyz * 2.0 - 1.0)), thr)));
		}
		float outer = 0.0;
		for (int k = 1; k <= 2; k++) {
			if (float(k) > outline_width) break;
			vec2 kx = ox * float(k), ky = oy * float(k);
			float dR = k == 1 ? d1R : lin_depth(raw_depth(uv + kx), uv + kx, ip);
			float dL = k == 1 ? d1L : lin_depth(raw_depth(uv - kx), uv - kx, ip);
			float dU = k == 1 ? d1U : lin_depth(raw_depth(uv - ky), uv - ky, ip);
			float dD = k == 1 ? d1D : lin_depth(raw_depth(uv + ky), uv + ky, ip);
			if (fol_sil) {
				// Leaf-on-leaf depth jumps don't count, only leaves against terrain / sky.
				if (k == 1) {
					dR = is_fol(qR.a) ? d : dR; dL = is_fol(qL.a) ? d : dL; dU = is_fol(qU.a) ? d : dU; dD = is_fol(qD.a) ? d : dD;
				} else {
					dR = is_fol(texture(nr_tex, uv + kx).a) ? d : dR; dL = is_fol(texture(nr_tex, uv - kx).a) ? d : dL;
					dU = is_fol(texture(nr_tex, uv - ky).a) ? d : dU; dD = is_fol(texture(nr_tex, uv + ky).a) ? d : dD;
				}
			}
			outer = max(outer, max(outer_axis(d, dR, dL, thr), outer_axis(d, dU, dD, thr)));
		}
		float inner_fade = clamp(1.6 - tw * 4.0, 0.0, 1.0);
		if (fol) { inner = 0.0; if (foliage_outline < 0.5) outer = 0.0; }
		if (outer > 0.5) {
			col = mix(col, outline_color, outline_outer);
		} else if (inner > normal_threshold) {
			float k = outline_inner * inner_fade;
			if (k >= 0.0) col = mix(col, outline_color, k);
			else col = mix(col, min(col * 1.5 + 0.06, vec3(1.0)), -k);
		}
	}

	// --- Grade ---
	col = (col - 0.5) * contrast + 0.5;
	col *= bright;
	float l = dot(col, vec3(0.299, 0.587, 0.114));
	col = clamp(mix(vec3(l), col, sat), 0.0, 1.0);

	if (tone_mix > 0.0) {
		float lt = dot(col, vec3(0.299, 0.587, 0.114));
		col = mix(col, mix(tone_a, tone_b, lt), tone_mix);
	}
	if (hatch > 0.0) {
		float lh = dot(col, vec3(0.299, 0.587, 0.114));
		float ink = 0.0;
		if (lh < 0.72 && mod(wp.x + wp.y, 4.0) < 1.0) ink = 1.0;
		if (lh < 0.48 && mod(wp.x - wp.y, 4.0) < 1.0) ink = 1.0;
		if (lh < 0.28 && mod(wp.y, 2.0) < 1.0) ink = 1.0;
		if (lh < 0.12) ink = 1.0;
		vec3 drawn = mix(paper, outline_color, ink);
		col = mix(col, drawn * mix(vec3(1.0), col / max(lh, 0.05), 0.15), hatch);
	}
	if (grain > 0.0) {
		vec2 gp = wp + (grain_anim > 0.5 ? floor(TIME * 12.0) * vec2(37.0, 91.0) : vec2(0.0));
		col = clamp(col + (hash2(gp) - 0.5) * grain * 0.22, 0.0, 1.0);
	}
	// --- Ordered dither + palette (one 3D lookup) ---
	if (palette_on) {
		float b = bayer_size < 3.0 ? bayer2(wp) : (bayer_size < 6.0 ? bayer4(wp) : bayer8(wp));
		b -= 0.5 * (1.0 - 1.0 / (bayer_size * bayer_size));
		vec3 c = clamp(col + b * dither_amount * dither_spread, 0.0, 1.0);
		col = texture(palette_lut, c * (31.0 / 32.0) + 0.5 / 32.0).rgb;
	}
	ALBEDO = srgb_to_lin(col);
}
