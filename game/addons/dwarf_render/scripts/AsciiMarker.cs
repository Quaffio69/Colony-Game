using Godot;

namespace DwarfRender;

/// <summary>
/// Put one of these (as a child Node3D) on every dwarf, creature or important item.
/// While the ASCII view is active, the character cell containing this point always shows
/// the marker's symbol (default ☺) in GlyphColour, even when the model is smaller than one
/// cell. It is hidden when something in the scene is clearly in front of it.
/// Place it at the body centre (about 0.75 above the feet for a dwarf).
/// </summary>
[GlobalClass]
public partial class AsciiMarker : Node3D
{
	public const string Group = "dr_marker";

	/// <summary>Glyph group to show (GlyphAtlas.Dwarf, GlyphAtlas.Creature, GlyphAtlas.Item or your own id).</summary>
	[Export] public int GlyphGroup { get; set; } = GlyphAtlas.Dwarf;
	/// <summary>Symbol colour (e.g. the dwarf's tunic colour).</summary>
	[Export] public Color GlyphColour { get; set; } = new Color(0.7f, 0.25f, 0.16f);

	public override void _EnterTree() => AddToGroup(Group);
	public override void _ExitTree() => RemoveFromGroup(Group);
}
