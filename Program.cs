// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using Velopack;
using VibeSSHMonitoring.Core;

namespace VibeSSHMonitoring;

public static class Program
{
    [STAThread]
    public static void Main()
    {
#if DEBUG
        // The demo never uses the real configuration, even when no folder was given
        if (UI.UpgradeDemo.Enabled && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VIBESSHMONITORING_HOME")))
            Environment.SetEnvironmentVariable("VIBESSHMONITORING_HOME", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VibeSSHMonitoring.Demo"));
#endif
        var config = AppConfig.Load();

        // First of all: the installer and the updater restart the exe with their own arguments
        // (install, update, uninstall), which are handled here before exiting.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => AppUpdater.CleanupBeforeUninstall())
            // A downloaded update is installed at the next startup, unless the user turned it off
            .SetAutoApplyOnStartup(config.AppUpdateAutoInstall)
            .Run();

        // Language before any window: the interface reads its texts when it is built
        Loc.Initialize(config.Language, config.ConciseLabels);

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
