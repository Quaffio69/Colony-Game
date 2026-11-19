using System;
using System.Collections.Generic;
using Godot;

namespace DwarfRender;

/// <summary>What the mesher needs from your world. Coordinates are WORLD voxel coordinates
/// and must also answer for neighbours just outside the chunk (borders, AO, rounding).</summary>
public interface IVoxelSource
{
	/// <summary>Block type id at a voxel (0 = air). Outside the loaded world, return 0.</summary>
	int GetBlock(int x, int y, int z);
	/// <summary>Sky exposure 0..1 of an AIR cell: 1 under open sky, fading to 0 deep in caves.
	/// (See SkyLight.Bake for a simple flood-fill implementation.)</summary>
	float GetSkyLight(int x, int y, int z);
}

/// <summary>
/// Port of the diorama's greedy mesher with edge rounding. Produces one ArrayMesh per chunk
/// carrying everything voxel_terrain.gdshader needs (see the comment at the top of that shader).
/// Faces merge only when block type, sky light, uniform AO and rounding flags match; rounding
/// insets "unconnected" edges (in-plane neighbour is air) and adds bevel strips, corner
/// triangles and caps. Output is watertight (verified in the prototype).
/// Assign DwarfMaterials.Terrain as the MeshInstance3D material.
/// </summary>
public static class VoxelMesher
{
	static readonly int[][] DIRS = { new[] { 1, 0, 0 }, new[] { -1, 0, 0 }, new[] { 0, 1, 0 }, new[] { 0, -1, 0 }, new[] { 0, 0, 1 }, new[] { 0, 0, -1 } };
	static readonly float[] AOB = { 0.5f, 0.68f, 0.84f, 1.0f };
	static readonly int[,] CORN = { { 0, 0 }, { 1, 0 }, { 1, 1 }, { 0, 1 } };

	sealed class Buf
	{
		public readonly List<Vector3> Pos = new(), Nor = new();
		public readonly List<Color> Col = new();
		public readonly List<float> C0 = new(), C1 = new(), C2 = new(), C3 = new();
		public readonly List<int> Idx = new();
		public int Vert(Vector3 p, Vector3 n, int t, float ao, float L, Vector3 tp, Vector3 ln, Vector3? cell)
		{
			var look = BlockLooks.Get(t);
			Pos.Add(p); Nor.Add(n); Col.Add(look.Color);
			C0.Add(ao); C0.Add(L); C0.Add(look.Emissive ? 1 : 0); C0.Add((int)look.Class);
			C1.Add(tp.X); C1.Add(tp.Y); C1.Add(tp.Z); C1.Add(t);
			C2.Add(ln.X); C2.Add(ln.Y); C2.Add(ln.Z); C2.Add(look.GlyphGroup);
			var c = cell ?? Vector3.Zero;
			C3.Add(c.X); C3.Add(c.Y); C3.Add(c.Z); C3.Add(cell.HasValue ? 1 : 0);
			return Pos.Count - 1;
		}
	}

	static Vector3 V(int[] d) => new(d[0], d[1], d[2]);
	static bool Facing(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 outDir) => (p1 - p0).Cross(p2 - p0).Dot(outDir) >= 0;

	/// <summary>Build the mesh for the box [origin, origin+size). Returns null if empty.</summary>
	public static ArrayMesh Build(IVoxelSource src, Vector3I origin, Vector3I size, float rounding)
	{
		var b = new Buf();
		int ox = origin.X, oy = origin.Y, oz = origin.Z;
		int[] dims = { size.X, size.Y, size.Z };
		int Get(int x, int y, int z) => src.GetBlock(ox + x, oy + y, oz + z);           // local -> world
		bool Solid(int x, int y, int z) => BlockLooks.IsSolid(Get(x, y, z));
		int Occ(int x, int y, int z) => BlockLooks.IsOpaque(Get(x, y, z)) ? 1 : 0;
		float Sky(int x, int y, int z) => src.GetSkyLight(ox + x, oy + y, oz + z);
		bool Roundable(int t) => t != 0 && BlockLooks.Get(t).Roundable;

		void Tri(Vector3[] P, Vector3[] N, int t, float L, Vector3 outDir, Vector3[] tp, Vector3 ln, Vector3 cell)
		{
			int[] o = Facing(P[0], P[1], P[2], outDir) ? new[] { 0, 1, 2 } : new[] { 0, 2, 1 };
			foreach (var k in o) b.Idx.Add(b.Vert(P[k], N[k], t, 1, L, tp[k], ln, cell));
		}
		void Quad(Vector3[] P, Vector3[] N, int t, float L, Vector3 outDir, Vector3[] tp, Vector3 ln, Vector3 cell)
		{
			bool fw = Facing(P[0], P[1], P[2], outDir);
			var v = new int[4];
			for (int k = 0; k < 4; k++) v[k] = b.Vert(P[k], N[k], t, 1, L, tp[k], ln, cell);
			if (fw) b.Idx.AddRange(new[] { v[0], v[1], v[2], v[0], v[2], v[3] });
			else b.Idx.AddRange(new[] { v[0], v[2], v[1], v[0], v[3], v[2] });
		}

		// ---- 1. Greedy faces (inset along rounded edges) ----
		for (int d = 0; d < 3; d++)
		{
			int u = (d + 1) % 3, w = (d + 2) % 3, nu = dims[u], nv = dims[w];
			var mask = new long[nu * nv]; var mT = new int[nu * nv]; var mAO = new int[nu * nv * 4]; var mL = new int[nu * nv]; var mF = new int[nu * nv];
			var x = new int[3]; var a = new int[3]; var bb = new int[3];
			foreach (int side in new[] { -1, 1 })
			{
				for (x[d] = 0; x[d] < dims[d]; x[d]++)
				{
					int n = 0;
					for (x[w] = 0; x[w] < nv; x[w]++)
						for (x[u] = 0; x[u] < nu; x[u]++, n++)
						{
							mask[n] = 0;
							int t = Get(x[0], x[1], x[2]);
							if (!BlockLooks.IsSolid(t)) continue;
							a[0] = x[0]; a[1] = x[1]; a[2] = x[2]; a[d] += side;
							if (Solid(a[0], a[1], a[2])) continue;
							bool uniform = true;
							for (int k = 0; k < 4; k++)
							{
								int su = CORN[k, 0] == 1 ? 1 : -1, sv = CORN[k, 1] == 1 ? 1 : -1;
								bb[0] = a[0]; bb[1] = a[1]; bb[2] = a[2]; bb[u] += su; int s1 = Occ(bb[0], bb[1], bb[2]);
								bb[u] -= su; bb[w] += sv; int s2 = Occ(bb[0], bb[1], bb[2]);
								bb[u] += su; int c = Occ(bb[0], bb[1], bb[2]);
								mAO[n * 4 + k] = (s1 == 1 && s2 == 1) ? 0 : 3 - (s1 + s2 + c);
								if (mAO[n * 4 + k] != mAO[n * 4]) uniform = false;
							}
							int fl = 0;
							if (rounding > 0 && Roundable(t))
							{
								bb[0] = x[0]; bb[1] = x[1]; bb[2] = x[2];
								bb[u] -= 1; if (!Solid(bb[0], bb[1], bb[2])) fl |= 1;
								bb[u] += 2; if (!Solid(bb[0], bb[1], bb[2])) fl |= 2;
								bb[u] -= 1; bb[w] -= 1; if (!Solid(bb[0], bb[1], bb[2])) fl |= 4;
								bb[w] += 2; if (!Solid(bb[0], bb[1], bb[2])) fl |= 8;
							}
							int L = (int)Mathf.Round(Sky(a[0], a[1], a[2]) * 7);
							mT[n] = t; mL[n] = L; mF[n] = fl;
							long key = 1 + t + 4096L * (mAO[n * 4] + 4 * mAO[n * 4 + 1] + 16 * mAO[n * 4 + 2] + 64 * mAO[n * 4 + 3])
							         + 4096L * 256 * L + 4096L * 256 * 8 * fl;
							if (!uniform) key += 4096L * 256 * 8 * 16 * (n + 1);
							mask[n] = key;
						}
					n = 0;
					for (int j = 0; j < nv; j++)
						for (int i = 0; i < nu;)
						{
							long k = mask[n];
							if (k == 0) { i++; n++; continue; }
							int wd = 1; while (i + wd < nu && mask[n + wd] == k) wd++;
							int h = 1; bool stop = false;
							for (; j + h < nv; h++) { for (int m = 0; m < wd; m++) if (mask[n + m + h * nu] != k) { stop = true; break; } if (stop) break; }
							int t = mT[n], fl = mF[n];
							float i0 = i + ((fl & 1) != 0 ? rounding : 0), i1 = i + wd - ((fl & 2) != 0 ? rounding : 0);
							float j0 = j + ((fl & 4) != 0 ? rounding : 0), j1 = j + h - ((fl & 8) != 0 ? rounding : 0);
							Vector3 P(float uu, float vv) { var p = new float[3]; p[d] = x[d] + (side > 0 ? 1 : 0); p[u] = uu; p[w] = vv; return new Vector3(p[0], p[1], p[2]); }
							var corners = new[] { P(i0, j0), P(i1, j0), P(i1, j1), P(i0, j1) };
							var nrmA = new float[3]; nrmA[d] = side; var nrm = new Vector3(nrmA[0], nrmA[1], nrmA[2]);
							var ao = new[] { mAO[n * 4], mAO[n * 4 + 1], mAO[n * 4 + 2], mAO[n * 4 + 3] };
							int baseV = b.Pos.Count;
							for (int c = 0; c < 4; c++) b.Vert(corners[c], nrm, t, AOB[ao[c]], mL[n] / 7f, corners[c], nrm, null);
							bool flip = ao[0] + ao[2] < ao[1] + ao[3];
							if (side > 0) b.Idx.AddRange(flip ? new[] { baseV, baseV + 1, baseV + 3, baseV + 1, baseV + 2, baseV + 3 } : new[] { baseV, baseV + 1, baseV + 2, baseV, baseV + 2, baseV + 3 });
							else b.Idx.AddRange(flip ? new[] { baseV, baseV + 3, baseV + 1, baseV + 1, baseV + 3, baseV + 2 } : new[] { baseV, baseV + 2, baseV + 1, baseV, baseV + 3, baseV + 2 });
							for (int hh = 0; hh < h; hh++) for (int ww = 0; ww < wd; ww++) mask[n + ww + hh * nu] = 0;
							i += wd; n += wd;
						}
				}
			}
		}

		// ---- 2. Bevel strips, corner triangles and caps ----
		if (rounding > 0)
		{
			float r = rounding;
			var ex = new bool[6];
			float Fc(int k) => (k & 1) == 1 ? 0 : 1;          // face plane coordinate for direction k
			float Ic(int k) => (k & 1) == 1 ? r : 1 - r;      // inset coordinate
			for (int y = 0; y < dims[1]; y++)
				for (int z = 0; z < dims[2]; z++)
					for (int x = 0; x < dims[0]; x++)
					{
						int t = Get(x, y, z); if (!BlockLooks.IsSolid(t) || !Roundable(t)) continue;
						bool any = false;
						for (int k = 0; k < 6; k++) { var D = DIRS[k]; ex[k] = !Solid(x + D[0], y + D[1], z + D[2]); any |= ex[k]; }
						if (!any) continue;
						var cellC = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
						Vector3 At(float lx, float ly, float lz) => new(x + lx, y + ly, z + lz);
						Vector3 AtA(float[] l) => At(l[0], l[1], l[2]);
						float Lof(params int[] ks) { float best = 0; foreach (var k in ks) { var D = DIRS[k]; best = Mathf.Max(best, Sky(x + D[0], y + D[1], z + D[2])); } return Mathf.Round(best * 7) / 7f; }
						for (int ia = 0; ia < 3; ia++)
							for (int ib = ia + 1; ib < 3; ib++)
							{
								int icx = 3 - ia - ib;
								foreach (int ka in new[] { ia * 2, ia * 2 + 1 })
									foreach (int kb in new[] { ib * 2, ib * 2 + 1 })
									{
										if (!ex[ka] || !ex[kb]) continue;
										Vector3 nA = V(DIRS[ka]), nB = V(DIRS[kb]), outDir = nA + nB; float L = Lof(ka, kb);
										int kcp = icx * 2, kcm = icx * 2 + 1;
										Vector3 PA(float c) { var l = new float[3]; l[ia] = Fc(ka); l[ib] = Ic(kb); l[icx] = c; return AtA(l); }
										Vector3 PB(float c) { var l = new float[3]; l[ia] = Ic(ka); l[ib] = Fc(kb); l[icx] = c; return AtA(l); }
										Vector3 PK(float c) { var l = new float[3]; l[ia] = Fc(ka); l[ib] = Fc(kb); l[icx] = c; return AtA(l); }
										float c0 = ex[kcm] ? r : 0, c1 = ex[kcp] ? 1 - r : 1;
										// Texture the bevel as the top face if it touches one (else face A), taking UVs
										// from that face's edge line so it continues the face's border texels.
										bool useB = nB.Y > 0 && !(nA.Y > 0);
										Func<float, Vector3> E = useB ? new Func<float, Vector3>(PB) : new Func<float, Vector3>(PA);
										Quad(new[] { PA(c0), PA(c1), PB(c1), PB(c0) }, new[] { nA, nA, nB, nB }, t, L, outDir,
										     new[] { E(c0), E(c1), E(c1), E(c0) }, useB ? nB : nA, cellC);
										// Caps where the bevel runs into a solid neighbour that doesn't continue it.
										foreach (var (kc, cc) in new[] { (kcm, 0f), (kcp, 1f) })
										{
											if (ex[kc]) continue;
											var D = DIRS[kc]; int nx = x + D[0], ny = y + D[1], nz = z + D[2]; int tn = Get(nx, ny, nz);
											bool cont = Roundable(tn) && !Solid(nx + (int)nA.X, ny + (int)nA.Y, nz + (int)nA.Z) && !Solid(nx + (int)nB.X, ny + (int)nB.Y, nz + (int)nB.Z);
											if (cont) continue;
											var capN = -V(D); var pk = PK(cc);
											Tri(new[] { PA(cc), PB(cc), pk }, new[] { capN, capN, capN }, tn, L, capN,
											    new[] { pk, pk, pk }, capN, new Vector3(nx + 0.5f, ny + 0.5f, nz + 0.5f));
										}
									}
							}
						// Corner triangles where three exposed faces meet.
						foreach (int kx in new[] { 0, 1 })
							foreach (int ky in new[] { 2, 3 })
								foreach (int kz in new[] { 4, 5 })
								{
									if (!ex[kx] || !ex[ky] || !ex[kz]) continue;
									Vector3 n0 = V(DIRS[kx]), n1 = V(DIRS[ky]), n2 = V(DIRS[kz]);
									var py = At(Ic(kx), Fc(ky), Ic(kz));
									Tri(new[] { At(Fc(kx), Ic(ky), Ic(kz)), py, At(Ic(kx), Ic(ky), Fc(kz)) }, new[] { n0, n1, n2 }, t, Lof(kx, ky, kz),
									    n0 + n1 + n2, new[] { py, py, py }, n1, cellC);
								}
					}
		}

		if (b.Idx.Count == 0) return null;
		// The math above produces counter-clockwise front faces; Godot wants clockwise.
		for (int i = 0; i < b.Idx.Count; i += 3) (b.Idx[i + 1], b.Idx[i + 2]) = (b.Idx[i + 2], b.Idx[i + 1]);

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = b.Pos.ToArray();
		arrays[(int)Mesh.ArrayType.Normal] = b.Nor.ToArray();
		arrays[(int)Mesh.ArrayType.Color] = b.Col.ToArray();
		arrays[(int)Mesh.ArrayType.Custom0] = b.C0.ToArray();
		arrays[(int)Mesh.ArrayType.Custom1] = b.C1.ToArray();
		arrays[(int)Mesh.ArrayType.Custom2] = b.C2.ToArray();
		arrays[(int)Mesh.ArrayType.Custom3] = b.C3.ToArray();
		arrays[(int)Mesh.ArrayType.Index] = b.Idx.ToArray();
		long f = (long)Mesh.ArrayCustomFormat.RgbaFloat;
		var flags = (Mesh.ArrayFormat)((f << (int)Mesh.ArrayFormat.FormatCustom0Shift) | (f << (int)Mesh.ArrayFormat.FormatCustom1Shift)
		                             | (f << (int)Mesh.ArrayFormat.FormatCustom2Shift) | (f << (int)Mesh.ArrayFormat.FormatCustom3Shift));
		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, flags);
		mesh.SurfaceSetMaterial(0, DwarfMaterials.Terrain);
		return mesh;
	}
}

/// <summary>Simple sky-exposure flood fill for a region (the diorama's method): air above the
/// highest solid block in its column = 1, air touching the region boundary = 0.85, then light
/// spreads through air losing 0.13 per step. For a huge world, bake per chunk with a margin,
/// or plug in your own lighting through IVoxelSource.GetSkyLight.</summary>
public sealed class SkyLight
{
	readonly float[] _light; readonly Vector3I _min, _size;
	public SkyLight(Func<int, int, int, int> getBlock, Vector3I min, Vector3I size)
	{
		_min = min; _size = size; _light = new float[size.X * size.Y * size.Z];
		int I(int x, int y, int z) => x + size.X * (z + size.Z * y);
		var top = new int[size.X * size.Z];
		for (int x = 0; x < size.X; x++) for (int z = 0; z < size.Z; z++)
		{
			top[x + size.X * z] = -1;
			for (int y = size.Y - 1; y >= 0; y--) if (BlockLooks.IsSolid(getBlock(min.X + x, min.Y + y, min.Z + z))) { top[x + size.X * z] = y; break; }
		}
		var q = new Queue<int>();
		for (int y = 0; y < size.Y; y++) for (int z = 0; z < size.Z; z++) for (int x = 0; x < size.X; x++)
		{
			if (BlockLooks.IsSolid(getBlock(min.X + x, min.Y + y, min.Z + z))) continue;
			int i = I(x, y, z);
			if (y > top[x + size.X * z]) { _light[i] = 1; q.Enqueue(i); }
			else if (x == 0 || z == 0 || x == size.X - 1 || z == size.Z - 1) { _light[i] = 0.85f; q.Enqueue(i); }
		}
		int[][] nb = { new[] { 1, 0, 0 }, new[] { -1, 0, 0 }, new[] { 0, 1, 0 }, new[] { 0, -1, 0 }, new[] { 0, 0, 1 }, new[] { 0, 0, -1 } };
		while (q.Count > 0)
		{
			int i = q.Dequeue(); float L = _light[i] - 0.13f; if (L <= 0) continue;
			int x = i % size.X, z = (i / size.X) % size.Z, y = i / (size.X * size.Z);
			foreach (var d in nb)
			{
				int nx = x + d[0], ny = y + d[1], nz = z + d[2];
				if (nx < 0 || ny < 0 || nz < 0 || nx >= size.X || ny >= size.Y || nz >= size.Z) continue;
				if (BlockLooks.IsSolid(getBlock(min.X + nx, min.Y + ny, min.Z + nz))) continue;
				int ni = I(nx, ny, nz); if (_light[ni] < L) { _light[ni] = L; q.Enqueue(ni); }
			}
		}
	}
	/// <summary>Sky exposure at a world cell (outside the baked region: below = 0.5, otherwise 1).</summary>
	public float Get(int x, int y, int z)
	{
		int lx = x - _min.X, ly = y - _min.Y, lz = z - _min.Z;
		if (lx < 0 || ly < 0 || lz < 0 || lx >= _size.X || ly >= _size.Y || lz >= _size.Z) return ly < 0 ? 0.5f : 1f;
		return _light[lx + _size.X * (lz + _size.Z * ly)];
	}
}
