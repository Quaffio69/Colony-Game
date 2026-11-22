using Godot;

namespace ColonyGame;

/// <summary>
/// What this build is: its version (project setting application/config/version, stamped from the git tag by the
/// release workflow; "0.1.0-dev" when built by hand) and whether it is a developer build (the editor or a debug export:
/// the Tests & demos window and its test DLL are only there).
/// </summary>
public static class GameInfo
{
    public static string Version => ProjectSettings.GetSetting("application/config/version", "dev").AsString();
    public static bool DevBuild => OS.IsDebugBuild();
}
