# Distribution

Releases (tag -> GitHub Actions), the launcher, the icon.

- **Releases:** pushing a tag `vX.Y.Z` runs `.github/workflows/release.yml` (windows-latest): tests, downloads Godot
  4.7.2 .NET + Windows export templates (cached), stamps `application/config/version` in `game/project.godot`
  (`0.1.0-dev` when built by hand; shown in the main menu via `GameInfo.Version`), `--import` then
  `--export-release "Windows Desktop"` (`game/export_presets.cfg`: includes `ui/*.json`, excludes the kit demo, icon
  `game/icon.ico`), zips `build/windows` + LICENSE + THIRD_PARTY_NOTICES.md as `ColonyGame-X.Y.Z-windows.zip` with a
  `.sha256`, builds the launcher with `-p:Version` and `-p:GitHubRepo=owner/name`, and publishes a GitHub Release (the
  annotated tag message is the notes; a hyphen makes a pre-release, which launchers skip). `ci.yml` tests every push.
  Release builds (`OS.IsDebugBuild()` false) hide Tests & demos (F1), which loads the test DLL from the source tree.
- **Launcher (`launcher/ColonyLauncher`, WPF on .NET Framework 4.8: one ~400 KB exe, nothing to install; not in the
  sln, so Godot's builds skip it):** asks `api.github.com/repos/<repo>/releases/latest`, installs per user to
  `%LOCALAPPDATA%\ColonyGame` (`game\<version>`, `state.json`, `launcher\ColonyLauncher.exe`), downloads beside the
  installed version, verifies the SHA-256, unpacks to staging and switches only when complete (keeps the previous
  version; zip-slip checked); first install copies itself into `launcher\`, adds Start menu (+ optional desktop)
  shortcuts and an Apps & features entry (HKCU, `--uninstall`); replaces itself when a release carries a newer
  `ColonyLauncher.exe` (rename the running exe to `.old`, restart with the same args); uninstall keeps saves
  (`%APPDATA%\Godot\app_userdata\Colony Game`). Look: Hearth theme, IBM Plex Mono (embedded), pixel glyphs from the
  game's 5x7 bitmaps (`Glyphs.cs`, `GlyphIcon`), a living DF-style world map backdrop (`WorldBackdrop`). Dev/test flags:
  `-repo=owner/name` (or `launcher.repo` beside the exe), `-feed=FOLDER` (latest.json + files, no GitHub),
  `-root=FOLDER` (no shortcuts/registry), `-auto-install`, `-auto-uninstall` (only with -root),
  `-state=install|installask|uptodate|update|download|offline|none|uninstall|licenses` and `-screenshot=FILE.png`
  (renders, saves, quits: always pass it in automated runs, or the window waits on the user's screen).
- **Icon:** 16x16 pixel art (a mountain hall with a lit doorway), generated into `game/icon.ico` (16-256 px).

