using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace ColonyGame.UI;

/// <summary>
/// Drawing helpers shared by the Glyph controls: CP437 glyphs from the 5x7 bitmaps in glyph_glyphs.json (never the
/// font: fonts lack them or draw emoji), eighth-block bars, chips, text in a cell grid.
/// </summary>
public static class GlyphDraw
{
    private static Dictionary<string, bool[,]>? _bitmaps;
    public const string Eighths = "▁▂▃▄▅▆▇█";

    private static Dictionary<string, bool[,]> Bitmaps
    {
        get
        {
            if (_bitmaps != null) return _bitmaps;
            _bitmaps = new Dictionary<string, bool[,]>();
            using var doc = JsonDocument.Parse(Godot.FileAccess.GetFileAsString("res://ui/glyph_glyphs.json"));
            foreach (var g in doc.RootElement.GetProperty("bitmap5x7").EnumerateObject())
            {
                var rows = new List<string>();
                foreach (var r in g.Value.EnumerateArray()) rows.Add(r.GetString()!);
                var bits = new bool[7, 5];
                for (int j = 0; j < 7 && j < rows.Count; j++)
                for (int i = 0; i < 5 && i < rows[j].Length; i++)
                    bits[j, i] = rows[j][i] == '#';
                _bitmaps[g.Name] = bits;
            }
            return _bitmaps;
        }
    }

    public static bool HasBitmap(string glyph) => Bitmaps.ContainsKey(glyph);

    /// <summary>Draws one glyph centred in a cell rect: from the bitmap if there is one, else the font (plain ASCII).</summary>
    public static void Glyph(CanvasItem ci, string glyph, Rect2 cell, Color color, bool bold = false)
    {
        if (Bitmaps.TryGetValue(glyph, out var bits))
        {
            float p = MathF.Min(cell.Size.X / 6f, cell.Size.Y / 10f);
            float ox = cell.Position.X + (cell.Size.X - 5 * p) / 2, oy = cell.Position.Y + (cell.Size.Y - 7 * p) / 2;
            for (int j = 0; j < 7; j++)
            for (int i = 0; i < 5; i++)
            {
                if (!bits[j, i]) continue;
                float x0 = MathF.Round(ox + i * p), x1 = MathF.Round(ox + (i + 1) * p);
                float y0 = MathF.Round(oy + j * p), y1 = MathF.Round(oy + (j + 1) * p);
                ci.DrawRect(new Rect2(x0, y0, MathF.Max(1, x1 - x0), MathF.Max(1, y1 - y0)), color);
            }
            return;
        }
        int e = Eighths.IndexOf(glyph, StringComparison.Ordinal);
        if (glyph.Length == 1 && e >= 0)
        {
            float h = MathF.Round(cell.Size.Y * 0.62f * (e + 1) / 8f);
            ci.DrawRect(new Rect2(cell.Position.X + 1, cell.Position.Y + MathF.Round(cell.Size.Y * 0.81f) - h, cell.Size.X - 2, h), color);
            return;
        }
        Text(ci, glyph, cell.Position, cell.Size.X, color, bold, HorizontalAlignment.Center, cell.Size.Y);
    }

    /// <summary>Text on one cell row: x is the left edge, width limits/aligns it; vertically centred in the row.</summary>
    public static void Text(CanvasItem ci, string text, Vector2 at, float width, Color color, bool bold = false,
        HorizontalAlignment align = HorizontalAlignment.Left, float rowHeight = -1)
    {
        var font = bold ? GlyphTheme.Bold : GlyphTheme.Regular;
        int size = GlyphTheme.FontSize;
        float h = rowHeight > 0 ? rowHeight : GlyphTheme.Ch;
        float baseline = at.Y + (h - font.GetHeight(size)) / 2f + font.GetAscent(size);
        ci.DrawString(font, new Vector2(at.X, MathF.Round(baseline)), text, align, width, size, color,
            TextServer.JustificationFlag.None);
    }

    public static float TextWidth(string text, bool bold = false) =>
        (bold ? GlyphTheme.Bold : GlyphTheme.Regular).GetStringSize(text, HorizontalAlignment.Left, -1, GlyphTheme.FontSize).X;

    /// <summary>A rounded rect (StyleBoxFlat, anti-aliased).</summary>
    public static void Round(CanvasItem ci, Rect2 r, Color fill, float radius)
    {
        if (fill.A <= 0) return;
        ci.DrawStyleBox(GlyphTheme.Box(fill, MathF.Min(radius, MathF.Min(r.Size.X, r.Size.Y) / 2)), r);
    }

    /// <summary>An icon chip: the glyph tinted with its role colour on a 20% rounded rect (rect off when chips are off).</summary>
    public static void Chip(CanvasItem ci, string glyph, Vector2 at, Color color)
    {
        float cw = GlyphTheme.Cw, ch = GlyphTheme.Ch;
        if (GlyphTheme.Options.Chips)
            Round(ci, new Rect2(at.X - 0.15f * cw, at.Y + 0.12f * ch, cw * 1.3f, ch * 0.76f), color with { A = GlyphTheme.Light ? 0.16f : 0.2f }, 4);
        Glyph(ci, glyph, new Rect2(at, new Vector2(cw, ch)), color);
    }

    /// <summary>Cells a keycap takes: the label plus a half cell of padding each side, rounded up.</summary>
    public static int KeycapCells(string label) => label.Length + 1;

    /// <summary>
    /// A keycap: the key's name in small bold text on a rounded plate (text @ 10%) with a hairline underside, at x on a
    /// row. Returns its width in cells (<see cref="KeycapCells"/>).
    /// </summary>
    public static int Keycap(CanvasItem ci, string label, float x, Color? tone = null)
    {
        float cw = GlyphTheme.Cw, ch = GlyphTheme.Ch;
        int cells = KeycapCells(label);
        var plate = new Rect2(x, ch * 0.16f, cells * cw, ch * 0.68f);
        Round(ci, plate, GlyphTheme.Tok("text", 0.10f, 0.12f), 4);
        ci.DrawRect(new Rect2(plate.Position.X + 3, plate.End.Y - 1, plate.Size.X - 6, 1), GlyphTheme.Tok("text", 0.18f));
        Text(ci, label, new Vector2(x + cw * 0.5f, 0), label.Length * cw + 2, tone ?? GlyphTheme.Tok("hi"), true);
        return cells;
    }

    /// <summary>Word-wraps text to lines of at most <paramref name="cols"/> characters ('\n' forces a break).</summary>
    public static List<string> Wrap(string text, int cols)
    {
        var lines = new List<string>();
        foreach (var para in text.Split('\n'))
        {
            var line = new System.Text.StringBuilder();
            foreach (var word in para.Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > cols)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(word.Length > cols ? word[..(cols - 1)] + "…" : word);
            }
            lines.Add(line.ToString());
        }
        return lines;
    }

    /// <summary>A thin rounded progress bar across a rect of one row (auto tone when tone is null).</summary>
    public static void Bar(CanvasItem ci, Rect2 row, float value, Color? tone = null)
    {
        float cw = GlyphTheme.Cw, ch = GlyphTheme.Ch;
        float h = MathF.Max(2, MathF.Round(ch * 0.2f));
        var track = new Rect2(row.Position.X + cw * 0.25f, row.Position.Y + (row.Size.Y - h) / 2, row.Size.X - cw * 0.5f, h);
        Round(ci, track, GlyphTheme.Tok("text", 0.09f, 0.10f), h / 2);
        value = Math.Clamp(value, 0, 1);
        if (value <= 0) return;
        var c = tone ?? (value >= 0.6f ? GlyphTheme.Tok("good") : value >= 0.3f ? GlyphTheme.Tok("warn") : GlyphTheme.Tok("bad"));
        Round(ci, track with { Size = new Vector2(MathF.Max(h, track.Size.X * value), h) }, c, h / 2);
    }
}
