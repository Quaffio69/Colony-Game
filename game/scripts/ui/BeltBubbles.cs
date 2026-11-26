using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace ColonyGame.UI;

/// <summary>
/// The conveyor tools' bubbles (<see cref="DesignationTool.BeltBubbles"/>): over a belt's start, what it picks up from (the
/// crates and stockpiles in its 3x3) with the most plentiful items as chips and "+N" for the rest; over its end, what it
/// delivers into and what those workshops want. Drawn in the Glyph UI font at the end's screen position.
/// </summary>
public partial class BeltBubbles : Control
{
    private readonly GameUi _ui;
    private string _drawn = "";

    public BeltBubbles(GameUi ui)
    {
        _ui = ui;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }
    public BeltBubbles() : this(null!) { }

    private readonly List<(Vector2 At, DesignationTool.BeltBubble Bubble)> _shown = new();

    public override void _Process(double delta)
    {
        if (_ui == null) return;
        var camera = GetViewport().GetCamera3D();
        _shown.Clear();
        if (camera != null && !(GlyphManager.Instance?.MenuOpen ?? false))
            foreach (var b in _ui.Tool.BeltBubbles)
                if (!camera.IsPositionBehind(b.At)) _shown.Add((camera.UnprojectPosition(b.At).Round(), b));
        string key = string.Join("|", _shown.Select(s => $"{s.At}{s.Bubble.Caption}{s.Bubble.Items.Count}{s.Bubble.More}"));
        if (key == _drawn) return;
        _drawn = key;
        QueueRedraw();
    }

    public override void _Draw()
    {
        float cw = GlyphTheme.Cw, ch = GlyphTheme.Ch;
        foreach (var (at, b) in _shown)
        {
            string caption = b.Caption.Length > 44 ? b.Caption[..43] + "…" : b.Caption;
            var chips = b.Items.Select(i => (ItemLook.Glyph(i.Type), ItemLook.Color(i.Type), $"{i.Type.Name.ToLowerInvariant()} {i.Count}")).ToList();
            string more = b.More > 0 ? $"+{b.More} more" : "";
            float rowW = chips.Sum(c => (c.Item3.Length + 3) * cw) + (more.Length > 0 ? (more.Length + 1) * cw : 0);
            float w = MathF.Max((caption.Length + 2) * cw, rowW + cw), h = (chips.Count > 0 || more.Length > 0 ? 2.3f : 1.3f) * ch;
            var box = new Rect2(at.X - w / 2, at.Y - h - 0.6f * ch, w, h);
            GlyphDraw.Round(this, box, new Color(0.07f, 0.06f, 0.05f, 0.86f), 6);
            var accent = GlyphTheme.Tok(b.Source ? "accent" : "good");
            DrawRect(new Rect2(box.Position + new Vector2(6, h - 2), new Vector2(w - 12, 2)), accent with { A = 0.7f });
            // A little tail pointing down at the tile.
            DrawColoredPolygon([new Vector2(at.X - 5, box.End.Y), new Vector2(at.X + 5, box.End.Y), new Vector2(at.X, box.End.Y + 0.5f * ch)], new Color(0.07f, 0.06f, 0.05f, 0.86f));
            GlyphDraw.Text(this, caption, box.Position + new Vector2(cw, 0.15f * ch), w - 2 * cw, accent, true);
            float x = box.Position.X + cw, y = box.Position.Y + 1.1f * ch;
            foreach (var (glyph, color, text) in chips)
            {
                GlyphDraw.Chip(this, glyph, new Vector2(x, y), color);
                GlyphDraw.Text(this, text, new Vector2(x + 1.5f * cw, y), text.Length * cw + 2, GlyphTheme.Tok("dim"));
                x += (text.Length + 3) * cw;
            }
            if (more.Length > 0) GlyphDraw.Text(this, more, new Vector2(x, y), more.Length * cw + 2, GlyphTheme.Tok("faint"));
        }
    }
}
