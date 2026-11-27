namespace Colony.Core.Procedural;

/// <summary>
/// Where a world's generation is, for a loading screen: the current step (a short title such as "Eroding valleys" and a
/// line about it), the share done, the steps finished with their times, and a small colour map of the world so far.
/// Written by the generating thread, read by another: every field is replaced whole (no torn reads), and
/// <see cref="Version"/> counts changes. Generation is identical with or without one.
/// </summary>
public sealed class WorldGenProgress
{
    /// <summary>A finished step: its title and how long it took.</summary>
    public readonly record struct Done(string Title, long Milliseconds);

    private readonly object _gate = new();
    private readonly List<Done> _done = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private volatile string _title = "", _detail = "";
    private volatile float _fraction;
    private volatile int _version;
    private (byte[] Rgb, int Width, int Height)? _map;
    private long _stepStart;
    private float _from, _to;

    /// <summary>The step under way ("Raising mountains").</summary>
    public string Title => _title;
    /// <summary>A line about it ("where the plates collide").</summary>
    public string Detail => _detail;
    /// <summary>Overall share done, 0..1.</summary>
    public float Fraction => _fraction;
    /// <summary>Bumped on every change (title, fraction, map).</summary>
    public int Version => _version;
    public long ElapsedMilliseconds => _clock.ElapsedMilliseconds;

    /// <summary>The steps finished so far, oldest first.</summary>
    public Done[] Finished
    {
        get { lock (_gate) return _done.ToArray(); }
    }

    /// <summary>The latest map of the world (RGB bytes, row by row, north up), or null before the first.</summary>
    public (byte[] Rgb, int Width, int Height)? Map
    {
        get { lock (_gate) return _map; }
    }

    /// <summary>Starts a step that covers overall progress <paramref name="from"/>..<paramref name="to"/>.</summary>
    public void Step(string title, string detail, float from, float to)
    {
        lock (_gate)
        {
            if (_title.Length > 0) _done.Add(new Done(_title, _clock.ElapsedMilliseconds - _stepStart));
            _stepStart = _clock.ElapsedMilliseconds;
        }
        _from = from;
        _to = to;
        _detail = detail;
        _title = title;
        _fraction = from;
        _version++;
    }

    /// <summary>Progress within the current step, 0..1.</summary>
    public void Within(float share)
    {
        _fraction = _from + (_to - _from) * Math.Clamp(share, 0f, 1f);
        _version++;
    }

    /// <summary>Retitles the current step (a count became known: "Growing 41,200 trees").</summary>
    public void Retitle(string title, string? detail = null)
    {
        if (detail != null) _detail = detail;
        _title = title;
        _version++;
    }

    /// <summary>Publishes a new map of the world so far.</summary>
    public void Show(byte[] rgb, int width, int height)
    {
        lock (_gate) _map = (rgb, width, height);
        _version++;
    }

    /// <summary>Closes the last step.</summary>
    public void Finish()
    {
        Step("", "", 1f, 1f);
        _fraction = 1f;
        _version++;
    }
}
