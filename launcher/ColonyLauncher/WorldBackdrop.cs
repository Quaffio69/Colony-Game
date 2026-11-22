using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ColonyLauncher;

/// <summary>
/// The launcher's backdrop: a world map in the game's own far-view style (DF-like symbols drawn from its 5x7 pixel
/// glyphs: grass " , ' . trees ♣ ♠, hills ^, mountains ▲, water ≈ ~), with a little settlement (⌂ homes, ≡ stores,
/// τ fields) whose colonists ☺ wander about while the rivers shimmer. Drawn into a small bitmap and scaled up in
/// whole pixels, so it stays crisp pixel art.
/// </summary>
public sealed class WorldBackdrop : Image
{
    /// <summary>Bitmap pixels per map cell (a 5x7 glyph plus spacing), and screen pixels per bitmap pixel.</summary>
    private const int CellW = 6, CellH = 9, Scale = 2;
    private const int Seed = 1337;

    private int _cols, _rows;
    private char[] _glyph = [];
    private uint[] _color = [];
    private bool[] _water = [], _land = [];
    private WriteableBitmap? _bitmap;
    private uint[] _pixels = [];
    private readonly DispatcherTimer _timer;
    private int _tick;
    private readonly List<Walker> _walkers = new();
    private (int X, int Y) _town;

    private sealed class Walker { public int X, Y; public uint Color; public int Rest; }

    // The Hearth theme and the game's map colours (game/ui/glyph_themes.json).
    private static readonly uint Bg = Rgb(0x0d0b0a);
    private static readonly uint[] People = [Rgb(0xef6a4c), Rgb(0x6aa8e8), Rgb(0x9cc65a), Rgb(0xf0c24a), Rgb(0xc88ae0), Rgb(0xf09a60)];

    public WorldBackdrop()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        Stretch = Stretch.Fill; // sized to exactly Scale x the bitmap: whole-pixel scaling
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        SizeChanged += (_, _) => { };
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(260), DispatcherPriority.Background, (_, _) => Step(), Dispatcher);
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>Builds the map for an area (in device-independent units).</summary>
    public void Build(double width, double height)
    {
        _cols = (int)Math.Ceiling(width / (CellW * Scale)) + 1;
        _rows = (int)Math.Ceiling(height / (CellH * Scale)) + 1;
        int n = _cols * _rows;
        _glyph = new char[n];
        _color = new uint[n];
        _water = new bool[n];
        _land = new bool[n];
        // The settlement sits in the open ground between the title and the panels.
        _town = ((int)(_cols * 0.55), (int)(_rows * 0.4));

        for (int y = 0; y < _rows; y++)
        for (int x = 0; x < _cols; x++)
        {
            int i = y * _cols + x;
            double nx = x / 22.0, ny = y / 11.0; // cells are tall: stretch y so features look round
            double h = Fbm(nx, ny, Seed);
            // Mountains rise to the right, a river valley runs down the middle-left, and the town's ground is flat.
            h += 0.18 * (x / (double)_cols - 0.5);
            double river = Math.Abs(Fbm(nx * 0.6, ny * 0.6, Seed + 9) - 0.5 + (x - _cols * 0.2) / (_cols * 2.2));
            double dt = Math.Sqrt(Sq(x - _town.X) + Sq((y - _town.Y) * 2.0));
            if (dt < 9) h = Lerp(0.5, h, dt / 9);
            double forest = Fbm(nx * 1.7, ny * 1.7, Seed + 21);
            uint grassTone = Hash(x, y, 3) < 0.5 ? Rgb(0x7aa646) : Rgb(0x56823a);

            char g;
            uint c;
            if (river < 0.022 && h < 0.7 && dt > 3)
            {
                (g, c) = ('≈', Rgb(0x4f90b0));
                _water[i] = true;
            }
            else if (h < 0.3)
            {
                (g, c) = ('≈', Rgb(0x4f90b0));
                _water[i] = true;
            }
            else if (h < 0.33 || river < 0.034 && h < 0.7 && dt > 3) (g, c) = ('.', Rgb(0xc8b08a));
            else if (h > 0.74) (g, c) = ('▲', h > 0.8 ? Rgb(0xece4d6) : Rgb(0xa49eaa));
            else if (h > 0.66) (g, c) = ('^', Rgb(0x7a6a56));
            else if (forest > 0.6 && dt > 6) (g, c) = h > 0.56 ? ('♠', Rgb(0x4a9a5a)) : ('♣', Rgb(0x86b84a));
            else
            {
                double r = Hash(x, y, 1);
                g = r < 0.28 ? '"' : r < 0.52 ? ',' : r < 0.7 ? '\'' : r < 0.82 ? '.' : ' ';
                c = grassTone;
            }
            _glyph[i] = g;
            _color[i] = c;
            _land[i] = !_water[i] && g is not ('▲' or '^');
        }

        // The settlement: homes, a stockpile, fields, a workshop.
        void Put(int dx, int dy, char g, uint c)
        {
            int x = _town.X + dx, y = _town.Y + dy;
            if (x < 0 || y < 0 || x >= _cols || y >= _rows) return;
            int i = y * _cols + x;
            _glyph[i] = g;
            _color[i] = c;
            _water[i] = false;
        }
        foreach (var (dx, dy) in new[] { (-3, -1), (-1, -2), (1, -1), (3, -2), (-2, 1), (2, 1) }) Put(dx, dy, '⌂', Rgb(0xd89a58));
        Put(0, 0, '☼', Rgb(0xf0a849));
        for (int k = -1; k <= 1; k++) Put(5, k, '≡', Rgb(0xb8864c));
        for (int fy = 2; fy <= 3; fy++)
        for (int fx = -6; fx <= -3; fx++) Put(fx, fy, 'τ', Rgb(0xc0a848));
        Put(4, 2, 'Θ', Rgb(0xc8b08a));

        _walkers.Clear();
        for (int k = 0; k < 7; k++)
            _walkers.Add(new Walker { X = _town.X + (k % 4) * 2 - 3, Y = _town.Y + k / 4 * 2, Color = People[k % People.Length], Rest = k * 3 });

        int bw = _cols * CellW, bh = _rows * CellH;
        _bitmap = new WriteableBitmap(bw, bh, 96, 96, PixelFormats.Bgra32, null);
        _pixels = new uint[bw * bh];
        Source = _bitmap;
        Width = bw * Scale;
        Height = bh * Scale;
        Paint();
    }

    private void Step()
    {
        if (_bitmap is null) return;
        _tick++;
        // Colonists amble about the settlement, pausing now and then.
        foreach (var w in _walkers)
        {
            if (w.Rest > 0) { w.Rest--; continue; }
            int dir = (int)(Hash(w.X * 31 + _tick, w.Y, w.GetHashCode() & 0xff) * 8);
            int nx = w.X + (dir % 3) - 1, ny = w.Y + (dir / 3 % 3) - 1;
            if (nx < 0 || ny < 0 || nx >= _cols || ny >= _rows) continue;
            if (Math.Abs(nx - _town.X) > 9 || Math.Abs(ny - _town.Y) > 4) continue;
            if (!_land[ny * _cols + nx]) continue;
            (w.X, w.Y) = (nx, ny);
            if (Hash(_tick, w.X, w.Y) < 0.2) w.Rest = 2 + (int)(Hash(w.Y, _tick, 9) * 6);
        }
        Paint();
    }

    private void Paint()
    {
        if (_bitmap is null) return;
        int bw = _cols * CellW;
        for (int i = 0; i < _pixels.Length; i++) _pixels[i] = Bg;
        var people = new Dictionary<int, uint>();
        foreach (var w in _walkers) people[w.Y * _cols + w.X] = w.Color;
        for (int y = 0; y < _rows; y++)
        for (int x = 0; x < _cols; x++)
        {
            int i = y * _cols + x;
            char g = _glyph[i];
            uint c = _color[i];
            if (people.TryGetValue(i, out var pc)) (g, c) = ('☺', pc);
            else if (_water[i])
            {
                // Water ripples: each cell flips between ≈ and ~ on its own slow rhythm, now and then catching the light.
                double phase = Hash(x, y, 5);
                int beat = (_tick + (int)(phase * 16)) % 16;
                g = beat < 8 ? '≈' : '~';
                c = beat is 3 or 11 ? Rgb(0x9ad0e8) : Rgb(0x4f90b0);
            }
            if (g == ' ' || !Glyphs.All.TryGetValue(g, out var rows)) continue;
            c = Dim(c, 0.7);
            int ox = x * CellW, oy = y * CellH + 1;
            for (int gy = 0; gy < Glyphs.H; gy++)
            {
                string row = rows[gy];
                for (int gx = 0; gx < Glyphs.W; gx++)
                    if (row[gx] == '#') _pixels[(oy + gy) * bw + ox + gx] = c;
            }
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, bw, _rows * CellH), _pixels, bw * 4, 0);
    }

    // ---- Noise ----------------------------------------------------------------------------------------------

    private static double Hash(int x, int y, int salt)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + salt * 2147483647 + Seed * 974634);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xffffff) / (double)0x1000000;
        }
    }

    private static double ValueNoise(double x, double y, int salt)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        double a = Hash(x0, y0, salt), b = Hash(x0 + 1, y0, salt), c = Hash(x0, y0 + 1, salt), d = Hash(x0 + 1, y0 + 1, salt);
        return Lerp(Lerp(a, b, fx), Lerp(c, d, fx), fy);
    }

    private static double Fbm(double x, double y, int salt)
    {
        double sum = 0, amp = 0.5, norm = 0;
        for (int o = 0; o < 4; o++)
        {
            sum += amp * ValueNoise(x, y, salt + o * 101);
            norm += amp;
            x *= 2.03; y *= 2.03; amp *= 0.5;
        }
        return sum / norm;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double Sq(double v) => v * v;
    private static uint Rgb(uint rgb) => 0xff000000 | rgb;

    private static uint Dim(uint c, double k)
    {
        uint r = (uint)(((c >> 16) & 0xff) * k), g = (uint)(((c >> 8) & 0xff) * k), b = (uint)((c & 0xff) * k);
        return 0xff000000 | (r << 16) | (g << 8) | b;
    }
}
