using System;
using System.Collections.Generic;
using Godot;

namespace DwarfRender;

/// <summary>
/// The pixel-art / ASCII renderer. Add ONE of these to your main scene and point GameCamera
/// at your existing Camera3D. Your camera keeps doing everything it does now (movement, zoom,
/// mouse picking); this node copies it every frame into two low-resolution SubViewports that
/// share your 3D world, applies the post pass there, and draws the result full-screen
/// underneath your UI.
///
///   Art viewport  : your scene at art resolution + post_color.gdshader (outlines, palette...)
///   Ids viewport  : same view, writes object id + depth per art pixel (only while ASCII shows)
///   Present       : full-screen TextureRect with present.gdshader (upscale, magnify, ASCII, CRT)
///
/// Pipeline order and maths are a 1:1 port of the "Voxel Pixel Shader Diorama" prototype.
/// </summary>
public partial class DwarfRenderer : Node
{
	public static DwarfRenderer Instance { get; private set; }

	[Export] public NodePath GameCamera { get; set; }
	[Export] public DwarfRenderSettings Settings { get; set; }
	[Export] public NodePath SunLight { get; set; }                 // optional: DirectionalLight3D (shadows skipped in the all-text far view)
	[Export] public bool DisableMain3D { get; set; } = true;        // the main window no longer renders 3D itself (saves a full render)
	[Export(PropertyHint.Range, "1,20,1")] public int ArtLayer { get; set; } = 20;   // render layers reserved for the post quads:
	[Export(PropertyHint.Range, "1,20,1")] public int IdsLayer { get; set; } = 19;   //   don't put your own objects on these two
	[Export] public int CanvasLayerIndex { get; set; } = -100;      // present layer; your UI must be on a higher CanvasLayer
	[Export] public bool CreateTuningPanel { get; set; } = true;    // F10 toggles the runtime slider panel
	[Export] public bool SyncEnvironment { get; set; } = true;      // copy ambient/background/fog from your WorldEnvironment every frame

	/// <summary>Raised when a setting that needs re-meshing changed (EdgeRounding, CardSize). Re-mesh chunks / rebuild trees.</summary>
	public event Action MeshSettingsChanged;

	/// <summary>Optional: your camera controller can set these instead of the automatic zoom / focus detection.</summary>
	public float? ZoomOverride { get; set; }
	public Vector3? FocusOverride { get; set; }

	/// <summary>Current ASCII amount (0..1) and whether the far view is all text; useful for gameplay/UI.</summary>
	public float AsciiAmount { get; private set; }
	public bool FarViewIsText { get; private set; }

	Camera3D _game, _artCam, _idsCam;
	SubViewport _art, _ids;
	MeshInstance3D _artQuad, _idsQuad;
	ShaderMaterial _post, _postIds, _present;
	TextureRect _rect;
	DirectionalLight3D _sun; bool _sunShadowDefault;
	Godot.Environment _envOverride;
	ImageTexture3D _lut; (PaletteName, int) _lutKey = ((PaletteName)(-1), -1);
	float _lastRounding = -1, _lastCards = -1;
	int _lw, _lh;
	readonly Vector4[] _markers = new Vector4[64];
	readonly Vector3[] _markerCols = new Vector3[64];

	static uint Bit(int layer) => 1u << (layer - 1);

	public override void _EnterTree() => Instance = this;
	public override void _ExitTree() { if (Instance == this) Instance = null; }

	public override void _Ready()
	{
		Settings ??= new DwarfRenderSettings();
		_game = GetNodeOrNull<Camera3D>(GameCamera);
		if (_game == null) { GD.PushError("DwarfRenderer: set GameCamera to your Camera3D."); SetProcess(false); return; }
		_sun = SunLight != null && !SunLight.IsEmpty ? GetNodeOrNull<DirectionalLight3D>(SunLight) : null;
		if (_sun != null) _sunShadowDefault = _sun.ShadowEnabled;

		var world = GetViewport().FindWorld3D();
		if (DisableMain3D) GetViewport().Disable3D = true;

		_art = MakeViewport("DR_Art", world, hdr: false);
		_ids = MakeViewport("DR_Ids", world, hdr: true);
		_ids.PositionalShadowAtlasSize = 0;
		_artCam = new Camera3D { Name = "DR_ArtCamera", CullMask = 0xFFFFF & ~Bit(IdsLayer) };
		_idsCam = new Camera3D { Name = "DR_IdsCamera", CullMask = 0xFFFFF & ~Bit(ArtLayer) };
		_art.AddChild(_artCam); _ids.AddChild(_idsCam);
		_artCam.Current = true; _idsCam.Current = true;

		_envOverride = BuildEnvironment(world.Environment);
		_artCam.Environment = _envOverride; _idsCam.Environment = _envOverride;

		_post = new ShaderMaterial { Shader = GD.Load<Shader>(DwarfMaterials.ShaderDir + "post_color.gdshader") };
		_postIds = new ShaderMaterial { Shader = GD.Load<Shader>(DwarfMaterials.ShaderDir + "post_ids.gdshader") };
		// The post quads read the screen copy, so they draw in the transparent pass: first among transparents, so overlays
		// (cursors, markers, labels, translucent ghosts) still draw over the processed image, depth-tested against the scene.
		_post.RenderPriority = (int)Material.RenderPriorityMin; _postIds.RenderPriority = (int)Material.RenderPriorityMin;
		_artQuad = MakeQuad(_post, ArtLayer); _artCam.AddChild(_artQuad);
		_idsQuad = MakeQuad(_postIds, IdsLayer); _idsCam.AddChild(_idsQuad);

		var layer = new CanvasLayer { Name = "DR_Present", Layer = CanvasLayerIndex };
		AddChild(layer);
		_present = new ShaderMaterial { Shader = GD.Load<Shader>(DwarfMaterials.ShaderDir + "present.gdshader") };
		_rect = new TextureRect
		{
			Name = "DR_PresentRect", Texture = _art.GetTexture(), Material = _present,
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
			MouseFilter = Control.MouseFilterEnum.Ignore, TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
		};
		_rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		layer.AddChild(_rect);
		_present.SetShaderParameter("color_tex", _art.GetTexture());
		_present.SetShaderParameter("ids_tex", _ids.GetTexture());
		_present.SetShaderParameter("glyph_tex", GlyphAtlas.Texture);

		if (CreateTuningPanel) AddChild(new SettingsPanel { Renderer = this });
		ApplySettings();
	}

	SubViewport MakeViewport(string name, World3D world, bool hdr)
	{
		var vp = new SubViewport
		{
			Name = name, World3D = world, Size = new Vector2I(64, 64),
			RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
			Msaa3D = Viewport.Msaa.Disabled, ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
			UseTaa = false, UseDebanding = false, UseHdr2D = hdr, Scaling3DScale = 1f,
			TransparentBg = false, GuiDisableInput = true, HandleInputLocally = false,
		};
		AddChild(vp);
		return vp;
	}

	static MeshInstance3D MakeQuad(ShaderMaterial mat, int layer) => new()
	{
		Mesh = new QuadMesh { Size = new Vector2(2, 2) }, MaterialOverride = mat, Layers = Bit(layer),
		ExtraCullMargin = 16384f, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
	};

	/// <summary>Camera environment for the art/ids passes: your environment, but with a Linear tonemap,
	/// exposure 1 and no glow/adjustments, so the palette and ids come out exact.</summary>
	static Godot.Environment BuildEnvironment(Godot.Environment src)
	{
		var e = src != null ? (Godot.Environment)src.Duplicate() : new Godot.Environment();
		e.TonemapMode = Godot.Environment.ToneMapper.Linear;
		e.TonemapExposure = 1f;
		e.GlowEnabled = false;
		e.AdjustmentEnabled = false;
		return e;
	}

	void SyncEnv()
	{
		var src = GetViewport().FindWorld3D()?.Environment;
		if (src == null || _envOverride == null) return;
		_envOverride.BackgroundMode = src.BackgroundMode; _envOverride.BackgroundColor = src.BackgroundColor; _envOverride.Sky = src.Sky;
		_envOverride.BackgroundEnergyMultiplier = src.BackgroundEnergyMultiplier;
		_envOverride.AmbientLightSource = src.AmbientLightSource; _envOverride.AmbientLightColor = src.AmbientLightColor;
		_envOverride.AmbientLightEnergy = src.AmbientLightEnergy; _envOverride.AmbientLightSkyContribution = src.AmbientLightSkyContribution;
		_envOverride.FogEnabled = src.FogEnabled; _envOverride.FogLightColor = src.FogLightColor; _envOverride.FogDensity = src.FogDensity;
	}

	static Vector3 V3(Color c) => new(c.R, c.G, c.B);

	/// <summary>Push all settings to the shaders. Call after changing Settings from code.</summary>
	public void ApplySettings()
	{
		var s = Settings;
		DwarfMaterials.Apply(s);

		if (_lutKey != (s.Palette, s.PaletteSize))
		{
			var (tex, spread) = PaletteLut.Bake(s.Palette, s.PaletteSize, _lut);
			_lut = tex; _lutKey = (s.Palette, s.PaletteSize);
			_post.SetShaderParameter("palette_lut", _lut);
			_post.SetShaderParameter("dither_spread", spread);
		}
		var p = _post;
		p.SetShaderParameter("reverse_z", s.ReverseZ);
		p.SetShaderParameter("outline_on", s.OutlinesEnabled);
		p.SetShaderParameter("outline_outer", s.OuterSilhouette);
		p.SetShaderParameter("outline_inner", s.InnerCrease);
		p.SetShaderParameter("depth_threshold", s.DepthThreshold);
		p.SetShaderParameter("normal_threshold", s.NormalThreshold);
		p.SetShaderParameter("outline_width", (float)s.OutlineWidth);
		p.SetShaderParameter("outline_color", V3(s.OutlineColour));
		p.SetShaderParameter("foliage_outline", (float)(int)s.FoliageOutlines);
		p.SetShaderParameter("sat", s.Saturation);
		p.SetShaderParameter("contrast", s.Contrast);
		p.SetShaderParameter("bright", s.Brightness);
		p.SetShaderParameter("fog_amount", s.DepthFog);
		p.SetShaderParameter("fog_color", V3(s.FogColour));
		p.SetShaderParameter("wobble", s.PaintWobble);
		p.SetShaderParameter("edge_darken", s.WatercolourEdges);
		p.SetShaderParameter("hatch", s.InkHatching);
		p.SetShaderParameter("paper", V3(s.PaperColour));
		p.SetShaderParameter("tone_mix", s.Duotone);
		p.SetShaderParameter("tone_a", V3(s.DuotoneDark));
		p.SetShaderParameter("tone_b", V3(s.DuotoneLight));
		p.SetShaderParameter("grain", s.Grain);
		p.SetShaderParameter("grain_anim", s.AnimatedGrain ? 1f : 0f);
		p.SetShaderParameter("palette_on", s.PaletteEnabled);
		p.SetShaderParameter("dither_amount", s.Dither);
		p.SetShaderParameter("bayer_size", (float)(int)s.DitherPattern);
		_postIds.SetShaderParameter("reverse_z", s.ReverseZ);

		var u = _present;
		u.SetShaderParameter("crt", s.Crt);
		u.SetShaderParameter("vignette", s.Vignette);
		u.SetShaderParameter("density_gamma", s.GlyphDensity);
		u.SetShaderParameter("invert", s.InvertDensity ? 1f : 0f);
		u.SetShaderParameter("color_amt", s.SceneColourVsMono);
		u.SetShaderParameter("bg_tint", s.BackgroundTint);
		u.SetShaderParameter("boost", s.GlyphBrightness);
		u.SetShaderParameter("edges_on", s.EdgeStrokes ? 1f : 0f);
		u.SetShaderParameter("edge_thr", s.EdgeThreshold);
		u.SetShaderParameter("materials_on", s.PerSurfaceSymbols ? 1f : 0f);
		u.SetShaderParameter("match_amt", s.BrightnessMatch);
		u.SetShaderParameter("soft", s.DissolveCrossfade);
		u.SetShaderParameter("mono_color", V3(s.MonoGlyphColour));
		u.SetShaderParameter("bg_color", V3(s.AsciiBackground));

		if (!Mathf.IsEqualApprox(_lastRounding, s.EdgeRounding) || !Mathf.IsEqualApprox(_lastCards, s.CardSize))
		{
			bool first = _lastRounding < 0;
			_lastRounding = s.EdgeRounding; _lastCards = s.CardSize;
			if (!first) MeshSettingsChanged?.Invoke();
		}
	}

	public override void _Process(double delta)
	{
		if (SyncEnvironment) SyncEnv();
		var s = Settings;
		Vector2 screen = GetViewport().GetVisibleRect().Size;
		float W = screen.X, H = screen.Y;
		bool ortho = _game.Projection == Camera3D.ProjectionType.Orthogonal;
		var gt = _game.GlobalTransform;
		Vector3 fwd = -gt.Basis.Z.Normalized();

		// ---- Focus + zoom (1 = reference view, >1 zoomed in) ----------------------------
		Vector3 focus;
		if (FocusOverride.HasValue) focus = FocusOverride.Value;
		else if (Mathf.Abs(fwd.Y) > 1e-4f && (s.FocusPlaneY - gt.Origin.Y) / fwd.Y > 0) focus = gt.Origin + fwd * ((s.FocusPlaneY - gt.Origin.Y) / fwd.Y);
		else focus = gt.Origin + fwd * s.ReferenceDistance;
		float dist = Mathf.Max(0.01f, (focus - gt.Origin).Length());
		float zoomF = ZoomOverride ?? (ortho ? s.ReferenceOrthoSize / Mathf.Max(0.001f, _game.Size) : s.ReferenceDistance / dist);

		// ---- ASCII zoom logic (same as the prototype) -------------------------------------
		bool asciiIn = s.AsciiMode is AsciiMode.WhenZoomedIn or AsciiMode.ZoomedInAndOut;
		bool asciiOut = s.AsciiMode is AsciiMode.WhenZoomedOut or AsciiMode.ZoomedInAndOut;
		float magnify = asciiIn ? Mathf.Max(1f, zoomF / s.ZoomInStart) : 1f;
		float farK = 0f;
		if (asciiOut)
		{
			float hi = Mathf.Max(s.ZoomOutStart, s.ZoomOutFullyText + 0.01f), lo = Mathf.Min(s.ZoomOutFullyText, hi - 0.01f);
			farK = Mathf.Clamp(Mathf.Log(hi / zoomF) / Mathf.Log(hi / lo), 0f, 1f);
		}
		float ps = Mathf.Max(1f, Mathf.Round(s.PixelSize));
		if (s.AsciiMode == AsciiMode.Always) ps = Mathf.Max(ps, s.AlwaysCharSize);
		float psF = farK > 0 ? ps * Mathf.Pow(Mathf.Max(1f, s.ZoomOutCharSize / ps), farK) : ps;   // far view: coarser pixels, continuously

		// ---- Art resolution (1 texel margin each side) -------------------------------------
		float vw = W / psF, vh = H / psF;
		int lw = Mathf.CeilToInt(vw) + 2, lh = Mathf.CeilToInt(vh) + 2;
		if (lw != _lw || lh != _lh)
		{
			_lw = lw; _lh = lh;
			_art.Size = new Vector2I(lw, lh); _ids.Size = new Vector2I(lw, lh);
		}

		// ---- Render camera: copy, magnify, pixel-snap ---------------------------------------
		var rt = gt;
		float distEff = dist;
		if (!ortho && magnify > 1f) { rt.Origin = focus + (gt.Origin - focus) * magnify; distEff = dist * magnify; }
		float wpt = ortho ? _game.Size * magnify / vh
		                  : 2f * distEff * Mathf.Tan(Mathf.DegToRad(_game.Fov) * 0.5f) / vh;   // world units per art pixel
		Vector2 sub = Vector2.Zero, ditherOff = Vector2.Zero, offFull = Vector2.Zero;
		if (s.CameraSnapping)
		{
			Vector3 right = rt.Basis.X.Normalized(), up = rt.Basis.Y.Normalized();
			float pr = rt.Origin.Dot(right) / wpt, pu = rt.Origin.Dot(up) / wpt;
			float sr = Mathf.Round(pr), su = Mathf.Round(pu);
			rt.Origin += right * (sr - pr) * wpt + up * (su - pu) * wpt;
			sub = new Vector2(pr - sr, -(pu - su));                      // texture axes: x right, y down
			ditherOff = new Vector2(Mod(sr, 8), Mod(-su, 8));
			offFull = new Vector2(Mod(sr, 4096), Mod(-su, 4096));
		}
		foreach (var c in new[] { _artCam, _idsCam })
		{
			c.GlobalTransform = rt;
			c.KeepAspect = Camera3D.KeepAspectEnum.Height;
			c.Near = _game.Near; c.Far = _game.Far;
			if (ortho) { c.Projection = Camera3D.ProjectionType.Orthogonal; c.Size = lh * wpt; }
			else { c.Projection = Camera3D.ProjectionType.Perspective; c.Fov = Mathf.RadToDeg(2f * Mathf.Atan(lh * wpt / (2f * distEff))); }
		}
		DwarfMaterials.SetFoliageScreenOffset(ditherOff);

		// ---- Post uniforms --------------------------------------------------------------------
		_post.SetShaderParameter("texel_world", ortho ? wpt : wpt / distEff);
		_post.SetShaderParameter("is_ortho", ortho ? 1f : 0f);
		_post.SetShaderParameter("fog_ref", distEff);
		_post.SetShaderParameter("dither_offset", ditherOff);

		// ---- ASCII amount + present uniforms ---------------------------------------------------
		static float S01(float x) { x = Mathf.Clamp(x, 0f, 1f); return x * x * (3 - 2 * x); }
		float inK = (asciiIn && magnify > 1f) ? Mathf.Log(magnify) / Mathf.Log(Mathf.Max(1.05f, s.ZoomInLength)) : 0f;
		float amt = s.AsciiMode == AsciiMode.Always ? 1f : Mathf.Max(S01(inK), S01(farK));
		AsciiAmount = amt;
		FarViewIsText = farK > 0 && amt >= 0.999f;
		float style = ((farK > 0 ? s.ZoomedOutSymbols : s.ZoomedInSymbols) == GlyphStyle.ObjectSymbols) ? 1f : 0f;
		_ids.RenderTargetUpdateMode = amt > 0 ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;

		var u = _present;
		u.SetShaderParameter("low_res", new Vector2(lw, lh));
		u.SetShaderParameter("screen_size", new Vector2(W, H));
		u.SetShaderParameter("pps", psF * magnify);
		u.SetShaderParameter("sub", sub);
		u.SetShaderParameter("glyph_amt", amt);
		u.SetShaderParameter("off_full", offFull);
		u.SetShaderParameter("style", style);
		u.SetShaderParameter("is_ortho", ortho ? 1f : 0f);

		// ---- Shadows off in the all-text far view ------------------------------------------------
		if (_sun != null) _sun.ShadowEnabled = _sunShadowDefault && !(FarViewIsText && s.SkipShadowsWhenAllText);

		// ---- Marker priority (dwarves etc.) ----------------------------------------------------
		int n = 0;
		if (amt > 0 && s.DwarfsAlwaysShow && (farK > 0 || style > 0.5f))
		{
			var inv = _artCam.GlobalTransform.AffineInverse();
			foreach (var node in GetTree().GetNodesInGroup(AsciiMarker.Group))
			{
				if (n >= 64) break;
				if (node is not AsciiMarker m || !m.Visible || !m.IsVisibleInTree()) continue;
				var pos = m.GlobalPosition;
				if (_artCam.IsPositionBehind(pos)) continue;
				Vector2 px = _artCam.UnprojectPosition(pos);
				if (px.X < 0 || px.Y < 0 || px.X >= lw || px.Y >= lh) continue;
				float depth = -(inv * pos).Z;
				_markers[n] = new Vector4(px.X, px.Y, depth, m.GlyphGroup);
				_markerCols[n] = V3(m.GlyphColour);
				n++;
			}
			u.SetShaderParameter("markers", _markers);
			u.SetShaderParameter("marker_colors", _markerCols);
		}
		u.SetShaderParameter("marker_count", n);
	}

	static float Mod(float a, float m) => ((a % m) + m) % m;
}
