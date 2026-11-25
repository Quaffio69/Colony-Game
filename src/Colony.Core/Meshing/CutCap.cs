using Colony.Core.World;

namespace Colony.Core.Meshing;

/// <summary>
/// The cut surface's texture: one byte per column of the map saying what the tile at the view level shows where the tile
/// above it is solid too (the cut hides its top). That is its look (natural rock by kind, <see cref="VoxelWorld.LookAt"/>),
/// <see cref="TileInfo.Hidden"/> (fog of war) where no open cell touches it, or 0 where nothing is cut. A whole map is
/// filled on every level change, so it lives here (optimized even in Debug) and runs in bands across threads: in the game
/// assembly it cost 105 ms a level on a 2048 map.
/// </summary>
public static class CutCap
{
    /// <summary>Rows per band of a whole-map fill (one band per worker task).</summary>
    public const int Band = 32;

    [ThreadStatic] private static TileType[]? _box;

    /// <summary>Fills the whole map's texels for a view level, in parallel bands. Reads the world: call it while nothing steps the sim.</summary>
    public static void FillAll(VoxelWorld world, int level, byte[] dest)
    {
        int bands = (world.Depth + Band - 1) / Band;
        Parallel.For(0, bands, b => Fill(world, level, 0, b * Band, world.Width, Math.Min(Band, world.Depth - b * Band), dest));
    }

    /// <summary>Fills the texels of a rectangle (dest rows are the map's width), reading the levels below, at and above
    /// the cut with a ring round the rectangle.</summary>
    public static void Fill(VoxelWorld world, int level, int x0, int y0, int sx, int sy, byte[] dest)
    {
        int bx = sx + 2, by = sy + 2, plane = bx * by;
        var box = _box;
        if (box == null || box.Length < plane * 3) _box = box = new TileType[plane * 3];
        world.CopyBox(x0 - 1, y0 - 1, level - 1, bx, by, 3, box);
        world.MaskUnseen(x0 - 1, y0 - 1, level - 1, bx, by, 3, box); // unseen cavern air: rock, as far as anyone knows
        // Off the map's sides counts as rock (no view in from there); CopyBox leaves it air, so fill the ring in.
        bool left = x0 == 0, right = x0 + sx >= world.Width, front = y0 == 0, back = y0 + sy >= world.Depth;
        for (int z = 0; z < 3; z++)
        {
            int p = z * plane;
            if (front) box.AsSpan(p, bx).Fill(TileType.Stone);
            if (back) box.AsSpan(p + (by - 1) * bx, bx).Fill(TileType.Stone);
            for (int y = 0; y < by; y++)
            {
                if (left) box[p + y * bx] = TileType.Stone;
                if (right) box[p + y * bx + bx - 1] = TileType.Stone;
            }
        }
        bool bottom = level == 0; // under the world: rock
        for (int y = 0; y < sy; y++)
        {
            int row = (y0 + y) * world.Width + x0;
            for (int x = 0; x < sx; x++)
            {
                int i = plane + (y + 1) * bx + x + 1;
                var t = box[i];
                byte texel = 0;
                if (TileInfo.IsSolid(t) && TileInfo.IsSolid(box[i + plane]))
                {
                    bool hidden = (bottom || TileInfo.IsSolid(box[i - plane])) && TileInfo.IsSolid(box[i - 1]) && TileInfo.IsSolid(box[i + 1])
                                  && TileInfo.IsSolid(box[i - bx]) && TileInfo.IsSolid(box[i + bx]);
                    texel = hidden ? (byte)TileInfo.Hidden : (byte)world.LookAt(x0 + x, y0 + y, level, t);
                }
                dest[row + x] = texel;
            }
        }
    }
}
