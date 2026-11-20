using System.Collections.Generic;
using Godot;

namespace ColonyGame;

/// <summary>
/// The styled look renders 3D at a fraction of the screen's resolution, where Label3D text (name tags, nest tags) turns
/// to specks. This layer takes every Label3D under the scene out of the 3D render and draws its text as a crisp 2D label
/// in the Glyph UI font at the label's screen position, following its text, colour, visibility and draw distance. Views
/// keep making Label3Ds as before.
/// </summary>
public partial class ScreenTags : CanvasLayer
{
    private readonly Dictionary<Label3D, Label> _tags = new();
    private readonly List<Label3D> _gone = new();

    public ScreenTags() => Layer = 1; // above the 3D image, below the UI (GameUi is layer 5)

    public void Track(Label3D l)
    {
        if (_tags.ContainsKey(l)) return;
        l.Layers = 0; // no camera draws it any more
        var label = new Label
        {
            Theme = UI.GlyphTheme.Theme,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.AddThemeConstantOverride("outline_size", 6);
        label.AddThemeFontSizeOverride("font_size", System.Math.Max(10, (int)(UI.GlyphTheme.Theme.DefaultFontSize * 0.8f)));
        label.AddThemeColorOverride("font_outline_color", new Color(0.05f, 0.04f, 0.04f, 0.9f));
        AddChild(label);
        _tags[l] = label;
    }

    public override void _Process(double delta)
    {
        var camera = GetViewport().GetCamera3D();
        foreach (var (l3, label) in _tags)
        {
            if (!IsInstanceValid(l3))
            {
                _gone.Add(l3);
                continue;
            }
            var pos = l3.GlobalPosition;
            bool show = camera != null && DwarfRender.DwarfRenderer.Instance?.FarViewIsText != true && l3.IsVisibleInTree() && !camera.IsPositionBehind(pos) && l3.Text.Length > 0 &&
                        (l3.VisibilityRangeEnd <= 0 || camera.GlobalPosition.DistanceTo(pos) <= l3.VisibilityRangeEnd);
            label.Visible = show;
            if (!show) continue;
            if (label.Text != l3.Text)
            {
                label.Text = l3.Text;
                label.ResetSize();
            }
            label.Modulate = l3.Modulate;
            label.Position = (camera!.UnprojectPosition(pos) - new Vector2(label.Size.X / 2, label.Size.Y)).Round();
        }
        foreach (var l3 in _gone)
        {
            _tags[l3].QueueFree();
            _tags.Remove(l3);
        }
        _gone.Clear();
    }
}
