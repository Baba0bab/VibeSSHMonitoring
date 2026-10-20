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
        // En tout premier : l'installateur et le programme de mise à jour relancent l'exe avec des
        // arguments à eux (installation, mise à jour, désinstallation), traités puis quittés ici.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => AppUpdater.CleanupBeforeUninstall())
            // Une mise à jour téléchargée s'installe au démarrage suivant, sauf si l'utilisateur l'a désactivé
            .SetAutoApplyOnStartup(AppConfig.Load().AppUpdateAutoInstall)
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
