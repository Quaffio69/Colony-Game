using System.Windows;
using System.Windows.Media;

namespace ColonyLauncher;

/// <summary>One of the game's 5x7 pixel glyphs as a crisp icon (the UI draws its symbols this way, never from the font).</summary>
public sealed class GlyphIcon : FrameworkElement
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(GlyphIcon),
        new FrameworkPropertyMetadata("•", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));
    public static readonly DependencyProperty PixelProperty = DependencyProperty.Register(nameof(Pixel), typeof(double), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    /// <summary>Size of one glyph pixel (device-independent units).</summary>
    public double Pixel { get => (double)GetValue(PixelProperty); set => SetValue(PixelProperty, value); }

    public GlyphIcon()
    {
        SnapsToDevicePixels = true;
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected override Size MeasureOverride(Size available) => new(Glyphs.W * Pixel, Glyphs.H * Pixel);

    protected override void OnRender(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(Glyph) || !Glyphs.All.TryGetValue(Glyph[0], out var rows)) return;
        double p = Pixel;
        for (int y = 0; y < Glyphs.H; y++)
        for (int x = 0; x < Glyphs.W; x++)
            if (rows[y][x] == '#') dc.DrawRectangle(Foreground, null, new Rect(x * p, y * p, p, p));
    }
}
