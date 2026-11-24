using System.Numerics;

namespace Colony.Core.World;

/// <param name="Tile">The solid tile that was hit.</param>
/// <param name="Normal">Outward normal of the face entered; (0,0,0) if the ray started inside a tile.</param>
/// <param name="Distance">Distance along the normalised ray to the hit.</param>
public readonly record struct RayHit(TilePos Tile, TilePos Normal, float Distance);

/// <summary>Grid traversal raycast (Amanatides and Woo) through a <see cref="VoxelWorld"/>.</summary>
public static class VoxelRaycast
{
    /// <param name="origin">Ray start, in simulation tile units.</param>
    /// <param name="direction">Ray direction; need not be normalised.</param>
    /// <param name="maxLevel">Tiles above this level are ignored (the view slice).</param>
    public static RayHit? Cast(VoxelWorld world, Vector3 origin, Vector3 direction, float maxDistance,
        int maxLevel = int.MaxValue)
    {
        if (direction == Vector3.Zero) return null;
        direction = Vector3.Normalize(direction);

        int topLevel = Math.Min(world.Height - 1, maxLevel);
        if (topLevel < 0) return null;

        // Skip the empty space between an outside camera and the world's bounding box.
        var boxMax = new Vector3(world.Width, world.Depth, topLevel + 1);
        if (!ClipToBox(origin, direction, boxMax, out float tEnter, out float tExit)) return null;
        float t = MathF.Max(0f, tEnter);
        if (t > maxDistance) return null;
        maxDistance = MathF.Min(maxDistance, tExit);

        var start = origin + direction * t;
        // Entering exactly on a max face would floor one cell outside; clamp back in.
        int x = Math.Clamp((int)MathF.Floor(start.X), 0, world.Width - 1);
        int y = Math.Clamp((int)MathF.Floor(start.Y), 0, world.Depth - 1);
        int z = Math.Clamp((int)MathF.Floor(start.Z), 0, topLevel);

        int stepX = Math.Sign(direction.X), stepY = Math.Sign(direction.Y), stepZ = Math.Sign(direction.Z);
        float tDeltaX = stepX != 0 ? MathF.Abs(1f / direction.X) : float.PositiveInfinity;
        float tDeltaY = stepY != 0 ? MathF.Abs(1f / direction.Y) : float.PositiveInfinity;
        float tDeltaZ = stepZ != 0 ? MathF.Abs(1f / direction.Z) : float.PositiveInfinity;
        float tMaxX = t + NextBoundary(start.X, x, stepX) * tDeltaX;
        float tMaxY = t + NextBoundary(start.Y, y, stepY) * tDeltaY;
        float tMaxZ = t + NextBoundary(start.Z, z, stepZ) * tDeltaZ;

        var normal = tEnter > 0 ? EntryNormal(start, boxMax, direction) : new TilePos(0, 0, 0);

        while (t <= maxDistance)
        {
            if (world.Get(x, y, z) != TileType.Air || world.IsUnseen(x, y, z)) // stairs are pickable too; unseen cavern air reads as rock
                return new RayHit(new TilePos(x, y, z), normal, t);

            if (tMaxX < tMaxY && tMaxX < tMaxZ)
            {
                x += stepX; t = tMaxX; tMaxX += tDeltaX; normal = new TilePos(-stepX, 0, 0);
            }
            else if (tMaxY < tMaxZ)
            {
                y += stepY; t = tMaxY; tMaxY += tDeltaY; normal = new TilePos(0, -stepY, 0);
            }
            else
            {
                z += stepZ; t = tMaxZ; tMaxZ += tDeltaZ; normal = new TilePos(0, 0, -stepZ);
            }

            if ((uint)x >= (uint)world.Width || (uint)y >= (uint)world.Depth || (uint)z > (uint)topLevel)
                return null;
        }

        return null;
    }

    /// <summary>Intersects a ray with the horizontal plane at height <paramref name="planeZ"/>.</summary>
    public static Vector3? IntersectLevelPlane(Vector3 origin, Vector3 direction, float planeZ)
    {
        if (MathF.Abs(direction.Z) < 1e-6f) return null;
        float t = (planeZ - origin.Z) / direction.Z;
        return t < 0 ? null : origin + direction * t;
    }

    private static float NextBoundary(float pos, int cell, int step) =>
        step > 0 ? cell + 1 - pos : step < 0 ? pos - cell : float.PositiveInfinity;

    private static TilePos EntryNormal(Vector3 p, Vector3 boxMax, Vector3 d)
    {
        const float eps = 1e-3f;
        if (d.Z < 0 && MathF.Abs(p.Z - boxMax.Z) < eps) return new TilePos(0, 0, 1);
        if (d.Z > 0 && MathF.Abs(p.Z) < eps) return new TilePos(0, 0, -1);
        if (d.X < 0 && MathF.Abs(p.X - boxMax.X) < eps) return new TilePos(1, 0, 0);
        if (d.X > 0 && MathF.Abs(p.X) < eps) return new TilePos(-1, 0, 0);
        if (d.Y < 0 && MathF.Abs(p.Y - boxMax.Y) < eps) return new TilePos(0, 1, 0);
        return new TilePos(0, -1, 0);
    }

    private static bool ClipToBox(Vector3 o, Vector3 d, Vector3 max, out float tEnter, out float tExit)
    {
        tEnter = float.NegativeInfinity;
        tExit = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            float oa = o[axis], da = d[axis], hi = max[axis];
            if (MathF.Abs(da) < 1e-9f)
            {
                if (oa < 0 || oa > hi) return false;
                continue;
            }
            float t1 = -oa / da, t2 = (hi - oa) / da;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tEnter = MathF.Max(tEnter, t1);
            tExit = MathF.Min(tExit, t2);
        }
        return tEnter <= tExit && tExit >= 0;
    }
}
