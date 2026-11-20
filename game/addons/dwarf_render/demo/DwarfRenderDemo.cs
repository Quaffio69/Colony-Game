using Godot;

namespace DwarfRender;

/// <summary>
/// Self-contained test: attach to an empty Node3D in a new scene, press F6.
/// Builds a small hilly island with a pond, stone, ore, trees, a torch and a wandering dwarf,
/// then hooks up DwarfRenderer. Right-drag = orbit, wheel = zoom, F10 = sliders.
/// If this works, the kit works in your project; then wire it into the real game.
/// </summary>
public partial class DwarfRenderDemo : Node3D, IVoxelSource
{
	const int SX = 64, SY = 32, SZ = 64;
	readonly byte[] _v = new byte[SX * SY * SZ];
	SkyLight _sky;
	Camera3D _cam;
	Node3D _dwarf;
	float _yaw = 0.8f, _pitch = 0.62f, _size = 60f, _t;
	int _frames;

	public int GetBlock(int x, int y, int z) => (uint)x < SX && (uint)y < SY && (uint)z < SZ ? _v[x + SX * (z + SZ * y)] : 0;
	public float GetSkyLight(int x, int y, int z) => _sky.Get(x, y, z);
	void Set(int x, int y, int z, int b) { if ((uint)x < SX && (uint)y < SY && (uint)z < SZ) _v[x + SX * (z + SZ * y)] = (byte)b; }

	public override void _Ready()
	{
		BlockLooks.RegisterDiorama();
		var noise = new FastNoiseLite { Seed = 7, Frequency = 0.045f };
		for (int x = 0; x < SX; x++)
			for (int z = 0; z < SZ; z++)
			{
				float edge = Mathf.Min(Mathf.Min(x, SX - 1 - x), Mathf.Min(z, SZ - 1 - z)) / 10f;
				int h = (int)(9 + noise.GetNoise2D(x, z) * 7 + Mathf.Min(edge, 1) * 3);
				for (int y = 0; y <= h; y++)
				{
					int b = y == h ? 1 : y > h - 3 ? 2 : y < 5 ? 5 : 4;
					if (b >= 4 && noise.GetNoise3D(x * 3, y * 3, z * 3) > 0.45f) b = 6 + (x + z) % 3;
					Set(x, y, z, b);
				}
				if (h < 9) for (int y = h + 1; y <= 9; y++) Set(x, y, z, 11);   // pond water
			}
		_sky = new SkyLight(GetBlock, Vector3I.Zero, new Vector3I(SX, SY, SZ));

		// Terrain in 32x32x32 chunks (just to show chunked use).
		for (int cx = 0; cx < SX; cx += 32)
			for (int cz = 0; cz < SZ; cz += 32)
			{
				var mesh = VoxelMesher.Build(this, new Vector3I(cx, 0, cz), new Vector3I(32, SY, 32), 0.18f);
				if (mesh != null) AddChild(new MeshInstance3D { Mesh = mesh, Position = new Vector3(cx, 0, cz) });
			}

		// Trees.
		var rng = new RandomNumberGenerator { Seed = 3 };
		for (int i = 0; i < 22; i++)
		{
			int x = rng.RandiRange(4, SX - 5), z = rng.RandiRange(4, SZ - 5), y = SY - 1;
			while (y > 0 && GetBlock(x, y, z) == 0) y--;
			if (GetBlock(x, y, z) != 1) continue;
			var sp = (TreeSpecies)(i % 4);
			var (canopy, bark) = TreeBuilder.Build(sp, rng.RandfRange(0.8f, 1.2f), i + 1);
			var root = new Node3D { Position = new Vector3(x + 0.5f, y + 1, z + 0.5f) };
			AddChild(root);
			if (canopy != null) root.AddChild(new MeshInstance3D { Mesh = canopy });
			if (bark != null) root.AddChild(new MeshInstance3D { Mesh = bark });
		}

		// A torch and a dwarf.
		var torch = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.2f, 0.8f, 0.2f) },
			MaterialOverride = DwarfMaterials.CreateObject(new Color("#ffb050"), GlyphAtlas.Torch, emission: 2f), Position = Top(30, 30) + Vector3.Up * 0.4f };
		torch.AddChild(new OmniLight3D { LightColor = new Color("#ffa040"), OmniRange = 8, LightEnergy = 1.6f, Position = Vector3.Up * 0.5f });
		AddChild(torch);
		_dwarf = new Node3D { Position = Top(32, 32) };
		_dwarf.AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.3f, Height = 1.1f }, Position = Vector3.Up * 0.55f,
			MaterialOverride = DwarfMaterials.CreateObject(new Color("#c8503c"), GlyphAtlas.Dwarf) });
		_dwarf.AddChild(new AsciiMarker { GlyphGroup = GlyphAtlas.Dwarf, Position = Vector3.Up * 0.6f });
		AddChild(_dwarf);

		// Light, environment, camera, renderer.
		var sun = new DirectionalLight3D { Name = "Sun", ShadowEnabled = true, LightEnergy = 1.2f, RotationDegrees = new Vector3(-50, 35, 0) };
		AddChild(sun);
		AddChild(new WorldEnvironment { Environment = new Environment
		{
			BackgroundMode = Environment.BGMode.Color, BackgroundColor = new Color("#9cc4d8"),
			AmbientLightSource = Environment.AmbientSource.Color, AmbientLightColor = new Color("#8a9ab0"), AmbientLightEnergy = 0.6f,
		}});
		_cam = new Camera3D { Name = "GameCamera", Projection = Camera3D.ProjectionType.Orthogonal, Size = _size, Far = 400 };
		AddChild(_cam);
		// Paths are relative to the renderer, which will be a sibling of the camera and sun.
		AddChild(new DwarfRenderer { Name = "DwarfRenderer", GameCamera = "../GameCamera", SunLight = "../Sun" });
		UpdateCamera();
	}

	Vector3 Top(int x, int z) { int y = SY - 1; while (y > 0 && !BlockLooks.IsSolid(GetBlock(x, y, z))) y--; return new Vector3(x + 0.5f, y + 1, z + 0.5f); }

	public override void _UnhandledInput(InputEvent e)
	{
		if (e is InputEventMouseMotion m && (m.ButtonMask & MouseButtonMask.Right) != 0)
		{ _yaw -= m.Relative.X * 0.008f; _pitch = Mathf.Clamp(_pitch + m.Relative.Y * 0.006f, 0.15f, 1.45f); }
		if (e is InputEventMouseButton b && b.Pressed)
		{
			if (b.ButtonIndex == MouseButton.WheelUp) _size = Mathf.Max(4, _size * 0.9f);
			if (b.ButtonIndex == MouseButton.WheelDown) _size = Mathf.Min(600, _size * 1.1f);
		}
	}

	public override void _Process(double delta)
	{
		// Dev: --screenshot=PATH saves a frame once things settle, then quits (lets an agent check the look headlessly).
		// Dev: --id-probe prints which glyph groups the ids pass decoded (checks the roughness-channel encoding).
		if (_frames == 89 && System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--id-probe") >= 0 && DwarfRenderer.Instance?.IdsViewport is { } ids)
		{
			var img = ids.GetTexture().GetImage();
			var counts = new System.Collections.Generic.SortedDictionary<int, int>();
			for (int y = 0; y < img.GetHeight(); y += 2) for (int x = 0; x < img.GetWidth(); x += 2)
			{ int id = (int)System.MathF.Round(img.GetPixel(x, y).R * 255); counts[id] = (counts.TryGetValue(id, out int had) ? had : 0) + 1; }
			GD.Print("[id-probe] " + string.Join(", ", System.Linq.Enumerable.Select(counts, kv => $"{kv.Key}:{kv.Value}")));
		}
		if (++_frames == 90 && System.Array.Find(OS.GetCmdlineUserArgs(), a => a.StartsWith("--screenshot=")) is { } shot)
		{
			GetViewport().GetTexture().GetImage().SavePng(shot["--screenshot=".Length..]);
			GetTree().Quit();
		}
		if (System.Array.Find(OS.GetCmdlineUserArgs(), a => a.StartsWith("--demo-zoom=")) is { } z && _frames == 1) _size = float.Parse(z["--demo-zoom=".Length..], System.Globalization.CultureInfo.InvariantCulture);
		_t += (float)delta;
		var p = new Vector3(32 + Mathf.Cos(_t * 0.3f) * 8, 0, 32 + Mathf.Sin(_t * 0.4f) * 8);
		var top = Top((int)p.X, (int)p.Z);
		_dwarf.Position = new Vector3(p.X, top.Y, p.Z);
		UpdateCamera();
	}

	void UpdateCamera()
	{
		var target = new Vector3(SX / 2f, 10, SZ / 2f);
		var dir = new Vector3(Mathf.Cos(_pitch) * Mathf.Sin(_yaw), Mathf.Sin(_pitch), Mathf.Cos(_pitch) * Mathf.Cos(_yaw));
		_cam.Size = _size;
		_cam.LookAtFromPosition(target + dir * 150, target, Vector3.Up);
	}
}
