// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Diagnostics;
using System.Runtime;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VibeSSHMonitoring.Core;
using VibeSSHMonitoring.UI;

namespace VibeSSHMonitoring;

/// <summary>
/// Core of the application, independent from the window: configuration, vault, monitoring and
/// notification area icon. As in GoveeScreen, the window only exists while it is shown, unless
/// built-in terminals are open: it is then only hidden.
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
            Config.Storage = Vault.Mode;   // the vault file wins
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

        // Auto-lock (master password) after a period of inactivity
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

        // Application updates: first check shortly after startup, then every 12 h
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
                _tray.ShowNotification(Loc.T("Vault.LockedNotifyTitle"), Loc.T("Vault.LockedNotifyText"));
        }
    }

    // ===== Window =====

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

    /// <summary>Sends the application to the notification area.</summary>
    public void HideToTray()
    {
        if (_window is null)
            return;
        if (_window.HasTerminals)
        {
            _window.Hide();   // keeps the sessions open
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
        if (_window.HasTerminals && !Dialogs.Confirm(_window, Loc.T("Exit.ConfirmTitle"),
                Loc.T("Exit.ConfirmTerminals", _window.TerminalCount), Loc.T("Tray.Exit")))
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
        // Downloaded update: installed silently once the application has closed
        if (!_installScheduled && Config.AppUpdateAutoInstall && Updater.State == AppUpdateState.Ready)
            _installScheduled = Updater.ScheduleInstall(restart: false);
        _window?.Close();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Restarts the application (to apply a language change). The new instance waits for this one
    /// to exit before taking over; open terminals are closed after confirmation.
    /// </summary>
    public void Restart()
    {
        if (_window is { HasTerminals: true } &&
            !Dialogs.Confirm(_window, Loc.T("Restart.ConfirmTitle"),
                Loc.T("Restart.ConfirmTerminals", _window.TerminalCount), Loc.T("Settings.LanguageRestart")))
            return;
        string? exe = Environment.ProcessPath;
        if (exe is null)
            return;
        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            ArgumentList = { App.WaitForArgument, Environment.ProcessId.ToString() }
        })?.Dispose();
        Exit();
    }

    // ===== Application updates =====

    /// <summary>Installs the downloaded update, then restarts the application.</summary>
    public void RestartToUpdate()
    {
        if (Updater.State != AppUpdateState.Ready)
            return;
        if (_window is { HasTerminals: true } &&
            !Dialogs.Confirm(_window, Loc.T("AppUpdate.ConfirmTitle", Updater.AvailableVersion),
                Loc.T("Restart.ConfirmTerminals", _window.TerminalCount), Loc.T("AppUpdate.RestartInstall")))
            return;
        if (!Updater.ScheduleInstall(restart: true))
        {
            ShowError(Loc.T("AppUpdate.ErrorTitle"), Loc.T("AppUpdate.InstallFailed", Updater.Error));
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
        if (!include && Updater.IsPrerelease)
            _ = OfferStableDowngradeAsync();
    }

    /// <summary>
    /// Pre-releases turned off while running one: offers to go back to the latest stable version
    /// (optional: otherwise the next newer stable version comes as a normal update).
    /// </summary>
    private async Task OfferStableDowngradeAsync()
    {
        string? version = await Updater.FindStableDowngradeAsync();
        if (version is null || Config.AppUpdatePrerelease)   // none, or turned back on meanwhile
            return;
        if (!Dialogs.Confirm(_window, Loc.T("AppUpdate.DowngradeTitle", version),
                Loc.T("AppUpdate.DowngradeText", Updater.CurrentVersion, version),
                Loc.T("AppUpdate.DowngradeButton", version), cancelText: Loc.T("AppUpdate.DowngradeKeep")))
            return;
        await Updater.DowngradeAsync();
    }

    private void OnAppUpdateChanged()
    {
        _window?.SyncAppUpdate();
        if (Updater.State != AppUpdateState.Ready || _appUpdateNotified)
            return;
        _appUpdateNotified = true;
        _tray.ShowNotification(Loc.T("AppUpdate.ReadyTitle", Updater.AvailableVersion),
            Loc.T(Config.AppUpdateAutoInstall ? "AppUpdate.ReadyOnExit" : "AppUpdate.ReadyManual"));
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

    // ===== Vault =====

    /// <summary>
    /// Asks for the master password when the vault is locked. One dialog at a time: simultaneous
    /// requests wait for the same one.
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
            await Task.Yield();   // lets the caller set up its await before the modal dialog
            if (Vault.NeedsMasterPasswordSetup)
            {
                string? created = Dialogs.Password(_window, Loc.T("Vault.CreateTitle"), Loc.T("Vault.CreateText"),
                    confirm: true, async pw => await Task.Run(() => Vault.Unlock(pw)) ? null : Loc.T("Vault.CreateFailed"),
                    okText: Loc.T("Vault.CreateButton"));
                return created is not null;
            }
            string? password = Dialogs.Password(_window, Loc.T("Vault.UnlockTitle"), Loc.T("Vault.UnlockText"),
                confirm: false, async pw => await Task.Run(() => Vault.Unlock(pw)) ? null : Loc.T("Vault.WrongPassword"),
                okText: Loc.T("Detail.Unlock"));
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

    // ===== Dialogs, notifications =====

    public bool ConfirmHostKey(ServerConfig server, SshOperationException ex)
    {
        bool changed = ex.Kind == SshErrorKind.HostKeyChanged;
        string detail = Loc.T("HostKey.Detail", ex.HostKeyType, ex.Fingerprint) +
                        (changed ? "\n\n" + Loc.T("HostKey.DetailSaved", server.HostKeyFingerprint) : "");
        return changed
            ? Dialogs.Confirm(Dialogs.ActiveWindow(), Loc.T("HostKey.ChangedTitle"),
                Loc.T("HostKey.ChangedText", server.DisplayName, server.Host),
                Loc.T("HostKey.TrustNew"), danger: true, detail: detail)
            : Dialogs.Confirm(Dialogs.ActiveWindow(), Loc.T("HostKey.NewTitle"),
                Loc.T("HostKey.NewText", server.DisplayName, server.Host),
                Loc.T("HostKey.Approve"), detail: detail);
    }

    public void ShowError(string title, string message) => Dialogs.Info(Dialogs.ActiveWindow(), title, message, error: true);

    public void Notify(string title, string text) => _tray.ShowNotification(title, text);

    /// <summary>Several servers within the same minute: a single grouped notification.</summary>
    public void NotifyUpdates(string server, int count, int security)
    {
        _pendingUpdateNotes.Add(security > 0
            ? Loc.T("Notify.UpdatesLineSecurity", server, count, security)
            : Loc.T("Notify.UpdatesLine", server, count));
        _notifyTimer.Stop();
        _notifyTimer.Start();
    }

    private void FlushUpdateNotifications()
    {
        _notifyTimer.Stop();
        if (_pendingUpdateNotes.Count == 0)
            return;
        string title = _pendingUpdateNotes.Count == 1
            ? Loc.T("Notify.UpdatesTitle")
            : Loc.T("Notify.UpdatesTitleMany", _pendingUpdateNotes.Count);
        _tray.ShowNotification(title, string.Join("\n", _pendingUpdateNotes));
        _pendingUpdateNotes.Clear();
    }

    public void CopySecretToClipboard(string secret)
    {
        ClipboardHelper.SetSecret(secret, TimeSpan.FromSeconds(30));
        _window?.ShowToast(Loc.T("Toast.PasswordCopied"));
    }

    public Task CheckAllAsync() =>
        Servers.RunForAllAsync(Servers.Servers.Where(s => !s.NoApt), vm => Servers.CheckUpdatesAsync(vm));

    // ===== Summary (side menu, icon) =====

    public string SummaryText
    {
        get
        {
            var list = Servers.Servers;
            if (list.Count == 0)
                return Loc.T("Summary.NoServer");
            int online = list.Count(s => s.IsOnline);
            int updates = list.Sum(s => s.UpdateCount ?? 0);
            string text = Loc.T("Summary.Online", online, list.Count);
            if (updates > 0)
                text += " · " + (updates == 1 ? Loc.T("Updates.One") : Loc.T("Updates.Many", updates));
            return text;
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
