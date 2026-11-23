using System;
using System.Collections.Generic;
using Godot;

namespace DwarfRender;

/// <summary>
/// One place that owns the kit's materials and keeps their shared uniforms in sync with
/// the settings. Use these instead of StandardMaterial3D for anything that should get the look:
///   - DwarfMaterials.Terrain                       -> every voxel chunk MeshInstance3D
///   - DwarfMaterials.Foliage                       -> tree canopies / bushes (TreeBuilder meshes)
///   - DwarfMaterials.CreateObject(colour, id, ...) -> dwarves, creatures, items, furniture, trunks, torches
/// </summary>
public static class DwarfMaterials
{
	public const string ShaderDir = "res://addons/dwarf_render/shaders/";

	static ShaderMaterial _terrain, _foliage, _foliageCore;
	static readonly List<WeakReference<ShaderMaterial>> _objects = new();

	public static ShaderMaterial Terrain => _terrain ??= Make("voxel_terrain.gdshader");
	public static ShaderMaterial Foliage => _foliage ??= Make("foliage.gdshader");
	/// <summary>The canopies' inner core: opaque, culled, no discard (TreeBuilder's coreMaterial). Same settings as Foliage.</summary>
	public static ShaderMaterial FoliageCore => _foliageCore ??= Make("foliage_core.gdshader");

	static ShaderMaterial Make(string file) => new() { Shader = GD.Load<Shader>(ShaderDir + file) };

	/// <summary>A toon material for a non-voxel object. glyphGroup = what ASCII shows (GlyphAtlas ids,
	/// e.g. GlyphAtlas.Dwarf, GlyphAtlas.Item, or your own registered group).</summary>
	public static ShaderMaterial CreateObject(Color albedo, int glyphGroup = GlyphAtlas.Prop, Texture2D texture = null,
	                                          bool useVertexColor = false, float emission = 0f)
	{
		var m = Make("toon_object.gdshader");
		m.SetShaderParameter("albedo_color", albedo);
		if (texture != null) m.SetShaderParameter("albedo_tex", texture);
		m.SetShaderParameter("use_vertex_color", useVertexColor);
		m.SetShaderParameter("object_id", (float)glyphGroup);
		m.SetShaderParameter("emission_strength", emission);
		_objects.Add(new WeakReference<ShaderMaterial>(m));
		if (_last != null) ApplyCommon(m, _last);
		return m;
	}

	static readonly List<ShaderMaterial> _terrains = new();

	/// <summary>A game terrain material (its own shader using dr_atlas / dr_tile_px / dr_tex_strength and dr_toon): gets the
	/// atlas and lighting values now and on every settings change, like Terrain.</summary>
	/// <summary>Forgets registered terrain materials (a new scene builds its own).</summary>
	public static void ClearTerrains() => _terrains.Clear();

	public static void RegisterTerrain(ShaderMaterial m)
	{
		_terrains.Add(m);
		if (_last != null) ApplyTerrain(m, _last);
	}

	static void ApplyTerrain(ShaderMaterial m, DwarfRenderSettings s)
	{
		ApplyCommon(m, s);
		m.SetShaderParameter("dr_atlas", TextureAtlas.Get(s.TexturePack, out int tile));
		m.SetShaderParameter("dr_tile_px", (float)tile);
		m.SetShaderParameter("dr_tex_strength", s.TexturePack == TexturePackName.Off ? 0f : s.TextureStrength);
	}

	static DwarfRenderSettings _last;

	/// <summary>Push lighting / texture / foliage values to every kit material. Called by DwarfRenderer.ApplySettings().</summary>
	public static void Apply(DwarfRenderSettings s)
	{
		_last = s;
		ApplyCommon(Terrain, s);
		ApplyCommon(Foliage, s);
		ApplyCommon(FoliageCore, s);
		var atlas = TextureAtlas.Get(s.TexturePack, out int tile);
		Terrain.SetShaderParameter("dr_atlas", atlas);
		Terrain.SetShaderParameter("dr_tile_px", (float)tile);
		Terrain.SetShaderParameter("dr_tex_strength", s.TexturePack == TexturePackName.Off ? 0f : s.TextureStrength);

		foreach (var fm in new[] { Foliage, FoliageCore }) ApplyFoliage(fm, s);

		foreach (var m in _terrains) ApplyTerrain(m, s);
		for (int i = _objects.Count - 1; i >= 0; i--)
		{
			if (_objects[i].TryGetTarget(out var m)) ApplyCommon(m, s); else _objects.RemoveAt(i);
		}
	}

	static void ApplyFoliage(ShaderMaterial f, DwarfRenderSettings s)
	{
		f.SetShaderParameter("fol_mode", (float)(int)s.FoliageTechnique);
		f.SetShaderParameter("fol_density", s.LeafDensity);
		f.SetShaderParameter("fol_leaf_size", s.LeafSize);
		f.SetShaderParameter("fol_edge_fade", s.EdgeFade);
		f.SetShaderParameter("fol_screen_mix", s.WorldScreenDither);
		f.SetShaderParameter("fol_sphere", s.SphericalNormals);
		f.SetShaderParameter("fol_transl", s.BackLightGlow);
		f.SetShaderParameter("fol_inner_dark", s.InnerShade);
		f.SetShaderParameter("fol_hue_var", s.LeafColourVariety);
		f.SetShaderParameter("fol_sway", s.WindSway);
	}

	static void ApplyCommon(ShaderMaterial m, DwarfRenderSettings s)
	{
		m.SetShaderParameter("dr_bands", (float)s.ShadingBands);
		m.SetShaderParameter("dr_torch", s.TorchGlow);
		m.SetShaderParameter("dr_rim", s.RimLight);
		m.SetShaderParameter("dr_spec", s.SpecularGlints);
	}

	/// <summary>World-locked screen offset for the foliage dither (set every frame by the renderer).</summary>
	public static void SetFoliageScreenOffset(Vector2 off) => Foliage.SetShaderParameter("fol_screen_offset", off);
}
