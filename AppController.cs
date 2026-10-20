// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Runtime;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VibeSSHMonitoring.Core;
using VibeSSHMonitoring.UI;

namespace VibeSSHMonitoring;

/// <summary>
/// Cœur de l'application, indépendant de la fenêtre : configuration, coffre, supervision et
/// icône de la zone de notification. Comme GoveeScreen, la fenêtre n'existe que lorsqu'elle est
/// affichée — sauf si des terminaux intégrés sont ouverts : elle est alors seulement masquée.
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly TrayIcon _tray;
    private readonly DispatcherTimer _autoLockTimer;
    private readonly DispatcherTimer _notifyTimer;
    private readonly DispatcherTimer _appUpdateTimer;
    private bool _appUpdateNotified;
    private bool _installScheduled;
    private readonly List<string> _pendingUpdateNotes = new();
    private MainWindow? _window;
    private bool _closingToTray;
    private bool _exiting;
    private Task<bool>? _unlockTask;
    private DateTime _lastActivity = DateTime.UtcNow;

    public AppConfig Config { get; }
    public SecretVault Vault { get; }
    public ServerManager Servers { get; }
    public AppUpdater Updater { get; }

    public AppController()
    {
        Config = AppConfig.Load();
        StartupManager.MigrateLegacyEntry(Config.StartMinimized);
        Vault = SecretVault.Open(Config.Storage);
        if (Config.Storage != Vault.Mode)
        {
            Config.Storage = Vault.Mode;   // le fichier du coffre fait foi
            Config.Save();
        }
        Vault.LockStateChanged += () => _dispatcher.BeginInvoke(OnVaultChanged);

        Updater = new AppUpdater(Config.AppUpdatePrerelease);
        Updater.StateChanged += () => _dispatcher.BeginInvoke(OnAppUpdateChanged);

        Servers = new ServerManager(this);
        Servers.SummaryChanged += UpdateSummary;

        _tray = new TrayIcon(GetTrayState);
        _tray.OpenRequested += ShowWindow;
        _tray.CheckAllRequested += () => _ = CheckAllAsync();
        _tray.RefreshAllRequested += () => _ = Servers.RunForAllAsync(Servers.Servers, vm => Servers.RefreshAsync(vm));
        _tray.LockToggled += () =>
        {
            if (Vault.IsLocked)
                _ = UnlockInteractiveAsync();
            else
                Vault.Lock();
        };
        _tray.InstallUpdateRequested += RestartToUpdate;
        _tray.ExitRequested += Exit;

        // Verrouillage automatique (mot de passe maître) après une période sans activité
        InputManager.Current.PreProcessInput += (_, e) =>
        {
            if (e.StagingItem.Input is MouseButtonEventArgs or KeyEventArgs)
                _lastActivity = DateTime.UtcNow;
        };
        _autoLockTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) =>
        {
            if (Config.AutoLockMinutes > 0 && Vault.Mode == StorageMode.MasterPassword && !Vault.IsLocked &&
                DateTime.UtcNow - _lastActivity > TimeSpan.FromMinutes(Config.AutoLockMinutes))
                Vault.Lock();
        }, _dispatcher);

        _notifyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _notifyTimer.Tick += (_, _) => FlushUpdateNotifications();

        // Mises à jour de l'application : premier passage peu après le démarrage, puis toutes les 12 h
        _appUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _appUpdateTimer.Tick += (_, _) =>
        {
            _appUpdateTimer.Interval = TimeSpan.FromHours(12);
            if (Config.AppUpdateCheck)
                _ = Updater.CheckAndDownloadAsync();
        };

        Application.Current.SessionEnding += (_, _) => _exiting = true;
        UpdateSummary();
    }

    public void Start(bool showWindow)
    {
        _autoLockTimer.Start();
        if (Updater.IsInstalled)
            _appUpdateTimer.Start();
        if (showWindow)
            ShowWindow();
        Servers.Start();
        if (Vault.IsLocked)
        {
            if (showWindow)
                _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _ = UnlockInteractiveAsync());
            else
                _tray.ShowNotification("Coffre verrouillé",
                    "Cliquez ici pour ouvrir l'application et saisir le mot de passe maître.");
        }
    }

    // ===== Fenêtre =====

    public MainWindow? Window => _window;

    public void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow(this);
            _window.Closing += OnWindowClosing;
            _window.Closed += OnWindowClosed;
            Application.Current.MainWindow = _window;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Envoie l'application dans la zone de notification.</summary>
    public void HideToTray()
    {
        if (_window is null)
            return;
        if (_window.HasTerminals)
        {
            _window.Hide();   // garde les sessions ouvertes
            return;
        }
        _closingToTray = true;
        _window.Close();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || _closingToTray || _window is null)
            return;
        if (Config.CloseToTray)
        {
            if (_window.HasTerminals)
            {
                e.Cancel = true;
                _window.Hide();
            }
            else
            {
                _closingToTray = true;
            }
            return;
        }
        if (_window.HasTerminals && !Dialogs.Confirm(_window, "Quitter ?",
                $"{_window.TerminalCount} session(s) de terminal sont ouvertes et seront fermées.", "Quitter"))
            e.Cancel = true;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (Application.Current.MainWindow == _window)
            Application.Current.MainWindow = null;
        _window = null;
        bool toTray = _closingToTray;
        _closingToTray = false;
        if (_exiting)
            return;
        if (!toTray)
        {
            Exit();
            return;
        }
        _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
        });
    }

    public void Exit()
    {
        if (_exiting)
            return;
        _exiting = true;
        // Mise à jour téléchargée : installée en silence une fois l'application fermée
        if (!_installScheduled && Config.AppUpdateAutoInstall && Updater.State == AppUpdateState.Ready)
            _installScheduled = Updater.ScheduleInstall(restart: false);
        _window?.Close();
        Application.Current.Shutdown();
    }

    // ===== Mises à jour de l'application =====

    /// <summary>Installe la mise à jour téléchargée puis relance l'application.</summary>
    public void RestartToUpdate()
    {
        if (Updater.State != AppUpdateState.Ready)
            return;
        if (_window is { HasTerminals: true } &&
            !Dialogs.Confirm(_window, $"Installer la version {Updater.AvailableVersion} ?",
                $"L'application va redémarrer : les {_window.TerminalCount} session(s) de terminal ouvertes seront fermées.",
                "Redémarrer et mettre à jour"))
            return;
        if (!Updater.ScheduleInstall(restart: true))
        {
            ShowError("Mise à jour", $"Installation impossible : {Updater.Error}");
            return;
        }
        _installScheduled = true;
        Exit();
    }

    public void SetAppUpdatePrerelease(bool include)
    {
        Config.AppUpdatePrerelease = include;
        Config.Save();
        Updater.SetIncludePrerelease(include);
    }

    private void OnAppUpdateChanged()
    {
        _window?.SyncAppUpdate();
        if (Updater.State != AppUpdateState.Ready || _appUpdateNotified)
            return;
        _appUpdateNotified = true;
        _tray.ShowNotification($"Vibe SSH Monitoring {Updater.AvailableVersion} est prête",
            Config.AppUpdateAutoInstall
                ? "Elle sera installée à la fermeture de l'application. Cliquez pour redémarrer maintenant depuis les paramètres."
                : "Ouvrez les paramètres pour redémarrer et l'installer.");
    }

    public void Dispose()
    {
        Config.Save();
        Servers.Dispose();
        _tray.Dispose();
    }

    public void OpenEmbeddedTerminal(ServerConfig server)
    {
        ShowWindow();
        _window?.OpenTerminal(server);
    }

    // ===== Coffre =====

    /// <summary>
    /// Demande le mot de passe maître si le coffre est verrouillé. Un seul dialogue à la fois :
    /// les demandes simultanées attendent le même.
    /// </summary>
    public Task<bool> UnlockInteractiveAsync()
    {
        if (!Vault.IsLocked)
            return Task.FromResult(true);
        return _unlockTask ??= UnlockCoreAsync();
    }

    private async Task<bool> UnlockCoreAsync()
    {
        try
        {
            await Task.Yield();   // laisse l'appelant poser son await avant le dialogue modal
            if (Vault.NeedsMasterPasswordSetup)
            {
                string? created = Dialogs.Password(_window, "Créer le mot de passe maître",
                    "Ce mot de passe chiffre les identifiants de vos serveurs. Il ne peut pas être récupéré : notez-le en lieu sûr.",
                    confirm: true, async pw => await Task.Run(() => Vault.Unlock(pw)) ? null : "Erreur de création du coffre.",
                    okText: "Créer");
                return created is not null;
            }
            string? password = Dialogs.Password(_window, "Déverrouiller le coffre",
                "Saisissez le mot de passe maître pour accéder aux identifiants de vos serveurs.",
                confirm: false, async pw => await Task.Run(() => Vault.Unlock(pw)) ? null : "Mot de passe incorrect.",
                okText: "Déverrouiller");
            return password is not null;
        }
        finally
        {
            _unlockTask = null;
        }
    }

    private void OnVaultChanged()
    {
        _lastActivity = DateTime.UtcNow;
        Servers.OnVaultChanged();
        _window?.SyncVault();
        UpdateSummary();
    }

    // ===== Dialogues, notifications =====

    public bool ConfirmHostKey(ServerConfig server, SshOperationException ex)
    {
        bool changed = ex.Kind == SshErrorKind.HostKeyChanged;
        string detail = $"Type : {ex.HostKeyType}\nSHA256:{ex.Fingerprint}" +
                        (changed ? $"\n\nEmpreinte enregistrée :\nSHA256:{server.HostKeyFingerprint}" : "");
        return changed
            ? Dialogs.Confirm(Dialogs.ActiveWindow(), "La clé d'hôte a changé !",
                $"La clé présentée par {server.DisplayName} ({server.Host}) ne correspond pas à celle enregistrée. " +
                "C'est normal après une réinstallation du serveur ; sinon, il peut s'agir d'une interception (man-in-the-middle). " +
                "N'acceptez que si vous savez pourquoi elle a changé.",
                "Faire confiance à la nouvelle clé", danger: true, detail: detail)
            : Dialogs.Confirm(Dialogs.ActiveWindow(), "Nouvelle clé d'hôte",
                $"Première connexion à {server.DisplayName} ({server.Host}). Vérifiez que l'empreinte correspond à celle du serveur " +
                "(ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub), puis approuvez-la.",
                "Approuver", detail: detail);
    }

    public void ShowError(string title, string message) => Dialogs.Info(Dialogs.ActiveWindow(), title, message, error: true);

    public void Notify(string title, string text) => _tray.ShowNotification(title, text);

    /// <summary>Plusieurs serveurs dans la même minute : une seule notification groupée.</summary>
    public void NotifyUpdates(string server, int count, int security)
    {
        _pendingUpdateNotes.Add(security > 0 ? $"{server} : {count} (dont {security} sécurité)" : $"{server} : {count}");
        _notifyTimer.Stop();
        _notifyTimer.Start();
    }

    private void FlushUpdateNotifications()
    {
        _notifyTimer.Stop();
        if (_pendingUpdateNotes.Count == 0)
            return;
        string title = _pendingUpdateNotes.Count == 1 ? "Mises à jour disponibles" : $"Mises à jour sur {_pendingUpdateNotes.Count} serveurs";
        _tray.ShowNotification(title, string.Join("\n", _pendingUpdateNotes));
        _pendingUpdateNotes.Clear();
    }

    public void CopySecretToClipboard(string secret)
    {
        ClipboardHelper.SetSecret(secret, TimeSpan.FromSeconds(30));
        _window?.ShowToast("Mot de passe copié dans le presse-papiers (effacé dans 30 s) : collez-le avec un clic droit.");
    }

    public Task CheckAllAsync() =>
        Servers.RunForAllAsync(Servers.Servers.Where(s => !s.NoApt), vm => Servers.CheckUpdatesAsync(vm));

    // ===== Résumé (menu latéral, icône) =====

    public string SummaryText
    {
        get
        {
            var list = Servers.Servers;
            if (list.Count == 0)
                return "Aucun serveur";
            int online = list.Count(s => s.IsOnline);
            int updates = list.Sum(s => s.UpdateCount ?? 0);
            return $"{online}/{list.Count} en ligne" + (updates > 0 ? $" · {updates} mise{(updates > 1 ? "s" : "")} à jour" : "");
        }
    }

    private void UpdateSummary()
    {
        _tray.SetStatus(SummaryText);
        _window?.SyncSummary();
    }

    private TrayState GetTrayState() =>
        new(SummaryText, Vault.Mode == StorageMode.MasterPassword, Vault.IsLocked, Servers.Servers.Count > 0,
            Updater.State == AppUpdateState.Ready ? Updater.AvailableVersion : null);
}
