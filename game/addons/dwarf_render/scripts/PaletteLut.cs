using System;
using System.Collections.Generic;
using Godot;

namespace DwarfRender;

/// <summary>
/// Palettes (anchor colours expanded into hue-shifted ramps) and the 32x32x32 lookup
/// texture that maps any colour to its nearest palette entry ("redmean" distance).
/// The post shader then snaps colours with a single 3D texture read.
/// </summary>
public static class PaletteLut
{
	public static readonly Dictionary<PaletteName, string[]> Anchors = new()
	{
		[PaletteName.WarmSnes]       = new[] { "#1e1424", "#6e9a3c", "#8a5232", "#c88a48", "#77707a", "#f4e6c0", "#4f86a0", "#3e6a46", "#4e2e2a", "#ecc878" },
		[PaletteName.AutumnHearth]   = new[] { "#1c100c", "#9a8a3a", "#8a4222", "#e8a640", "#7a6a5e", "#f4d8a0", "#5a7480", "#c86a2a", "#4a2618", "#5a6a3a" },
		[PaletteName.FadedCartridge] = new[] { "#3a3236", "#8e9a72", "#8e7a64", "#bca886", "#9a929a", "#e6dac0", "#7a8a96", "#6a7a6c", "#62544e" },
		[PaletteName.ChunkyRetro]    = new[] { "#1a1018", "#58902c", "#b0602c", "#e8b048", "#7a7480", "#f8ecc8", "#3a6a84", "#5c3020" },
		[PaletteName.PocketMono]     = new[] { "#0f2a1d", "#d6e29a", "#5a7f3a", "#a8c068" },
		[PaletteName.OneBit]         = new[] { "#18141c", "#efe6d2" },
		[PaletteName.PastelClear]    = new[] { "#2a2430", "#9cc0a8", "#e8c9a0", "#d98a6a", "#7fa3c0", "#f3ead2", "#b8a67a", "#a35a4a", "#6f8a5a" },
	};

	static Vector3 Hex(string h) { var c = Color.FromHtml(h); return new Vector3(c.R, c.G, c.B); }
	static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

	/// <summary>Expand a palette's anchors into exactly 'size' colours (sRGB, 0..1).</summary>
	public static List<Vector3> Build(PaletteName name, int size)
	{
		var anchors = Array.ConvertAll(Anchors[name], Hex);
		var shadow = Hex("#2a1430"); var light = Hex("#fff0c8");
		var outList = new List<Vector3>();
		int k = Math.Min(anchors.Length, size), b = size / k, rem = size % k;
		for (int a = 0; a < k; a++)
		{
			var c = anchors[a]; int m = b + (a < rem ? 1 : 0); float step = 0.7f / m;
			for (int i = 0; i < m; i++)
			{
				float t = 0.5f + (i - (m - 1) / 2f) * step;
				outList.Add(t < 0.5f
					? Lerp(Lerp(c * 0.3f, shadow, 0.5f), c, MathF.Pow(t / 0.5f, 0.8f))
					: Lerp(c, light, (t - 0.5f) / 0.5f * 0.75f));
			}
		}
		return outList;
	}

	/// <summary>Bake the lookup texture. Returns (texture, dither spread for the post shader).</summary>
	public static (ImageTexture3D tex, float spread) Bake(PaletteName name, int size, ImageTexture3D reuse = null)
	{
		var pal = Build(name, Math.Clamp(size, 2, 64));
		const int L = 32;
		var layers = new Godot.Collections.Array<Image>();
		var buf = new byte[L * L * 4];
		for (int bz = 0; bz < L; bz++)
		{
			for (int gy = 0; gy < L; gy++)
				for (int rx = 0; rx < L; rx++)
				{
					float r = rx / (L - 1f), g = gy / (L - 1f), bl = bz / (L - 1f);
					int best = 0; float bd = 1e9f;
					for (int i = 0; i < pal.Count; i++)
					{
						var p = pal[i]; float dr = r - p.X, dg = g - p.Y, db = bl - p.Z, rm = (r + p.X) * 0.5f;
						float d = (2 + rm) * dr * dr + 4 * dg * dg + (3 - rm) * db * db;
						if (d < bd) { bd = d; best = i; }
					}
					var c = pal[best]; int o = (rx + L * gy) * 4;
					buf[o] = (byte)Math.Round(Math.Clamp(c.X, 0, 1) * 255); buf[o + 1] = (byte)Math.Round(Math.Clamp(c.Y, 0, 1) * 255);
					buf[o + 2] = (byte)Math.Round(Math.Clamp(c.Z, 0, 1) * 255); buf[o + 3] = 255;
				}
			layers.Add(Image.CreateFromData(L, L, false, Image.Format.Rgba8, buf));
		}
		var tex = reuse ?? new ImageTexture3D();
		if (reuse == null) tex.Create(Image.Format.Rgba8, L, L, L, false, layers);
		else tex.Update(layers);
		return (tex, 0.6f / MathF.Cbrt(pal.Count));
	}
}
