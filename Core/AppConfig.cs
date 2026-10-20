// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeSSHMonitoring.Core;

/// <summary>
/// Application configuration (%APPDATA%\VibeSSHMonitoring\config.json): global settings and
/// the server list, without any secret.
/// </summary>
public class AppConfig
{
    public List<ServerConfig> Servers { get; set; } = new();

    /// <summary>Global settings, which each server can override.</summary>
    public ServerSettings Defaults { get; set; } = ServerSettings.CreateDefaults();

    /// <summary>Interface language: "system" (Windows display language) or a code from <see cref="Loc.Languages"/>.</summary>
    public string Language { get; set; } = Loc.SystemLanguage;

    public StorageMode Storage { get; set; } = StorageMode.Dpapi;
    public int AutoLockMinutes { get; set; }               // 0 = never (master password)

    /// <summary>Custom terminal: {host} {port} {user} {name} {keyopt} {ssh}.</summary>
    public string CustomTerminalCommand { get; set; } = "wt.exe new-tab ssh {keyopt} -p {port} {user}@{host}";
    /// <summary>External terminals: copy the password to the clipboard (cleared after 30 s).</summary>
    public bool CopyPasswordForExternal { get; set; } = true;

    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;

    // ===== Application updates (GitHub Releases, through Velopack) =====
    public bool AppUpdateCheck { get; set; } = true;          // check at startup, then every 12 h
    public bool AppUpdateAutoInstall { get; set; } = true;    // install on exit (or at the next startup)
    public bool AppUpdatePrerelease { get; set; }             // include pre-releases (beta)

    /// <summary>%APPDATA%\VibeSSHMonitoring, or the VIBESSHMONITORING_HOME folder (tests, portable use).</summary>
    public static string ConfigDirectory { get; } =
        Environment.GetEnvironmentVariable("VIBESSHMONITORING_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeSSHMonitoring");

    /// <summary>
    /// Local cache (WebView2 data of the built-in terminal). Outside %LOCALAPPDATA%\VibeSSHMonitoring,
    /// which is the install folder owned by the installer.
    /// </summary>
    public static string CacheDirectory { get; } =
        Environment.GetEnvironmentVariable("VIBESSHMONITORING_HOME") is { Length: > 0 } custom
            ? Path.Combine(custom, "cache")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeSSHMonitoring.Cache");

    private static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Picks up the configuration of the application's former name (SshVibeMonitoring) on first
    /// launch. Files are copied, not moved: the old folder stays as a backup.
    /// </summary>
    private static void MigrateLegacyDirectory()
    {
        if (Environment.GetEnvironmentVariable("VIBESSHMONITORING_HOME") is { Length: > 0 })
            return;
        string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SshVibeMonitoring");
        if (Directory.Exists(ConfigDirectory) || !File.Exists(Path.Combine(legacy, "config.json")))
            return;
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            foreach (var name in new[] { "config.json", "secrets.json" })
            {
                string source = Path.Combine(legacy, name);
                if (File.Exists(source))
                    File.Copy(source, Path.Combine(ConfigDirectory, name));
            }
        }
        catch
        {
            // Not critical: the application starts from an empty configuration
        }
    }

    public static AppConfig Load()
    {
        MigrateLegacyDirectory();
        AppConfig config;
        try
        {
            config = File.Exists(ConfigPath)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOptions) ?? new AppConfig()
                : new AppConfig();
        }
        catch
        {
            // Unreadable configuration: keep it aside rather than overwrite it
            try { File.Copy(ConfigPath, ConfigPath + ".corrupt", overwrite: true); } catch { }
            config = new AppConfig();
        }
        config.Defaults.FillMissing(ServerSettings.CreateDefaults());
        return config;
    }

    /// <summary>Atomic write: a temporary file replaces the previous one.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            string temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, ConfigPath, overwrite: true);
        }
        catch
        {
            // Not critical: the next change will try again
        }
    }

    public EffectiveSettings EffectiveFor(ServerConfig server) => EffectiveSettings.Resolve(Defaults, server.Overrides);
}
