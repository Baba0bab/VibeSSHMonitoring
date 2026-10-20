// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeSSHMonitoring.Core;

/// <summary>
/// Configuration de l'application (%APPDATA%\VibeSSHMonitoring\config.json) :
/// réglages globaux et liste des serveurs, sans aucun secret.
/// </summary>
public class AppConfig
{
    public List<ServerConfig> Servers { get; set; } = new();

    /// <summary>Réglages globaux, que chaque serveur peut remplacer.</summary>
    public ServerSettings Defaults { get; set; } = ServerSettings.CreateDefaults();

    public StorageMode Storage { get; set; } = StorageMode.Dpapi;
    public int AutoLockMinutes { get; set; }               // 0 = jamais (mot de passe maître)

    /// <summary>Terminal personnalisé : {host} {port} {user} {name} {keyopt}.</summary>
    public string CustomTerminalCommand { get; set; } = "wt.exe new-tab ssh {keyopt} -p {port} {user}@{host}";
    /// <summary>Terminaux externes : copie le mot de passe dans le presse-papiers (effacé après 30 s).</summary>
    public bool CopyPasswordForExternal { get; set; } = true;

    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;

    // ===== Mises à jour de l'application (GitHub Releases, via Velopack) =====
    public bool AppUpdateCheck { get; set; } = true;          // recherche au démarrage puis toutes les 12 h
    public bool AppUpdateAutoInstall { get; set; } = true;    // installe à la fermeture (ou au démarrage suivant)
    public bool AppUpdatePrerelease { get; set; }             // inclut les préversions (bêta)

    /// <summary>%APPDATA%\VibeSSHMonitoring, ou le dossier de la variable VIBESSHMONITORING_HOME (tests, mode portable).</summary>
    public static string ConfigDirectory { get; } =
        Environment.GetEnvironmentVariable("VIBESSHMONITORING_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeSSHMonitoring");

    /// <summary>
    /// Cache local (WebView2 du terminal intégré). Hors de %LOCALAPPDATA%\VibeSSHMonitoring,
    /// qui est le dossier d'installation géré par l'installateur.
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
    /// Reprend la configuration de l'ancien nom de l'application (SshVibeMonitoring) au premier
    /// lancement. Les fichiers sont copiés, pas déplacés : l'ancien dossier reste en sauvegarde.
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
            // Migration non critique : l'application repart d'une configuration vide
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
            // Config illisible : on la garde de côté plutôt que de l'écraser
            try { File.Copy(ConfigPath, ConfigPath + ".corrompu", overwrite: true); } catch { }
            config = new AppConfig();
        }
        config.Defaults.FillMissing(ServerSettings.CreateDefaults());
        return config;
    }

    /// <summary>Écriture atomique : un fichier temporaire remplace l'ancien.</summary>
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
            // Sauvegarde non critique : la prochaine modification réessaiera
        }
    }

    public EffectiveSettings EffectiveFor(ServerConfig server) => EffectiveSettings.Resolve(Defaults, server.Overrides);
}
