using System.Numerics;
using Colony.Core.World;

namespace Colony.Core.Meshing;

/// <summary>
/// Far-view terrain: a coarse heightfield of the visible top (ground or standing water) in cells of several tiles,
/// meshed into the same <see cref="Quad"/>s as <see cref="SectionMesher"/> (tops greedy-merged, sides where a cell
/// stands above its neighbour). When the whole map is in view one art pixel covers several tiles, so a block of
/// thousands of columns can be one mesh: tens of draws for the map instead of ten thousand sections.
/// Sampling reads the live world and water (main thread); <see cref="Build"/> works on the sampled arrays only, so it
/// runs on any thread.
/// </summary>
public static class FarMesher
{
    /// <summary>The tile code far meshes use for standing water (no world tile has it; tile tables give it water's look).</summary>
    public const TileType Water = (TileType)31;
    /// <summary>The tile code coarse far cells use for woodland (two of their four sampled columns carry a tree): far out the
    /// trees themselves are not drawn (each would be a fraction of a pixel), so the ground shows they are there.</summary>
    public const TileType Forest = (TileType)29;

    /// <summary>
    /// Greedy rectangles meet in T-junctions, which rasterise with hairline cracks; under a far top there is nothing, so a
    /// crack showed the sky as a stray pixel (outlined dark) that jumped as the camera moved. Every top hangs a skirt this
    /// deep from each edge (facing out, so the two skirts of a shared edge never fight), and sides reach as far below the
    /// lower neighbour's top. (Overlapping tops instead made different tiles fight for the same depth in slivers.)
    /// </summary>
    public const float Skirt = 0.5f;
    /// <summary>Sides overlap each other along an edge by this much (their seams are T-junctions too).</summary>
    public const float Seam = 0.05f;

    /// <summary>
    /// Samples cells of <paramref name="cell"/> x <paramref name="cell"/> columns starting at (x0, y0), with a ring of
    /// one cell around the block (for its sides): arrays are (cellsX + 2) x (cellsY + 2), index (cy + 1) * (cellsX + 2) +
    /// cx + 1. A cell's height is the top of its highest column (levels above z = 0; 0 = nothing or off the map); a cell
    /// counts as water when a third of its columns hold standing water, at their highest surface.
    /// </summary>
    public static void Sample(VoxelWorld world, Fluids.Water? water, int x0, int y0, int cellsX, int cellsY, int cell,
        float[] heights, TileType[] types, Nature.Flora? flora = null)
    {
        int stride = cellsX + 2, rows = cellsY + 2;
        if (heights.Length < stride * rows || types.Length < stride * rows) throw new ArgumentException("Arrays too small.");
        // Columns covered, ring included, clipped to the world.
        int ax = x0 - cell, ay = y0 - cell, bx = x0 + (cellsX + 1) * cell, by = y0 + (cellsY + 1) * cell;
        int cx0 = Math.Max(0, ax), cy0 = Math.Max(0, ay), cx1 = Math.Min(world.Width, bx), cy1 = Math.Min(world.Depth, by);
        int n = stride * rows;
        Span<int> wet = n <= 4096 ? stackalloc int[n] : new int[n];
        wet.Clear();
        var waterTop = new float[n];
        Array.Clear(heights, 0, n);
        Array.Fill(types, TileType.Air, 0, n);
        if (cell > 2)
        {
            SampleSparse(world, water, flora, ax, ay, cx0, cy0, cx1, cy1, stride, cell, heights, types, wet, waterTop);
            return;
        }

        Span<short> top = stackalloc short[Chunk.Size * Chunk.Size];
        Span<TileType> kind = stackalloc TileType[Chunk.Size * Chunk.Size];
        for (int ky = cy0 >> Chunk.Shift; ky <= (cy1 - 1) >> Chunk.Shift; ky++)
        for (int kx = cx0 >> Chunk.Shift; kx <= (cx1 - 1) >> Chunk.Shift; kx++)
        {
            ColumnTops(world, kx, ky, top, kind);
            for (int ly = 0; ly < Chunk.Size; ly++)
            for (int lx = 0; lx < Chunk.Size; lx++)
            {
                int x = (kx << Chunk.Shift) + lx, y = (ky << Chunk.Shift) + ly;
                if (x < cx0 || x >= cx1 || y < cy0 || y >= cy1) continue;
                int i = ((y - ay) / cell) * stride + (x - ax) / cell;
                int z = top[ly * Chunk.Size + lx];
                if (z < 0) continue;
                float ground = z + 1;
                if (ground > heights[i]) { heights[i] = ground; types[i] = world.LookAt(x, y, z, kind[ly * Chunk.Size + lx]); }
                if (water != null && z + 1 < world.Height && water.SurfaceAt(x, y, z + 1) is { } s && s > ground + 0.1f)
                {
                    wet[i]++;
                    waterTop[i] = Math.Max(waterTop[i], s);
                }
            }
        }
        int third = Math.Max(1, cell * cell / 3);
        for (int i = 0; i < n; i++)
            if (wet[i] >= third) { heights[i] = Math.Max(waterTop[i], 1); types[i] = Water; }
    }

    /// <summary>
    /// Coarse cells (more than 2 tiles): a 2x2 grid of columns per cell (at a quarter and three quarters across), not every
    /// column, so a block costs the same to sample at every level of detail (a far level's block covers 4x the columns of the
    /// level below). The highest of the four is the cell's top and the commonest kind its look; it counts as water when two
    /// of the four stand in water, and
    /// (with <paramref name="flora"/>) as <see cref="Forest"/> where trees stand on 6% of its columns.
    /// </summary>
    private static void SampleSparse(VoxelWorld world, Fluids.Water? water, Nature.Flora? flora, int ax, int ay, int cx0, int cy0, int cx1, int cy1,
        int stride, int cell, float[] heights, TileType[] types, Span<int> wet, float[] waterTop)
    {
        int q = cell / 4, n = heights.Length;
        Span<TileType> kinds = stackalloc TileType[4];
        Span<int> counts = stackalloc int[4];
        for (int i = 0; i < n; i++)
        {
            int gx = ax + i % stride * cell, gy = ay + i / stride * cell;
            int distinct = 0, highestKind = -1;
            for (int s = 0; s < 4; s++)
            {
                int x = gx + q + (s & 1) * 2 * q, y = gy + q + (s >> 1) * 2 * q;
                if (x < cx0 || x >= cx1 || y < cy0 || y >= cy1) continue;
                int z = ColumnTop(world, x, y, out var t);
                if (z < 0) continue;
                float ground = z + 1;
                var look = world.LookAt(x, y, z, t);
                int k = 0;
                while (k < distinct && kinds[k] != look) k++;
                if (k == distinct) { kinds[k] = look; counts[k] = 0; distinct++; }
                counts[k]++;
                if (ground > heights[i]) { heights[i] = ground; highestKind = k; }
                if (water != null && z + 1 < world.Height && water.SurfaceAt(x, y, z + 1) is { } sf && sf > ground + 0.1f)
                {
                    wet[i]++;
                    waterTop[i] = Math.Max(waterTop[i], sf);
                }
            }
            // The cell shows the kind most of its columns show (a hilltop or a sand bar in one corner doesn't paint all of
            // it); a tie goes to the highest column's kind.
            if (distinct == 0) continue;
            int best = highestKind;
            for (int k = 0; k < distinct; k++)
                if (counts[k] > counts[best]) best = k;
            types[i] = kinds[best];
        }
        // Woodland by the cell's real tree count (flora's region lists, not the four read columns: trees stand on a few
        // percent of columns even in a wood, so two of four columns rarely both carry one).
        var trees = flora != null ? new int[n] : null;
        if (flora != null)
        {
            int rows = n / stride, x1 = ax + stride * cell, y1 = ay + rows * cell;
            for (int ry = Math.Max(0, ay) >> Nature.Flora.RegionShift; ry <= Math.Min(flora.RegionsY - 1, (y1 - 1) >> Nature.Flora.RegionShift); ry++)
            for (int rx = Math.Max(0, ax) >> Nature.Flora.RegionShift; rx <= Math.Min(flora.RegionsX - 1, (x1 - 1) >> Nature.Flora.RegionShift); rx++)
                foreach (var p in flora.InRegion(rx, ry))
                {
                    int x = p.Position.X, y = p.Position.Y;
                    if (x < ax || y < ay || x >= x1 || y >= y1 || !Nature.PlantInfo.IsTree(p.Kind)) continue;
                    trees![(y - ay) / cell * stride + (x - ax) / cell]++;
                }
        }
        // Woodland where trees stand on 6% of the columns: generated trees are at most one per 3x3 tiles (a dense wood is
        // ~9%), so that is a real wood, not the scatter over meadows.
        int wood = Math.Max(1, (cell * cell * 6 + 50) / 100);
        for (int i = 0; i < n; i++)
            if (wet[i] >= 2) { heights[i] = Math.Max(waterTop[i], 1); types[i] = Water; }
            else if (trees != null && trees[i] >= wood && heights[i] > 0) types[i] = Forest;
    }

    /// <summary>The highest occluding tile of one column (-1: none) and its type; uniform chunks are skipped whole.</summary>
    public static int ColumnTop(VoxelWorld world, int x, int y, out TileType type)
    {
        int kx = x >> Chunk.Shift, ky = y >> Chunk.Shift, lx = x & Chunk.Mask, ly = y & Chunk.Mask;
        for (int kz = world.ChunksZ - 1; kz >= 0; kz--)
        {
            var ch = world.GetChunk(kx, ky, kz);
            if (ch == null) continue;
            if (ch.IsUniform)
            {
                if (!TileInfo.Occludes(ch.UniformType)) continue;
                type = ch.UniformType;
                return (kz << Chunk.Shift) + Chunk.Size - 1;
            }
            for (int lz = Chunk.Size - 1; lz >= 0; lz--)
            {
                var t = ch.Get(lx, ly, lz);
                if (!TileInfo.Occludes(t)) continue;
                type = t;
                return (kz << Chunk.Shift) + lz;
            }
        }
        type = TileType.Air;
        return -1;
    }

    /// <summary>Highest occluding tile of each column of a chunk column (-1: none) and its type. Skips uniform chunks whole.</summary>
    private static void ColumnTops(VoxelWorld world, int kx, int ky, Span<short> top, Span<TileType> kind)
    {
        top.Fill(-1);
        int left = top.Length;
        for (int kz = world.ChunksZ - 1; kz >= 0 && left > 0; kz--)
        {
            var ch = world.GetChunk(kx, ky, kz);
            if (ch == null) continue;
            int zBase = kz << Chunk.Shift;
            if (ch.IsUniform)
            {
                if (!TileInfo.Occludes(ch.UniformType)) continue;
                for (int c = 0; c < top.Length; c++)
                    if (top[c] < 0) { top[c] = (short)(zBase + Chunk.Size - 1); kind[c] = ch.UniformType; }
                return;
            }
            for (int ly = 0; ly < Chunk.Size; ly++)
            for (int lx = 0; lx < Chunk.Size; lx++)
            {
                int c = ly * Chunk.Size + lx;
                if (top[c] >= 0) continue;
                for (int lz = Chunk.Size - 1; lz >= 0; lz--)
                {
                    var t = ch.Get(lx, ly, lz);
                    if (!TileInfo.Occludes(t)) continue;
                    top[c] = (short)(zBase + lz);
                    kind[c] = t;
                    left--;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Meshes a sampled block (see <see cref="Sample"/>): one top per run of equal cells (greedy rectangles), and a side
    /// wherever a cell stands above its neighbour, down to the neighbour's height (merged along the edge).
    /// </summary>
    /// <param name="borderSkirt">Along the block's own border every top hangs a side at least this deep (0: only where the ring
    /// is lower). Blocks of different levels of detail meet there, each with its own idea of the heights across the seam.</param>
    public static void Build(float[] heights, TileType[] types, int cellsX, int cellsY, int x0, int y0, int cell, List<Quad> output,
        float borderSkirt = 0f)
    {
        output.Clear();
        int stride = cellsX + 2;
        int At(int cx, int cy) => (cy + 1) * stride + cx + 1;

        // Tops: greedy rectangles of equal (height, type).
        var done = new bool[cellsX * cellsY];
        for (int cy = 0; cy < cellsY; cy++)
        for (int cx = 0; cx < cellsX;)
        {
            int i = At(cx, cy);
            float h = heights[i];
            var t = types[i];
            if (done[cy * cellsX + cx] || h <= 0) { cx++; continue; }
            bool Same(int x, int y) => !done[y * cellsX + x] && heights[At(x, y)] == h && types[At(x, y)] == t;
            int w = 1;
            while (cx + w < cellsX && Same(cx + w, cy)) w++;
            int d = 1;
            while (cy + d < cellsY)
            {
                bool row = true;
                for (int k = 0; k < w && row; k++) row = Same(cx + k, cy + d);
                if (!row) break;
                d++;
            }
            for (int j = 0; j < d; j++)
            for (int k = 0; k < w; k++) done[(cy + j) * cellsX + cx + k] = true;
            float ax = x0 + cx * cell, ay = y0 + cy * cell, bx = x0 + (cx + w) * cell, by = y0 + (cy + d) * cell, lo = h - Skirt;
            output.Add(Make(new Vector3(ax, ay, h), new Vector3(bx, ay, h), new Vector3(bx, by, h), new Vector3(ax, by, h), FaceDir.PosZ, t));
            // Skirts only where the cell across the edge is a top at the same height (another rectangle: a T-junction). A
            // lower neighbour gets this cell's side, a higher one comes down to below this top with its own.
            Skirts(cx, cy, w, 0, -1, FaceDir.NegY);
            Skirts(cx, cy + d - 1, w, 0, 1, FaceDir.PosY);
            Skirts(cx, cy, d, -1, 0, FaceDir.NegX);
            Skirts(cx + w - 1, cy, d, 1, 0, FaceDir.PosX);

            // Along one edge of the rectangle: runs of cells whose neighbour across it (dx, dy) is at height h.
            void Skirts(int sx, int sy, int len, int dx, int dy, FaceDir dir)
            {
                bool alongX = dy != 0;
                for (int k = 0; k < len;)
                {
                    int qx = alongX ? sx + k : sx, qy = alongX ? sy : sy + k;
                    if (heights[At(qx + dx, qy + dy)] != h) { k++; continue; }
                    int run = 1;
                    while (k + run < len && heights[At(alongX ? sx + k + run + dx : sx + dx, alongX ? sy + dy : sy + k + run + dy)] == h) run++;
                    if (alongX)
                    {
                        float y = dy > 0 ? by : ay, xa = x0 + (sx + k) * cell, xb = xa + run * cell;
                        output.Add(Make(new Vector3(xa, y, lo), new Vector3(xb, y, lo), new Vector3(xb, y, h), new Vector3(xa, y, h), dir, t));
                    }
                    else
                    {
                        float x = dx > 0 ? bx : ax, ya = y0 + (sy + k) * cell, yb = ya + run * cell;
                        output.Add(Make(new Vector3(x, ya, lo), new Vector3(x, yb, lo), new Vector3(x, yb, h), new Vector3(x, ya, h), dir, t));
                    }
                    k += run;
                }
            }
            cx += w;
        }

        // Sides: along x edges (faces +-y) and y edges (faces +-x), merged over runs of equal (height, neighbour, type).
        foreach (var (dx, dy, dir) in new[] { (1, 0, FaceDir.PosX), (-1, 0, FaceDir.NegX), (0, 1, FaceDir.PosY), (0, -1, FaceDir.NegY) })
        {
            bool alongX = dy != 0;                        // the run walks along x for faces facing +-y
            int lines = alongX ? cellsY : cellsX, len = alongX ? cellsX : cellsY;
            for (int line = 0; line < lines; line++)
            {
                // The block's own edge on this side: neighbours across it are seen as at least borderSkirt lower.
                bool border = borderSkirt > 0 && (alongX ? (dy < 0 ? line == 0 : line == cellsY - 1) : (dx < 0 ? line == 0 : line == cellsX - 1));
                float Across(int qx, int qy)
                {
                    float own = heights[At(qx, qy)], other = heights[At(qx + dx, qy + dy)];
                    return border && own > 0 ? Math.Min(other, own - borderSkirt) : other;
                }
            for (int p = 0; p < len;)
            {
                int cx = alongX ? p : line, cy = alongX ? line : p;
                float h = heights[At(cx, cy)], nh = Across(cx, cy);
                var t = types[At(cx, cy)];
                if (h <= nh) { p++; continue; }
                int run = 1;
                while (p + run < len)
                {
                    int qx = alongX ? p + run : line, qy = alongX ? line : p + run;
                    if (heights[At(qx, qy)] != h || Across(qx, qy) != nh || types[At(qx, qy)] != t) break;
                    run++;
                }
                // The face plane and its extent (overlapping its neighbours and the lower top: see Seam).
                float lo = nh - Skirt;
                if (alongX)
                {
                    float y = y0 + (cy + (dy > 0 ? 1 : 0)) * cell, xa = x0 + cx * cell - Seam, xb = x0 + (cx + run) * cell + Seam;
                    output.Add(Make(new Vector3(xa, y, lo), new Vector3(xb, y, lo), new Vector3(xb, y, h), new Vector3(xa, y, h), dir, t));
                }
                else
                {
                    float x = x0 + (cx + (dx > 0 ? 1 : 0)) * cell, ya = y0 + cy * cell - Seam, yb = y0 + (cy + run) * cell + Seam;
                    output.Add(Make(new Vector3(x, ya, lo), new Vector3(x, yb, lo), new Vector3(x, yb, h), new Vector3(x, ya, h), dir, t));
                }
                p += run;
            }
            }
        }
    }

    /// <summary>A quad wound counter-clockwise seen from outside (the <see cref="Quad"/> convention).</summary>
    private static Quad Make(Vector3 a, Vector3 b, Vector3 c, Vector3 d, FaceDir dir, TileType t)
    {
        var (nx, ny, nz) = SectionMesher.Normal(dir);
        return Vector3.Dot(Vector3.Cross(b - a, c - a), new Vector3(nx, ny, nz)) > 0 ? new Quad(a, b, c, d, dir, t) : new Quad(a, d, c, b, dir, t);
    }
}
