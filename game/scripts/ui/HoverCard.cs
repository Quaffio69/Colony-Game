using System;
using System.Collections.Generic;
using System.Linq;
using Colony.Core.World;
using Godot;

namespace ColonyGame.UI;

/// <summary>
/// Inspect on hover: a glass card at the bottom right describing what is under the cursor (<see cref="WorldInfo"/>): the
/// thing a click would select, then the tile's facts with their materials, and what a click or key does there. Hidden
/// over the UI, while dragging a box and in the menu. Clicking still keeps the thing in the Inspect window.
/// </summary>
public partial class HoverCard : Control
{
    private readonly GameUi _ui;
    private readonly GlyphPanel _panel = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly Body _body;
    private TilePos? _tile;
    private double _refreshIn;
    private string _shown = "";
    /// <summary>Widest the card grows (columns, frame included).</summary>
    public const int MaxCols = 50;

    public HoverCard(GameUi ui)
    {
        _ui = ui;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        _body = new Body();
        _panel.AddChild(_body);
        AddChild(_panel);
    }
    public HoverCard() : this(null!) { }

    /// <summary>The card is up (for laying out what shares the corner).</summary>
    public bool Showing => _panel.Visible;

    public override void _Process(double delta)
    {
        if (_ui == null || GlyphManager.Instance is not { } m || m.Cols == 0) return;
        var tool = _ui.Tool;
        bool show = !m.MenuOpen && tool.Hovered is not null && !tool.Selecting && m.Cols >= 60 && !m.Blocks(GetViewport().GetMousePosition());
        if (!show)
        {
            _panel.Visible = false;
            _tile = null;
            return;
        }
        // Rebuild when the tile changes, else a few times a second (colonists walk, crops ripen).
        if (tool.Hovered != _tile || (_refreshIn -= delta) <= 0)
        {
            _tile = tool.Hovered;
            _refreshIn = 0.25;
            var card = WorldInfo.For(_ui, _tile!.Value);
            int maxCols = Math.Min(MaxCols, Math.Max(30, m.Cols / 3)) - 4;
            if (card.Key + maxCols != _shown)
            {
                _shown = card.Key + maxCols;
                _body.Show(card, maxCols);
            }
        }
        float cw = GlyphTheme.Cw, ch = GlyphTheme.Ch;
        int cols = _body.Cols + 4;
        float h = (_body.Rows + 0.8f) * ch;
        _panel.Visible = true;
        _panel.Size = new Vector2(cols * cw, h);
        _panel.Position = m.Origin + new Vector2((m.Cols - 1 - cols) * cw, (m.Rows - 1.3f) * ch - h);
        _body.Position = new Vector2(2 * cw, 0.4f * ch);
        _body.Size = new Vector2(_body.Cols * cw, _body.Rows * ch);
    }

    /// <summary>Title row (chip, bold title, dim subtitle), fact rows (glyph, text), and a faint hint row under a rule.</summary>
    private sealed partial class Body : GlyphControl
    {
        private InfoCard? _card;
        private readonly List<(string Glyph, Color Color, string Text, string Tone)> _lines = new();
        public int Cols { get; private set; } = 20;
        public int Rows { get; private set; } = 1;
        private const int MaxLines = 8;

        public Body() => MouseFilter = MouseFilterEnum.Ignore;

        public void Show(InfoCard card, int maxCols)
        {
            _card = card;
            _lines.Clear();
            foreach (var l in card.Lines)
            {
                // Long facts wrap onto a second row, indented under their text; the glyph only on the first.
                var wrapped = GlyphDraw.Wrap(l.Text, Math.Max(10, maxCols - 3));
                for (int i = 0; i < wrapped.Count && i < 2; i++) _lines.Add((i == 0 ? l.Glyph : "", l.Color, wrapped[i], l.Tone));
                if (_lines.Count >= MaxLines) break;
            }
            int title = 3 + card.Title.Length + (card.Sub.Length > 0 ? 1 + card.Sub.Length : 0);
            int widest = _lines.Count == 0 ? 0 : _lines.Max(l => l.Text.Length + 3);
            Cols = Math.Clamp(Math.Max(title, Math.Max(card.Hint.Length, widest)), 24, maxCols);
            Rows = 1 + _lines.Count + (card.Hint.Length > 0 ? 1 : 0);
            QueueRedraw();
        }

        public override void _Draw()
        {
            if (_card == null) return;
            float w = Size.X;
            GlyphDraw.Chip(this, _card.Glyph, new Vector2(0, 0), _card.Color);
            string title = _card.Title.Length > Cols - 3 ? _card.Title[..Math.Max(1, Cols - 4)] + "…" : _card.Title;
            GlyphDraw.Text(this, title, new Vector2(2.4f * Cw, 0), title.Length * Cw + 2, Tok("hi"), true);
            if (_card.Sub.Length > 0)
            {
                float sx = (3 + title.Length + 1) * Cw;
                if (sx < w) GlyphDraw.Text(this, _card.Sub, new Vector2(sx, 0), w - sx, Tok("dim"));
            }
            float y = Ch;
            foreach (var (glyph, color, text, tone) in _lines)
            {
                if (glyph.Length > 0) GlyphDraw.Glyph(this, glyph, new Rect2(0.15f * Cw, y, Cw, Ch), color);
                GlyphDraw.Text(this, text, new Vector2(2.4f * Cw, y), w - 2.4f * Cw, Tok(tone.ToLowerInvariant()));
                y += Ch;
            }
            if (_card.Hint.Length > 0)
            {
                DrawRect(new Rect2(0, y + 1, w, 1), GlyphTheme.Tok("text", 0.08f));
                GlyphDraw.Text(this, _card.Hint, new Vector2(0, y), w, Tok("faint"));
            }
        }
    }
}
