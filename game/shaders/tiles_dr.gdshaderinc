// The Dwarf Render look for our tiles (terrain_dr and cap_dr share it so the cut surface matches the terrain).
// Tile types index per-tile tables pushed by WorldView (GameLooks): base colour, glyph group; and rows of the kit's
// baked block-texture atlas (BlockLooks ids = TileType values). Needs dr_toon.gdshaderinc included first.

uniform sampler2D dr_atlas : filter_nearest, repeat_disable; // 3 columns (top, side, bottom) x one row per tile type
uniform float dr_tile_px = 8.0;
uniform float dr_tex_strength = 0.85;
uniform vec4 dr_palette[32];   // sRGB base colour; alpha < 1 glows (aetherite)
uniform float dr_group[32];    // glyph group per tile type (what ASCII draws)
uniform float dr_cap_group[32]; // ... on the cut surface (inside rock: cut-rock symbols, not mountains)

// t: tile type; cell: integer tile (Godot axes); world/n: position and face normal; on_cut: the view level's own tiles.
vec3 dr_tile_albedo(int t, vec3 cell, vec3 world, vec3 n, bool on_cut) {
	float jitter = 0.93 + 0.14 * dr_hash3(cell);
	vec3 albedo = dr_palette[t].rgb * jitter;
	if (dr_tex_strength > 0.0 && t != 29 && t != 31) {   // far-view woodland (29) and water (31): no block texture row
		vec3 an = abs(n);
		vec2 fuv; float face = 1.0;
		if (an.y >= an.x && an.y >= an.z) { fuv = world.xz; face = n.y > 0.0 ? 0.0 : 2.0; }
		else if (an.x >= an.z) { fuv = world.zy; }
		else { fuv = world.xy; }
		vec2 f = fract(fuv);
		if (face != 1.0) {                       // tops and bottoms turn a random quarter per tile
			float r = floor(dr_hash3(cell + 5.3) * 4.0);
			if (r == 1.0) f = vec2(f.y, 1.0 - f.x); else if (r == 2.0) f = 1.0 - f; else if (r == 3.0) f = vec2(1.0 - f.y, f.x);
		}
		f.y = 1.0 - f.y;                          // atlas rows are stored top-down
		ivec2 texel = ivec2(min(floor(f * dr_tile_px), vec2(dr_tile_px - 1.0)));
		ivec2 apx = ivec2(int(face * dr_tile_px), t * int(dr_tile_px)) + texel;
		vec3 tex = texelFetch(dr_atlas, apx, 0).rgb;
		albedo = mix(albedo, tex * mix(1.0, jitter, 0.5), dr_tex_strength);
	}
	if (on_cut) albedo = mix(albedo, vec3(1.0), 0.1); // the view level reads a touch lighter
	return dr_srgb_to_linear(clamp(albedo, 0.0, 1.0));
}

vec3 dr_tile_glow(int t, vec3 lin_albedo) {
	return lin_albedo * (1.0 - dr_palette[t].a) * 1.4;
}
