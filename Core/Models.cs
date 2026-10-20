// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

namespace VibeSSHMonitoring.Core;

public enum AuthMethod { Password, PrivateKey }

/// <summary>Passage en root pour apt et le redémarrage.</summary>
public enum ElevationMethod
{
    None,   // utilisateur root, ou aucune élévation
    Sudo,   // sudo avec le mot de passe de l'utilisateur (ou NOPASSWD)
    Su      // su avec le mot de passe de root
}

public enum StorageMode
{
    Dpapi,           // chiffré par Windows et lié à la session : aucune saisie
    MasterPassword,  // Argon2id + AES-256-GCM, mot de passe demandé au démarrage
    PlainText        // en clair dans le fichier
}

public enum TerminalMode { Embedded, WindowsTerminal, PowerShell, Cmd, PuTTY, Custom }

public enum UpgradeKind { Upgrade, FullUpgrade }

/// <summary>
/// Réglages de supervision. Les valeurs globales sont toutes renseignées ; sur un serveur,
/// une valeur null signifie « utiliser le réglage global ».
/// </summary>
public class ServerSettings
{
    public bool? ShowOnline { get; set; }
    public bool? ShowResources { get; set; }
    public bool? ShowSystem { get; set; }
    public bool? ShowUpdates { get; set; }
    public int? RefreshSeconds { get; set; }
    public int? AutoCheckHours { get; set; }      // 0 = jamais (apt update automatique)
    public bool? NotifyUpdates { get; set; }
    public bool? NotifyOffline { get; set; }
    public TerminalMode? Terminal { get; set; }
    public bool? RootShell { get; set; }          // terminal ouvert directement en root
    public UpgradeKind? Upgrade { get; set; }
    public bool? AutoRemove { get; set; }

    public static ServerSettings CreateDefaults() => new()
    {
        ShowOnline = true,
        ShowResources = true,
        ShowSystem = true,
        ShowUpdates = true,
        RefreshSeconds = 60,
        AutoCheckHours = 12,
        NotifyUpdates = true,
        NotifyOffline = true,
        Terminal = TerminalMode.Embedded,
        RootShell = false,
        Upgrade = UpgradeKind.Upgrade,
        AutoRemove = false
    };

    /// <summary>Complète les valeurs manquantes (config d'une version précédente).</summary>
    public void FillMissing(ServerSettings defaults)
    {
        ShowOnline ??= defaults.ShowOnline;
        ShowResources ??= defaults.ShowResources;
        ShowSystem ??= defaults.ShowSystem;
        ShowUpdates ??= defaults.ShowUpdates;
        RefreshSeconds ??= defaults.RefreshSeconds;
        AutoCheckHours ??= defaults.AutoCheckHours;
        NotifyUpdates ??= defaults.NotifyUpdates;
        NotifyOffline ??= defaults.NotifyOffline;
        Terminal ??= defaults.Terminal;
        RootShell ??= defaults.RootShell;
        Upgrade ??= defaults.Upgrade;
        AutoRemove ??= defaults.AutoRemove;
    }

    public bool HasOverrides =>
        ShowOnline.HasValue || ShowResources.HasValue || ShowSystem.HasValue || ShowUpdates.HasValue ||
        RefreshSeconds.HasValue || AutoCheckHours.HasValue || NotifyUpdates.HasValue || NotifyOffline.HasValue ||
        Terminal.HasValue || RootShell.HasValue || Upgrade.HasValue || AutoRemove.HasValue;
}

/// <summary>Réglages effectifs d'un serveur : le sien s'il est défini, sinon le global.</summary>
public sealed record EffectiveSettings(
    bool ShowOnline, bool ShowResources, bool ShowSystem, bool ShowUpdates,
    int RefreshSeconds, int AutoCheckHours, bool NotifyUpdates, bool NotifyOffline,
    TerminalMode Terminal, bool RootShell, UpgradeKind Upgrade, bool AutoRemove)
{
    public static EffectiveSettings Resolve(ServerSettings global, ServerSettings? server)
    {
        var s = server ?? new ServerSettings();
        return new EffectiveSettings(
            s.ShowOnline ?? global.ShowOnline ?? true,
            s.ShowResources ?? global.ShowResources ?? true,
            s.ShowSystem ?? global.ShowSystem ?? true,
            s.ShowUpdates ?? global.ShowUpdates ?? true,
            Math.Max(10, s.RefreshSeconds ?? global.RefreshSeconds ?? 60),
            Math.Max(0, s.AutoCheckHours ?? global.AutoCheckHours ?? 0),
            s.NotifyUpdates ?? global.NotifyUpdates ?? true,
            s.NotifyOffline ?? global.NotifyOffline ?? true,
            s.Terminal ?? global.Terminal ?? TerminalMode.Embedded,
            s.RootShell ?? global.RootShell ?? false,
            s.Upgrade ?? global.Upgrade ?? UpgradeKind.Upgrade,
            s.AutoRemove ?? global.AutoRemove ?? false);
    }
}

/// <summary>Serveur supervisé. Les mots de passe ne sont jamais ici : voir <see cref="SecretVault"/>.</summary>
public class ServerConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "root";
    public AuthMethod Auth { get; set; } = AuthMethod.Password;
    public string? KeyPath { get; set; }
    public ElevationMethod Elevation { get; set; } = ElevationMethod.None;
    /// <summary>sudo : réutilise le mot de passe de connexion.</summary>
    public bool SudoSamePassword { get; set; } = true;
    public List<string> Tags { get; set; } = new();

    /// <summary>Clé d'hôte approuvée (empreinte SHA256 au format ssh, sans préfixe).</summary>
    public string? HostKeyType { get; set; }
    public string? HostKeyFingerprint { get; set; }

    public DateTime? LastUpdateCheckUtc { get; set; }
    public ServerSettings Overrides { get; set; } = new();

    public bool IsRootUser => Username == "root";

    /// <summary>Méthode d'élévation réellement utilisée (aucune si on est déjà root).</summary>
    public ElevationMethod EffectiveElevation => IsRootUser ? ElevationMethod.None : Elevation;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    public string Endpoint => Port == 22 ? $"{Username}@{Host}" : $"{Username}@{Host}:{Port}";

    public ServerConfig Clone()
    {
        var copy = (ServerConfig)MemberwiseClone();
        copy.Tags = new List<string>(Tags);
        copy.Overrides = new ServerSettings
        {
            ShowOnline = Overrides.ShowOnline, ShowResources = Overrides.ShowResources,
            ShowSystem = Overrides.ShowSystem, ShowUpdates = Overrides.ShowUpdates,
            RefreshSeconds = Overrides.RefreshSeconds, AutoCheckHours = Overrides.AutoCheckHours,
            NotifyUpdates = Overrides.NotifyUpdates, NotifyOffline = Overrides.NotifyOffline,
            Terminal = Overrides.Terminal, RootShell = Overrides.RootShell,
            Upgrade = Overrides.Upgrade, AutoRemove = Overrides.AutoRemove
        };
        return copy;
    }
}

/// <summary>Secrets d'un serveur, stockés dans le coffre.</summary>
public class ServerSecrets
{
    public string? Password { get; set; }
    /// <summary>Mot de passe sudo (si différent) ou mot de passe root pour su.</summary>
    public string? ElevationPassword { get; set; }
    public string? KeyPassphrase { get; set; }

    public bool IsEmpty => string.IsNullOrEmpty(Password) && string.IsNullOrEmpty(ElevationPassword)
                           && string.IsNullOrEmpty(KeyPassphrase);

    public ServerSecrets Clone() => (ServerSecrets)MemberwiseClone();

    /// <summary>Mot de passe à fournir à sudo ou su, null s'il n'y en a pas.</summary>
    public string? ElevationSecretFor(ServerConfig server) => server.EffectiveElevation switch
    {
        ElevationMethod.Sudo when server.SudoSamePassword => NullIfEmpty(Password),
        ElevationMethod.Sudo or ElevationMethod.Su => NullIfEmpty(ElevationPassword),
        _ => null
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

/// <summary>Paquet pouvant être mis à jour (ligne de « apt list --upgradable »).</summary>
public sealed record PackageUpdate(string Name, string Suites, string NewVersion, string OldVersion, string Arch)
{
    public bool IsSecurity => Suites.Contains("-security", StringComparison.OrdinalIgnoreCase);
}
