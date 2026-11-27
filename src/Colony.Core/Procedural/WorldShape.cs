namespace Colony.Core.Procedural;

/// <summary>How much of the world is sea (oceanic plates).</summary>
public enum Oceans : byte { None, Coasts, Seas, Islands }

/// <summary>How strongly the plates push up mountains.</summary>
public enum Relief : byte { Gentle, Rolling, Mountainous, Alpine }

/// <summary>The world's temperature band (its latitude).</summary>
public enum Climate : byte { Frozen, Cold, Temperate, Warm, Tropical }

/// <summary>How much moisture the prevailing wind brings.</summary>
public enum Rainfall : byte { Arid, Dry, Normal, Wet }

/// <summary>How readily streams gather into rivers.</summary>
public enum Rivers : byte { Few, Normal, Many }

/// <summary>
/// The world-generation choices (the New colony menu's World options) and which generator makes the world. Part of a
/// world's recipe: saves and multiplayer games carry it, and every peer builds the same world from it.
/// <see cref="Generator"/> 1 is the first generator (noise and drainage), kept so worlds saved before the tectonic one
/// rebuild exactly; 2 is the tectonic generator (<see cref="Landform"/>), which reads the rest.
/// </summary>
public sealed record WorldShape(
    int Generator = WorldShape.Current,
    Oceans Oceans = Oceans.Seas,
    Relief Relief = Relief.Mountainous,
    Climate Climate = Climate.Temperate,
    Rainfall Rainfall = Rainfall.Normal,
    Rivers Rivers = Rivers.Normal)
{
    /// <summary>The generator new worlds use.</summary>
    public const int Current = 2;

    public static readonly WorldShape Default = new();
    /// <summary>The first generator (worlds saved before version 2 of the recipe).</summary>
    public static readonly WorldShape Legacy = new(Generator: 1);

    public bool IsLegacy => Generator < 2;

    public void Write(BinaryWriter w)
    {
        w.Write((byte)Generator);
        w.Write((byte)Oceans);
        w.Write((byte)Relief);
        w.Write((byte)Climate);
        w.Write((byte)Rainfall);
        w.Write((byte)Rivers);
    }

    public static WorldShape Read(BinaryReader r) =>
        new(r.ReadByte(), (Oceans)r.ReadByte(), (Relief)r.ReadByte(), (Climate)r.ReadByte(), (Rainfall)r.ReadByte(), (Rivers)r.ReadByte());

    /// <summary>Short summary for save lists ("temperate, seas, mountainous").</summary>
    public string Describe() => IsLegacy ? "classic terrain"
        : $"{Climate.ToString().ToLowerInvariant()}, {Rainfall.ToString().ToLowerInvariant()}, {Oceans.ToString().ToLowerInvariant()}, {Relief.ToString().ToLowerInvariant()}";
}
