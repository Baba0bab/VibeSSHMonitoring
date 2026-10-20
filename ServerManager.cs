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
/// Supervision et actions sur les serveurs. Tout part du thread UI : le travail réseau
/// s'exécute en arrière-plan et les cartes sont mises à jour au retour des await.
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

    /// <summary>État global changé (nombre en ligne, mises à jour) : menu, icône.</summary>
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
            _ = Task.Run(connection.Dispose);   // la déconnexion peut prendre un moment
    }

    // ===== Liste des serveurs =====

    public ServerViewModel Add(ServerConfig config, ServerSecrets secrets)
    {
        _app.Config.Servers.Add(config);
        _app.Config.Save();
        if (!_app.Vault.IsLocked)
            _app.Vault.Set(config.Id, secrets);
        var vm = new ServerViewModel(config, _app.Config.EffectiveFor(config));
        Servers.Add(vm);
        // Premier relevé interactif : la clé d'hôte, si elle n'a pas été approuvée au test, est proposée tout de suite
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

    /// <summary>Réglages globaux modifiés : tous les serveurs recalculent leurs réglages effectifs.</summary>
    public void ApplySettings()
    {
        foreach (var vm in Servers)
            vm.Settings = _app.Config.EffectiveFor(vm.Config);
        _nextRefresh.Clear();
    }

    /// <summary>Coffre (dé)verrouillé : les connexions repartent avec les nouveaux secrets.</summary>
    public void OnVaultChanged()
    {
        foreach (var id in _connections.Keys.ToList())
            ResetConnection(id);
        _nextRefresh.Clear();
        _nextAutoCheck.Clear();
        Tick();
    }

    // ===== Boucle de supervision =====

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

            // apt update automatique, pour un serveur joignable
            int hours = vm.Settings.AutoCheckHours;
            if (hours > 0 && vm.IsOnline && !vm.NoApt &&
                (vm.Config.LastUpdateCheckUtc is not { } last || now - last > TimeSpan.FromHours(hours)) &&
                (!_nextAutoCheck.TryGetValue(vm.Id, out var retry) || retry <= now))
            {
                _nextAutoCheck[vm.Id] = now.AddHours(1);   // en cas d'échec, pas avant une heure
                _ = CheckUpdatesAsync(vm, interactive: false);
            }
        }
    }

    /// <summary>Relevé de l'état : test du port SSH puis sonde (système, ressources, mises à jour).</summary>
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

        if (settings.ShowOnline)
        {
            double? latency = await Task.Run(() => PingAsync(config.Host.Trim(), config.Port, ct), ct);
            vm.LatencyMs = latency;
            if (latency is null)
                throw new SshOperationException(SshErrorKind.Unreachable, $"Aucune réponse sur le port {config.Port}.");
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
            _app.Notify("Serveur de nouveau joignable", $"{vm.Name} répond à nouveau.");
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
                    _app.Notify("Serveur injoignable", $"{vm.Name} ne répond plus.");
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
                    _app.Notify("Clé d'hôte modifiée !",
                        $"La clé de {vm.Name} a changé. Connexion refusée par sécurité : vérifiez le serveur.");
                break;
            case SshErrorKind.VaultLocked:
                vm.State = ServerState.Locked;
                break;
            case SshErrorKind.ElevationFailed:
                break;   // le serveur répond : seule l'action a échoué
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
    /// Cadre commun des actions : une seule à la fois par serveur, coffre ouvert si besoin,
    /// clé d'hôte inconnue proposée à l'approbation, erreurs affichées dans la carte et le journal.
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
    /// Exécute l'action ; si la clé d'hôte est inconnue ou a changé, la fait approuver
    /// (actions lancées par l'utilisateur uniquement) puis réessaie une fois.
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

    /// <summary>Demande l'approbation de la clé et l'enregistre ; true si acceptée.</summary>
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
        RunActionAsync(vm, "Actualisation…", interactive: true, (_, _) => RefreshCoreAsync(vm));

    /// <summary>« Check updates » : apt update en root, puis relevé de la liste des paquets.</summary>
    public Task<bool> CheckUpdatesAsync(ServerViewModel vm, bool interactive = true) =>
        RunActionAsync(vm, "Recherche de mises à jour…", interactive, async (connection, ct) =>
        {
            vm.AppendLogLine($"\n━━ {DateTime.Now:HH:mm:ss} · Recherche de mises à jour (apt-get update) ━━");
            var result = await Task.Run(() => connection.RunAsRootAsync(RemoteCommands.AptUpdate, vm.AppendLog, ct), ct);
            if (!result.Success)
                throw new SshOperationException(SshErrorKind.Other, $"apt-get update a échoué (code {result.ExitCode}). Voir le journal.");

            vm.Config.LastUpdateCheckUtc = DateTime.UtcNow;
            _app.Config.Save();
            await RefreshCoreAsync(vm);
            vm.AppendLogLine(vm.UpdateCount is { } n
                ? $"→ {vm.UpdatesText}{(vm.SecurityCount > 0 ? $" ({vm.UpdatesDetail})" : "")}"
                : "→ Liste des paquets indisponible.");
        });

    /// <summary>« Update » : apt update + upgrade (ou full-upgrade) non interactifs.</summary>
    public Task<bool> UpgradeAsync(ServerViewModel vm) =>
        RunActionAsync(vm, "Mise à jour en cours…", interactive: true, async (connection, ct) =>
        {
            var settings = vm.Settings;
            string verb = settings.Upgrade == UpgradeKind.FullUpgrade ? "full-upgrade" : "upgrade";
            vm.AppendLogLine($"\n━━ {DateTime.Now:HH:mm:ss} · Mise à jour (apt-get {verb}) ━━");
            var script = RemoteCommands.AptUpgrade(settings.Upgrade, settings.AutoRemove);
            var result = await Task.Run(() => connection.RunAsRootAsync(script, vm.AppendLog, ct), ct);
            if (!result.Success)
                throw new SshOperationException(SshErrorKind.Other, $"La mise à jour a échoué (code {result.ExitCode}). Voir le journal.");

            vm.Config.LastUpdateCheckUtc = DateTime.UtcNow;
            _app.Config.Save();
            await RefreshCoreAsync(vm);
            vm.AppendLogLine("✔ Mise à jour terminée." + (vm.RebootRequired ? " Un redémarrage est requis." : ""));
        });

    public Task<bool> RebootAsync(ServerViewModel vm) =>
        RunActionAsync(vm, "Redémarrage…", interactive: true, async (connection, ct) =>
        {
            vm.AppendLogLine($"\n━━ {DateTime.Now:HH:mm:ss} · Redémarrage ━━");
            var result = await Task.Run(() => connection.RunAsRootAsync(RemoteCommands.Reboot, vm.AppendLog, ct), ct);
            if (!result.Success)
                throw new SshOperationException(SshErrorKind.Other, $"Le redémarrage a échoué (code {result.ExitCode}).");
            ResetConnection(vm.Id);
            vm.State = ServerState.Offline;
            vm.StateDetail = "Redémarrage en cours…";
            vm.LatencyMs = null;
            // Premier relevé une minute plus tard, le temps que le serveur revienne
            _nextRefresh[vm.Id] = DateTime.UtcNow.AddSeconds(60);
        });

    /// <summary>Action sur plusieurs serveurs, quatre à la fois.</summary>
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

    /// <summary>Session du terminal intégré, avec approbation de la clé d'hôte si besoin.</summary>
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

    /// <summary>« Open SSH » : terminal intégré ou externe selon les réglages.</summary>
    public async Task OpenSshAsync(ServerViewModel vm)
    {
        var settings = vm.Settings;
        if (settings.Terminal == TerminalMode.Embedded)
        {
            _app.OpenEmbeddedTerminal(vm.Config);
            return;
        }

        if (_app.Config.CopyPasswordForExternal)
        {
            if (_app.Vault.IsLocked && !await _app.UnlockInteractiveAsync())
                return;
            var secrets = _app.Vault.Get(vm.Id);
            // Le premier mot de passe que demandera la session
            string? toCopy = vm.Config.Auth == AuthMethod.Password
                ? secrets.Password
                : settings.RootShell ? secrets.ElevationSecretFor(vm.Config) : null;
            if (!string.IsNullOrEmpty(toCopy))
                _app.CopySecretToClipboard(toCopy);
        }
        try
        {
            ExternalTerminal.Launch(vm.Config, settings.Terminal, settings.RootShell, _app.Config.CustomTerminalCommand);
        }
        catch (Exception ex)
        {
            _app.ShowError("Ouverture du terminal", ex.Message);
        }
    }

    // ===== Fiche serveur : test et déploiement de clé =====

    /// <summary>Test complet d'une configuration non enregistrée : connexion, puis élévation root.</summary>
    public async Task<string> TestConfigurationAsync(ServerConfig config, ServerSecrets secrets)
    {
        var lines = new List<string>();
        await WithHostKeyApprovalAsync(config, interactive: true, async () =>
        {
            lines.Clear();
            using var connection = await Task.Run(() => SshConnector.ConnectAsync(config, secrets, false, _shutdown.Token));
            var info = await Task.Run(() => RemoteExec.RunAsync(connection.Client,
                "echo \"$(id -un) · $(. /etc/os-release 2>/dev/null; echo \"$PRETTY_NAME\")\"", null, _shutdown.Token));
            lines.Add($"Connexion réussie : {info.Output.Trim()}");

            if (config.EffectiveElevation != ElevationMethod.None)
            {
                var root = await Task.Run(() => RemoteExec.RunAsRootAsync(connection.Client, config, secrets, false,
                    "id -u", null, _shutdown.Token));
                lines.Add(root.Output.Trim().EndsWith('0') && root.Success
                    ? $"Élévation {(config.EffectiveElevation == ElevationMethod.Sudo ? "sudo" : "su")} : OK (root)"
                    : $"Élévation : réponse inattendue ({root.Output.Trim()})");
            }
            else if (!config.IsRootUser)
            {
                lines.Add("Aucune élévation configurée : apt et le redémarrage échoueront sans droits root.");
            }
        });
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Installe la clé publique sur le serveur (connexion par mot de passe), puis vérifie que
    /// la connexion par clé fonctionne. La configuration passe alors en authentification par clé.
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
                throw new SshOperationException(SshErrorKind.Other, $"Installation de la clé impossible : {result.Output.Trim()}");
        });

        var keyConfig = config.Clone();
        keyConfig.Auth = AuthMethod.PrivateKey;
        keyConfig.KeyPath = keyPath;
        using (await Task.Run(() => SshConnector.ConnectAsync(keyConfig, new ServerSecrets(), false, _shutdown.Token)))
        {
        }
        config.Auth = AuthMethod.PrivateKey;
        config.KeyPath = keyPath;
        return $"Clé installée et vérifiée : la connexion se fait désormais avec {keyPath}.";
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
