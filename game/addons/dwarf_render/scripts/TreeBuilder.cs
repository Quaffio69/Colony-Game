using System;
using System.Collections.Generic;
using Godot;

namespace DwarfRender;

public enum TreeSpecies { Oak = 0, Pine = 1, Birch = 2, Bush = 3 }

/// <summary>
/// Procedural low-poly trees and bushes (port of the diorama's buildFoliage).
/// Build() returns two meshes whose origin is the base of the trunk: a canopy mesh for
/// DwarfMaterials.Foliage and a bark mesh (trunk + branches) using a shared trunk material.
/// Canopies carry an outer shell, an inner core and leaf cards; the foliage shader shows the
/// right parts for the chosen technique. Place the result with a MeshInstance3D (or MultiMesh
/// for forests: build a few variants per species and reuse them).
/// </summary>
public static class TreeBuilder
{
	static readonly Dictionary<TreeSpecies, (string bark, string leaf)> Colours = new()
	{
		[TreeSpecies.Oak] = ("#6a4428", "#5f8a32"), [TreeSpecies.Pine] = ("#5a3a24", "#3f6c3a"),
		[TreeSpecies.Birch] = ("#d8cfbc", "#8aa640"), [TreeSpecies.Bush] = ("#5a3a24", "#5e8a34"),
	};

	static ShaderMaterial _barkMat;
	public static ShaderMaterial BarkMaterial => _barkMat ??= DwarfMaterials.CreateObject(Colors.White, GlyphAtlas.Trunk, null, useVertexColor: true);

	sealed class MeshBuf
	{
		public readonly List<Vector3> Pos = new(), Nor = new();
		public readonly List<Color> Col = new();
		public readonly List<Vector2> Uv = new();
		public readonly List<float> C0 = new(), C1 = new();
		public void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 outward, Color col, Func<Vector3, (Vector3 sph, float layer, float sway, float card, float seed, float part)> attr, Vector2? ua = null, Vector2? ub = null, Vector2? uc = null)
		{
			var n = (b - a).Cross(c - a);
			if (n.Dot(outward) < 0) { (b, c) = (c, b); (ub, uc) = (uc, ub); n = -n; }
			n = n.Normalized();
			// Godot front faces are clockwise: emit a, c, b
			foreach (var (p, uv) in new[] { (a, ua), (c, uc), (b, ub) })
			{
				Pos.Add(p); Nor.Add(n); Col.Add(col); Uv.Add(uv ?? Vector2.Zero);
				if (attr != null)
				{
					var t = attr(p);
					C0.Add(t.sph.X); C0.Add(t.sph.Y); C0.Add(t.sph.Z); C0.Add(t.layer);
					C1.Add(t.sway); C1.Add(t.card); C1.Add(t.seed); C1.Add(t.part);
				}
			}
		}
		public ArrayMesh ToMesh(bool foliage, ArrayMesh into = null, Material material = null)
		{
			if (Pos.Count == 0) return into;
			var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
			arrays[(int)Mesh.ArrayType.Vertex] = Pos.ToArray();
			arrays[(int)Mesh.ArrayType.Normal] = Nor.ToArray();
			arrays[(int)Mesh.ArrayType.Color] = Col.ToArray();
			arrays[(int)Mesh.ArrayType.TexUV] = Uv.ToArray();
			Mesh.ArrayFormat flags = 0;
			if (foliage)
			{
				arrays[(int)Mesh.ArrayType.Custom0] = C0.ToArray();
				arrays[(int)Mesh.ArrayType.Custom1] = C1.ToArray();
				long f = (long)Mesh.ArrayCustomFormat.RgbaFloat;
				flags = (Mesh.ArrayFormat)((f << (int)Mesh.ArrayFormat.FormatCustom0Shift) | (f << (int)Mesh.ArrayFormat.FormatCustom1Shift));
			}
			var m = into ?? new ArrayMesh();
			m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, flags);
			m.SurfaceSetMaterial(m.GetSurfaceCount() - 1, material ?? (foliage ? DwarfMaterials.Foliage : BarkMaterial));
			return m;
		}
	}

	static float JHash(float x, float y, float z) { double v = Math.Sin(x * 127.1 + y * 311.7 + z * 74.7) * 43758.5453; return (float)(v - Math.Floor(v)); }

	// Icosahedron subdivided once (80 faces), unit radius.
	static List<(Vector3, Vector3, Vector3)> Icosphere()
	{
		float t = (1f + MathF.Sqrt(5f)) / 2f;
		var v = new[] { new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0), new Vector3(0, -1, t), new Vector3(0, 1, t),
		                new Vector3(0, -1, -t), new Vector3(0, 1, -t), new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1) };
		int[] f = { 0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11, 1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8, 3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9, 4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1 };
		var tris = new List<(Vector3, Vector3, Vector3)>();
		for (int i = 0; i < f.Length; i += 3)
		{
			Vector3 a = v[f[i]].Normalized(), b = v[f[i + 1]].Normalized(), c = v[f[i + 2]].Normalized();
			Vector3 ab = ((a + b) / 2).Normalized(), bc = ((b + c) / 2).Normalized(), ca = ((c + a) / 2).Normalized();
			tris.Add((a, ab, ca)); tris.Add((b, bc, ab)); tris.Add((c, ca, bc)); tris.Add((ab, bc, ca));
		}
		return tris;
	}
	static readonly List<(Vector3, Vector3, Vector3)> Ico = Icosphere();

	static void Blob(MeshBuf mb, Vector3 c, float r, float squash, int seed, Color col,
	                 Func<Vector3, (Vector3, float, float, float, float, float)> attr)
	{
		Vector3 J(Vector3 p)
		{
			float k = 1 + (JHash(MathF.Round(p.X * 100) + seed, MathF.Round(p.Y * 100), MathF.Round(p.Z * 100)) - 0.5f) * 0.32f;
			return new Vector3(p.X * k * r, p.Y * k * r * squash, p.Z * k * r) + c;
		}
		foreach (var (a, b, d) in Ico) { var A = J(a); var B = J(b); var D = J(d); mb.Tri(A, B, D, (A + B + D) / 3 - c, col, attr); }
	}

	static void Cone(MeshBuf mb, Vector3 c, float r, float h, int seed, Color col, Func<Vector3, (Vector3, float, float, float, float, float)> attr)
	{
		const int N = 7;
		Vector3 Ring(int i, float y, float rr)
		{
			float a = i % N * MathF.Tau / N;
			var p = new Vector3(MathF.Cos(a) * rr, y, MathF.Sin(a) * rr);
			float k = 1 + (JHash(MathF.Round(p.X * 100) + seed, MathF.Round(y * 100), MathF.Round(p.Z * 100)) - 0.5f) * 0.22f;
			return new Vector3(p.X * k, y, p.Z * k) + c;
		}
		float y0 = -h / 2, y1 = 0, y2 = h / 2;
		var top = new Vector3(0, y2, 0) + c;
		for (int i = 0; i < N; i++)
		{
			Vector3 a0 = Ring(i, y0, r), b0 = Ring(i + 1, y0, r), a1 = Ring(i, y1, r * 0.5f), b1 = Ring(i + 1, y1, r * 0.5f);
			var mid = (a0 + b0) / 2 - new Vector3(c.X, a0.Y, c.Z);
			mb.Tri(a0, b0, b1, mid, col, attr); mb.Tri(a0, b1, a1, mid, col, attr);
			mb.Tri(a1, b1, top, mid, col, attr);
			mb.Tri(a0, new Vector3(c.X, y0 + c.Y, c.Z), b0, Vector3.Down, col, attr);
		}
	}

	static void Cylinder(MeshBuf mb, Vector3 a, Vector3 b, float r0, float r1, Color col)
	{
		const int N = 6;
		var axis = (b - a).Normalized();
		var side = Mathf.Abs(axis.Y) < 0.9f ? axis.Cross(Vector3.Up).Normalized() : axis.Cross(Vector3.Right).Normalized();
		var side2 = axis.Cross(side);
		for (int i = 0; i < N; i++)
		{
			float t0 = i * MathF.Tau / N, t1 = (i + 1) * MathF.Tau / N;
			Vector3 d0 = side * MathF.Cos(t0) + side2 * MathF.Sin(t0), d1 = side * MathF.Cos(t1) + side2 * MathF.Sin(t1);
			Vector3 p0 = a + d0 * r0, p1 = a + d1 * r0, q0 = b + d0 * r1, q1 = b + d1 * r1;
			var outward = (d0 + d1) / 2;
			mb.Tri(p0, p1, q1, outward, col, null); mb.Tri(p0, q1, q0, outward, col, null);
			mb.Tri(q0, q1, b, axis, col, null);
		}
	}

	/// <summary>Build one tree or bush. seed varies the shape; cardSize matches DwarfRenderSettings.CardSize.</summary>
	/// <summary>Builds a tree. With <paramref name="into"/>, both parts go into that one mesh (canopy surface, then bark) with
	/// the given materials (for MultiMesh instancing) and it is returned as both.</summary>
	public static (ArrayMesh canopy, ArrayMesh bark) Build(TreeSpecies species, float scale = 1f, int seed = 1, float cardSize = 0.5f,
		ArrayMesh into = null, Material canopyMaterial = null, Material barkMaterial = null, bool leafCards = true)
	{
		var rnd = new Random(seed * 7919 + (int)species * 104729);
		float R() => (float)rnd.NextDouble();
		var (barkHex, leafHex) = Colours[species];
		float tint = 0.9f + R() * 0.2f;
		var leafC = Color.FromHtml(leafHex); leafC = new Color(leafC.R * tint * (0.95f + R() * 0.1f), leafC.G * tint, leafC.B * tint);
		var barkC = Color.FromHtml(barkHex);
		float s = scale, seedF = (int)species + (seed * 0.137f) % 1f;
		var canopy = new MeshBuf(); var bark = new MeshBuf();
		var baseP = new Vector3(0, -0.15f, 0);
		var blobs = new List<(Vector3 c, float r, float sq, Vector3 sphC, float h)>();
		float H = 1; bool cones = false;

		switch (species)
		{
			case TreeSpecies.Oak:
			{
				float h = 3.0f * s; var top = baseP + new Vector3(0, h, 0);
				Cylinder(bark, baseP, top, 0.32f * s, 0.2f * s, barkC);
				var C = top + new Vector3(0, 0.8f * s, 0);
				blobs.Add((top + new Vector3(0, 1.0f * s, 0), 1.8f * s, 0.85f, C, 0));
				int n = 3 + (int)(R() * 2);
				for (int i = 0; i < n; i++)
				{
					float a = seed * 6.283f + i * 6.283f / n + R() * 0.6f;
					var c = top + new Vector3(MathF.Cos(a) * 1.35f * s, (R() * 0.7f - 0.15f) * s, MathF.Sin(a) * 1.35f * s);
					blobs.Add((c, (1.05f + R() * 0.4f) * s, 0.85f, C, 0));
					Cylinder(bark, top + new Vector3(0, -0.7f * s, 0), c, 0.13f * s, 0.07f * s, barkC);
				}
				H = h + 2.8f * s; break;
			}
			case TreeSpecies.Birch:
			{
				float h = 3.8f * s; var top = baseP + new Vector3(0, h, 0);
				Cylinder(bark, baseP, top, 0.17f * s, 0.11f * s, barkC);
				var C = top + new Vector3(0, 0.5f * s, 0);
				blobs.Add((top + new Vector3(0, 0.9f * s, 0), 1.15f * s, 1.35f, C, 0));
				blobs.Add((top + new Vector3(0.5f * s, -0.4f * s, 0.2f * s), 0.9f * s, 1.25f, C, 0));
				blobs.Add((top + new Vector3(-0.4f * s, -0.1f * s, -0.4f * s), 0.85f * s, 1.25f, C, 0));
				H = h + 2.4f * s; break;
			}
			case TreeSpecies.Pine:
			{
				float h = 4.6f * s; var top = baseP + new Vector3(0, h, 0);
				Cylinder(bark, baseP, top, 0.24f * s, 0.1f * s, barkC);
				cones = true; float y = 1.4f * s;
				foreach (var (r, hh) in new[] { (1.75f, 2.1f), (1.35f, 1.9f), (0.95f, 1.7f), (0.55f, 1.3f) })
				{
					var c = baseP + new Vector3(0, y + hh * s / 2, 0);
					blobs.Add((c, r * s, 0, baseP + new Vector3(0, y - 0.4f * s, 0), hh * s));
					y += hh * s * 0.62f;
				}
				H = y + 1.0f * s; break;
			}
			default: // bush
			{
				baseP = Vector3.Zero;
				blobs.Add((baseP + new Vector3(0, 0.45f * s, 0), 0.75f * s, 0.75f, baseP, 0));
				if (R() < 0.7f) blobs.Add((baseP + new Vector3((R() - 0.5f) * 0.9f * s, 0.35f * s, (R() - 0.5f) * 0.9f * s), 0.55f * s, 0.8f, baseP, 0));
				H = 1.8f * s; break;
			}
		}

		foreach (var bl in blobs)
		{
			float sqSph = cones ? 0.6f : 1f;
			Func<Vector3, (Vector3, float, float, float, float, float)> Attr(float layer, float card, float part) => p =>
			{
				var d = p - bl.sphC; var sph = new Vector3(d.X, d.Y * sqSph, d.Z).Normalized();
				return (sph, layer, Mathf.Clamp((p.Y - baseP.Y) / H, 0, 1), card, seedF, part);
			};
			if (cones)
			{
				Cone(canopy, bl.c, bl.r, bl.h, seed * 97, leafC, Attr(0, 0, 0));
				Cone(canopy, bl.c, bl.r * 0.62f, bl.h * 0.8f, seed * 53, leafC, Attr(1, 0, 1));
			}
			else
			{
				Blob(canopy, bl.c, bl.r, bl.sq, seed * 97, leafC, Attr(0, 0, 0));
				Blob(canopy, bl.c, bl.r * 0.66f, bl.sq, seed * 53, leafC, Attr(1, 0, 1));
			}
			// Leaf cards scattered over the blob surface, facing roughly outward.
			var cc = cones ? bl.c + new Vector3(0, -bl.h * 0.15f, 0) : bl.c;
			float cr = cones ? bl.r * 0.8f : bl.r, csq = cones ? bl.h / bl.r * 0.4f : bl.sq;
			float size = cardSize * (cones ? 0.85f : Mathf.Min(1, 0.6f + s * 0.4f));
			int count = leafCards ? (int)MathF.Round(18 * cr * cr) : 0; // cards only matter to the leaf-card technique
			var cardAttr = Attr(0, 1, 2);
			for (int k = 0; k < count; k++)
			{
				var dir = new Vector3(R() * 2 - 1, R() * 2 - 1, R() * 2 - 1).Normalized();
				var pos = cc + new Vector3(dir.X, dir.Y * csq, dir.Z) * cr * (0.78f + 0.3f * R());
				var nrm = (dir + new Vector3(R() - 0.5f, R() - 0.5f, R() - 0.5f) * 1.2f).Normalized();
				var t1 = (Mathf.Abs(nrm.Y) < 0.9f ? nrm.Cross(Vector3.Up) : nrm.Cross(Vector3.Right)).Normalized();
				var t2 = nrm.Cross(t1);
				float rot = R() * MathF.Tau; var ax = t1 * MathF.Cos(rot) + t2 * MathF.Sin(rot); var ay = nrm.Cross(ax);
				Vector3 hx = ax * size * 0.35f, hy = ay * size * 0.5f;
				Vector3 p00 = pos - hx - hy, p10 = pos + hx - hy, p11 = pos + hx + hy, p01 = pos - hx + hy;
				canopy.Tri(p00, p10, p11, nrm, leafC, cardAttr, new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1));
				canopy.Tri(p00, p11, p01, nrm, leafC, cardAttr, new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, 1));
			}
		}
		if (into != null) { canopy.ToMesh(true, into, canopyMaterial); bark.ToMesh(false, into, barkMaterial); return (into, into); }
		return (canopy.ToMesh(true), bark.ToMesh(false));
	}
}
