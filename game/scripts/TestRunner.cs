using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Colony.Core;
using Godot;

namespace ColonyGame;

/// <summary>One unit test (a [Fact], or one data row of a [Theory]) found in the test assembly.</summary>
public sealed record TestCase(string Class, string Name, MethodInfo Method, object?[]? Args, string? Skip)
{
    public string FullName => $"{Class}.{Name}";
}

/// <summary>How a test run went, and the world it built (if any).</summary>
public sealed record TestResult(TestCase Case, bool Passed, bool Stopped, string Message, double Ms, ColonySim? Sim);

/// <summary>
/// Runs the xUnit tests of <c>tests/Colony.Core.Tests</c> inside the game, without xUnit's runner: finds [Fact] and
/// [Theory] methods by attribute name, invokes them on a background thread, and catches the first
/// <see cref="ColonySim"/> each one builds (<see cref="DevHooks"/>) so the game can show it. "Watch" stops the test
/// at its first sim or water step: the world as the test set it up, before anything has run.
/// The assembly is loaded from bytes into its own (never unloaded) load context sharing the game's Colony.Core, so a rebuild while
/// the game runs isn't blocked, and a newer build is picked up the next time the list is refreshed.
/// </summary>
public static class TestRunner
{
    private sealed class Stop : Exception;

    private sealed class TestContext(string dir) : AssemblyLoadContext("ColonyTests", isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            // Core types must be the game's own, or a test's ColonySim isn't one the views can draw.
            if (name.Name == typeof(ColonySim).Assembly.GetName().Name) return typeof(ColonySim).Assembly;
            if (Default.Assemblies.Any(a => a.GetName().Name == name.Name)) return null;
            string path = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private static Assembly? _assembly;
    private static TestContext? _context; // held: a context nobody references gets finalized mid-run ("is unloading")
    private static DateTime _loadedStamp;
    private static List<TestCase> _cases = new();

    /// <summary>Where the test assembly is built (Debug; the game's build task builds the whole solution).</summary>
    public static string AssemblyPath =>
        Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "tests", "Colony.Core.Tests", "bin", "Debug", "net8.0", "Colony.Core.Tests.dll"));

    /// <summary>All tests, reloading the assembly if it was rebuilt. Throws with a readable message if it isn't built.</summary>
    public static IReadOnlyList<TestCase> Discover()
    {
        string path = AssemblyPath;
        if (!File.Exists(path))
            throw new FileNotFoundException($"The tests aren't built ({path}). Run: dotnet build ColonyGame.sln");
        var stamp = File.GetLastWriteTimeUtc(path);
        if (_assembly != null && stamp == _loadedStamp) return _cases;

        var context = new TestContext(Path.GetDirectoryName(path)!);
        using var dll = File.OpenRead(path);
        string pdbPath = Path.ChangeExtension(path, ".pdb");
        using var pdb = File.Exists(pdbPath) ? File.OpenRead(pdbPath) : null;
        var assembly = context.LoadFromStream(dll, pdb);

        var cases = new List<TestCase>();
        foreach (var type in assembly.GetTypes().Where(t => t.IsPublic && t.IsClass && !t.IsAbstract).OrderBy(t => t.Name))
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .OrderBy(m => m.MetadataToken)) // source order
        {
            var attrs = method.GetCustomAttributes(inherit: true);
            var test = attrs.FirstOrDefault(a => a.GetType().Name is "FactAttribute" or "TheoryAttribute");
            if (test == null) continue;
            string? skip = test.GetType().GetProperty("Skip")?.GetValue(test) as string;
            string cls = type.Name.EndsWith("Tests") ? type.Name[..^5] : type.Name;
            if (test.GetType().Name == "TheoryAttribute")
            {
                foreach (var data in attrs.Where(a => a.GetType().GetMethod("GetData") != null && a.GetType().Name.EndsWith("DataAttribute")))
                {
                    if (data.GetType().GetMethod("GetData")!.Invoke(data, [method]) is not IEnumerable<object?[]> rows) continue;
                    foreach (var row in rows)
                        cases.Add(new TestCase(cls, $"{method.Name}({string.Join(", ", row.Select(v => v?.ToString() ?? "null"))})", method, row, skip));
                }
            }
            else cases.Add(new TestCase(cls, method.Name, method, null, skip));
        }
        _assembly = assembly;
        _context = context;
        _loadedStamp = stamp;
        _cases = cases;
        return cases;
    }

    /// <summary>Runs a test on a background thread. <paramref name="watch"/>: stop at its first step and keep the world as set up.</summary>
    public static Task<TestResult> Run(TestCase test, bool watch) =>
        Task.Factory.StartNew(() => RunNow(test, watch), TaskCreationOptions.LongRunning);

    public static TestResult RunNow(TestCase test, bool watch)
    {
        ColonySim? sim = null;
        DevHooks.SimCreated = s => sim ??= s;
        DevHooks.Stepping = watch ? () => { if (sim != null) throw new Stop(); } : null;
        var clock = Stopwatch.StartNew();
        try
        {
            if (test.Skip != null) return new TestResult(test, true, false, $"skipped: {test.Skip}", 0, null);
            object? instance = test.Method.IsStatic ? null : Activator.CreateInstance(test.Method.DeclaringType!);
            try
            {
                if (test.Method.Invoke(instance, test.Args) is Task task) task.GetAwaiter().GetResult();
            }
            finally
            {
                (instance as IDisposable)?.Dispose();
            }
            return new TestResult(test, true, false, "passed", clock.Elapsed.TotalMilliseconds, sim);
        }
        catch (Exception e)
        {
            var error = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
            if (error is Stop) return new TestResult(test, true, true, "stopped at its first step (the world as the test set it up)", clock.Elapsed.TotalMilliseconds, sim);
            // The assertion's message and where in the test it failed.
            string where = error.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains(".Tests.") && l.Contains(":line ")) ?? "";
            if (where.LastIndexOf(" in ", StringComparison.Ordinal) is int at and > 0) where = where[(at + 4)..];
            string message = error.Message.Trim();
            return new TestResult(test, false, false, $"FAILED: {message}" + (where.Length > 0 ? $"\n  at {Path.GetFileName(where)}" : ""),
                clock.Elapsed.TotalMilliseconds, sim);
        }
        finally
        {
            DevHooks.SimCreated = null;
            DevHooks.Stepping = null;
        }
    }
}
