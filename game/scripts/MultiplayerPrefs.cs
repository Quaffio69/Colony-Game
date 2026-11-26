using System;
using System.Text.Json;
using Godot;

namespace ColonyGame;

/// <summary>
/// What this install remembers for online play, in user://multiplayer.json: the player's name, a private key made once
/// (a random secret: hosts know a returning player by it, whatever name they use, and keep only a digest of it), and the
/// last game joined (for Rejoin). Dev flags (--name, --key) override it for the run without saving.
/// </summary>
public sealed class MultiplayerPrefs
{
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    public string LastAddress { get; set; } = "";
    public int LastPort { get; set; }

    private const string Path = "user://multiplayer.json";
    private static MultiplayerPrefs? s_current;

    /// <summary>The prefs, read on first use (a key is made and saved if there is none yet).</summary>
    public static MultiplayerPrefs Current
    {
        get
        {
            if (s_current != null) return s_current;
            try
            {
                using var f = FileAccess.Open(Path, FileAccess.ModeFlags.Read);
                if (f != null) s_current = JsonSerializer.Deserialize<MultiplayerPrefs>(f.GetAsText());
            }
            catch (Exception e)
            {
                GD.PrintErr($"[net] couldn't read {Path}: {e.Message}");
            }
            s_current ??= new MultiplayerPrefs();
            if (s_current.Name.Length == 0) s_current.Name = System.Environment.UserName is { Length: > 0 } n ? n : "Player";
            if (s_current.Key.Length == 0)
            {
                s_current.Key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
                s_current.Save();
            }
            return s_current;
        }
    }

    public void Save()
    {
        using var f = FileAccess.Open(Path, FileAccess.ModeFlags.Write);
        f?.StoreString(JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
