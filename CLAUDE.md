# Vibe SSH Monitoring: notes for Claude (and contributors)

Windows application (C# / .NET 10 / WPF) that monitors and updates Debian / Ubuntu servers over SSH.
Display name **Vibe SSH Monitoring**, identifier **VibeSSHMonitoring** (exe, namespace, package), repository
https://github.com/Baba0bab/VibeSSHMonitoring, license GPL-3.0-or-later. **English is the project's default
language**: code comments, commit messages, CHANGELOG / release notes, GitHub texts and this file. The public
documentation is **bilingual** (README.md English + README.fr.md, CONTRIBUTING and RELEASING with an English then
a French section). The interface is translated into English, French, German and Spanish.

## Commands

```powershell
dotnet build VibeSSHMonitoring.slnx -c Release      # must give 0 warnings
dotnet test Tests                                    # 26+ tests (vault, parsing, translations, shell scripts via Git's sh)
dotnet run                                           # real configuration (Properties/launchSettings.json clears the test variables)
dotnet run --launch-profile Test                     # test mode: %TEMP%\VibeSSHMonitoring.Demo + demo server
dotnet run --launch-profile Demo                     # README screenshot servers (UI/DemoServers.cs), reset at each launch
./scripts/pack.ps1 -Version 1.2.3 -OutputDir $env:TEMP\vsm-pack  # installer + Velopack packages, publishes nothing
```

Stop the dev build before compiling (`Get-Process VibeSSHMonitoring | Stop-Process`): the running exe locks the output.
The user may have the installed copy running (`%LOCALAPPDATA%\VibeSSHMonitoring\current\`): don't kill it.

## Layout

- `Core/`: no WPF. `SshConnection.cs` (connection, host key, plain / sudo / su execution), `RemoteCommands.cs` (probe, apt, parsing), `SecretVault.cs` (DPAPI / master password / plain), `AppUpdater.cs` (Velopack), `Loc.cs` (translations), `TerminalSession.cs`, `ExternalTerminal.cs`.
- `ServerManager.cs` (monitoring, actions), `AppController.cs` (lifecycle, vault, tray, updates, `Restart()`), `MainWindow.xaml` (pages), `ServerEditWindow.xaml` (server editor), `Program.cs` (Velopack, then `Loc.Initialize`, then WPF).
- `Localization/*.json`: interface texts, flat `"Area.Key": "text"` with `{0}` placeholders, embedded in the exe.
- `Themes/Theme.xaml`: theme inherited from GoveeScreen (another project of the author, `C:\serveurs\GoveeScreenV2`).
- `Assets/terminal/`: offline xterm.js (WebView2). `scripts/pack.ps1`: packaging. `tools/ui-test/`: UI automation.

## Translations

- Never hard-code interface text: `Loc.T("Area.Key")` / `Loc.T("Area.Key", args)` in C#, `{ui:Tr Area.Key}` in XAML (`UI/TrExtension.cs`).
- Add every new key to **all** files of `Localization/` (`en.json` is the reference and the fallback). `LocalizationTests` fails if a language misses a key or a placeholder, or has a different number of `|` separators.
- Texts are read when an element is built: a language change needs a restart (`AppController.Restart()`, which starts the exe with `--wait-for <pid>`).
- `Loc.Culture` formats numbers and dates; `Format.SizeUnits` holds the size units (`KB|MB|…`, `Ko|Mo|…` in French).
- A new language: copy `en.json`, translate it, add it to `Loc.Languages`.

## Invariants that must never change

Baked into every installed copy or into users' data:
- DPAPI entropy `"SshVibeMonitoring.v1"` (`Core/SecretVault.cs`), otherwise existing vaults become unreadable;
- folders `%APPDATA%\VibeSSHMonitoring` (config, vault) and `%LOCALAPPDATA%\VibeSSHMonitoring.Cache`; the migration from the old `SshVibeMonitoring` name stays;
- `%LOCALAPPDATA%\VibeSSHMonitoring` belongs to the installer (Velopack): write nothing there;
- package id `VibeSSHMonitoring`, channel `win`, `AppUpdater.RepositoryUrl`, release file names;
- the setting values stored in `config.json` (enum names, `Language` codes such as `"system"`, `"fr"`);
- no secret on a command line, in a log or in `config.json` (sudo: password on stdin; root commands through `sudo -- sh -c` with `exec </dev/null`);
- commands sent to servers are always non-interactive (apt: `DEBIAN_FRONTEND=noninteractive`, `--force-confold`).

## Checking a change

- **No Linux server here** (no WSL, no Docker): anything touching a real server must be validated by the user on their servers. Shell scripts are at least checked by the tests (`sh -n` with Git's sh).
- **`dotnet run` and environment variables**: the default launch profile empties `VIBESSHMONITORING_HOME` and
  `VIBESSHMONITORING_DEMO`, so `$env:VIBESSHMONITORING_HOME = …; dotnet run` opens the **real** config. For an
  isolated folder, use `--launch-profile Test` or start the built exe directly. Never give the user `$env:` commands
  to paste: they stay set in their terminal.
- **Upgrade progress without a server**: a Debug build started with `VIBESSHMONITORING_DEMO=1` (profile `Test`) opens a fake
  server that replays an upgrade in a loop (`UI/UpgradeDemo.cs`), and runs beside an installed copy.
- **Interface**: start with `VIBESSHMONITORING_HOME` (never touch the real config), then
  `tools/ui-test/Invoke-UiElement.ps1` (click by name, `-AutomationId x -Value y`, `-List`) and
  `tools/ui-test/Capture-Window.ps1 -Out shot.png` (captures even a hidden window). Desktop screenshots
  (computer use) hide the user's other windows and don't show the app: use these scripts.
  Element names depend on the interface language: set `"Language": "en"` in the test `config.json` for stable names.
  README screenshots (`docs/screenshots/`): profile `Demo`, then `Capture-Window.ps1`.
  Glyph buttons are named after their character (`[char]0xE712` = "⋯"); modal dialogs built in code (`UI/Dialogs.cs`) are not always exposed to UI Automation.
- **Installer and updates** (tested end to end on 2026-10-20): `pack.ps1` two versions into the same folder (delta),
  install the first one with `VibeSSHMonitoring-win-Setup.exe --silent` with `VIBESSHMONITORING_HOME` and
  `VIBESSHMONITORING_UPDATE_SOURCE=<packages folder>` set, check from the settings, then **uninstall**
  (`%LOCALAPPDATA%\VibeSSHMonitoring\Update.exe uninstall --silent`) to leave the machine clean.
  Careful: the user has their own installed copy; a test install on top of it breaks it. Prefer testing on a machine
  or account without it, or ask first.

## Publishing a release

Full procedure: [docs/RELEASING.md](docs/RELEASING.md). In short: a `## [X.Y.Z] - YYYY-MM-DD` section in
CHANGELOG.md (exact heading: it is the source of the release notes, **written in English**), commit on `main`,
annotated tag `vX.Y.Z`, push the tag → `.github/workflows/release.yml` builds and publishes; follow with `gh run watch`,
check the files with `gh release view`.

**Always ask the user for explicit confirmation before** `git push`, creating or pushing a tag,
`gh release create/edit/delete`, `gh repo create` or `gh repo edit`: it is public, and a release is downloaded
automatically by every installed copy. Never delete a published release: if something is wrong, publish a fix.

## Known pitfalls

- Bash (tool): heredocs containing apostrophes sometimes fail → write the script to a file.
- `.ps1` scripts with non-ASCII characters: save as UTF-8 **with BOM** (Windows PowerShell 5.1), see `.editorconfig`.
- PowerShell: variable names are case-insensitive (`$window` = parameter `$Window`); `$matches` is reserved.
- WebView2 (built-in terminal) is drawn above the WPF content: no animation or overlay on that page.
- Python replacements: use raw strings (`r'''…'''`) for C# containing `\n` / `\r`.
- `MainWindow.xaml.cs` and `ServerEditWindow.xaml.cs` contain raw private-use glyph characters (Segoe icons): edit
  them with targeted replacements, never by rewriting the whole file from a copy.
