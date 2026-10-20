// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.IO;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace VibeSSHMonitoring.Core;

public enum AppUpdateState
{
    Unavailable,   // application non installée (exécutable de développement) : pas de mise à jour
    Idle,
    Checking,
    UpToDate,
    Downloading,
    Ready,         // téléchargée, en attente d'installation
    Error
}

/// <summary>
/// Mises à jour de l'application depuis les Releases GitHub (Velopack) : recherche, téléchargement
/// en arrière-plan (delta si possible), puis installation à la fermeture ou au redémarrage.
/// Les événements arrivent sur un thread quelconque.
/// </summary>
public sealed class AppUpdater
{
    public const string RepositoryUrl = "https://github.com/Baba0bab/VibeSSHMonitoring";

    /// <summary>Source de test (dossier local ou URL), à la place de GitHub.</summary>
    private const string SourceOverrideVariable = "VIBESSHMONITORING_UPDATE_SOURCE";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateManager _manager;
    private UpdateInfo? _pending;
    private bool _includePrerelease;

    public AppUpdateState State { get; private set; }
    public string? AvailableVersion { get; private set; }
    public string? ReleaseNotes { get; private set; }
    public int Progress { get; private set; }
    public string? Error { get; private set; }
    public DateTime? LastCheckUtc { get; private set; }

    public event Action? StateChanged;

    public AppUpdater(bool includePrerelease)
    {
        _includePrerelease = includePrerelease;
        _manager = CreateManager(includePrerelease);
        State = _manager.IsInstalled ? AppUpdateState.Idle : AppUpdateState.Unavailable;

        // Mise à jour déjà téléchargée lors d'une session précédente
        if (_manager.IsInstalled && _manager.UpdatePendingRestart is { } pending)
        {
            AvailableVersion = pending.Version.ToString();
            ReleaseNotes = pending.NotesMarkdown;
            State = AppUpdateState.Ready;
        }
    }

    public bool IsInstalled => _manager.IsInstalled;
    public bool IsPortable => _manager.IsInstalled && _manager.IsPortable;

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString()
        ?? typeof(AppUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "?";

    private static UpdateManager CreateManager(bool includePrerelease)
    {
        IUpdateSource source = Environment.GetEnvironmentVariable(SourceOverrideVariable) is { Length: > 0 } custom
            ? new SimpleFileSource(new DirectoryInfo(custom))
            : new GithubSource(RepositoryUrl, accessToken: null, prerelease: includePrerelease);
        return new UpdateManager(source);
    }

    /// <summary>Change le canal (stable seul, ou avec les préversions) ; la recherche suivante en tient compte.</summary>
    public void SetIncludePrerelease(bool include)
    {
        if (include == _includePrerelease)
            return;
        _includePrerelease = include;
        _manager = CreateManager(include);
        if (State is AppUpdateState.UpToDate or AppUpdateState.Error)
            SetState(AppUpdateState.Idle);
    }

    /// <summary>Recherche une nouvelle version et, s'il y en a une, la télécharge.</summary>
    public async Task CheckAndDownloadAsync(CancellationToken ct = default)
    {
        if (!_manager.IsInstalled || State is AppUpdateState.Ready)
            return;
        if (!await _gate.WaitAsync(0, ct))
            return;   // une recherche est déjà en cours
        try
        {
            Error = null;
            SetState(AppUpdateState.Checking);
            var update = await _manager.CheckForUpdatesAsync().WaitAsync(ct);
            LastCheckUtc = DateTime.UtcNow;
            if (update is null)
            {
                SetState(AppUpdateState.UpToDate);
                return;
            }

            AvailableVersion = update.TargetFullRelease.Version.ToString();
            ReleaseNotes = update.TargetFullRelease.NotesMarkdown;
            Progress = 0;
            SetState(AppUpdateState.Downloading);
            await _manager.DownloadUpdatesAsync(update, percent =>
            {
                Progress = percent;
                StateChanged?.Invoke();
            }, ct);
            _pending = update;
            SetState(AppUpdateState.Ready);
        }
        catch (OperationCanceledException)
        {
            SetState(AppUpdateState.Idle);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            LastCheckUtc = DateTime.UtcNow;
            SetState(AppUpdateState.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Confie l'installation au programme de mise à jour, qui attend la fermeture de l'application
    /// (60 s au plus) puis la relance si <paramref name="restart"/>. L'appelant doit ensuite quitter.
    /// </summary>
    public bool ScheduleInstall(bool restart)
    {
        if (State != AppUpdateState.Ready)
            return false;
        try
        {
            // null : la dernière version téléchargée (y compris lors d'une session précédente)
            _manager.WaitExitThenApplyUpdates(_pending?.TargetFullRelease, silent: !restart, restart: restart);
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            SetState(AppUpdateState.Error);
            return false;
        }
    }

    private void SetState(AppUpdateState state)
    {
        State = state;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Avant désinstallation : retire le lancement avec Windows et le cache du terminal.
    /// Les serveurs et le coffre (%APPDATA%) sont conservés pour une réinstallation.
    /// </summary>
    public static void CleanupBeforeUninstall()
    {
        try { StartupManager.SetEnabled(false, false); } catch { }
        try
        {
            if (Directory.Exists(AppConfig.CacheDirectory))
                Directory.Delete(AppConfig.CacheDirectory, recursive: true);
        }
        catch
        {
        }
    }
}
