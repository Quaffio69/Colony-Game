using System;
using System.Collections.Generic;
using Godot;

namespace DwarfRender;

/// <summary>How a block surface is textured by the procedural texture packs.</summary>
public enum TileRecipe { Noise, Grass, Dirt, Path, Sand, Stone, DeepStone, Ore, Wood, Plank, Brick, Ember, Water, Metal }

/// <summary>Material class used by the terrain shader (jitter, grass lip, ore flecks, water glints).</summary>
public enum SurfaceClass { Generic = 0, Water = 1, Prop = 2, Grass = 3, Ore = 4 }

/// <summary>Everything the renderer needs to know about one block type.</summary>
public record BlockLook(
	string Name,
	Color Color,                 // base colour, sRGB
	TileRecipe Recipe,
	int GlyphGroup,              // GlyphAtlas group id (what ASCII shows)
	SurfaceClass Class = SurfaceClass.Generic,
	bool Emissive = false,
	bool Solid = true,           // false = air-like (never meshed)
	bool Opaque = true,          // false = water/glass: neighbours still get faces, no AO from it
	bool Roundable = true);      // edge rounding applies

/// <summary>
/// Registry: your game's block type id -> BlockLook. Block id 0 is always air.
/// Call Register(...) for every block type at startup (before the first chunk is meshed),
/// or edit RegisterDiorama() to match your block list. Blocks you don't register get a
/// magenta "missing" look so they are easy to spot.
/// </summary>
public static class BlockLooks
{
	static readonly Dictionary<int, BlockLook> _looks = new();
	static readonly BlockLook Missing = new("missing", new Color(1, 0, 1), TileRecipe.Noise, GlyphAtlas.Generic);
	public static int MaxId { get; private set; }

	public static void Register(int id, BlockLook look) { _looks[id] = look; MaxId = Math.Max(MaxId, id); TextureAtlas.Invalidate(); }
	public static BlockLook Get(int id) => id == 0 ? null : _looks.TryGetValue(id, out var l) ? l : Missing;
	public static bool IsSolid(int id) => id != 0 && (Get(id)?.Solid ?? false);
	public static bool IsOpaque(int id) => IsSolid(id) && Get(id).Opaque;

	/// <summary>The diorama's 19 block types, as a starting point / example.</summary>
	public static void RegisterDiorama()
	{
		void R(int id, string name, string hex, TileRecipe rec, int group, SurfaceClass cls = SurfaceClass.Generic, bool emissive = false, bool opaque = true, bool round = true)
			=> Register(id, new BlockLook(name, Color.FromHtml(hex), rec, group, cls, emissive, true, opaque, round));
		R(1,  "grass",   "#6f9a3a", TileRecipe.Grass,     GlyphAtlas.Grass,     SurfaceClass.Grass);
		R(2,  "dirt",    "#8a5a34", TileRecipe.Dirt,      GlyphAtlas.Dirt);
		R(3,  "path",    "#b48a58", TileRecipe.Path,      GlyphAtlas.Path);
		R(4,  "stone",   "#77706a", TileRecipe.Stone,     GlyphAtlas.Stone);
		R(5,  "deep",    "#4d4650", TileRecipe.DeepStone, GlyphAtlas.DeepStone);
		R(6,  "copper",  "#d0783a", TileRecipe.Ore,       GlyphAtlas.Ore,       SurfaceClass.Ore);
		R(7,  "gold",    "#f0c440", TileRecipe.Ore,       GlyphAtlas.Ore,       SurfaceClass.Ore);
		R(8,  "iron",    "#c89a8a", TileRecipe.Ore,       GlyphAtlas.Ore,       SurfaceClass.Ore);
		R(9,  "wood",    "#6a4428", TileRecipe.Wood,      GlyphAtlas.Wood);
		R(10, "leaves",  "#4f7a32", TileRecipe.Noise,     GlyphAtlas.Oak);
		R(11, "water",   "#4f86a0", TileRecipe.Water,     GlyphAtlas.Water,     SurfaceClass.Water, opaque: false, round: false);
		R(12, "rock",    "#8a8378", TileRecipe.Stone,     GlyphAtlas.Boulder);
		R(13, "bush",    "#5e8a34", TileRecipe.Noise,     GlyphAtlas.Bush);
		R(14, "plank",   "#a8763c", TileRecipe.Plank,     GlyphAtlas.Plank);
		R(15, "brick",   "#8a3c2a", TileRecipe.Brick,     GlyphAtlas.Brick);
		R(16, "ember",   "#ff8a2a", TileRecipe.Ember,     GlyphAtlas.Ember,     SurfaceClass.Prop, emissive: true);
		R(17, "sand",    "#d8c08a", TileRecipe.Sand,      GlyphAtlas.Sand);
		R(18, "metal",   "#46424a", TileRecipe.Metal,     GlyphAtlas.Metal,     SurfaceClass.Prop);
		R(19, "crystal", "#7fe0d8", TileRecipe.Noise,     GlyphAtlas.Crystal,   SurfaceClass.Prop, emissive: true);
	}
}

/// <summary>Procedural low-res texture packs baked into one atlas (columns: top, side, bottom; row = block id).</summary>
public static class TextureAtlas
{
	public record Pack(int T, float Noise, float Contrast, float Sat, float Lift, float Moss, bool Bevel, bool Cobble, bool Flowers);
	public static readonly Dictionary<TexturePackName, Pack> Packs = new()
	{
		[TexturePackName.HandPainted8] = new(8, 0.10f, 1.0f, 1.0f, 0f, 0f, false, false, true),
		[TexturePackName.Crisp16]      = new(16, 0.13f, 1.25f, 1.05f, 0f, 0f, false, false, true),
		[TexturePackName.MossyRuins]   = new(8, 0.12f, 1.1f, 0.85f, -0.03f, 0.55f, false, true, false),
		[TexturePackName.PastelTiles]  = new(8, 0.04f, 0.75f, 0.8f, 0.07f, 0f, true, false, true),
		[TexturePackName.Chunky4]      = new(4, 0.16f, 1.35f, 1.1f, 0f, 0f, false, false, false),
		[TexturePackName.Off]          = new(4, 0f, 1f, 1f, 0f, 0f, false, false, false),
	};

	static ImageTexture _tex; static TexturePackName _texPack;
	public static void Invalidate() => _tex = null;

	public static ImageTexture Get(TexturePackName pack, out int tilePx)
	{
		tilePx = Packs[pack].T;
		if (_tex == null || _texPack != pack) { _tex = Bake(pack); _texPack = pack; }
		return _tex;
	}

	static Vector3 V(Color c) => new(c.R, c.G, c.B);
	static Vector3 Hex(string h) => V(Color.FromHtml(h));

	static ImageTexture Bake(TexturePackName packName)
	{
		var P = Packs[packName]; int T = P.T, rows = BlockLooks.MaxId + 1, W = 3 * T, H = rows * T;
		var img = Image.CreateEmpty(W, Math.Max(H, T), false, Image.Format.Rgba8);
		img.Fill(Colors.White);
		if (packName == TexturePackName.Off) return ImageTexture.CreateFromImage(img);

		float th(int i, int j, int s) { double v = Math.Sin(i * 127.1 + j * 311.7 + s * 74.7 + T * 3.1) * 43758.5453; return (float)(v - Math.Floor(v)); }
		Vector3 sc(Vector3 c, float k) => c * k;
		Vector3 nz(Vector3 c, int i, int j, int s, float amt = 1) => c * (1 + (th(i, j, s) - 0.5f) * 2 * P.Noise * amt);
		float vn(float x, float y, int s)
		{
			int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y); float fx = x - xi, fy = y - yi;
			float g(int a, int b) => th(((a % T) + T) % T, ((b % T) + T) % T, s);
			return Mathf.Lerp(Mathf.Lerp(g(xi, yi), g(xi + 1, yi), fx), Mathf.Lerp(g(xi, yi + 1), g(xi + 1, yi + 1), fx), fy);
		}
		var MOSS = Hex("#5d7a35"); var MORTAR = Hex("#7a6c5e"); var GRAVEL = Hex("#a89c88"); var HOST = Hex("#6f6a66");
		int q = Math.Max(1, (int)Math.Round(T / 8f));
		HashSet<(int, int)> Crack(int s)
		{
			var m = new HashSet<(int, int)>(); int x = (int)(th(1, 2, s) * T), y = (int)(th(3, 4, s) * T);
			for (int k = 0; k < T * 0.9f; k++) { m.Add((((x % T) + T) % T, ((y % T) + T) % T)); float r = th(k, s, 9); x += r < 0.5f ? 1 : 0; y += r < 0.5f ? 0 : 1; if (th(k, s, 10) < 0.25f) x -= 1; }
			return m;
		}
		Vector3 StoneLike(Vector3 b, int i, int j, int s, int f)
		{
			var c = nz(b, i, j, s);
			if (P.Cobble)
			{
				int cs = Math.Max(2, T / 2), row = j / cs, off = (row % 2) * cs / 2, ci = (i + off) % cs, cj = j % cs;
				if (ci == 0 || cj == 0) c = sc(b, 0.62f); else if (ci == 1 || cj == 1) c = sc(c, 1.12f);
				else c = sc(c, 0.94f + 0.12f * th((i + off) / cs, row, s + 5));
			}
			else
			{
				var cr = Crack(s + f);
				if (cr.Contains((i, j))) c = sc(b, 0.68f); else if (cr.Contains((i + 1, j + 1))) c = sc(b, 1.14f); else if (th(i, j, s + 7) < 0.07f) c = sc(b, 1.12f);
			}
			if (P.Moss > 0)
			{
				float mm = vn(i / (2f * q), j / (2f * q), s + 11);
				if (f == 0 && mm > 1 - P.Moss) c = nz(MOSS, i, j, s + 12);
				if (f == 1 && j < Math.Round(T * 0.18f) + Math.Floor(th(i, 0, s + 13) * T * 0.35f * P.Moss * 2) && mm > 0.35f) c = nz(MOSS, i, j, s + 12);
			}
			return c;
		}
		Vector3 DirtLike(Vector3 b, int i, int j, int s) { var c = nz(b, i, j, s); float r = th(i, j, s + 1); return r < 0.08f ? sc(b, 0.72f) : r < 0.14f ? sc(b, 1.2f) : c; }
		Vector3 GrassTop(Vector3 b, int i, int j, int s)
		{
			var c = nz(b, i, j, s); float r = th(i, j, s + 1);
			if (r < 0.16f) c = sc(b, 1.2f); else if (r < 0.3f) c = sc(b, 0.82f);
			if (P.Flowers && th(i, j, s + 2) < 0.03f) c = th(i, j, s + 3) < 0.5f ? Hex("#f0e070") : Hex("#e890a8");
			if (P.Moss > 0 && vn(i / 2f, j / 2f, s + 4) > 0.7f) c = sc(c, 0.85f);
			return c;
		}
		Vector3 Tile(int t, BlockLook L, int f, int i, int j)
		{
			var b = V(L.Color); int s = t * 13 + f * 3;
			switch (L.Recipe)
			{
				case TileRecipe.Grass:
				{
					if (f == 0) return GrassTop(b, i, j, s);
					var dirt = Hex("#8a5a34");
					if (f == 2) return DirtLike(dirt, i, j, s);
					int lip = Math.Max(1, (int)Math.Round(T * 0.22f)) + (int)Math.Floor(th(i, 0, s + 6) * (T * 0.22f + 1));
					return j < lip ? (j == lip - 1 ? sc(b, 0.78f) : GrassTop(b, i, j, s)) : DirtLike(dirt, i, j, s);
				}
				case TileRecipe.Dirt: return DirtLike(b, i, j, s);
				case TileRecipe.Path: { var c = nz(b, i, j, s, 0.6f); float r = th(i, j, s + 1); return r < 0.12f ? nz(GRAVEL, i, j, s + 2) : r < 0.2f ? sc(b, 0.85f) : c; }
				case TileRecipe.Stone: return StoneLike(b, i, j, s, f);
				case TileRecipe.DeepStone:
				{
					var c = StoneLike(b, i, j, s, f);
					if (f == 1 && (j + (int)Math.Floor(th(i, 0, s) * 2)) % Math.Max(3, (int)Math.Round(T / 2f)) == 0) c = sc(b, 0.76f);
					return c;
				}
				case TileRecipe.Ore:
				{
					var c = StoneLike(HOST, i, j, s, f);
					for (int k = 0; k < 3; k++)
					{
						float cx = th(k, 1, s + t) * T, cy = th(k, 2, s + t) * T, r = T * (0.14f + 0.08f * th(k, 3, s));
						float dx = i + 0.5f - cx, dy = j + 0.5f - cy, d = MathF.Sqrt(dx * dx + dy * dy);
						if (d < r) c = d < r * 0.5f && dx < 0 && dy < 0 ? sc(b, 1.3f) : (dx > 0 && dy > 0 && d > r * 0.6f ? sc(b, 0.72f) : nz(b, i, j, s + k));
					}
					return c;
				}
				case TileRecipe.Wood:
				{
					if (f == 1) { var c = sc(b, 0.9f + 0.2f * th(i / q, 0, s)); if (th(i, j / (2 * q), s + 1) < 0.15f) c = sc(b, 0.7f); return c; }
					float d = MathF.Sqrt((i + 0.5f - T / 2f) * (i + 0.5f - T / 2f) + (j + 0.5f - T / 2f) * (j + 0.5f - T / 2f));
					return sc(Hex("#b08a5a"), ((int)(d / q)) % 2 == 1 ? 0.85f : 1.0f);
				}
				case TileRecipe.Plank:
				{
					int bh = Math.Max(2, (int)Math.Round(T / 4f)), r = j / bh;
					if (j % bh == bh - 1) return sc(b, 0.6f);
					if ((i + (int)Math.Floor(th(r, 0, s) * T)) % T == 0) return sc(b, 0.65f);
					return sc(nz(b, i, j, s, 0.5f), th(i / (2 * q), r, s + 2) < 0.3f ? 0.9f : 1.0f);
				}
				case TileRecipe.Brick:
				{
					int rh = Math.Max(2, (int)Math.Round(T / 4f)), r = j / rh, bw = Math.Max(2, T / 2);
					if (j % rh == rh - 1 || (i + (r % 2) * bw / 2) % bw == 0) return nz(MORTAR, i, j, s, 0.5f);
					return sc(nz(b, i, j, s, 0.6f), 0.88f + 0.24f * th((i + (r % 2) * bw / 2) / bw, r, s + 1));
				}
				case TileRecipe.Ember: { float n = vn(i / (float)q, j / (float)q, s); return n < 0.25f ? Hex("#8a2a10") : b.Lerp(Hex("#ffe070"), n); }
				case TileRecipe.Sand: { var c = nz(b, i, j, s, 0.7f); return th(i, j, s + 1) < 0.1f ? sc(b, 1.12f) : th(i, j, s + 2) < 0.06f ? sc(b, 0.86f) : c; }
				case TileRecipe.Water: { var c = nz(b, i, j, s, 0.4f); return th((i + j * 2) / 2, j / 2, s) < 0.12f ? sc(b, 1.3f) : c; }
				case TileRecipe.Metal:
				{
					float e = (i == 0 || j == 0) ? 1.45f : (i == T - 1 || j == T - 1) ? 0.7f : 1.0f;
					bool rv = (i == q || i == T - 1 - q) && (j == q || j == T - 1 - q);
					return sc(nz(b, i, j, s, 0.5f), rv ? 1.7f : e);
				}
				default: return nz(b, i, j, s);
			}
		}

		var px = new Vector3[T * T];
		for (int t = 1; t < rows; t++)
		{
			var L = BlockLooks.Get(t); if (L == null) continue;
			for (int f = 0; f < 3; f++)
			{
				var mean = Vector3.Zero;
				for (int j = 0; j < T; j++) for (int i = 0; i < T; i++) { var c = Tile(t, L, f, i, j); px[j * T + i] = c; mean += c / (T * T); }
				for (int j = 0; j < T; j++)
					for (int i = 0; i < T; i++)
					{
						var c = px[j * T + i];
						c = mean + (c - mean) * P.Contrast;                                       // contrast around the tile's own mean
						float l = c.X * 0.299f + c.Y * 0.587f + c.Z * 0.114f;
						c = new Vector3(l, l, l) + (c - new Vector3(l, l, l)) * P.Sat + new Vector3(P.Lift, P.Lift, P.Lift);
						if (P.Bevel) { if (i == 0 || j == 0) c *= 1.14f; else if (i == T - 1 || j == T - 1) c *= 0.8f; }
						img.SetPixel(f * T + i, t * T + j, new Color(Math.Clamp(c.X, 0, 1), Math.Clamp(c.Y, 0, 1), Math.Clamp(c.Z, 0, 1)));   // row j=0 = top of tile
					}
			}
		}
		return ImageTexture.CreateFromImage(img);
	}
}
