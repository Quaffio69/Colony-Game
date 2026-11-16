using System.Collections.Generic;
using Godot;

namespace ColonyGame;

/// <summary>
/// Small helpers for building low-poly stand-in models from primitive shapes: a part list of
/// (shape, transform, material) merged into one mesh with one surface per material.
/// </summary>
public static class MeshKit
{
    public sealed class Parts : List<(PrimitiveMesh Mesh, Transform3D At, Material Mat)>
    {
        public void Add(PrimitiveMesh mesh, Transform3D at, Material mat) => Add((mesh, at, mat));
        public void Add(PrimitiveMesh mesh, float x, float y, float z, Material mat) => Add((mesh, At(x, y, z), mat));
    }

    public static Transform3D At(float x, float y, float z) => new(Basis.Identity, new Vector3(x, y, z));

    /// <summary>Rotated by Euler angles (radians; X, then Y, then Z as Godot applies them).</summary>
    public static Transform3D At(float x, float y, float z, float rx, float ry = 0, float rz = 0) =>
        new(Basis.FromEuler(new Vector3(rx, ry, rz)), new Vector3(x, y, z));

    /// <summary>Lying along X (a cylinder's axis turned from Y to X).</summary>
    public static Transform3D AlongX(float x, float y, float z) => new(new Basis(Vector3.Forward, Mathf.Pi / 2), new Vector3(x, y, z));
    /// <summary>Lying along Z (towards the viewer).</summary>
    public static Transform3D AlongZ(float x, float y, float z) => new(new Basis(Vector3.Right, Mathf.Pi / 2), new Vector3(x, y, z));

    public static BoxMesh Box(float x, float y, float z) => new() { Size = new Vector3(x, y, z) };

    public static CylinderMesh Cyl(float radius, float height, int segments = 10) =>
        new() { TopRadius = radius, BottomRadius = radius, Height = height, RadialSegments = segments, Rings = 1 };

    public static CylinderMesh Cone(float bottom, float top, float height, int segments = 10) =>
        new() { TopRadius = top, BottomRadius = bottom, Height = height, RadialSegments = segments, Rings = 1 };

    public static SphereMesh Ball(float radius, float height = -1, int segments = 10) =>
        new() { Radius = radius, Height = height < 0 ? radius * 2 : height, RadialSegments = segments, Rings = segments / 2 };

    /// <summary>A ring lying flat (in the XZ plane).</summary>
    public static TorusMesh Ring(float inner, float outer, int segments = 16) =>
        new() { InnerRadius = inner, OuterRadius = outer, Rings = segments, RingSegments = 6 };

    public static ArrayMesh Compose(IEnumerable<(PrimitiveMesh Mesh, Transform3D At, Material Mat)> parts)
    {
        var mesh = new ArrayMesh();
        var order = new List<Material>();
        var byMat = new Dictionary<Material, List<(PrimitiveMesh, Transform3D)>>();
        foreach (var (m, at, mat) in parts)
        {
            if (!byMat.TryGetValue(mat, out var list))
            {
                byMat[mat] = list = new List<(PrimitiveMesh, Transform3D)>();
                order.Add(mat);
            }
            list.Add((m, at));
        }
        foreach (var mat in order)
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            foreach (var (m, at) in byMat[mat]) st.AppendFrom(m, 0, at);
            st.Commit(mesh);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, mat);
        }
        return mesh;
    }

    /// <summary>sRGB 0xRRGGBB to a Godot colour.</summary>
    public static Color Rgb(uint rgb) => new(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f);
}
