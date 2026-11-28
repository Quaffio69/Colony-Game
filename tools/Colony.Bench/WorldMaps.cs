using System.Diagnostics;
using System.IO.Compression;
using Colony.Core.Procedural;

/// <summary>
/// World pictures for tuning generation: dotnet run -c Release --project tools/Colony.Bench -- map [size] [seed] [dir]
/// [oceans] [relief] [climate] [rainfall] [rivers] (names as in <see cref="WorldShape"/>). Writes the loading screen's maps
/// (plates, climate, erosion, biomes) as PNGs into dir and prints timings, biome shares and cliff counts.
/// </summary>
internal static class WorldMaps
{
    public static void Run(string[] args)
    {
        int size = args.Length > 1 ? int.Parse(args[1]) : 1024;
        int seed = args.Length > 2 ? int.Parse(args[2]) : 12345;
        string dir = args.Length > 3 ? args[3] : ".";
        var shape = WorldShape.Default;
        if (args.Length > 4) shape = shape with { Oceans = Enum.Parse<Oceans>(args[4], true) };
        if (args.Length > 5) shape = shape with { Relief = Enum.Parse<Relief>(args[5], true) };
        if (args.Length > 6) shape = shape with { Climate = Enum.Parse<Climate>(args[6], true) };
        if (args.Length > 7) shape = shape with { Rainfall = Enum.Parse<Rainfall>(args[7], true) };
        if (args.Length > 8) shape = shape with { Rivers = Enum.Parse<Rivers>(args[8], true) };
        Directory.CreateDirectory(dir);
        var s = new WorldGenSettings(seed, size, size, 384) { Shape = shape };
        var progress = new WorldGenProgress();
        int lastVersion = -1, maps = 0;
        string lastTitle = "";
        var clock = Stopwatch.StartNew();
        var gen = Task.Run(() => WorldGenerator.GenerateWithTerrain(s, progress));
        while (true)
        {
            bool finished = gen.IsCompleted;
            if (progress.Version != lastVersion)
            {
                lastVersion = progress.Version;
                if (progress.Title != lastTitle && progress.Title.Length > 0)
                {
                    lastTitle = progress.Title;
                    Console.WriteLine($"  {clock.ElapsedMilliseconds,6} ms  {progress.Title} - {progress.Detail}");
                }
                if (progress.Map is { } m && m.Rgb != null)
                {
                    string file = Path.Combine(dir, $"map{maps}.png");
                    if (maps == 0 || !File.Exists(file)) { }
                    WritePng(file, m.Rgb, m.Width, m.Height);
                }
            }
            if (finished) break;
            Thread.Sleep(5);
        }
        var (world, t) = gen.Result;
        Console.WriteLine($"generated {size}² in {clock.ElapsedMilliseconds} ms: {t.RiverCells} river cells, {t.LakeCells} lake cells, sea level {t.SeaLevel}, " +
                          $"plates {t.Landform?.PlateCount}, volcanoes {t.Landform?.Volcanoes.Count}, spawn ({t.SpawnX},{t.SpawnY}) {Biomes.At(t, t.SpawnX, t.SpawnY).Biome}");
        if (Environment.GetEnvironmentVariable("CROP") is { } crop)
        {
            var parts = crop.Split(',').Select(int.Parse).ToArray();
            int cx0 = parts[0], cy0 = parts[1], cs = parts[2];
            var px = new byte[cs * cs * 3];
            for (int yy = 0; yy < cs; yy++)
            for (int xx = 0; xx < cs; xx++)
            {
                int x = Math.Clamp(cx0 + xx, 0, t.Width - 1), y = Math.Clamp(cy0 + yy, 0, t.Depth - 1);
                var (r, g, b) = Biomes.Colour(Biomes.At(t, x, y).Biome);
                int i = y * t.Width + x;
                float shade = 1f;
                if (x > 0 && y > 0) shade = Math.Clamp(1f + (t.Surface[i - 1] - t.Surface[i] + t.Surface[i - t.Width] - t.Surface[i]) * 0.15f, 0.5f, 1.5f);
                int o = (yy * cs + xx) * 3;
                px[o] = (byte)Math.Clamp(r * shade * 255, 0, 255); px[o + 1] = (byte)Math.Clamp(g * shade * 255, 0, 255); px[o + 2] = (byte)Math.Clamp(b * shade * 255, 0, 255);
            }
            WritePng(Path.Combine(dir, "crop.png"), px, cs, cs);
        }
        if (Environment.GetEnvironmentVariable("LEVELS") is { } lv)
        {
            // LEVELS=x,y,r[,step]: the surface round a column, water as ~level (lake) or =level (river).
            var p = lv.Split(',').Select(int.Parse).ToArray();
            int step = p.Length > 3 ? p[3] : 1;
            for (int yy = p[1] - p[2]; yy <= p[1] + p[2]; yy += step)
            {
                var line = new System.Text.StringBuilder($"{yy,5} ");
                for (int xx = p[0] - p[2]; xx <= p[0] + p[2]; xx += step)
                {
                    if ((uint)xx >= (uint)t.Width || (uint)yy >= (uint)t.Depth) { line.Append("    "); continue; }
                    int i = yy * t.Width + xx;
                    char k = t.Kind[i] == SurfaceKind.LakeBed ? '~' : t.Kind[i] == SurfaceKind.RiverBed ? '=' : ' ';
                    line.Append($"{k}{t.Surface[i] + t.Water[i],3}");
                }
                Console.WriteLine(line);
            }
        }
        int[] edgeRiver = new int[4];
        for (int k = 0; k < t.Width; k++)
        {
            if (t.Kind[k] == SurfaceKind.RiverBed) edgeRiver[0]++;
            if (t.Kind[(t.Depth - 1) * t.Width + k] == SurfaceKind.RiverBed) edgeRiver[1]++;
            if (t.Kind[k * t.Width] == SurfaceKind.RiverBed) edgeRiver[2]++;
            if (t.Kind[k * t.Width + t.Width - 1] == SurfaceKind.RiverBed) edgeRiver[3]++;
        }
        Console.WriteLine($"river tiles on the edges: top {edgeRiver[0]}, bottom {edgeRiver[1]}, left {edgeRiver[2]}, right {edgeRiver[3]}");
        var big = t.Picture(1024);
        WritePng(Path.Combine(dir, "world.png"), big.Rgb, big.Width, big.Height);
        if (t.Landform is { } lf)
        {
            for (int stage = 0; stage < 3; stage++)
                WritePng(Path.Combine(dir, $"landform{stage}.png"), lf.Picture(stage), lf.PictureW, lf.PictureH);
            // Climate at the surface: rain (brown dry .. blue wet) and temperature (blue cold .. red hot), a pixel per 4 tiles.
            int pw = t.Width / 4, ph = t.Depth / 4;
            var rain = new byte[pw * ph * 3];
            var temp = new byte[pw * ph * 3];
            for (int py = 0; py < ph; py++)
            for (int px = 0; px < pw; px++)
            {
                var c = Biomes.At(t, px * 4, py * 4);
                int o = (py * pw + px) * 3;
                float r = Math.Clamp(c.Rain / 1.5f, 0f, 1f), k = Math.Clamp((c.Temperature + 15f) / 45f, 0f, 1f);
                rain[o] = (byte)(200 - 170 * r); rain[o + 1] = (byte)(150 + 40 * r); rain[o + 2] = (byte)(80 + 170 * r);
                temp[o] = (byte)(255 * k); temp[o + 1] = (byte)(90 + 60 * (1 - Math.Abs(k - 0.5f) * 2)); temp[o + 2] = (byte)(255 * (1 - k));
                if (c.Biome is Biome.Sea or Biome.Lake or Biome.River) { rain[o] = rain[o + 1] = rain[o + 2] = 40; temp[o] = temp[o + 1] = temp[o + 2] = 40; }
            }
            WritePng(Path.Combine(dir, "rain.png"), rain, pw, ph);
            WritePng(Path.Combine(dir, "temperature.png"), temp, pw, ph);
        }

        var count = new int[Enum.GetValues<Biome>().Length];
        int total = 0, walk = 0, pairs = 0, cliff10 = 0, maxStep = 0;
        for (int y = 0; y < t.Depth; y += 2)
        for (int x = 0; x < t.Width; x += 2)
        {
            count[(int)Biomes.At(t, x, y).Biome]++;
            total++;
        }
        for (int y = 0; y < t.Depth; y++)
        for (int x = 0; x + 1 < t.Width; x++)
        {
            int i = y * t.Width + x;
            if (t.Kind[i] != SurfaceKind.Land || t.Kind[i + 1] != SurfaceKind.Land) continue;
            int step = Math.Abs(t.Surface[i] - t.Surface[i + 1]);
            pairs++;
            if (step <= 1) walk++;
            if (step >= 10) cliff10++;
            maxStep = Math.Max(maxStep, step);
        }
        Console.WriteLine("biomes: " + string.Join(", ", Enum.GetValues<Biome>().Where(b => count[(int)b] > 0)
            .OrderByDescending(b => count[(int)b]).Select(b => $"{Biomes.Name(b)} {count[(int)b] * 100.0 / total:0.0}%")));
        int lo = int.MaxValue, hi = 0;
        foreach (int z in t.Surface) { lo = Math.Min(lo, z); hi = Math.Max(hi, z); }
        Console.WriteLine($"land steps {walk * 100.0 / Math.Max(1, pairs):0.0}% walkable, {cliff10} of 10+, highest {maxStep}; surface {lo}..{hi}; " +
                          $"zero level {world.Geology!.ZeroLevel}, cavern layers {world.Geology.CavernLayers.Length}");
        Console.WriteLine($"  hydrology: {t.Timings}");
        Console.WriteLine($"  drowned valleys flooded: {t.DrownedValleys} tiles; banks dug 10+: {t.DeepCuts.Tiles} (most {t.DeepCuts.Max} at {t.DeepCuts.At % t.Width},{t.DeepCuts.At / t.Width}); levees: {t.Levees.Tiles} tiles raised 3+, most {t.Levees.Max}" +
                          (t.Levees.At < 0 ? "" : $" at {t.Levees.At % t.Width},{t.Levees.At / t.Width}") +
                          (t.Levees.LakeAt < 0 ? "" : $"; by a lake {t.Levees.LakeMax} at {t.Levees.LakeAt % t.Width},{t.Levees.LakeAt / t.Width}"));
        foreach (var d in progress.Finished) Console.WriteLine($"  {d.Title,-36} {d.Milliseconds,6} ms");
        for (int k = 9; k + 1 < args.Length; k += 2)
        {
            int px = int.Parse(args[k]), py = int.Parse(args[k + 1]);
            var c = Biomes.At(t, px, py);
            Console.WriteLine($"  ({px},{py}): {c.Biome} {c.Temperature:0.0} C rain {c.Rain:0.00} slope {c.Slope} z {t.SurfaceAt(px, py)} water distance {t.WaterDistance[py * t.Width + px]}" +
                              (t.Landform is { } l ? $" rugged {l.At(l.Rugged, px, py):0.00} uplift {l.At(l.Uplift, px, py):0} volcanic {l.At(l.Volcanic, px, py):0.00}" : ""));
        }
    }

    /// <summary>A plain RGB PNG (zlib via .NET, CRCs computed here).</summary>
    public static void WritePng(string path, byte[] rgb, int w, int h)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            for (int y = 0; y < h; y++)
            {
                z.WriteByte(0);
                z.Write(rgb, y * w * 3, w * 3);
            }
        using var f = File.Create(path);
        f.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, w);
        BigEndian(ihdr, 4, h);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk(f, "IHDR", ihdr);
        Chunk(f, "IDAT", raw.ToArray());
        Chunk(f, "IEND", []);
    }

    private static void Chunk(Stream f, string type, byte[] data)
    {
        var head = new byte[8];
        BigEndian(head, 0, data.Length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        f.Write(head);
        f.Write(data);
        uint crc = Crc(head.AsSpan(4, 4), 0xFFFFFFFFu);
        crc = Crc(data, crc) ^ 0xFFFFFFFFu;
        var tail = new byte[4];
        BigEndian(tail, 0, (int)crc);
        f.Write(tail);
    }

    private static uint Crc(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc;
    }

    private static void BigEndian(byte[] a, int at, int v)
    {
        a[at] = (byte)(v >> 24); a[at + 1] = (byte)(v >> 16); a[at + 2] = (byte)(v >> 8); a[at + 3] = (byte)v;
    }
}
