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
/// A server card: configuration, last known state, running operation and log.
/// Only changed from the UI thread (the log accepts any thread).
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

    /// <summary>Set during an upgrade: the output drives the package rows.</summary>
    private volatile AptProgressParser? _upgradeProgress;

    public ObservableCollection<PackageRow> Packages { get; } = new();

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
            OnPropertyChanged(string.Empty);   // everything derives from the configuration
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
        ElevationMethod.Sudo => Loc.T("Access.Sudo"),
        ElevationMethod.Su => Loc.T("Access.Su"),
        _ => Loc.T(_config.IsRootUser ? "Access.Root" : "Access.None")
    };

    public bool ShowOnline => _settings.ShowOnline;
    public bool ShowResources => _settings.ShowResources && (_probe?.CpuPercent is not null || _probe?.MemTotalKb is not null);
    public bool ShowSystem => _settings.ShowSystem && _probe?.Os is not null;
    public bool ShowUpdates => _settings.ShowUpdates;

    // ===== State =====

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

    /// <summary>Detailed error message (tooltip, details page).</summary>
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
        ServerState.Checking => Loc.T("State.Checking"),
        ServerState.Online => Loc.T("State.Online"),
        ServerState.Offline => Loc.T("State.Offline"),
        ServerState.AuthFailed => Loc.T("State.AuthFailed"),
        ServerState.HostKeyUnknown => Loc.T("State.HostKeyUnknown"),
        ServerState.HostKeyChanged => Loc.T("State.HostKeyChanged"),
        ServerState.Locked => Loc.T("State.Locked"),
        ServerState.Error => Loc.T("State.Error"),
        _ => Loc.T("State.Waiting")
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

    // ===== Probe =====

    public ProbeResult? Probe
    {
        get => _probe;
        set
        {
            _probe = value;
            if (_upgradeProgress is null)
                SyncPackages();
            OnPropertyChanged(string.Empty);
        }
    }

    public string OsText => _probe?.Os ?? "—";
    public string KernelText => _probe?.Kernel is { } k ? $"{k}{(_probe.Arch is { } a ? $" · {a}" : "")}" : "—";
    public string HostnameText => _probe?.Hostname ?? "—";
    public string UptimeText => _probe?.Uptime is { } up ? Format.Duration(up) : "—";
    public string LoadText => _probe?.Load is { } load ? $"{load}{(_probe.Cpus is { } c ? $" · {Loc.T("Detail.Cores", c)}" : "")}" : "—";

    public double CpuPercent => _probe?.CpuPercent ?? 0;
    public string CpuText => _probe?.CpuPercent is { } cpu ? Format.Percent(cpu) : "—";
    public Brush CpuBrush => MeterBrush(_probe?.CpuPercent);

    public double MemPercent => MemUsedPercent ?? 0;
    private double? MemUsedPercent => _probe is { MemTotalKb: > 0 and var total, MemAvailableKb: { } avail }
        ? 100.0 * (total - avail) / total : null;
    public string MemText => _probe is { MemTotalKb: { } total, MemAvailableKb: { } avail }
        ? $"{Format.Size(total - avail)} / {Format.Size(total)}" : "—";
    public Brush MemBrush => MeterBrush(MemUsedPercent);

    public string SwapText => _probe is { SwapTotalKb: > 0 and var total, SwapFreeKb: { } free }
        ? $"{Format.Size(total - free)} / {Format.Size(total)}" : _probe?.SwapTotalKb == 0 ? Loc.T("Detail.NoSwap") : "—";

    public double DiskPercent => DiskUsedPercent ?? 0;
    private double? DiskUsedPercent => _probe is { DiskTotalKb: > 0 and var total, DiskUsedKb: { } used }
        ? 100.0 * used / total : null;
    public string DiskText => _probe is { DiskTotalKb: { } total, DiskUsedKb: { } used }
        ? $"{Format.Size(used)} / {Format.Size(total)}" : "—";
    public Brush DiskBrush => MeterBrush(DiskUsedPercent);

    public bool RebootRequired => _probe?.RebootRequired == true;

    /// <summary>Why a reboot is required: the marker file, the packages that created it, the running kernel.</summary>
    public string RebootTip
    {
        get
        {
            var lines = new List<string> { Loc.T("Card.RebootTip") };
            if (_probe?.RebootPackages is { Count: > 0 } packages)
            {
                const int shown = 8;
                string list = string.Join(", ", packages.Take(shown));
                if (packages.Count > shown)
                    list += " " + Loc.T("Card.RebootTipMore", packages.Count - shown);
                lines.Add(Loc.T("Card.RebootTipPackages", list));
            }
            if (_probe?.Kernel is { } kernel)
                lines.Add(Loc.T("Card.RebootTipKernel", kernel));
            return string.Join("\n\n", lines);
        }
    }

    public int? UpdateCount => _probe?.Packages?.Count;
    public int SecurityCount => _probe?.Packages?.Count(p => p.IsSecurity) ?? 0;
    public bool HasUpdates => UpdateCount > 0;
    public bool HasSecurityUpdates => SecurityCount > 0;
    public bool IsUpToDate => UpdateCount == 0;
    public bool NoApt => _probe is { HasApt: false };
    public bool HasUpdateInfo => UpdateCount is not null || NoApt;

    public string UpdatesText => UpdateCount switch
    {
        null => _probe is { HasApt: false } ? Loc.T("Updates.NoApt") : "—",
        0 => Loc.T("Updates.UpToDate"),
        1 => Loc.T("Updates.One"),
        var n => Loc.T("Updates.Many", n)
    };

    public string UpdatesDetail => SecurityCount > 0 ? Loc.T("Updates.SecurityDetail", SecurityCount) : "";

    public string LastCheckText => _config.LastUpdateCheckUtc is { } t
        ? Loc.T("Updates.LastCheck", Format.Ago(t))
        : Loc.T("Updates.NeverChecked");

    /// <summary>Status line under the card: running operation, error, or last reading.</summary>
    public string StatusLine =>
        _isBusy ? _busyText ?? Loc.T("Status.Busy") :
        HasProblem && _stateDetail is not null ? _stateDetail :
        _lastRefresh is { } t ? Loc.T("Status.LastReading", Format.Ago(t.ToUniversalTime())) : Loc.T("Status.FirstReading");

    // ===== Package list and upgrade progress =====

    /// <summary>
    /// Rebuilds the list from the last reading: upgradable packages first (a failure stays shown),
    /// then the packages the last upgrade installed, kept until the next check or upgrade.
    /// </summary>
    private void SyncPackages()
    {
        var listed = _probe?.Packages ?? [];
        var failed = Packages.Where(r => r.Stage == PackageStage.Failed).Select(r => r.Name).ToHashSet();
        var listedNames = listed.Select(p => p.Name).ToHashSet();
        var rows = listed.OrderByDescending(p => p.IsSecurity).ThenBy(p => p.Name)
            .Select(p => new PackageRow(p, failed.Contains(p.Name) ? PackageStage.Failed : PackageStage.Pending))
            .Concat(Packages.Where(r => r.Stage == PackageStage.Done && !listedNames.Contains(r.Name)))
            .ToList();
        Packages.Clear();
        foreach (var row in rows)
            Packages.Add(row);
    }

    public bool IsUpgrading => _upgradeProgress is not null;

    public double UpgradePercent => Packages.Count == 0
        ? 0 : 100.0 * Packages.Count(r => r.Stage == PackageStage.Done) / Packages.Count;

    /// <summary>Forgets the results of the previous upgrade ("Check updates", new upgrade).</summary>
    public void ClearUpgradeResults()
    {
        foreach (var row in Packages.Where(r => r.Stage == PackageStage.Done).ToList())
            Packages.Remove(row);
        foreach (var row in Packages)
            row.Stage = PackageStage.Pending;
    }

    public void BeginUpgrade()
    {
        ClearUpgradeResults();
        _upgradeProgress = new AptProgressParser();
        OnPropertyChanged(nameof(IsUpgrading));
        OnPropertyChanged(nameof(UpgradePercent));
    }

    /// <summary>Upgrade output, from any thread.</summary>
    public void FeedUpgradeOutput(string chunk)
    {
        if (_upgradeProgress is not { } parser)
            return;
        List<PackageProgress> changes;
        lock (parser)
            changes = parser.Feed(chunk);
        if (changes.Count > 0)
            Application.Current.Dispatcher.BeginInvoke(() => ApplyProgress(changes));
    }

    /// <summary>
    /// End of the upgrade, before the list is read again: on success everything started is done
    /// (what stays pending was kept back), on failure what was being installed failed.
    /// </summary>
    public void EndUpgrade(bool success)
    {
        if (_upgradeProgress is not { } parser)
            return;
        List<PackageProgress> changes;
        lock (parser)
            changes = parser.Complete(success);
        ApplyProgress(changes);
        foreach (var row in Packages)
        {
            if (row.Stage == PackageStage.Installing)
                row.Stage = success ? PackageStage.Done : PackageStage.Failed;
            else if (row.Stage == PackageStage.Downloading)
                row.Stage = PackageStage.Pending;
        }
        _upgradeProgress = null;
        OnPropertyChanged(nameof(IsUpgrading));
        OnPropertyChanged(nameof(UpgradePercent));
    }

    private void ApplyProgress(List<PackageProgress> changes)
    {
        if (_upgradeProgress is null)
            return;
        foreach (var change in changes)
        {
            bool found = false;
            foreach (var row in Packages.Where(r => r.Name == change.Name))
            {
                found = true;
                row.Advance(change.Stage);
            }
            // Packages the list did not show (new dependency, new kernel…) appear once installing
            if (!found && change.Stage is PackageStage.Installing or PackageStage.Done)
                Packages.Add(new PackageRow(new PackageUpdate(change.Name, "", change.NewVersion ?? "",
                    change.OldVersion ?? "", ""), change.Stage));
        }
        OnPropertyChanged(nameof(UpgradePercent));
    }

    // ===== Running operation =====

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

    /// <summary>Refreshes relative texts ("2 min ago").</summary>
    public void Tick()
    {
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(LastCheckText));
    }

    // ===== Log =====

    public string LogText => _log.Text;
    public bool HasLog => _log.Length > 0;

    /// <summary>Appends to the log from any thread; display updates are batched.</summary>
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

    // ===== Helpers =====

    private static Brush Resource(string key) => (Brush)Application.Current.FindResource(key);

    private static Brush MeterBrush(double? percent) => Resource(percent switch
    {
        >= 90 => "DangerBrush",
        >= 75 => "WarningBrush",
        _ => "AccentBrush"
    });
}

/// <summary>A row of the package list: an upgradable package and where it stands during an upgrade.</summary>
public sealed class PackageRow : ObservableObject
{
    private PackageStage _stage;

    public PackageRow(PackageUpdate package, PackageStage stage = PackageStage.Pending)
    {
        Package = package;
        _stage = stage;
    }

    public PackageUpdate Package { get; }
    public string Name => Package.Name;
    public string Suites => Package.Suites;
    public string NewVersion => Package.NewVersion;
    public string OldVersion => Package.OldVersion.Length > 0 ? Package.OldVersion : Loc.T("Package.New");
    public bool IsSecurity => Package.IsSecurity;

    public PackageStage Stage
    {
        get => _stage;
        set
        {
            if (Set(ref _stage, value))
                OnPropertyChanged(nameof(StageText));
        }
    }

    /// <summary>Tooltip of the state icon.</summary>
    public string StageText => Loc.T("Package.Stage" + _stage);

    /// <summary>Moves forward only (a failure always shows).</summary>
    public void Advance(PackageStage stage)
    {
        if (stage == PackageStage.Failed || stage > _stage)
            Stage = stage;
    }
}

/// <summary>
/// Terminal-like log text: a lone carriage return (apt progress bars) rewrites the current
/// line instead of stacking hundreds of them.
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
                    _text.Length = _lineStart;   // lone \r: start again from the beginning of the line
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

/// <summary>Sizes, durations and relative dates in the interface language.</summary>
public static class Format
{
    /// <summary>Size from KiB: "812 MB", "7.6 GB" (units and decimal separator follow the language).</summary>
    public static string Size(long kb)
    {
        double value = kb;
        string[] units = Loc.T("Format.SizeUnits").Split('|');
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value >= 100 || unit == 0
            ? $"{value.ToString("0", Loc.Culture)} {units[unit]}"
            : $"{value.ToString("0.#", Loc.Culture)} {units[unit]}";
    }

    public static string Percent(double value) => Loc.T("Format.Percent", value.ToString("0", Loc.Culture));

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? Loc.T("Format.DaysHours", (int)t.TotalDays, t.Hours) :
        t.TotalHours >= 1 ? Loc.T("Format.HoursMinutes", (int)t.TotalHours, t.Minutes) :
        Loc.T("Format.Minutes", Math.Max(1, (int)t.TotalMinutes));

    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalSeconds < 45) return Loc.T("Format.JustNow");
        if (d.TotalMinutes < 60) return Loc.T("Format.MinutesAgo", Math.Max(1, (int)Math.Round(d.TotalMinutes)));
        if (d.TotalHours < 24) return Loc.T("Format.HoursAgo", (int)d.TotalHours);
        if (d.TotalDays < 7) return Loc.T("Format.DaysAgo", (int)d.TotalDays);
        return Loc.T("Format.OnDate", utc.ToLocalTime().ToString("d MMM yyyy", Loc.Culture));
    }
}
