using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace System.Runtime.CompilerServices
{
    /// <summary>Lets records and init-only setters compile for .NET Framework.</summary>
    internal static class IsExternalInit { }
}

namespace ColonyLauncher
{
    /// <summary>
    /// Windows integration, all per user (no administrator rights): the launcher copies itself into the install folder,
    /// adds Start menu (and desktop) shortcuts and an Apps &amp; features entry, replaces itself when a newer launcher is
    /// released, and uninstalls (keeping the player's saves).
    /// </summary>
    internal static class Shell
    {
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ColonyGame";
        public static string RunningExe => Process.GetCurrentProcess().MainModule!.FileName;
        private static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Colony Game.lnk");
        private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Colony Game.lnk");

        public static bool RunningInstalled(Installation inst) =>
            string.Equals(Path.GetFullPath(RunningExe), Path.GetFullPath(inst.LauncherExe), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Puts this launcher in the install folder (unless it is already running from there) and registers it with
        /// Windows: shortcuts and an uninstall entry. <paramref name="integrate"/> false (a test root) skips the Windows side.
        /// </summary>
        public static void InstallSelf(Installation inst, bool integrate, bool desktopShortcut)
        {
            Directory.CreateDirectory(inst.LauncherDir);
            if (!RunningInstalled(inst)) File.Copy(RunningExe, inst.LauncherExe, true);
            if (!integrate) return;
            CreateShortcut(StartMenuLink, inst.LauncherExe);
            if (desktopShortcut && !File.Exists(DesktopLink)) CreateShortcut(DesktopLink, inst.LauncherExe);
            Register(inst);
        }

        public static bool HasDesktopShortcut => File.Exists(DesktopLink);

        private static void CreateShortcut(string link, string target)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic sc = shell.CreateShortcut(link);
                sc.TargetPath = target;
                sc.WorkingDirectory = Path.GetDirectoryName(target);
                sc.IconLocation = target + ",0";
                sc.Description = "Colony Game: install, update and play";
                sc.Save();
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }

        /// <summary>The Apps &amp; features entry (version, size, uninstall).</summary>
        public static void Register(Installation inst)
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
            if (key is null) return;
            key.SetValue("DisplayName", "Colony Game");
            key.SetValue("DisplayVersion", inst.State.Installed ?? Versions.Show(App.OwnVersion));
            key.SetValue("Publisher", "Colony Game");
            key.SetValue("DisplayIcon", inst.LauncherExe + ",0");
            key.SetValue("InstallLocation", inst.Root);
            key.SetValue("UninstallString", $"\"{inst.LauncherExe}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(inst.InstalledBytes() / 1024), RegistryValueKind.DWord);
        }

        /// <summary>
        /// Removes the game, the launcher's files, its shortcuts and its Apps &amp; features entry. Saves (in the game's
        /// user folder) stay. The running launcher can't delete itself: a hidden command removes its folder once it exits.
        /// </summary>
        public static void Uninstall(Installation inst, bool integrate)
        {
            foreach (var dir in new[] { inst.GameRoot, inst.Downloads })
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            foreach (var f in new[] { Path.Combine(inst.Root, "state.json") })
                if (File.Exists(f)) File.Delete(f);
            if (integrate)
            {
                foreach (var link in new[] { StartMenuLink, DesktopLink })
                    if (File.Exists(link)) File.Delete(link);
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            }
            string root = inst.Root;
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{root}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }

        /// <summary>
        /// Swaps in a newly downloaded launcher: a running exe may be renamed (not deleted), so the old one steps aside
        /// as .old (removed on the next start) and the new one takes its name.
        /// </summary>
        public static void ReplaceSelf(Installation inst, string downloaded)
        {
            string exe = inst.LauncherExe, old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            if (File.Exists(exe)) File.Move(exe, old);
            File.Move(downloaded, exe);
        }

        public static void CleanOld(Installation inst)
        {
            try { if (File.Exists(inst.LauncherExe + ".old")) File.Delete(inst.LauncherExe + ".old"); }
            catch { /* the old one is still closing: next time */ }
        }

        public static void Open(string pathOrUrl)
        {
            if (!pathOrUrl.StartsWith("http") && !Directory.Exists(pathOrUrl)) Directory.CreateDirectory(pathOrUrl);
            Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
        }
    }
}
