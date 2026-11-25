using System.Collections.Generic;
using Godot;

namespace DwarfRender;

/// <summary>
/// ASCII glyphs: a hand-drawn 5x7 font (drawn in 8x8 cells) plus "glyph groups".
/// A glyph group is what a pixel IS for ASCII purposes (grass, stone, dwarf, an item...).
/// Every block, object and creature maps to one group id (0..126), written by its shader
/// into the roughness channel and read back by the present pass.
///
/// Each group has two 8-character strings:
///   Ramp   = characters from sparse to dense, picked by pixel brightness (zoomed-in look)
///   Object = Dwarf Fortress-style symbols, picked by position only (zoomed-out look).
///            Repeats weight the mix, e.g. "♣♣♣♣♣♣♣τ" = mostly ♣, sometimes τ.
///
/// Atlas layout (64 x 2048, mipmapped): column = character index 0..7,
/// row = group id (ramp style), 127 = edge strokes, 128 + group id (object style).
/// </summary>
public static class GlyphAtlas
{
	// ---- Reserved group ids (keep in sync with the shaders) -----------------
	public const int Sky = 0;
	public const int Oak = 1, Pine = 2, Birch = 3, Bush = 4;          // foliage (outline pass treats 1..4 as leaves)
	public const int Prop = 5, Dwarf = 6, Trunk = 7, Torch = 8, Creature = 9, Item = 10;
	public const int Mist = 11;                                        // haze (waterfall mist): the outline pass skips it
	public const int Generic = 16;
	public const int Grass = 17, Dirt = 18, Path = 19, Sand = 20, Stone = 21, DeepStone = 22, Boulder = 23,
	                 Ore = 24, Wood = 25, Plank = 26, Water = 27, Brick = 28, Ember = 29, Metal = 30, Crystal = 31;
	public const int FirstCustomGroup = 32, MaxGroup = 63, EdgeRow = 127; // the roughness channel carries ids 0..63

	public record Group(string Name, string Ramp, string Object);

	static readonly Dictionary<int, Group> _groups = new()
	{
		[Sky]       = new("sky",      "    . .`", "        "),
		[Oak]       = new("oak",      " .,*&%8@", "♣♣♣♣♣♣♣♣"),
		[Pine]      = new("pine",     " .,*&%8@", "♠♠♠♠♠♠♠♠"),
		[Birch]     = new("birch",    " .,*&%8@", "♣♣♣♣♣♣♣τ"),
		[Bush]      = new("bush",     " .,*&%8@", "\"\"\"\"\"\"\"\""),
		[Prop]      = new("prop",     " .:oO0@@", "oooooooo"),
		[Dwarf]     = new("dwarf",    " .:oO0@@", "☺☺☺☺☺☺☺☺"),
		[Trunk]     = new("trunk",    " .-==#HH", "OOOOOOOO"),
		[Torch]     = new("torch",    " .'^*%#@", "!!!!!!!!"),
		[Creature]  = new("creature", " .:oO0@@", "MMMMMMMM"),
		[Item]      = new("item",     " .:*o%$@", "********"),
		[Mist]      = new("mist",     "  ..::~~", "~~~~~~~~"),
		[Generic]   = new("generic",  " .:-=+#@", "########"),
		[Grass]     = new("grass",    " .,'\"vwW", "`,.\",.',"),
		[Dirt]      = new("dirt",     " .,:;+o%", ".,.,.,`."),
		[Path]      = new("path",     " ..,:;+o", ".:.:.,.:"),
		[Sand]      = new("sand",     " ..,:;+o", ".,`.,.`,"),
		[Stone]     = new("stone",    " .:+=#%@", "▒▒▒▒▒▒▒▒"),
		[DeepStone] = new("deep",     " .:+=#%@", "▓▓▓▓▓▓▓▓"),
		[Boulder]   = new("boulder",  " .:+=#%@", "••••••••"),
		[Ore]       = new("ore",      " .*o$%0@", "££££££££"),
		[Wood]      = new("wood",     " .-==#HH", "OOOOOOOO"),
		[Plank]     = new("plank",    " .-==#HH", "++++++++"),
		[Water]     = new("water",    " .-~~~==", "≈≈≈~≈≈≈~"),
		[Brick]     = new("brick",    " .-==#H8", "########"),
		[Ember]     = new("ember",    " .'^*%#@", "☼☼☼☼☼☼☼☼"),
		[Metal]     = new("metal",    " .-+=#HM", "■■■■■■■■"),
		[Crystal]   = new("crystal",  " .*o$%0@", "*☼**☼***"),
	};
	const string EdgeStrokes = "|/-\\    ";

	/// <summary>Register (or override) a glyph group, e.g. for your game's items or creatures.
	/// Use ids from FirstCustomGroup (32) to MaxGroup (126). Characters must exist in Font (add any you need).</summary>
	public static void RegisterGroup(int id, string name, string ramp, string objectSymbols)
	{
		if (id < 0 || id > MaxGroup) { GD.PushError($"Glyph group id {id} out of range 0..{MaxGroup}"); return; }
		_groups[id] = new Group(name, Pad8(ramp), Pad8(objectSymbols));
		_texture = null;
	}
	public static IReadOnlyDictionary<int, Group> Groups => _groups;

	static string Pad8(string s)
	{
		var list = new List<string>(Enumerate(System.Globalization.StringInfo.GetTextElementEnumerator(s ?? "")));
		while (list.Count < 8) list.Add(list.Count > 0 ? list[^1] : " ");
		return string.Concat(list.GetRange(0, 8));
	}
	static IEnumerable<string> Enumerate(System.Globalization.TextElementEnumerator e) { while (e.MoveNext()) yield return e.GetTextElement(); }
	static List<string> Chars(string s) { var l = new List<string>(Enumerate(System.Globalization.StringInfo.GetTextElementEnumerator(s))); while (l.Count < 8) l.Add(" "); return l; }

	/// <summary>The 5x7 font. '#' = ink. Add glyphs here if your groups need more characters.</summary>
	public static readonly Dictionary<string, string[]> Font = new()
	{
		{ " ", new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." } },
		{ ".", new[] { ".....", ".....", ".....", ".....", ".....", ".....", "..#.." } },
		{ ",", new[] { ".....", ".....", ".....", ".....", ".....", "..#..", ".#..." } },
		{ "'", new[] { "..#..", "..#..", ".#...", ".....", ".....", ".....", "....." } },
		{ "`", new[] { ".#...", "..#..", ".....", ".....", ".....", ".....", "....." } },
		{ "\"", new[] { ".#.#.", ".#.#.", ".....", ".....", ".....", ".....", "....." } },
		{ ":", new[] { ".....", ".....", "..#..", ".....", ".....", "..#..", "....." } },
		{ ";", new[] { ".....", ".....", "..#..", ".....", ".....", "..#..", ".#..." } },
		{ "-", new[] { ".....", ".....", ".....", ".###.", ".....", ".....", "....." } },
		{ "_", new[] { ".....", ".....", ".....", ".....", ".....", ".....", "#####" } },
		{ "=", new[] { ".....", ".....", "#####", ".....", "#####", ".....", "....." } },
		{ "+", new[] { ".....", "..#..", "..#..", "#####", "..#..", "..#..", "....." } },
		{ "*", new[] { ".....", "..#..", "#.#.#", ".###.", "#.#.#", "..#..", "....." } },
		{ "^", new[] { "..#..", ".#.#.", "#...#", ".....", ".....", ".....", "....." } },
		{ "~", new[] { ".....", ".....", ".#...", "#.#.#", "...#.", ".....", "....." } },
		{ "|", new[] { "..#..", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.." } },
		{ "/", new[] { "....#", "....#", "...#.", "..#..", ".#...", "#....", "#...." } },
		{ "\\", new[] { "#....", "#....", ".#...", "..#..", "...#.", "....#", "....#" } },
		{ "v", new[] { ".....", ".....", "#...#", "#...#", ".#.#.", ".#.#.", "..#.." } },
		{ "w", new[] { ".....", ".....", "#...#", "#...#", "#.#.#", "#.#.#", ".#.#." } },
		{ "W", new[] { "#...#", "#...#", "#...#", "#.#.#", "#.#.#", "##.##", "#...#" } },
		{ "o", new[] { ".....", ".....", ".###.", "#...#", "#...#", "#...#", ".###." } },
		{ "O", new[] { ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." } },
		{ "0", new[] { ".###.", "#..##", "#.#.#", "#.#.#", "#.#.#", "##..#", ".###." } },
		{ "8", new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." } },
		{ "#", new[] { ".#.#.", "#####", ".#.#.", ".#.#.", "#####", ".#.#.", "....." } },
		{ "%", new[] { "##..#", "##..#", "...#.", "..#..", ".#...", "#..##", "#..##" } },
		{ "&", new[] { ".##..", "#..#.", "#.#..", ".#...", "#.#.#", "#..#.", ".##.#" } },
		{ "$", new[] { "..#..", ".####", "#.#..", ".###.", "..#.#", "####.", "..#.." } },
		{ "@", new[] { ".###.", "#...#", "#.###", "#.#.#", "#.###", "#....", ".###." } },
		{ "H", new[] { "#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" } },
		{ "M", new[] { "#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#" } },
		{ "!", new[] { "..#..", "..#..", "..#..", "..#..", "..#..", ".....", "..#.." } },
		{ "☺", new[] { ".###.", "#####", "#.#.#", "#####", "#...#", ".###.", "....." } },
		{ "♣", new[] { ".###.", ".###.", "##.##", "#####", "##.##", "..#..", ".###." } },
		{ "♠", new[] { "..#..", ".###.", "#####", "#####", "#.#.#", "..#..", ".###." } },
		{ "≈", new[] { ".....", ".#...", "#.#.#", "...#.", ".#...", "#.#.#", "...#." } },
		{ "▒", new[] { "#.#.#", ".#.#.", "#.#.#", ".#.#.", "#.#.#", ".#.#.", "#.#.#" } },
		{ "▓", new[] { "####.", "#.###", "###.#", ".####", "##.##", "####.", "#.###" } },
		{ "•", new[] { ".....", ".....", ".###.", ".###.", ".###.", ".....", "....." } },
		{ "£", new[] { "..##.", ".#..#", ".#...", "###..", ".#...", ".#..#", "####." } },
		{ "☼", new[] { "..#..", "#.#.#", ".###.", "##.##", ".###.", "#.#.#", "..#.." } },
		{ "■", new[] { ".....", "#####", "#####", "#####", "#####", "#####", "....." } },
		{ "τ", new[] { ".....", ".....", "#####", "..#..", "..#..", "..#..", "...##" } },
	};

	static ImageTexture _texture;

	/// <summary>The baked, mipmapped atlas (64 x 2048). Rebuilt lazily after RegisterGroup.</summary>
	public static ImageTexture Texture => _texture ??= Bake();

	static ImageTexture Bake()
	{
		const int W = 64, H = 256 * 8;
		var img = Image.CreateEmpty(W, H, false, Image.Format.Rgba8);
		img.Fill(new Color(0, 0, 0, 0));
		void Draw(int row, List<string> chars)
		{
			for (int lv = 0; lv < 8; lv++)
			{
				if (!Font.TryGetValue(chars[lv], out var g)) { if (chars[lv] != " ") GD.PushWarning($"GlyphAtlas: no font glyph for '{chars[lv]}'"); continue; }
				for (int gy = 0; gy < 7; gy++)
					for (int gx = 0; gx < 5; gx++)
						if (g[gy][gx] == '#') img.SetPixel(lv * 8 + 1 + gx, row * 8 + gy, Colors.White);   // 1px left margin, top-down
			}
		}
		foreach (var (id, grp) in _groups)
		{
			Draw(id, Chars(grp.Ramp));
			Draw(128 + id, Chars(grp.Object));
		}
		Draw(EdgeRow, Chars(EdgeStrokes));
		img.GenerateMipmaps();   // 8x8 tiles stay aligned down to 1x1 at mip 3 -> per-glyph ink coverage
		return ImageTexture.CreateFromImage(img);
	}
}
