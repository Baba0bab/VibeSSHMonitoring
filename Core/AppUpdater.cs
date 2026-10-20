// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.IO;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace VibeSSHMonitoring.Core;

public enum AppUpdateState
{
    Unavailable,   // application not installed (development build): no updates
    Idle,
    Checking,
    UpToDate,
    Downloading,
    Ready,         // downloaded, waiting to be installed
    Error
}

/// <summary>
/// Application updates from GitHub Releases (Velopack): check, background download (delta when
/// possible), then install on exit or on restart. Events are raised on any thread.
/// </summary>
public sealed class AppUpdater
{
    public const string RepositoryUrl = "https://github.com/Baba0bab/VibeSSHMonitoring";

    /// <summary>Test source (local folder or URL) used instead of GitHub.</summary>
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

        // Update already downloaded during a previous session
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

    /// <summary>Switches the channel (stable only, or with pre-releases); used by the next check.</summary>
    public void SetIncludePrerelease(bool include)
    {
        if (include == _includePrerelease)
            return;
        _includePrerelease = include;
        _manager = CreateManager(include);
        if (State is AppUpdateState.UpToDate or AppUpdateState.Error)
            SetState(AppUpdateState.Idle);
    }

    /// <summary>Checks for a new version and downloads it if there is one.</summary>
    public async Task CheckAndDownloadAsync(CancellationToken ct = default)
    {
        if (!_manager.IsInstalled || State is AppUpdateState.Ready)
            return;
        if (!await _gate.WaitAsync(0, ct))
            return;   // a check is already running
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
    /// Hands the install over to the updater, which waits for the application to exit (60 s at
    /// most), then restarts it if <paramref name="restart"/>. The caller must then exit.
    /// </summary>
    public bool ScheduleInstall(bool restart)
    {
        if (State != AppUpdateState.Ready)
            return false;
        try
        {
            // null: the latest downloaded version (including from a previous session)
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
    /// Before uninstalling: removes the start-with-Windows entry and the terminal cache.
    /// Servers and the vault (%APPDATA%) are kept for a reinstall.
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
