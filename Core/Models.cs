// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

namespace VibeSSHMonitoring.Core;

public enum AuthMethod { Password, PrivateKey }

/// <summary>How root access is obtained for apt and reboots.</summary>
public enum ElevationMethod
{
    None,   // root user, or no elevation
    Sudo,   // sudo with the user's password (or NOPASSWD)
    Su      // su with the root password
}

public enum StorageMode
{
    Dpapi,           // encrypted by Windows and tied to the session: nothing to type
    MasterPassword,  // Argon2id + AES-256-GCM, password asked at startup
    PlainText        // clear text in the file
}

public enum TerminalMode { Embedded, WindowsTerminal, PowerShell, Cmd, PuTTY, Custom }

public enum UpgradeKind { Upgrade, FullUpgrade }

/// <summary>
/// Monitoring settings. Global values are all set; on a server, a null value means
/// "use the global setting".
/// </summary>
public class ServerSettings
{
    public bool? ShowOnline { get; set; }
    public bool? ShowEndpoint { get; set; }      // user@host under the name on the home cards
    public bool? ShowResources { get; set; }
    public bool? ShowSystem { get; set; }
    public bool? ShowUpdates { get; set; }
    public int? RefreshSeconds { get; set; }
    public int? AutoCheckHours { get; set; }      // 0 = never (automatic apt update)
    public bool? NotifyUpdates { get; set; }
    public bool? NotifyOffline { get; set; }
    public TerminalMode? Terminal { get; set; }
    public bool? RootShell { get; set; }          // terminal opened directly as root
    public UpgradeKind? Upgrade { get; set; }
    public bool? AutoRemove { get; set; }
    public bool? OpenDetailsOnUpgrade { get; set; }  // "Update" opens the server page to follow the log

    public static ServerSettings CreateDefaults() => new()
    {
        ShowOnline = true,
        ShowEndpoint = true,
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
        AutoRemove = false,
        OpenDetailsOnUpgrade = true
    };

    /// <summary>Fills in missing values (configuration written by an older version).</summary>
    public void FillMissing(ServerSettings defaults)
    {
        ShowOnline ??= defaults.ShowOnline;
        ShowEndpoint ??= defaults.ShowEndpoint;
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
        OpenDetailsOnUpgrade ??= defaults.OpenDetailsOnUpgrade;
    }

    public bool HasOverrides =>
        ShowOnline.HasValue || ShowEndpoint.HasValue || ShowResources.HasValue || ShowSystem.HasValue || ShowUpdates.HasValue ||
        RefreshSeconds.HasValue || AutoCheckHours.HasValue || NotifyUpdates.HasValue || NotifyOffline.HasValue ||
        Terminal.HasValue || RootShell.HasValue || Upgrade.HasValue || AutoRemove.HasValue ||
        OpenDetailsOnUpgrade.HasValue;
}

/// <summary>Effective settings of a server: its own value when set, otherwise the global one.</summary>
public sealed record EffectiveSettings(
    bool ShowOnline, bool ShowResources, bool ShowSystem, bool ShowUpdates,
    int RefreshSeconds, int AutoCheckHours, bool NotifyUpdates, bool NotifyOffline,
    TerminalMode Terminal, bool RootShell, UpgradeKind Upgrade, bool AutoRemove,
    bool OpenDetailsOnUpgrade, bool ShowEndpoint)
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
            s.AutoRemove ?? global.AutoRemove ?? false,
            s.OpenDetailsOnUpgrade ?? global.OpenDetailsOnUpgrade ?? true,
            s.ShowEndpoint ?? global.ShowEndpoint ?? true);
    }
}

/// <summary>A monitored server. Passwords are never stored here: see <see cref="SecretVault"/>.</summary>
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
    /// <summary>sudo: reuse the login password.</summary>
    public bool SudoSamePassword { get; set; } = true;
    public List<string> Tags { get; set; } = new();

    /// <summary>Approved host key (SHA256 fingerprint in ssh format, without prefix).</summary>
    public string? HostKeyType { get; set; }
    public string? HostKeyFingerprint { get; set; }

    public DateTime? LastUpdateCheckUtc { get; set; }
    public ServerSettings Overrides { get; set; } = new();

    public bool IsRootUser => Username == "root";

    /// <summary>Elevation method actually used (none when already root).</summary>
    public ElevationMethod EffectiveElevation => IsRootUser ? ElevationMethod.None : Elevation;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    public string Endpoint => Port == 22 ? $"{Username}@{Host}" : $"{Username}@{Host}:{Port}";

    public ServerConfig Clone()
    {
        var copy = (ServerConfig)MemberwiseClone();
        copy.Tags = new List<string>(Tags);
        copy.Overrides = new ServerSettings
        {
            ShowOnline = Overrides.ShowOnline, ShowEndpoint = Overrides.ShowEndpoint, ShowResources = Overrides.ShowResources,
            ShowSystem = Overrides.ShowSystem, ShowUpdates = Overrides.ShowUpdates,
            RefreshSeconds = Overrides.RefreshSeconds, AutoCheckHours = Overrides.AutoCheckHours,
            NotifyUpdates = Overrides.NotifyUpdates, NotifyOffline = Overrides.NotifyOffline,
            Terminal = Overrides.Terminal, RootShell = Overrides.RootShell,
            Upgrade = Overrides.Upgrade, AutoRemove = Overrides.AutoRemove,
            OpenDetailsOnUpgrade = Overrides.OpenDetailsOnUpgrade
        };
        return copy;
    }
}

/// <summary>Secrets of a server, kept in the vault.</summary>
public class ServerSecrets
{
    public string? Password { get; set; }
    /// <summary>sudo password (when different) or root password for su.</summary>
    public string? ElevationPassword { get; set; }
    public string? KeyPassphrase { get; set; }

    public bool IsEmpty => string.IsNullOrEmpty(Password) && string.IsNullOrEmpty(ElevationPassword)
                           && string.IsNullOrEmpty(KeyPassphrase);

    public ServerSecrets Clone() => (ServerSecrets)MemberwiseClone();

    /// <summary>Password to give to sudo or su, null when there is none.</summary>
    public string? ElevationSecretFor(ServerConfig server) => server.EffectiveElevation switch
    {
        ElevationMethod.Sudo when server.SudoSamePassword => NullIfEmpty(Password),
        ElevationMethod.Sudo or ElevationMethod.Su => NullIfEmpty(ElevationPassword),
        _ => null
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

/// <summary>A package that can be upgraded (a line of "apt list --upgradable").</summary>
public sealed record PackageUpdate(string Name, string Suites, string NewVersion, string OldVersion, string Arch)
{
    public bool IsSecurity => Suites.Contains("-security", StringComparison.OrdinalIgnoreCase);
}
