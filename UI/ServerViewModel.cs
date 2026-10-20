// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using VibeSSHMonitoring.Core;

namespace VibeSSHMonitoring.UI;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public enum ServerState { Unknown, Checking, Online, Offline, AuthFailed, HostKeyUnknown, HostKeyChanged, Locked, Error }

/// <summary>
/// Carte d'un serveur : configuration, dernier état relevé, opération en cours et journal.
/// Modifié uniquement depuis le thread UI (le journal accepte n'importe quel thread).
/// </summary>
public sealed class ServerViewModel : ObservableObject
{
    private ServerConfig _config;
    private EffectiveSettings _settings;
    private ServerState _state = ServerState.Unknown;
    private string? _stateDetail;
    private double? _latencyMs;
    private ProbeResult? _probe;
    private bool _isBusy;
    private string? _busyText;
    private bool _isSelected;
    private DateTime? _lastRefresh;

    private readonly LogBuffer _log = new();
    private readonly StringBuilder _pendingLog = new();
    private bool _logFlushScheduled;

    public ObservableCollection<PackageUpdate> Packages { get; } = new();

    public ServerViewModel(ServerConfig config, EffectiveSettings settings)
    {
        _config = config;
        _settings = settings;
    }

    // ===== Configuration =====

    public ServerConfig Config
    {
        get => _config;
        set
        {
            _config = value;
            OnPropertyChanged(string.Empty);   // tout est dérivé de la config
        }
    }

    public EffectiveSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            OnPropertyChanged(string.Empty);
        }
    }

    public Guid Id => _config.Id;
    public string Name => _config.DisplayName;
    public string Endpoint => _config.Endpoint;
    public IReadOnlyList<string> Tags => _config.Tags;
    public bool HasTags => _config.Tags.Count > 0;

    public string AccessText => _config.EffectiveElevation switch
    {
        ElevationMethod.Sudo => "via sudo",
        ElevationMethod.Su => "via su",
        _ => _config.IsRootUser ? "root direct" : "sans élévation"
    };

    public bool ShowOnline => _settings.ShowOnline;
    public bool ShowResources => _settings.ShowResources && (_probe?.CpuPercent is not null || _probe?.MemTotalKb is not null);
    public bool ShowSystem => _settings.ShowSystem && _probe?.Os is not null;
    public bool ShowUpdates => _settings.ShowUpdates;

    // ===== État =====

    public ServerState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateBrush));
                OnPropertyChanged(nameof(IsOnline));
                OnPropertyChanged(nameof(HasProblem));
            }
        }
    }

    /// <summary>Message d'erreur détaillé (info-bulle, page de détail).</summary>
    public string? StateDetail
    {
        get => _stateDetail;
        set
        {
            if (Set(ref _stateDetail, value))
                OnPropertyChanged(nameof(StatusLine));
        }
    }

    public bool IsOnline => _state == ServerState.Online;
    public bool HasProblem => _state is ServerState.Offline or ServerState.AuthFailed or ServerState.HostKeyChanged
        or ServerState.HostKeyUnknown or ServerState.Error or ServerState.Locked;

    public string StateText => _state switch
    {
        ServerState.Checking => "Vérification…",
        ServerState.Online => "En ligne",
        ServerState.Offline => "Hors ligne",
        ServerState.AuthFailed => "Authentification refusée",
        ServerState.HostKeyUnknown => "Clé d'hôte à approuver",
        ServerState.HostKeyChanged => "Clé d'hôte modifiée !",
        ServerState.Locked => "Coffre verrouillé",
        ServerState.Error => "Erreur",
        _ => "En attente"
    };

    public Brush StateBrush => Resource(_state switch
    {
        ServerState.Online => "SuccessBrush",
        ServerState.Offline or ServerState.HostKeyChanged or ServerState.Error or ServerState.AuthFailed => "DangerBrush",
        ServerState.HostKeyUnknown or ServerState.Locked => "WarningBrush",
        ServerState.Checking => "AccentLightBrush",
        _ => "NeutralBrush"
    });

    public double? LatencyMs
    {
        get => _latencyMs;
        set
        {
            if (Set(ref _latencyMs, value))
                OnPropertyChanged(nameof(LatencyText));
        }
    }

    public string LatencyText => _latencyMs is { } ms ? $"{ms:0} ms" : "";

    public DateTime? LastRefresh
    {
        get => _lastRefresh;
        set
        {
            if (Set(ref _lastRefresh, value))
                OnPropertyChanged(nameof(StatusLine));
        }
    }

    // ===== Sonde =====

    public ProbeResult? Probe
    {
        get => _probe;
        set
        {
            _probe = value;
            Packages.Clear();
            if (value?.Packages is { } packages)
                foreach (var p in packages.OrderByDescending(p => p.IsSecurity).ThenBy(p => p.Name))
                    Packages.Add(p);
            OnPropertyChanged(string.Empty);
        }
    }

    public string OsText => _probe?.Os ?? "—";
    public string KernelText => _probe?.Kernel is { } k ? $"{k}{(_probe.Arch is { } a ? $" · {a}" : "")}" : "—";
    public string HostnameText => _probe?.Hostname ?? "—";
    public string UptimeText => _probe?.Uptime is { } up ? Format.Duration(up) : "—";
    public string LoadText => _probe?.Load is { } load ? $"{load}{(_probe.Cpus is { } c ? $" · {c} cœurs" : "")}" : "—";

    public double CpuPercent => _probe?.CpuPercent ?? 0;
    public string CpuText => _probe?.CpuPercent is { } cpu ? $"{cpu:0} %" : "—";
    public Brush CpuBrush => MeterBrush(_probe?.CpuPercent);

    public double MemPercent => MemUsedPercent ?? 0;
    private double? MemUsedPercent => _probe is { MemTotalKb: > 0 and var total, MemAvailableKb: { } avail }
        ? 100.0 * (total - avail) / total : null;
    public string MemText => _probe is { MemTotalKb: { } total, MemAvailableKb: { } avail }
        ? $"{Format.Size(total - avail)} / {Format.Size(total)}" : "—";
    public Brush MemBrush => MeterBrush(MemUsedPercent);

    public string SwapText => _probe is { SwapTotalKb: > 0 and var total, SwapFreeKb: { } free }
        ? $"{Format.Size(total - free)} / {Format.Size(total)}" : _probe?.SwapTotalKb == 0 ? "aucun" : "—";

    public double DiskPercent => DiskUsedPercent ?? 0;
    private double? DiskUsedPercent => _probe is { DiskTotalKb: > 0 and var total, DiskUsedKb: { } used }
        ? 100.0 * used / total : null;
    public string DiskText => _probe is { DiskTotalKb: { } total, DiskUsedKb: { } used }
        ? $"{Format.Size(used)} / {Format.Size(total)}" : "—";
    public Brush DiskBrush => MeterBrush(DiskUsedPercent);

    public bool RebootRequired => _probe?.RebootRequired == true;

    public int? UpdateCount => _probe?.Packages?.Count;
    public int SecurityCount => _probe?.Packages?.Count(p => p.IsSecurity) ?? 0;
    public bool HasUpdates => UpdateCount > 0;
    public bool HasSecurityUpdates => SecurityCount > 0;
    public bool IsUpToDate => UpdateCount == 0;
    public bool NoApt => _probe is { HasApt: false };
    public bool HasUpdateInfo => UpdateCount is not null || NoApt;

    public string UpdatesText => UpdateCount switch
    {
        null => _probe is { HasApt: false } ? "apt indisponible" : "—",
        0 => "À jour",
        1 => "1 mise à jour",
        var n => $"{n} mises à jour"
    };

    public string UpdatesDetail => SecurityCount > 0 ? $"dont {SecurityCount} de sécurité" : "";

    public string LastCheckText => _config.LastUpdateCheckUtc is { } t
        ? $"Dernière recherche de mises à jour : {Format.Ago(t)}"
        : "Aucune recherche de mises à jour depuis l'application";

    /// <summary>Ligne d'état sous la carte : opération en cours, erreur, ou dernier relevé.</summary>
    public string StatusLine =>
        _isBusy ? _busyText ?? "En cours…" :
        HasProblem && _stateDetail is not null ? _stateDetail :
        _lastRefresh is { } t ? $"Relevé {Format.Ago(t.ToUniversalTime())}" : "En attente du premier relevé";

    // ===== Opération en cours =====

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(StatusLine));
            }
        }
    }

    public bool IsIdle => !_isBusy;

    public string? BusyText
    {
        get => _busyText;
        set
        {
            if (Set(ref _busyText, value))
                OnPropertyChanged(nameof(StatusLine));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>Rafraîchit les textes relatifs (« il y a 2 min »).</summary>
    public void Tick()
    {
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(LastCheckText));
    }

    // ===== Journal =====

    public string LogText => _log.Text;
    public bool HasLog => _log.Length > 0;

    /// <summary>Ajoute au journal depuis n'importe quel thread ; l'affichage est regroupé.</summary>
    public void AppendLog(string text)
    {
        lock (_pendingLog)
        {
            _pendingLog.Append(text);
            if (_logFlushScheduled)
                return;
            _logFlushScheduled = true;
        }
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, FlushLog);
    }

    public void AppendLogLine(string line) => AppendLog(line + "\n");

    private void FlushLog()
    {
        string text;
        lock (_pendingLog)
        {
            text = _pendingLog.ToString();
            _pendingLog.Clear();
            _logFlushScheduled = false;
        }
        _log.Append(text);
        OnPropertyChanged(nameof(LogText));
        OnPropertyChanged(nameof(HasLog));
        LogAppended?.Invoke();
    }

    public event Action? LogAppended;

    public void ClearLog()
    {
        _log.Clear();
        OnPropertyChanged(nameof(LogText));
        OnPropertyChanged(nameof(HasLog));
    }

    // ===== Utilitaires =====

    private static Brush Resource(string key) => (Brush)Application.Current.FindResource(key);

    private static Brush MeterBrush(double? percent) => Resource(percent switch
    {
        >= 90 => "DangerBrush",
        >= 75 => "WarningBrush",
        _ => "AccentBrush"
    });
}

/// <summary>
/// Texte de journal façon terminal : un retour chariot seul (barres de progression d'apt)
/// réécrit la ligne courante au lieu d'en empiler des centaines.
/// </summary>
public sealed class LogBuffer
{
    private const int MaxLength = 400_000;
    private readonly StringBuilder _text = new();
    private int _lineStart;
    private bool _pendingCr;

    public int Length => _text.Length;
    public string Text => _text.ToString();

    public void Append(string chunk)
    {
        foreach (char c in RemoteExec.StripAnsi(chunk))
        {
            if (_pendingCr)
            {
                _pendingCr = false;
                if (c != '\n')
                    _text.Length = _lineStart;   // \r seul : on repart du début de la ligne
            }
            switch (c)
            {
                case '\r':
                    _pendingCr = true;
                    break;
                case '\n':
                    _text.Append('\n');
                    _lineStart = _text.Length;
                    break;
                default:
                    if (c >= ' ' || c == '\t')
                        _text.Append(c);
                    break;
            }
        }

        if (_text.Length > MaxLength)
        {
            int cut = _text.Length - MaxLength / 2;
            int newline = _text.ToString(cut, Math.Min(2000, _text.Length - cut)).IndexOf('\n');
            cut += newline >= 0 ? newline + 1 : 0;
            _text.Remove(0, cut);
            _lineStart = Math.Max(0, _lineStart - cut);
        }
    }

    public void Clear()
    {
        _text.Clear();
        _lineStart = 0;
        _pendingCr = false;
    }
}

public static class Format
{
    private static readonly System.Globalization.CultureInfo Fr = new("fr-FR");

    /// <summary>Taille depuis des kio : « 812 Mo », « 7,6 Go ».</summary>
    public static string Size(long kb)
    {
        double value = kb;
        string[] units = { "Ko", "Mo", "Go", "To", "Po" };
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value >= 100 || unit == 0
            ? $"{value.ToString("0", Fr)} {units[unit]}"
            : $"{value.ToString("0.#", Fr)} {units[unit]}";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} j {t.Hours} h" :
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" :
        $"{Math.Max(1, (int)t.TotalMinutes)} min";

    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalSeconds < 45) return "à l'instant";
        if (d.TotalMinutes < 60) return $"il y a {Math.Max(1, (int)Math.Round(d.TotalMinutes))} min";
        if (d.TotalHours < 24) return $"il y a {(int)d.TotalHours} h";
        if (d.TotalDays < 7) return $"il y a {(int)d.TotalDays} j";
        return "le " + utc.ToLocalTime().ToString("d MMM yyyy", Fr);
    }
}
