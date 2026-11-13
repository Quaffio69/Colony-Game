using Colony.Core.Procedural;

namespace Colony.Core.Agents;

/// <summary>Deterministic two-syllable names, stable for a given seed and index.</summary>
public static class NameGenerator
{
    private static readonly string[] Starts =
        ["Ar", "Bel", "Cor", "Da", "Ed", "Fen", "Gar", "Hal", "Ilm", "Jor", "Kel", "Lun", "Mor", "Nad", "Os", "Pell", "Ro", "Sab", "Tor", "Ul", "Ves", "Wyn"];
    private static readonly string[] Ends =
        ["a", "an", "ec", "en", "ia", "ik", "in", "is", "o", "om", "ra", "ric", "un", "us", "wen", "yn"];

    public static string Generate(int seed, int index)
    {
        string s = Starts[(int)(Noise.Hash(index, 0, 17, seed) * (Starts.Length - 1e-3f))];
        string e = Ends[(int)(Noise.Hash(index, 1, 17, seed) * (Ends.Length - 1e-3f))];
        return s + e;
    }
}
