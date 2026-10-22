// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Windows.Threading;
using VibeSSHMonitoring.Core;
using VibeSSHMonitoring.UI;

namespace VibeSSHMonitoring;

/// <summary>
/// Monitoring and actions on the servers. Everything starts from the UI thread: network work
/// runs in the background and the cards are updated when the awaits return.
/// </summary>
public sealed class ServerManager : IDisposable
{
    private const int MaxParallel = 4;

    private readonly AppController _app;
    private readonly Dictionary<Guid, ServerConnection> _connections = new();
    private readonly Dictionary<Guid, DateTime> _nextRefresh = new();
    private readonly Dictionary<Guid, DateTime> _nextAutoCheck = new();
    private readonly HashSet<Guid> _refreshing = new();
    private readonly SemaphoreSlim _backgroundSlots = new(MaxParallel);
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _shutdown = new();

    public ObservableCollection<ServerViewModel> Servers { get; } = new();

    /// <summary>Global state changed (number online, updates): menu, icon.</summary>
    public event Action? SummaryChanged;

    public ServerManager(AppController app)
    {
        _app = app;
        foreach (var config in app.Config.Servers)
            Servers.Add(new ServerViewModel(config, app.Config.EffectiveFor(config)));
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Tick(),
            Dispatcher.CurrentDispatcher);
    }

    public void Start()
    {
        _timer.Start();
        Tick();
    }

    public ServerViewModel? Find(Guid id) => Servers.FirstOrDefault(s => s.Id == id);

    private ServerConnection ConnectionFor(ServerViewModel vm)
    {
        if (!_connections.TryGetValue(vm.Id, out var connection))
        {
            Guid id = vm.Id;
            connection = new ServerConnection(vm.Config, () => _app.Vault.Get(id), () => _app.Vault.IsLocked);
            _connections[vm.Id] = connection;
        }
        connection.Server = vm.Config;
        return connection;
    }

    private void ResetConnection(Guid id)
    {
        if (_connections.Remove(id, out var connection))
            _ = Task.Run(connection.Dispose);   // disconnecting may take a moment
    }

    // ===== Server list =====

    public ServerViewModel Add(ServerConfig config, ServerSecrets secrets)
    {
        _app.Config.Servers.Add(config);
        _app.Config.Save();
        if (!_app.Vault.IsLocked)
            _app.Vault.Set(config.Id, secrets);
        var vm = new ServerViewModel(config, _app.Config.EffectiveFor(config));
        Servers.Add(vm);
        // Interactive first reading: if the host key was not approved during a test, it is offered right away
        _ = RefreshNowAsync(vm);
        return vm;
    }

    public void Update(ServerViewModel vm, ServerConfig config, ServerSecrets? secrets)
    {
        int index = _app.Config.Servers.FindIndex(s => s.Id == config.Id);
        if (index >= 0)
            _app.Config.Servers[index] = config;
        _app.Config.Save();
        if (secrets is not null && !_app.Vault.IsLocked)
            _app.Vault.Set(config.Id, secrets);
        vm.Config = config;
        vm.Settings = _app.Config.EffectiveFor(config);
        ResetConnection(config.Id);
        _ = RefreshNowAsync(vm);
    }

    public void Remove(ServerViewModel vm)
    {
        _app.Config.Servers.RemoveAll(s => s.Id == vm.Id);
        _app.Config.Save();
        _app.Vault.Remove(vm.Id);
        ResetConnection(vm.Id);
        Servers.Remove(vm);
        SummaryChanged?.Invoke();
    }

    /// <summary>Global settings changed: every server recomputes its effective settings.</summary>
    public void ApplySettings()
    {
        foreach (var vm in Servers)
            vm.Settings = _app.Config.EffectiveFor(vm.Config);
        _nextRefresh.Clear();
    }

    /// <summary>Vault (un)locked: connections restart with the new secrets.</summary>
    public void OnVaultChanged()
    {
        foreach (var id in _connections.Keys.ToList())
            ResetConnection(id);
        _nextRefresh.Clear();
        _nextAutoCheck.Clear();
        Tick();
    }

    // ===== Monitoring loop =====

    private void Tick()
    {
        var now = DateTime.UtcNow;
        foreach (var vm in Servers)
        {
            vm.Tick();
            if (vm.IsBusy || _refreshing.Contains(vm.Id))
                continue;

            if (!_nextRefresh.TryGetValue(vm.Id, out var due) || due <= now)
            {
                _nextRefresh[vm.Id] = now.AddSeconds(vm.Settings.RefreshSeconds);
                _ = RefreshAsync(vm, background: true);
                continue;
            }

            // automatic apt update, for a reachable server
            int hours = vm.Settings.AutoCheckHours;
            if (hours > 0 && vm.IsOnline && !vm.NoApt &&
                (vm.Config.LastUpdateCheckUtc is not { } last || now - last > TimeSpan.FromHours(hours)) &&
                (!_nextAutoCheck.TryGetValue(vm.Id, out var retry) || retry <= now))
            {
                _nextAutoCheck[vm.Id] = now.AddHours(1);   // after a failure, not before an hour
                _ = CheckUpdatesAsync(vm, interactive: false);
            }
        }
    }

    /// <summary>Reads the state: SSH port test, then probe (system, resources, updates).</summary>
    public async Task RefreshAsync(ServerViewModel vm, bool background = false)
    {
        if (!_refreshing.Add(vm.Id))
            return;
        bool slot = false;
        try
        {
            if (background)
            {
                await _backgroundSlots.WaitAsync(_shutdown.Token);
                slot = true;
            }
            if (vm.State == ServerState.Unknown)
                vm.State = ServerState.Checking;
            await RefreshCoreAsync(vm);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SshOperationException ex)
        {
            ApplyError(vm, ex, notify: background);
        }
        catch (Exception ex)
        {
            vm.State = ServerState.Error;
            vm.StateDetail = ex.Message;
        }
        finally
        {
            if (slot)
                _backgroundSlots.Release();
            _refreshing.Remove(vm.Id);
            vm.LastRefresh = DateTime.Now;
            SummaryChanged?.Invoke();
        }
    }

    private async Task RefreshCoreAsync(ServerViewModel vm)
    {
        var settings = vm.Settings;
        var config = vm.Config;
        var ct = _shutdown.Token;

#if DEBUG
        if (DemoServers.ProbeFor(config) is var (demoProbe, demoLatency))
        {
            await Task.Delay(300, ct);
            vm.LatencyMs = settings.ShowOnline ? demoLatency : null;
            vm.Probe = demoProbe;
            vm.State = ServerState.Online;
            vm.StateDetail = null;
            return;
        }
#endif

        if (settings.ShowOnline)
        {
            double? latency = await Task.Run(() => PingAsync(config.Host.Trim(), config.Port, ct), ct);
            vm.LatencyMs = latency;
            if (latency is null)
                throw new SshOperationException(SshErrorKind.Unreachable, Loc.T("Monitor.NoResponse", config.Port));
        }
        else
        {
            vm.LatencyMs = null;
        }

        int? previousUpdates = vm.UpdateCount;
        var previousState = vm.State;
        string probe = RemoteCommands.BuildProbe(settings.ShowSystem, settings.ShowResources, settings.ShowUpdates);
        var connection = ConnectionFor(vm);
        var result = await Task.Run(() => connection.RunAsync(probe, null, ct), ct);
        vm.Probe = RemoteCommands.ParseProbe(result.Output, settings.ShowUpdates);
        vm.State = ServerState.Online;
        vm.StateDetail = null;

        if (settings.NotifyUpdates && vm.UpdateCount is > 0 and var count && previousUpdates is { } before && count > before)
            _app.NotifyUpdates(vm.Name, count, vm.SecurityCount);
        if (previousState is ServerState.Offline && settings.NotifyOffline)
            _app.Notify(Loc.T("Notify.BackOnlineTitle"), Loc.T("Notify.BackOnlineText", vm.Name));
    }

    private static async Task<double?> PingAsync(string host, int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var tcp = new TcpClient();
        var watch = Stopwatch.StartNew();
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token);
            return watch.Elapsed.TotalMilliseconds;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private void ApplyError(ServerViewModel vm, SshOperationException ex, bool notify)
    {
        var previous = vm.State;
        switch (ex.Kind)
        {
            case SshErrorKind.Unreachable or SshErrorKind.Timeout:
                vm.State = ServerState.Offline;
                vm.LatencyMs = null;
                if (notify && previous == ServerState.Online && vm.Settings.NotifyOffline)
                    _app.Notify(Loc.T("Notify.OfflineTitle"), Loc.T("Notify.OfflineText", vm.Name));
                break;
            case SshErrorKind.AuthFailed or SshErrorKind.KeyFile:
                vm.State = ServerState.AuthFailed;
                break;
            case SshErrorKind.HostKeyUnknown:
                vm.State = ServerState.HostKeyUnknown;
                break;
            case SshErrorKind.HostKeyChanged:
                vm.State = ServerState.HostKeyChanged;
                if (previous != ServerState.HostKeyChanged)
                    _app.Notify(Loc.T("Notify.HostKeyChangedTitle"), Loc.T("Notify.HostKeyChangedText", vm.Name));
                break;
            case SshErrorKind.VaultLocked:
                vm.State = ServerState.Locked;
                break;
            case SshErrorKind.ElevationFailed:
                break;   // the server responds: only the action failed
            default:
                vm.State = ServerState.Error;
                break;
        }
        if (ex.Kind != SshErrorKind.ElevationFailed)
            vm.StateDetail = ex.Message;
        if (ex.Kind is SshErrorKind.Unreachable or SshErrorKind.Timeout or SshErrorKind.AuthFailed)
            ResetConnection(vm.Id);
    }

    // ===== Actions =====

    /// <summary>
    /// Common frame of actions: one at a time per server, vault unlocked when needed, unknown host
    /// keys offered for approval, errors shown in the card and the log.
    /// </summary>
    private async Task<bool> RunActionAsync(ServerViewModel vm, string busyText, bool interactive,
        Func<ServerConnection, CancellationToken, Task> action)
    {
        if (vm.IsBusy)
            return false;
        if (interactive && _app.Vault.IsLocked && !await _app.UnlockInteractiveAsync())
            return false;

        vm.IsBusy = true;
        vm.BusyText = busyText;
        try
        {
            await WithHostKeyApprovalAsync(vm.Config, interactive, async () =>
            {
                var connection = ConnectionFor(vm);
                await action(connection, _shutdown.Token);
            });
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (SshOperationException ex)
        {
            ApplyError(vm, ex, notify: !interactive);
            vm.AppendLogLine($"✖ {ex.Message}");
            if (interactive && ex.Kind is SshErrorKind.ElevationFailed or SshErrorKind.AuthFailed or SshErrorKind.KeyFile
                    or SshErrorKind.Other)
                _app.ShowError(vm.Name, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            vm.AppendLogLine($"✖ {ex.Message}");
            if (interactive)
                _app.ShowError(vm.Name, ex.Message);
            return false;
        }
        finally
        {
            vm.IsBusy = false;
            vm.BusyText = null;
            SummaryChanged?.Invoke();
        }
    }

    /// <summary>
    /// Runs the action; if the host key is unknown or has changed, has it approved (actions started
    /// by the user only), then tries once more.
    /// </summary>
    private async Task WithHostKeyApprovalAsync(ServerConfig config, bool interactive, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (SshOperationException ex) when (interactive && ex.Kind is SshErrorKind.HostKeyUnknown or SshErrorKind.HostKeyChanged)
        {
            if (!ApproveHostKey(config, ex))
                throw;
            ResetConnection(config.Id);
            await action();
        }
    }

    /// <summary>Asks for the key to be approved and saves it; true when accepted.</summary>
    public bool ApproveHostKey(ServerConfig config, SshOperationException ex)
    {
        if (ex.Fingerprint is null || !_app.ConfirmHostKey(config, ex))
            return false;
        config.HostKeyType = ex.HostKeyType;
        config.HostKeyFingerprint = ex.Fingerprint;
        _app.Config.Save();
        return true;
    }

    public Task RefreshNowAsync(ServerViewModel vm) =>
        RunActionAsync(vm, Loc.T("Busy.Refreshing"), interactive: true, (_, _) => RefreshCoreAsync(vm));

    /// <summary>"Check updates": apt update as root, then reads the package list.</summary>
    public Task<bool> CheckUpdatesAsync(ServerViewModel vm, bool interactive = true) =>
        RunActionAsync(vm, Loc.T("Busy.CheckingUpdates"), interactive, async (connection, ct) =>
        {
            vm.AppendLogLine($"\n━━ {DateTime.Now:HH:mm:ss} · {Loc.T("Log.CheckUpdates")} ━━");
            vm.ClearUpgradeResults();
            var result = await Task.Run(() => connection.RunAsRootAsync(RemoteCommands.AptUpdate, vm.AppendLog, ct), ct);
            if (!result.Success)
                throw new SshOperationException(SshErrorKind.Other, Loc.T("Error.AptUpdateFailed", result.ExitCode));

            vm.Config.LastUpdateCheckUtc = DateTime.UtcNow;
            _app.Config.Save();
            await RefreshCoreAsync(vm);
            vm.AppendLogLine(vm.UpdateCount is { } n
                ? $"→ {vm.UpdatesText}{(vm.SecurityCount > 0 ? $" ({vm.UpdatesDetail})" : "")}"
                : "→ " + Loc.T("Log.NoPackageList"));
        });

    /// <summary>"Update": non-interactive apt update + upgrade (or full-upgrade).</summary>
    public Task<bool> UpgradeAsync(ServerViewModel vm) =>
        RunActionAsync(vm, Loc.T("Busy.Upgrading"), interactive: true, async (connection, ct) =>
        {
            var settings = vm.Settings;
            string verb = settings.Upgrade == UpgradeKind.FullUpgrade ? "full-upgrade" : "upgrade";
            vm.AppendLogLine($"\n━━ {DateTime.Now:HH:mm:ss} · {Loc.T("Log.Upgrade", verb)} ━━");
            var script = RemoteCommands.AptUpgrade(settings.Upgrade, settings.AutoRemove);
            // The package list follows the output: pending, being installed, done
            vm.BeginUpgrade();
            CommandResult? result = null;
            try
            {
                result = await Task.Run(() => connection.RunAsRootAsync(script, chunk =>
                {
                    vm.AppendLog(chunk);
                    vm.FeedUpgradeOutput(chunk);
                }, ct), ct);
            }
            finally
            {
                vm.EndUpgrade(result?.Success == true);
            }
            if (!result.Success)
                throw new SshOperationException(SshErrorKind.Other, Loc.T("Error.UpgradeFailed", result.ExitCode));

            vm.Config.LastUpdateCheckUtc = DateTime.UtcNow;
            _app.Config.Save();
            await RefreshCoreAsync(vm);
            vm.AppendLogLine("✔ " + Loc.T("Log.UpgradeDone") + (vm.RebootRequired ? " " + Loc.T("Log.RebootRequired") : ""));
        });

    public Task<bool> RebootAsync(ServerViewModel vm) =>
        RunActionAsync(vm, Loc.T("Busy.Rebooting"), interactive: true, async (connection, ct) =>
        {
            vm.AppendLogLine($"\n━━ {DateTime.Now:HH:mm:ss} · {Loc.T("Log.Reboot")} ━━");
            var result = await Task.Run(() => connection.RunAsRootAsync(RemoteCommands.Reboot, vm.AppendLog, ct), ct);
            if (!result.Success)
                throw new SshOperationException(SshErrorKind.Other, Loc.T("Error.RebootFailed", result.ExitCode));
            ResetConnection(vm.Id);
            vm.State = ServerState.Offline;
            vm.StateDetail = Loc.T("Busy.RebootInProgress");
            vm.LatencyMs = null;
            // First reading a minute later, giving the server time to come back
            _nextRefresh[vm.Id] = DateTime.UtcNow.AddSeconds(60);
        });

    /// <summary>Action on several servers, four at a time.</summary>
    public async Task RunForAllAsync(IEnumerable<ServerViewModel> servers, Func<ServerViewModel, Task> action)
    {
        using var slots = new SemaphoreSlim(MaxParallel);
        var tasks = servers.ToList().Select(async vm =>
        {
            await slots.WaitAsync();
            try { await action(vm); }
            finally { slots.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    // ===== Terminal =====

    /// <summary>Built-in terminal session, with host key approval when needed.</summary>
    public async Task<TerminalSession?> OpenTerminalSessionAsync(ServerConfig config, uint columns, uint rows)
    {
        if (_app.Vault.IsLocked && !await _app.UnlockInteractiveAsync())
            return null;
        var settings = _app.Config.EffectiveFor(config);
        TerminalSession? session = null;
        await WithHostKeyApprovalAsync(config, interactive: true, async () =>
        {
            session = await Task.Run(() => TerminalSession.OpenAsync(config, _app.Vault.Get(config.Id),
                _app.Vault.IsLocked, columns, rows, settings.RootShell, _shutdown.Token));
        });
        return session;
    }

    /// <summary>
    /// "Open SSH": built-in or external terminal, depending on the settings. In the background
    /// (middle click), the terminal does not take the focus.
    /// </summary>
    public async Task OpenSshAsync(ServerViewModel vm, bool background = false)
    {
        var settings = vm.Settings;
        if (settings.Terminal == TerminalMode.Embedded)
        {
            _app.OpenEmbeddedTerminal(vm.Config, background);
            return;
        }

        if (_app.Config.CopyPasswordForExternal)
        {
            if (_app.Vault.IsLocked && !await _app.UnlockInteractiveAsync())
                return;
            var secrets = _app.Vault.Get(vm.Id);
            // The first password the session will ask for
            string? toCopy = vm.Config.Auth == AuthMethod.Password
                ? secrets.Password
                : settings.RootShell ? secrets.ElevationSecretFor(vm.Config) : null;
            if (!string.IsNullOrEmpty(toCopy))
                _app.CopySecretToClipboard(toCopy);
        }
        try
        {
            ExternalTerminal.Launch(vm.Config, settings.Terminal, settings.RootShell, _app.Config.CustomTerminalCommand, background);
        }
        catch (Exception ex)
        {
            _app.ShowError(Loc.T("Error.OpenTerminalTitle"), ex.Message);
        }
    }

    // ===== Server editor: test and key deployment =====

    /// <summary>Full test of an unsaved configuration: connection, then root elevation.</summary>
    public async Task<string> TestConfigurationAsync(ServerConfig config, ServerSecrets secrets)
    {
        var lines = new List<string>();
        await WithHostKeyApprovalAsync(config, interactive: true, async () =>
        {
            lines.Clear();
            using var connection = await Task.Run(() => SshConnector.ConnectAsync(config, secrets, false, _shutdown.Token));
            var info = await Task.Run(() => RemoteExec.RunAsync(connection.Client,
                "echo \"$(id -un) · $(. /etc/os-release 2>/dev/null; echo \"$PRETTY_NAME\")\"", null, _shutdown.Token));
            lines.Add(Loc.T("Test.Connected", info.Output.Trim()));

            if (config.EffectiveElevation != ElevationMethod.None)
            {
                var root = await Task.Run(() => RemoteExec.RunAsRootAsync(connection.Client, config, secrets, false,
                    "id -u", null, _shutdown.Token));
                lines.Add(root.Output.Trim().EndsWith('0') && root.Success
                    ? Loc.T("Test.ElevationOk", config.EffectiveElevation == ElevationMethod.Sudo ? "sudo" : "su")
                    : Loc.T("Test.ElevationUnexpected", root.Output.Trim()));
            }
            else if (!config.IsRootUser)
            {
                lines.Add(Loc.T("Test.NoElevation"));
            }
        });
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Installs the public key on the server (password login), then checks that the key login works.
    /// The configuration then switches to key authentication.
    /// </summary>
    public async Task<string> DeployKeyAsync(ServerConfig config, ServerSecrets secrets, string keyPath)
    {
        if (!SshKeys.HasKeyPair(keyPath))
            await SshKeys.GenerateAsync(keyPath);
        string publicKey = SshKeys.ReadPublicKey(keyPath);

        await WithHostKeyApprovalAsync(config, interactive: true, async () =>
        {
            using var connection = await Task.Run(() => SshConnector.ConnectAsync(config, secrets, false, _shutdown.Token));
            var result = await Task.Run(() => RemoteExec.ExecAsync(connection.Client,
                "sh -c " + RemoteExec.Quote(SshKeys.InstallScript), publicKey + "\n", null, _shutdown.Token));
            if (!result.Success || !result.Output.Contains("OK"))
                throw new SshOperationException(SshErrorKind.Other, Loc.T("Error.KeyInstallFailed", result.Output.Trim()));
        });

        var keyConfig = config.Clone();
        keyConfig.Auth = AuthMethod.PrivateKey;
        keyConfig.KeyPath = keyPath;
        using (await Task.Run(() => SshConnector.ConnectAsync(keyConfig, new ServerSecrets(), false, _shutdown.Token)))
        {
        }
        config.Auth = AuthMethod.PrivateKey;
        config.KeyPath = keyPath;
        return Loc.T("Test.KeyInstalled", keyPath);
    }

    public void Dispose()
    {
        _timer.Stop();
        _shutdown.Cancel();
        foreach (var connection in _connections.Values)
            connection.Dispose();
        _connections.Clear();
    }
}
