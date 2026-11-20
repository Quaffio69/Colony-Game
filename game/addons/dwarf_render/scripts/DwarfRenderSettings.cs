using System;
using System.Collections.Generic;
using Godot;

namespace DwarfRender;

public enum PaletteName { WarmSnes, AutumnHearth, FadedCartridge, ChunkyRetro, PocketMono, OneBit, PastelClear }
public enum TexturePackName { HandPainted8, Crisp16, MossyRuins, PastelTiles, Chunky4, Off }
public enum DitherPattern { Bayer2x2 = 2, Bayer4x4 = 4, Bayer8x8 = 8 }
public enum FoliageMode { DitheredShell = 0, Solid = 1, LeafCards = 2 }
public enum FoliageOutlines { None = 0, Silhouette = 1, EveryClump = 2 }
public enum AsciiMode { Off, WhenZoomedIn, WhenZoomedOut, ZoomedInAndOut, Always }
public enum GlyphStyle { BrightnessRamps, ObjectSymbols }
public enum ShadowMapSize { Size512 = 512, Size1024 = 1024, Size2048 = 2048, Size4096 = 4096 }

/// <summary>
/// Every knob of the pixel-art / ASCII renderer, mirroring the diorama's panel.
/// Edit in the inspector, at runtime with the F10 tuning panel (SettingsPanel), or from code.
/// After changing values from code call DwarfRenderer.Instance.ApplySettings().
/// Presets: ApplyStylePreset / ApplyFoliagePreset / ApplyAsciiPreset.
/// </summary>
[GlobalClass]
public partial class DwarfRenderSettings : Resource
{
	// ----------------------------------------------------------------- Pixels
	[ExportGroup("Pixels")]
	[Export(PropertyHint.Range, "1,8,0.5")] public float PixelSize { get; set; } = 3f;        // screen pixels per art pixel
	[Export] public bool CameraSnapping { get; set; } = true;
	/// <summary>The art is redrawn at a new scale only when the zoom has changed by this factor; in between the finished
	/// picture is scaled on screen, so zooming doesn't make every pixel crawl. 1 = a new scale every frame.</summary>
	[Export(PropertyHint.Range, "1,2,0.05")] public float ZoomStep { get; set; } = 1.4f;
	/// <summary>Zoomed in: the zoom (1 = reference) from which art pixels grow instead of showing more detail.</summary>
	[Export(PropertyHint.Range, "0.5,20,0.1")] public float PixelGrowStart { get; set; } = 2.5f;
	/// <summary>How much of the zoom past PixelGrowStart goes into bigger pixels (1 all of it, 0 none: more detail instead).</summary>
	[Export(PropertyHint.Range, "0,1,0.05")] public float PixelGrowth { get; set; } = 1f;
	/// <summary>Art pixels never get bigger than this (screen pixels); zooming further shows more detail.</summary>
	[Export(PropertyHint.Range, "2,48,1")] public float MaxPixelSize { get; set; } = 24f;
	/// <summary>Camera turns are drawn in steps of this many degrees (0 = smooth). Steps keep the pixel art still between them.</summary>
	[Export(PropertyHint.Range, "0,45,1")] public float OrbitStep { get; set; } = 0f;

	// ----------------------------------------------------------------- Voxels
	[ExportGroup("Voxels")]
	[Export] public TexturePackName TexturePack { get; set; } = TexturePackName.HandPainted8;
	[Export(PropertyHint.Range, "0,1,0.01")] public float TextureStrength { get; set; } = 0.85f;
	[Export(PropertyHint.Range, "0,0.45,0.05")] public float EdgeRounding { get; set; } = 0.25f;   // needs a re-mesh (MeshSettingsChanged)

	// ----------------------------------------------------------------- Palette & dither
	[ExportGroup("Palette & dither")]
	[Export] public bool PaletteEnabled { get; set; } = true;
	[Export] public PaletteName Palette { get; set; } = PaletteName.WarmSnes;
	[Export(PropertyHint.Range, "2,64,1")] public int PaletteSize { get; set; } = 32;
	[Export(PropertyHint.Range, "0,1,0.01")] public float Dither { get; set; } = 0.35f;
	[Export] public DitherPattern DitherPattern { get; set; } = DitherPattern.Bayer4x4;

	// ----------------------------------------------------------------- Foliage
	[ExportGroup("Foliage")]
	[Export] public FoliageMode FoliageTechnique { get; set; } = FoliageMode.DitheredShell;
	[Export(PropertyHint.Range, "0.1,1,0.01")] public float LeafDensity { get; set; } = 0.7f;
	[Export(PropertyHint.Range, "0.06,0.6,0.01")] public float LeafSize { get; set; } = 0.2f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float EdgeFade { get; set; } = 0.55f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float WorldScreenDither { get; set; } = 0.35f;
	[Export(PropertyHint.Range, "0.2,0.9,0.05")] public float CardSize { get; set; } = 0.5f;          // needs a tree rebuild
	[Export(PropertyHint.Range, "0,1,0.01")] public float SphericalNormals { get; set; } = 0.85f;
	[Export(PropertyHint.Range, "0,1.5,0.01")] public float BackLightGlow { get; set; } = 0.5f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float InnerShade { get; set; } = 0.6f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float LeafColourVariety { get; set; } = 0.5f;
	[Export(PropertyHint.Range, "0,1.5,0.01")] public float WindSway { get; set; } = 0.35f;
	[Export] public FoliageOutlines FoliageOutlines { get; set; } = FoliageOutlines.Silhouette;

	// ----------------------------------------------------------------- Outlines
	[ExportGroup("Outlines")]
	[Export] public bool OutlinesEnabled { get; set; } = true;
	[Export] public Color OutlineColour { get; set; } = Color.FromHtml("#2b1a16");
	[Export(PropertyHint.Range, "1,2,1")] public int OutlineWidth { get; set; } = 1;
	[Export(PropertyHint.Range, "0,1,0.01")] public float OuterSilhouette { get; set; } = 0.75f;
	[Export(PropertyHint.Range, "-1,1,0.01")] public float InnerCrease { get; set; } = -0.35f;     // <0 glint, >0 ink
	[Export(PropertyHint.Range, "0.05,3,0.05")] public float DepthThreshold { get; set; } = 0.6f;
	[Export(PropertyHint.Range, "0.05,1.5,0.05")] public float NormalThreshold { get; set; } = 0.35f;

	// ----------------------------------------------------------------- Lighting
	[ExportGroup("Lighting")]
	[Export(PropertyHint.Range, "1,8,1")] public int ShadingBands { get; set; } = 3;
	[Export(PropertyHint.Range, "0,3,0.05")] public float TorchGlow { get; set; } = 1.2f;
	[Export(PropertyHint.Range, "0,1.5,0.01")] public float RimLight { get; set; } = 0f;
	[Export(PropertyHint.Range, "0,1.5,0.01")] public float SpecularGlints { get; set; } = 0f;
	[Export(PropertyHint.Range, "0,360,1")] public float SunAngle { get; set; } = 52f;        // 0 sunrise, 90 noon, 180 sunset, beyond: night
	[Export(PropertyHint.Range, "0,1,0.01")] public float SunWarmth { get; set; } = 0.55f;
	[Export(PropertyHint.Range, "0,1.5,0.01")] public float Ambient { get; set; } = 0.5f;
	[Export] public bool SunShadows { get; set; } = true;
	[Export] public bool DayNightCycle { get; set; } = false;                                  // the sun follows the game clock (DwarfRenderer.TimeOfDay)

	// ----------------------------------------------------------------- Grade
	[ExportGroup("Grade")]
	[Export(PropertyHint.Range, "0,2,0.01")] public float Saturation { get; set; } = 1.15f;
	[Export(PropertyHint.Range, "0.5,1.5,0.01")] public float Contrast { get; set; } = 1.05f;
	[Export(PropertyHint.Range, "0.5,1.5,0.01")] public float Brightness { get; set; } = 1.0f;

	// ----------------------------------------------------------------- Art effects
	[ExportGroup("Art effects")]
	[Export(PropertyHint.Range, "0,1,0.01")] public float DepthFog { get; set; } = 0f;
	[Export] public Color FogColour { get; set; } = Color.FromHtml("#e6d2a4");
	[Export(PropertyHint.Range, "0,1,0.01")] public float InkHatching { get; set; } = 0f;
	[Export] public Color PaperColour { get; set; } = Color.FromHtml("#efe4c8");
	[Export(PropertyHint.Range, "0,3,0.05")] public float PaintWobble { get; set; } = 0f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float WatercolourEdges { get; set; } = 0f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float Duotone { get; set; } = 0f;
	[Export] public Color DuotoneDark { get; set; } = Color.FromHtml("#2a1e1a");
	[Export] public Color DuotoneLight { get; set; } = Color.FromHtml("#f0e0c0");
	[Export(PropertyHint.Range, "0,1,0.01")] public float Grain { get; set; } = 0f;
	[Export] public bool AnimatedGrain { get; set; } = false;
	[Export(PropertyHint.Range, "0,1,0.01")] public float Crt { get; set; } = 0f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float Vignette { get; set; } = 0f;

	// ----------------------------------------------------------------- ASCII
	[ExportGroup("ASCII")]
	[Export] public AsciiMode AsciiMode { get; set; } = AsciiMode.ZoomedInAndOut;
	[Export] public GlyphStyle ZoomedOutSymbols { get; set; } = GlyphStyle.ObjectSymbols;
	[Export] public GlyphStyle ZoomedInSymbols { get; set; } = GlyphStyle.BrightnessRamps;
	[Export] public bool DwarfsAlwaysShow { get; set; } = true;
	[Export(PropertyHint.Range, "0,1,0.01")] public float BrightnessMatch { get; set; } = 1f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float DissolveCrossfade { get; set; } = 0.6f;
	[Export(PropertyHint.Range, "1,20,0.1")] public float ZoomInStart { get; set; } = 2.5f;
	[Export(PropertyHint.Range, "1.2,10,0.1")] public float ZoomInLength { get; set; } = 3f;
	[Export(PropertyHint.Range, "0.02,1.5,0.01")] public float ZoomOutStart { get; set; } = 0.9f;
	[Export(PropertyHint.Range, "0.01,1.2,0.01")] public float ZoomOutFullyText { get; set; } = 0.3f;
	[Export(PropertyHint.Range, "4,16,1")] public int ZoomOutCharSize { get; set; } = 8;
	[Export(PropertyHint.Range, "4,16,1")] public int AlwaysCharSize { get; set; } = 8;
	[Export] public bool SkipShadowsWhenAllText { get; set; } = true;
	[Export(PropertyHint.Range, "0.3,2,0.01")] public float GlyphDensity { get; set; } = 0.8f;
	[Export(PropertyHint.Range, "0.4,2.2,0.01")] public float GlyphBrightness { get; set; } = 1.3f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float SceneColourVsMono { get; set; } = 1f;
	[Export] public Color MonoGlyphColour { get; set; } = Color.FromHtml("#9fe870");
	[Export] public Color AsciiBackground { get; set; } = Color.FromHtml("#0b0a0d");
	[Export(PropertyHint.Range, "0,0.6,0.01")] public float BackgroundTint { get; set; } = 0.12f;
	[Export] public bool PerSurfaceSymbols { get; set; } = true;
	[Export] public bool EdgeStrokes { get; set; } = true;
	[Export(PropertyHint.Range, "0.1,3,0.05")] public float EdgeThreshold { get; set; } = 0.7f;
	[Export] public bool InvertDensity { get; set; } = false;

	// ----------------------------------------------------------------- Camera / zoom reference
	[ExportGroup("Camera")]
	[Export] public float ReferenceOrthoSize { get; set; } = 88f;     // ortho Size that counts as "zoom 1x"
	[Export] public bool Orthographic { get; set; } = true;            // iso projection (the game camera keeps its zoom)
	[Export(PropertyHint.Range, "5,300,1")] public float ReferenceDistance { get; set; } = 140f;     // perspective distance to the focus that counts as "zoom 1x"
	[Export] public float FocusPlaneY { get; set; } = 0f;             // perspective focus = where the view ray hits this height

	// ----------------------------------------------------------------- Performance
	[ExportGroup("Performance")]
	[Export] public bool ReverseZ { get; set; } = true;               // Godot 4.3+ = true
	[Export] public ShadowMapSize ShadowMap { get; set; } = ShadowMapSize.Size2048;
	[Export] public bool ShowProfiler { get; set; } = false;

	// =================================================================== Presets
	public static readonly string[] StylePresets =
		{ "Warm SNES", "Autumn Hearth", "Faded Cartridge", "Clean Low Poly", "Pocket Mono", "1-Bit Dither", "CRT Arcade",
		  "Ink & Parchment", "Storybook Gouache", "Ligne Claire", "Misty Morning", "Cel Shaded", "Old Photograph", "Chunky Retro" };
	public static readonly string[] FoliagePresets = { "Dithered canopy", "Wispy pixels", "Chunky clumps", "Leaf cards", "Solid toon", "Faceted low poly" };
	public static readonly string[] AsciiPresets = { "Colour glyphs", "Green terminal", "Amber monitor", "Ink on paper", "Classic ramp" };

	void ResetStyle()
	{
		PixelSize = 3; PaletteEnabled = true; Palette = PaletteName.WarmSnes; PaletteSize = 32; Dither = 0.35f; DitherPattern = DitherPattern.Bayer4x4;
		OutlinesEnabled = true; OutlineColour = Color.FromHtml("#2b1a16"); OuterSilhouette = 0.75f; InnerCrease = -0.35f; DepthThreshold = 0.6f; NormalThreshold = 0.35f; OutlineWidth = 1;
		ShadingBands = 3; TorchGlow = 1.2f; RimLight = 0; SpecularGlints = 0; Saturation = 1.15f; Contrast = 1.05f; Brightness = 1;
		SunAngle = 52; SunWarmth = 0.55f; Ambient = 0.5f;
		DepthFog = 0; InkHatching = 0; PaperColour = Color.FromHtml("#efe4c8"); PaintWobble = 0; WatercolourEdges = 0; Duotone = 0;
		DuotoneDark = Color.FromHtml("#2a1e1a"); DuotoneLight = Color.FromHtml("#f0e0c0"); Grain = 0; AnimatedGrain = false; Crt = 0; Vignette = 0;
	}

	public void ApplyStylePreset(string name)
	{
		ResetStyle();
		switch (name)
		{
			case "Autumn Hearth": SunAngle = 22; SunWarmth = 0.95f; Ambient = 0.38f; Palette = PaletteName.AutumnHearth; PaletteSize = 24; Dither = 0.45f; OutlineColour = Color.FromHtml("#2a140c"); OuterSilhouette = 0.8f; InnerCrease = 0.25f; TorchGlow = 2.2f; Saturation = 1.2f; Contrast = 1.1f; Brightness = 0.98f; break;
			case "Faded Cartridge": SunAngle = 60; SunWarmth = 0.45f; Ambient = 0.65f; PixelSize = 4; Palette = PaletteName.FadedCartridge; PaletteSize = 16; Dither = 0.85f; OutlineColour = Color.FromHtml("#4a3e3a"); OuterSilhouette = 0.45f; InnerCrease = 0.15f; TorchGlow = 1f; Saturation = 0.65f; Contrast = 0.82f; Brightness = 1.08f; break;
			case "Clean Low Poly": SunAngle = 50; SunWarmth = 0.5f; PixelSize = 2; PaletteEnabled = false; PaletteSize = 64; Dither = 0; OutlineColour = Color.FromHtml("#1e1410"); OuterSilhouette = 1; InnerCrease = 0.5f; DepthThreshold = 0.5f; NormalThreshold = 0.3f; Saturation = 1.05f; break;
			case "Pocket Mono": SunAngle = 55; SunWarmth = 0.3f; Ambient = 0.55f; PixelSize = 5; Palette = PaletteName.PocketMono; PaletteSize = 4; Dither = 0.55f; OutlineColour = Color.FromHtml("#0f2a1d"); OuterSilhouette = 1; InnerCrease = 0.4f; Saturation = 1; Contrast = 1.1f; Duotone = 1; DuotoneDark = Color.FromHtml("#0f2a1d"); DuotoneLight = Color.FromHtml("#d6e29a"); break;
			case "1-Bit Dither": SunAngle = 50; Ambient = 0.45f; PixelSize = 4; Palette = PaletteName.OneBit; PaletteSize = 2; Dither = 1; DitherPattern = DitherPattern.Bayer8x8; OutlineColour = Color.FromHtml("#18141c"); OuterSilhouette = 1; InnerCrease = -0.6f; ShadingBands = 4; Contrast = 1.15f; Brightness = 1.05f; Duotone = 1; DuotoneDark = Colors.Black; DuotoneLight = Colors.White; break;
			case "CRT Arcade": SunAngle = 58; SunWarmth = 0.5f; PixelSize = 6; PaletteSize = 48; Dither = 0.3f; OuterSilhouette = 0.9f; InnerCrease = -0.4f; Saturation = 1.35f; Contrast = 1.15f; Brightness = 1.05f; RimLight = 0.4f; Crt = 1; Vignette = 0.35f; break;
			case "Ink & Parchment": SunAngle = 45; SunWarmth = 0.4f; Ambient = 0.55f; PixelSize = 3; PaletteEnabled = false; OutlineColour = Color.FromHtml("#2a1c14"); OuterSilhouette = 1; InnerCrease = 0.7f; DepthThreshold = 0.45f; ShadingBands = 4; Saturation = 0.9f; Contrast = 1; InkHatching = 1; PaperColour = Color.FromHtml("#efe2c4"); Grain = 0.3f; Vignette = 0.3f; break;
			case "Storybook Gouache": SunAngle = 48; SunWarmth = 0.7f; Ambient = 0.6f; PixelSize = 3; PaletteEnabled = false; OutlineColour = Color.FromHtml("#4a3020"); OuterSilhouette = 0.45f; InnerCrease = 0.15f; ShadingBands = 4; Saturation = 1.15f; Contrast = 0.95f; Brightness = 1.04f; PaintWobble = 1.4f; WatercolourEdges = 0.8f; Grain = 0.35f; RimLight = 0.25f; DepthFog = 0.25f; Vignette = 0.25f; break;
			case "Ligne Claire": SunAngle = 62; SunWarmth = 0.35f; Ambient = 0.7f; PixelSize = 2; Palette = PaletteName.PastelClear; PaletteSize = 24; Dither = 0; OutlineColour = Color.FromHtml("#141014"); OuterSilhouette = 1; InnerCrease = 0.9f; NormalThreshold = 0.25f; OutlineWidth = 2; ShadingBands = 2; Saturation = 0.95f; Brightness = 1.05f; DepthFog = 0.3f; break;
			case "Misty Morning": SunAngle = 24; SunWarmth = 0.75f; Ambient = 0.65f; PixelSize = 4; Palette = PaletteName.FadedCartridge; PaletteSize = 24; Dither = 0.6f; OutlineColour = Color.FromHtml("#5a5560"); OuterSilhouette = 0.5f; InnerCrease = -0.25f; Saturation = 0.8f; Contrast = 0.9f; Brightness = 1.06f; DepthFog = 0.85f; RimLight = 0.5f; break;
			case "Cel Shaded": SunAngle = 55; SunWarmth = 0.45f; Ambient = 0.55f; PixelSize = 2; PaletteEnabled = false; OutlineColour = Color.FromHtml("#1a1216"); OuterSilhouette = 1; InnerCrease = 0.55f; OutlineWidth = 2; ShadingBands = 2; Saturation = 1.25f; Contrast = 1.08f; RimLight = 0.8f; SpecularGlints = 0.9f; break;
			case "Old Photograph": SunAngle = 50; Ambient = 0.6f; PixelSize = 3; PaletteEnabled = false; OutlineColour = Color.FromHtml("#2a2018"); OuterSilhouette = 0.5f; InnerCrease = 0.2f; ShadingBands = 5; Contrast = 1.2f; Duotone = 1; DuotoneDark = Color.FromHtml("#24180f"); DuotoneLight = Color.FromHtml("#f2dcb0"); Grain = 0.5f; AnimatedGrain = true; Vignette = 0.8f; break;
			case "Chunky Retro": SunAngle = 48; SunWarmth = 0.6f; PixelSize = 6; Palette = PaletteName.ChunkyRetro; PaletteSize = 8; Dither = 1; OuterSilhouette = 1; InnerCrease = 0.3f; ShadingBands = 2; TorchGlow = 1.4f; Saturation = 1.2f; Contrast = 1.1f; Brightness = 1.02f; break;
			default: break;   // "Warm SNES" = the reset values
		}
	}

	public void ApplyFoliagePreset(string name)
	{
		LeafDensity = 0.7f; LeafSize = 0.2f; EdgeFade = 0.55f; WorldScreenDither = 0.35f; SphericalNormals = 0.85f; BackLightGlow = 0.5f;
		InnerShade = 0.6f; LeafColourVariety = 0.5f; WindSway = 0.35f; CardSize = 0.5f; FoliageOutlines = FoliageOutlines.Silhouette; FoliageTechnique = FoliageMode.DitheredShell;
		switch (name)
		{
			case "Wispy pixels": LeafDensity = 0.5f; LeafSize = 0.12f; EdgeFade = 0.85f; WorldScreenDither = 0.6f; SphericalNormals = 0.7f; BackLightGlow = 0.7f; InnerShade = 0.4f; LeafColourVariety = 0.6f; WindSway = 0.55f; break;
			case "Chunky clumps": LeafDensity = 0.82f; LeafSize = 0.42f; EdgeFade = 0.3f; WorldScreenDither = 0; SphericalNormals = 0.9f; BackLightGlow = 0.35f; InnerShade = 0.7f; LeafColourVariety = 0.7f; WindSway = 0.25f; FoliageOutlines = FoliageOutlines.EveryClump; break;
			case "Leaf cards": FoliageTechnique = FoliageMode.LeafCards; LeafSize = 0.3f; SphericalNormals = 1; BackLightGlow = 0.6f; InnerShade = 0.7f; LeafColourVariety = 0.6f; WindSway = 0.5f; break;
			case "Solid toon": FoliageTechnique = FoliageMode.Solid; LeafSize = 0.3f; SphericalNormals = 1; BackLightGlow = 0.3f; InnerShade = 0; LeafColourVariety = 0.25f; WindSway = 0.2f; FoliageOutlines = FoliageOutlines.EveryClump; break;
			case "Faceted low poly": FoliageTechnique = FoliageMode.Solid; LeafSize = 0.3f; SphericalNormals = 0; BackLightGlow = 0; InnerShade = 0; LeafColourVariety = 0.15f; WindSway = 0.1f; FoliageOutlines = FoliageOutlines.EveryClump; break;
			default: break;   // "Dithered canopy"
		}
	}

	public void ApplyAsciiPreset(string name)
	{
		GlyphDensity = 0.8f; InvertDensity = false; SceneColourVsMono = 1; AsciiBackground = Color.FromHtml("#0b0a0d"); BackgroundTint = 0.12f;
		GlyphBrightness = 1.3f; PerSurfaceSymbols = true; EdgeStrokes = true; MonoGlyphColour = Color.FromHtml("#9fe870");
		switch (name)
		{
			case "Green terminal": GlyphDensity = 0.9f; SceneColourVsMono = 0; AsciiBackground = Color.FromHtml("#06120a"); BackgroundTint = 0; break;
			case "Amber monitor": GlyphDensity = 0.85f; SceneColourVsMono = 0.15f; MonoGlyphColour = Color.FromHtml("#ffb347"); AsciiBackground = Color.FromHtml("#140a02"); BackgroundTint = 0.04f; GlyphBrightness = 1.2f; break;
			case "Ink on paper": GlyphDensity = 1; InvertDensity = true; SceneColourVsMono = 0.25f; MonoGlyphColour = Color.FromHtml("#2a1c14"); AsciiBackground = Color.FromHtml("#efe4c8"); BackgroundTint = 0; GlyphBrightness = 0.55f; break;
			case "Classic ramp": AsciiBackground = Colors.Black; BackgroundTint = 0; GlyphBrightness = 1.4f; PerSurfaceSymbols = false; EdgeStrokes = false; break;
			default: break;   // "Colour glyphs"
		}
	}
}
