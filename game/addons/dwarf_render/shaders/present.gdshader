// =====================================================================
// Dwarf Render — present pass (port of the diorama's upscale-fs).
// canvas_item shader on a full-screen TextureRect. Per SCREEN pixel:
//   - nearest upscale of the low-res art with sub-texel camera compensation,
//   - magnification (zoomed-in ASCII: pixels grow instead of adding detail),
//   - per-pixel ASCII: one character per art pixel, chosen by object id
//     (ramp or Dwarf-Fortress object symbols), exact brightness match,
//     mip-blended glyph coverage, soft dissolve, dwarf/marker priority,
//   - optional CRT + vignette.
// Texture coordinates are top-left origin, y down (Godot convention).
// =====================================================================
shader_type canvas_item;

uniform sampler2D color_tex : filter_nearest, repeat_disable;   // low-res art (SubViewport "Art")
uniform sampler2D ids_tex : filter_nearest, repeat_disable;     // low-res ids  (SubViewport "Ids"): R id/255, G linear depth
uniform sampler2D glyph_tex : filter_nearest_mipmap, repeat_disable; // 64 x 2048 glyph atlas with mipmaps

uniform vec2 low_res = vec2(320.0, 180.0);  // art target size in texels (includes margin)
uniform vec2 screen_size = vec2(1280.0, 720.0);
uniform float pps = 4.0;                    // screen px per art pixel (after magnification)
uniform vec2 sub = vec2(0.0);               // sub-texel camera remainder, already in texture axes (x right, y down)
uniform float crt = 0.0;
uniform float vignette = 0.0;

uniform float glyph_amt = 0.0;              // 0..1 share of pixels drawn as characters
uniform vec2 off_full = vec2(0.0);          // whole-texel camera offset (world-locks the reveal order)
uniform float style = 0.0;                  // 0 ramps, 1 objects (Dwarf Fortress)
uniform float density_gamma = 0.8;
uniform float invert = 0.0;
uniform float color_amt = 1.0;
uniform float bg_tint = 0.12;
uniform float boost = 1.3;
uniform float edges_on = 1.0;
uniform float edge_thr = 0.7;
uniform float materials_on = 1.0;
uniform float match_amt = 1.0;
uniform float soft = 0.6;
uniform float is_ortho = 1.0;
uniform vec3 mono_color = vec3(0.62, 0.91, 0.44);
uniform vec3 bg_color = vec3(0.043, 0.039, 0.051);
uniform int marker_count = 0;
uniform vec4 markers[64];                   // xy = art-pixel coords (top-left origin), z = view depth, w = glyph group
uniform vec3 marker_colors[64];

const float WATER_GROUP = 27.0;             // glyph group used for water (ripples); keep in sync with GlyphAtlas.cs

float h21(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }
vec3 tap(vec2 t) {
	ivec2 p = clamp(ivec2(floor(t)), ivec2(0), ivec2(low_res) - 1);
	return texelFetch(color_tex, p, 0).rgb;
}
vec3 ids3(vec2 cell) {
	ivec2 p = clamp(ivec2(cell), ivec2(0), ivec2(low_res) - 1);
	return texelFetch(ids_tex, p, 0).rgb;
}
vec2 ids(vec2 cell) { return ids3(cell).rg; }

vec3 ascii_pixel(vec2 t, vec3 c) {
	vec2 cell = floor(t);
	vec3 idd = ids3(cell);
	bool sky = idd.g <= 0.0;
	// Per-tile randomness (the ids pass hashes the world tile under the pixel): a tile keeps its symbol variant and
	// its place in the dissolve however the camera turns or zooms. The sky has no tile: it hashes the screen cell.
	float th = sky ? h21(cell + off_full) : idd.b;
	float h = fract(th * 7.31 + 0.137) * (1.0 - soft);
	float k = smoothstep(h, h + soft + 1e-4, glyph_amt);
	if (k <= 0.0) return c;
	float id = floor(idd.r * 255.0 + 0.5);
	float dc = idd.g;
	// Rows: ramp style = group id (row 0 = sky), 127 = edge strokes; object style = 128 + group id.
	float row = (materials_on > 0.5 || sky) ? id : 16.0;
	float orow = 128.0 + id;
	float lum = luma(c);
	float lt = invert > 0.5 ? 1.0 - lum : lum;
	float lv = clamp(floor(pow(clamp(lt, 0.0, 1.0), density_gamma) * 8.0), 0.0, 7.0);
	bool water = abs(id - WATER_GROUP) < 0.5;
	if (water) lv = clamp(lv + floor(h21(vec2(th * 977.0, floor(TIME * 3.0 + th * 5.0))) * 3.0) - 1.0, 1.0, 7.0);
	if (style > 0.5) {
		row = orow;
		lv = floor(fract(th + (water ? floor(TIME * 2.0 + th * 3.0) * 0.377 : 0.0)) * 8.0);
	}
	bool edge = false;
	if (style < 0.5 && edges_on > 0.5 && !sky) {
		float dl = ids(cell - vec2(1.0, 0.0)).g, dr = ids(cell + vec2(1.0, 0.0)).g;
		float du = ids(cell - vec2(0.0, 1.0)).g, dd = ids(cell + vec2(0.0, 1.0)).g;
		dl = dl <= 0.0 ? 1e4 : dl; dr = dr <= 0.0 ? 1e4 : dr; du = du <= 0.0 ? 1e4 : du; dd = dd <= 0.0 ? 1e4 : dd;
		float lx = abs(dr + dl - 2.0 * dc), ly = abs(du + dd - 2.0 * dc);
		float thr = edge_thr * (is_ortho > 0.5 ? 1.0 : max(dc, 1.0) * 0.015);
		if (max(lx, ly) > thr) {
			edge = true; row = 127.0;
			float gx = dr - dl, gy = dd - du;            // y down
			if (lx > ly * 2.0) lv = 0.0; else if (ly > lx * 2.0) lv = 2.0; else lv = (gx * gy > 0.0) ? 3.0 : 1.0;
		}
	}
	// Marker priority (dwarves etc.): a cell containing a visible marker shows its symbol.
	bool mark = false; vec3 mcol = vec3(0.0);
	for (int i = 0; i < 64; i++) {
		if (i >= marker_count) break;
		vec4 m = markers[i];
		if (floor(m.x) == cell.x && floor(m.y) == cell.y && (sky || m.z < dc + 1.2)) { mark = true; mcol = marker_colors[i]; row = 128.0 + m.w; lv = 0.0; edge = false; }
	}
	// Mip-blended glyph coverage + exact brightness match.
	vec2 atlas = vec2(64.0, 2048.0);
	float lod = clamp(log2(8.0 / pps), 0.0, 3.0);
	vec2 f = fract(t);
	float on  = textureLod(glyph_tex, (vec2(lv, row) * 8.0 + f * 8.0) / atlas, lod).r;
	float cov = textureLod(glyph_tex, (vec2(lv, row) * 8.0 + 4.0) / atlas, 3.0).r;
	vec3 G = mix(mono_color, c * boost, color_amt) * (edge ? 1.15 : 1.0);
	vec3 B = bg_color + c * bg_tint;
	float lc = luma(c), lg = max(luma(G), 1e-4), lb = luma(B);
	vec3 Ge = G * clamp((lc - (1.0 - cov) * lb) / max(cov * lg, 1e-4), 0.0, 64.0);
	float mx = max(max(Ge.r, Ge.g), Ge.b);
	if (mx > 1.0) Ge /= mx;
	float need = (lc - cov * luma(Ge)) / max(1.0 - cov, 1e-4);
	vec3 Be = lb > 1e-3 ? B * clamp(need / lb, 0.0, 64.0) : c * clamp(need / max(lc, 1e-4), 0.0, 64.0);
	G = mix(G, Ge, match_amt); B = mix(B, clamp(Be, 0.0, 1.0), match_amt);
	if (mark) { G = min(mcol * 1.35 + 0.08, vec3(1.0)); B = c * 0.35; }
	return mix(c, mix(B, G, on), k);
}

void fragment() {
	vec2 sp = UV;
	if (crt > 0.0) {
		vec2 cc = sp * 2.0 - 1.0;
		cc *= 1.0 + crt * 0.07 * dot(cc, cc);
		sp = cc * 0.5 + 0.5;
		if (any(lessThan(sp, vec2(0.0))) || any(greaterThan(sp, vec2(1.0)))) { COLOR = vec4(0.0, 0.0, 0.0, 1.0); }
	}
	vec2 frag = sp * screen_size;
	vec2 t = low_res * 0.5 + (frag - screen_size * 0.5) / pps + sub;
	vec3 col;
	if (crt > 0.0 && glyph_amt <= 0.0) {
		float ca = crt * 0.8 / pps;
		col = vec3(tap(t + vec2(ca, 0.0)).r, tap(t).g, tap(t - vec2(ca, 0.0)).b);
	} else {
		col = tap(t);
	}
	if (glyph_amt > 0.0) col = ascii_pixel(t, col);
	if (crt > 0.0) {
		float scan = mix(1.0, 0.45 + 0.55 * sin(fract(t.y) * 3.14159), crt * (1.0 - glyph_amt));
		float m = mod(floor(frag.x), 3.0);
		vec3 mask = mix(vec3(1.0), vec3(m < 0.5 ? 1.0 : 0.72, abs(m - 1.0) < 0.5 ? 1.0 : 0.72, m > 1.5 ? 1.0 : 0.72), crt * 0.6);
		vec3 glow = (tap(t + vec2(1.0, 0.0)) + tap(t - vec2(1.0, 0.0)) + tap(t + vec2(0.0, 1.0)) + tap(t - vec2(0.0, 1.0))) * 0.25;
		col = (col * scan * mask + glow * 0.18 * crt) * (1.0 + 0.3 * crt);
	}
	if (vignette > 0.0) {
		vec2 cc = UV * 2.0 - 1.0;
		col *= mix(1.0, smoothstep(1.45, 0.35, length(cc * vec2(1.0, 0.85))), vignette);
	}
	if (crt > 0.0 && (any(lessThan(sp, vec2(0.0))) || any(greaterThan(sp, vec2(1.0))))) col = vec3(0.0);
	COLOR = vec4(col, 1.0);
}
