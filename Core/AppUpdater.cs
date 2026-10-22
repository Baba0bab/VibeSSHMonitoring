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
    private (UpdateManager Manager, UpdateInfo Update)? _downgrade;
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

    /// <summary>The running version is a pre-release (beta).</summary>
    public bool IsPrerelease => _manager.CurrentVersion?.IsPrerelease == true;

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString()
        ?? typeof(AppUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "?";

    private static UpdateManager CreateManager(bool includePrerelease, bool allowDowngrade = false)
    {
        IUpdateSource source = Environment.GetEnvironmentVariable(SourceOverrideVariable) is { Length: > 0 } custom
            ? new SimpleFileSource(new DirectoryInfo(custom))
            : new GithubSource(RepositoryUrl, accessToken: null, prerelease: includePrerelease);
        return new UpdateManager(source, new UpdateOptions { AllowVersionDowngrade = allowDowngrade });
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

    /// <summary>
    /// Looks for the latest stable version when it is older than the running pre-release (a newer
    /// one comes through the normal check). Returns its version, or null; nothing is downloaded.
    /// </summary>
    public async Task<string?> FindStableDowngradeAsync(CancellationToken ct = default)
    {
        _downgrade = null;
        if (!IsInstalled || !IsPrerelease)
            return null;
        try
        {
            var manager = CreateManager(includePrerelease: false, allowDowngrade: true);
            if (await manager.CheckForUpdatesAsync().WaitAsync(ct) is not { IsDowngrade: true } update)
                return null;
            _downgrade = (manager, update);
            return update.TargetFullRelease.Version.ToString();
        }
        catch (Exception)
        {
            return null;   // only an offer: the next normal check reports the errors
        }
    }

    /// <summary>
    /// Downloads the stable version found by <see cref="FindStableDowngradeAsync"/>, in place of a
    /// pending update; it is then installed like any update.
    /// </summary>
    public Task DowngradeAsync(CancellationToken ct = default) =>
        _downgrade is var (manager, update)
            ? RunAsync(manager, () => Task.FromResult<UpdateInfo?>(update), ct)
            : Task.CompletedTask;

    /// <summary>Checks for a new version and downloads it if there is one.</summary>
    public Task CheckAndDownloadAsync(CancellationToken ct = default) =>
        State is AppUpdateState.Ready
            ? Task.CompletedTask
            : RunAsync(_manager, () => _manager.CheckForUpdatesAsync(), ct);

    private async Task RunAsync(UpdateManager manager, Func<Task<UpdateInfo?>> check, CancellationToken ct)
    {
        if (!manager.IsInstalled)
            return;
        if (!await _gate.WaitAsync(0, ct))
            return;   // a check is already running
        try
        {
            Error = null;
            SetState(AppUpdateState.Checking);
            var update = await check().WaitAsync(ct);
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
            await manager.DownloadUpdatesAsync(update, percent =>
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
