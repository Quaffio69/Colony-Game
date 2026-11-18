using System;

namespace Colony.Core;

/// <summary>
/// Dev-only observation points: the game's test menu runs the unit tests on a background thread and shows the world
/// a test built. Per thread, so a test never sees the game's own sim (and the game never sees a test's), and null
/// (one null check per step) everywhere else.
/// </summary>
public static class DevHooks
{
    /// <summary>Called at the end of every <see cref="ColonySim"/> constructor on this thread.</summary>
    [ThreadStatic] public static Action<ColonySim>? SimCreated;

    /// <summary>Called at the start of <see cref="ColonySim.Step"/> and <see cref="Fluids.Water.Step"/> on this thread.</summary>
    [ThreadStatic] public static Action? Stepping;
}
