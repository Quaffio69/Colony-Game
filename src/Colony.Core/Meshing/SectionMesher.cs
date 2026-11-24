using System.Numerics;
using Colony.Core.World;

namespace Colony.Core.Meshing;

/// <summary>Outward direction of a cube face, in simulation axes (z = up).</summary>
public enum FaceDir : byte { PosX, NegX, PosY, NegY, PosZ, NegZ }

/// <summary>
/// One rectangle of mesh in simulation coordinates. Corners are ordered counter-clockwise when viewed from
/// the side the normal points to (right-handed sim axes); triangulate as (A,B,C), (A,C,D).
/// Mapping sim (x,y,z) to a Y-up engine as (x,z,y) is a reflection, which flips this to clockwise:
/// exactly Godot's front-face convention.
/// </summary>
public readonly record struct Quad(Vector3 A, Vector3 B, Vector3 C, Vector3 D, FaceDir Dir, TileType Tile);

/// <summary>
/// Builds the mesh for an axis-aligned box of the world ("section"). Full cubes are greedy-meshed: visible
/// faces of the same tile type in a plane are merged into large rectangles, which cuts vertex counts many
/// times over on terrain. Up and up/down stairs are emitted as rising steps, down stairs as a rim around the
/// opening. The mesh does not depend on the view level; the renderer slices it in a shader.
/// </summary>
public static class SectionMesher
{
    /// <summary>Snapshot padding the mesher needs around the section.</summary>
    public const int Pad = 1;

    /// <summary>Convenience overload that snapshots the world itself.</summary>
    public static void Build(VoxelWorld world, int x0, int y0, int z0, int sx, int sy, int sz, List<Quad> output)
    {
        var box = TileBox.Capture(world, x0 - Pad, y0 - Pad, z0 - Pad, sx + 2 * Pad, sy + 2 * Pad, sz + 2 * Pad);
        Build(box, x0, y0, z0, sx, sy, sz, output, world.Geology);
    }

    /// <param name="tiles">Snapshot covering the section plus <see cref="Pad"/> on every side.</param>
    public static void Build(in TileBox tiles, int x0, int y0, int z0, int sx, int sy, int sz, List<Quad> output,
        Procedural.Geology? geology = null)
    {
        output.Clear();
        int[] size = [sx, sy, sz];
        int[] origin = [x0, y0, z0];
        Span<int> c = stackalloc int[3];
        var mask = new TileType[Math.Max(sx, Math.Max(sy, sz)) * Math.Max(sx, Math.Max(sy, sz))];

        for (int dir = 0; dir < 6; dir++)
        {
            int axis = dir / 2;                 // 0 = x, 1 = y, 2 = z
            int sign = dir % 2 == 0 ? 1 : -1;   // PosX, NegX, PosY, NegY, PosZ, NegZ
            int ua = axis == 0 ? 1 : 0;         // in-plane axes
            int va = axis == 2 ? 1 : 2;
            int su = size[ua], sv = size[va];

            for (int k = 0; k < size[axis]; k++)
            {
                // 1. Mask of visible faces in this slice.
                bool any = false;
                for (int v = 0; v < sv; v++)
                for (int u = 0; u < su; u++)
                {
                    c[axis] = origin[axis] + k;
                    c[ua] = origin[ua] + u;
                    c[va] = origin[va] + v;
                    var t = tiles.Get(c[0], c[1], c[2]);
                    var face = TileType.Air;
                    if (TileInfo.Occludes(t) && !(axis == 2 && sign < 0 && c[2] == 0)) // world underside is never seen
                    {
                        c[axis] += sign;
                        if (!TileInfo.Occludes(tiles.Get(c[0], c[1], c[2])))
                            face = t == TileType.Stone && geology != null // natural rock is drawn by its kind
                                ? Procedural.Geology.Look(geology.RockAt(c[0] - (axis == 0 ? sign : 0), c[1] - (axis == 1 ? sign : 0), c[2] - (axis == 2 ? sign : 0)))
                                : t;
                    }
                    mask[v * su + u] = face;
                    any |= face != TileType.Air;
                }
                if (!any) continue;

                // 2. Greedy merge into rectangles.
                float plane = origin[axis] + k + (sign > 0 ? 1 : 0);
                for (int v = 0; v < sv; v++)
                for (int u = 0; u < su;)
                {
                    var t = mask[v * su + u];
                    if (t == TileType.Air) { u++; continue; }

                    int w = 1;
                    while (u + w < su && mask[v * su + u + w] == t) w++;
                    int h = 1;
                    while (v + h < sv)
                    {
                        bool rowMatches = true;
                        for (int i = 0; i < w && rowMatches; i++) rowMatches = mask[(v + h) * su + u + i] == t;
                        if (!rowMatches) break;
                        h++;
                    }
                    for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++)
                        mask[(v + j) * su + u + i] = TileType.Air;

                    float u0 = origin[ua] + u, u1 = u0 + w, v0 = origin[va] + v, v1 = v0 + h;
                    output.Add(MakeQuad(axis, ua, va, plane, u0, u1, v0, v1, (FaceDir)dir, t));
                    u += w;
                }
            }
        }

        // Stairs: per-tile step geometry.
        for (int z = z0; z < z0 + sz; z++)
        for (int y = y0; y < y0 + sy; y++)
        for (int x = x0; x < x0 + sx; x++)
        {
            var t = tiles.Get(x, y, z);
            if (TileInfo.GoesUp(t)) AddSteps(tiles, x, y, z, t, output);
            if (t == TileType.DownStair) AddRim(tiles, x, y, z, t, output);
        }
    }

    /// <summary>Steps per stair cell (one level).</summary>
    public const int StepsPerLevel = 3;

    /// <summary>
    /// Horizontal direction (dx, dy) an up stair's steps rise towards. Turns a quarter per level, so a stacked
    /// shaft of up/down stairs reads as a spiral staircase.
    /// </summary>
    public static (int Dx, int Dy) StairFacing(int z) => (z & 3) switch
    {
        0 => (1, 0),
        1 => (0, 1),
        2 => (-1, 0),
        _ => (0, -1),
    };

    /// <summary>Rising steps filling the cell: column k spans [k, k+1]/N along the facing, height (k+1)/N.</summary>
    private static void AddSteps(TileBox tiles, int x, int y, int z, TileType t, List<Quad> output)
    {
        var (dx, dy) = StairFacing(z);
        const float n = StepsPerLevel;
        for (int k = 0; k < StepsPerLevel; k++)
        {
            float a0 = k / n, a1 = (k + 1) / n;
            // Along-axis extent in tile-local [0,1], flipped when rising towards negative x/y.
            float lo = (dx < 0 || dy < 0) ? 1 - a1 : a0, hi = (dx < 0 || dy < 0) ? 1 - a0 : a1;
            var min = dx != 0 ? new Vector3(x + lo, y, z) : new Vector3(x, y + lo, z);
            var max = dx != 0 ? new Vector3(x + hi, y + 1, z + a1) : new Vector3(x + 1, y + hi, z + a1);
            // The face towards the next (taller) step is covered by it.
            FaceDir? covered = k == StepsPerLevel - 1 ? null
                : dx > 0 ? FaceDir.PosX : dx < 0 ? FaceDir.NegX : dy > 0 ? FaceDir.PosY : FaceDir.NegY;
            AddBox(tiles, x, y, z, min, max, t, output, covered);
        }
    }

    /// <summary>A low lip around the edge of a down stair's opening, so the stairwell reads from above.</summary>
    private static void AddRim(TileBox tiles, int x, int y, int z, TileType t, List<Quad> output)
    {
        const float w = 0.12f, h = 0.08f;
        AddBox(tiles, x, y, z, new Vector3(x, y, z), new Vector3(x + 1, y + w, z + h), t, output);
        AddBox(tiles, x, y, z, new Vector3(x, y + 1 - w, z), new Vector3(x + 1, y + 1, z + h), t, output);
        AddBox(tiles, x, y, z, new Vector3(x, y + w, z), new Vector3(x + w, y + 1 - w, z + h), t, output);
        AddBox(tiles, x, y, z, new Vector3(x + 1 - w, y + w, z), new Vector3(x + 1, y + 1 - w, z + h), t, output);
    }

    /// <summary>Adds a box inside tile (x,y,z), skipping faces flush against an occluding neighbour.</summary>
    private static void AddBox(TileBox tiles, int x, int y, int z, Vector3 min, Vector3 max, TileType t,
        List<Quad> output, FaceDir? skip = null)
    {
        bool Hidden(FaceDir dir, float face, float tileEdge, int nx, int ny, int nz) =>
            dir == skip || (face == tileEdge && TileInfo.Occludes(tiles.Get(x + nx, y + ny, z + nz)));

        if (!Hidden(FaceDir.PosX, max.X, x + 1, 1, 0, 0)) output.Add(MakeQuad(0, 1, 2, max.X, min.Y, max.Y, min.Z, max.Z, FaceDir.PosX, t));
        if (!Hidden(FaceDir.NegX, min.X, x, -1, 0, 0)) output.Add(MakeQuad(0, 1, 2, min.X, min.Y, max.Y, min.Z, max.Z, FaceDir.NegX, t));
        if (!Hidden(FaceDir.PosY, max.Y, y + 1, 0, 1, 0)) output.Add(MakeQuad(1, 0, 2, max.Y, min.X, max.X, min.Z, max.Z, FaceDir.PosY, t));
        if (!Hidden(FaceDir.NegY, min.Y, y, 0, -1, 0)) output.Add(MakeQuad(1, 0, 2, min.Y, min.X, max.X, min.Z, max.Z, FaceDir.NegY, t));
        if (!Hidden(FaceDir.PosZ, max.Z, z + 1, 0, 0, 1)) output.Add(MakeQuad(2, 0, 1, max.Z, min.X, max.X, min.Y, max.Y, FaceDir.PosZ, t));
        if (!Hidden(FaceDir.NegZ, min.Z, z, 0, 0, -1) && z > 0) output.Add(MakeQuad(2, 0, 1, min.Z, min.X, max.X, min.Y, max.Y, FaceDir.NegZ, t));
    }

    private static Quad MakeQuad(int axis, int ua, int va, float plane, float u0, float u1, float v0, float v1,
        FaceDir dir, TileType tile)
    {
        Vector3 P(float u, float v)
        {
            Span<float> c = stackalloc float[3];
            c[axis] = plane;
            c[ua] = u;
            c[va] = v;
            return new Vector3(c[0], c[1], c[2]);
        }

        Vector3 a = P(u0, v0), b = P(u1, v0), c = P(u1, v1), d = P(u0, v1);
        var (nx, ny, nz) = Normal(dir);
        // Ensure counter-clockwise from outside regardless of axis order.
        return Vector3.Dot(Vector3.Cross(b - a, c - a), new Vector3(nx, ny, nz)) > 0
            ? new Quad(a, b, c, d, dir, tile)
            : new Quad(a, d, c, b, dir, tile);
    }

    public static (int X, int Y, int Z) Normal(FaceDir dir) => dir switch
    {
        FaceDir.PosX => (1, 0, 0),
        FaceDir.NegX => (-1, 0, 0),
        FaceDir.PosY => (0, 1, 0),
        FaceDir.NegY => (0, -1, 0),
        FaceDir.PosZ => (0, 0, 1),
        FaceDir.NegZ => (0, 0, -1),
        _ => throw new ArgumentOutOfRangeException(nameof(dir)),
    };
}
