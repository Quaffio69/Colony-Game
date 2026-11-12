namespace Colony.Core.Procedural;

/// <summary>
/// Small deterministic value-noise implementation. Same seed + coordinates always give the same
/// result on every machine, which keeps world generation reproducible and testable.
/// </summary>
public static class Noise
{
    /// <summary>Hash of integer coordinates to a float in [0, 1].</summary>
    public static float Hash(int x, int y, int z, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + z * 1440662683 + seed * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return h / (float)uint.MaxValue;
        }
    }

    public static float Value2D(float x, float y, int seed)
    {
        int x0 = FastFloor(x), y0 = FastFloor(y);
        float tx = Smooth(x - x0), ty = Smooth(y - y0);

        float a = Hash(x0, y0, 0, seed), b = Hash(x0 + 1, y0, 0, seed);
        float c = Hash(x0, y0 + 1, 0, seed), d = Hash(x0 + 1, y0 + 1, 0, seed);
        return Lerp(Lerp(a, b, tx), Lerp(c, d, tx), ty);
    }

    public static float Value3D(float x, float y, float z, int seed)
    {
        int x0 = FastFloor(x), y0 = FastFloor(y), z0 = FastFloor(z);
        float tx = Smooth(x - x0), ty = Smooth(y - y0), tz = Smooth(z - z0);

        float Plane(int zi)
        {
            float a = Hash(x0, y0, zi, seed), b = Hash(x0 + 1, y0, zi, seed);
            float c = Hash(x0, y0 + 1, zi, seed), d = Hash(x0 + 1, y0 + 1, zi, seed);
            return Lerp(Lerp(a, b, tx), Lerp(c, d, tx), ty);
        }

        return Lerp(Plane(z0), Plane(z0 + 1), tz);
    }

    /// <summary>Fractal (multi-octave) 2D noise, normalised to roughly [0, 1].</summary>
    public static float Fbm2D(float x, float y, int seed, int octaves = 4)
    {
        float sum = 0, amp = 1, norm = 0, freq = 1;
        for (int i = 0; i < octaves; i++)
        {
            sum += Value2D(x * freq, y * freq, seed + i * 101) * amp;
            norm += amp;
            amp *= 0.5f;
            freq *= 2f;
        }
        return sum / norm;
    }

    private static int FastFloor(float v) => v >= 0 ? (int)v : (int)v - 1;
    private static float Smooth(float t) => t * t * (3 - 2 * t);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
