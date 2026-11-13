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
/// times over on terrain. Stairs are emitted as step geometry (a half slab plus a raised half on the side of
/// the next step up). The mesh does not depend on the view level; the renderer slices it in a shader.
/// </summary>
public static class SectionMesher
{
    /// <summary>Snapshot padding the mesher needs around the section.</summary>
    public const int Pad = 1;

    /// <summary>Convenience overload that snapshots the world itself.</summary>
    public static void Build(VoxelWorld world, int x0, int y0, int z0, int sx, int sy, int sz, List<Quad> output)
    {
        var box = TileBox.Capture(world, x0 - Pad, y0 - Pad, z0 - Pad, sx + 2 * Pad, sy + 2 * Pad, sz + 2 * Pad);
        Build(box, x0, y0, z0, sx, sy, sz, output);
    }

    /// <param name="tiles">Snapshot covering the section plus <see cref="Pad"/> on every side.</param>
    public static void Build(in TileBox tiles, int x0, int y0, int z0, int sx, int sy, int sz, List<Quad> output)
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
                        if (!TileInfo.Occludes(tiles.Get(c[0], c[1], c[2]))) face = t;
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
            if (tiles.Get(x, y, z) == TileType.Stairs)
                AddStair(tiles, x, y, z, output);
    }

    /// <summary>
    /// Horizontal direction (dx, dy) the step rises towards: towards the next stair step one level up, else
    /// towards a wall, else +x.
    /// </summary>
    public static (int Dx, int Dy) StairFacing<T>(in T tiles, int x, int y, int z) where T : ITileReader
    {
        ReadOnlySpan<(int, int)> dirs = [(1, 0), (0, 1), (-1, 0), (0, -1)];
        foreach (var (dx, dy) in dirs)
            if (tiles.Get(x + dx, y + dy, z + 1) == TileType.Stairs) return (dx, dy);
        foreach (var (dx, dy) in dirs)
            if (TileInfo.Occludes(tiles.Get(x + dx, y + dy, z + 1))) return (dx, dy);
        return (1, 0);
    }

    private static void AddStair(TileBox tiles, int x, int y, int z, List<Quad> output)
    {
        var (dx, dy) = StairFacing(tiles, x, y, z);
        // Raised half: the half of the tile on the rising side (its underside is inside the slab).
        float hx0 = dx > 0 ? x + 0.5f : x, hx1 = dx < 0 ? x + 0.5f : x + 1;
        float hy0 = dy > 0 ? y + 0.5f : y, hy1 = dy < 0 ? y + 0.5f : y + 1;
        AddBox(tiles, x, y, z, new Vector3(hx0, hy0, z + 0.5f), new Vector3(hx1, hy1, z + 1), output, bottom: false);
        // Lower slab: the whole tile, half height. Its top is only exposed on the low half (the tread).
        AddBox(tiles, x, y, z, new Vector3(x, y, z), new Vector3(x + 1, y + 1, z + 0.5f), output, top: false);
        float lx0 = dx < 0 ? x + 0.5f : x, lx1 = dx > 0 ? x + 0.5f : x + 1;
        float ly0 = dy < 0 ? y + 0.5f : y, ly1 = dy > 0 ? y + 0.5f : y + 1;
        output.Add(MakeQuad(2, 0, 1, z + 0.5f, lx0, lx1, ly0, ly1, FaceDir.PosZ, TileType.Stairs));
    }

    /// <summary>Adds a box inside tile (x,y,z), skipping faces flush against an occluding neighbour.</summary>
    private static void AddBox(TileBox tiles, int x, int y, int z, Vector3 min, Vector3 max, List<Quad> output,
        bool top = true, bool bottom = true)
    {
        bool Hidden(float face, float tileEdge, int nx, int ny, int nz) =>
            face == tileEdge && TileInfo.Occludes(tiles.Get(x + nx, y + ny, z + nz));

        if (!Hidden(max.X, x + 1, 1, 0, 0)) output.Add(MakeQuad(0, 1, 2, max.X, min.Y, max.Y, min.Z, max.Z, FaceDir.PosX, TileType.Stairs));
        if (!Hidden(min.X, x, -1, 0, 0)) output.Add(MakeQuad(0, 1, 2, min.X, min.Y, max.Y, min.Z, max.Z, FaceDir.NegX, TileType.Stairs));
        if (!Hidden(max.Y, y + 1, 0, 1, 0)) output.Add(MakeQuad(1, 0, 2, max.Y, min.X, max.X, min.Z, max.Z, FaceDir.PosY, TileType.Stairs));
        if (!Hidden(min.Y, y, 0, -1, 0)) output.Add(MakeQuad(1, 0, 2, min.Y, min.X, max.X, min.Z, max.Z, FaceDir.NegY, TileType.Stairs));
        if (top && !Hidden(max.Z, z + 1, 0, 0, 1)) output.Add(MakeQuad(2, 0, 1, max.Z, min.X, max.X, min.Y, max.Y, FaceDir.PosZ, TileType.Stairs));
        if (bottom && !Hidden(min.Z, z, 0, 0, -1) && z > 0) output.Add(MakeQuad(2, 0, 1, min.Z, min.X, max.X, min.Y, max.Y, FaceDir.NegZ, TileType.Stairs));
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
