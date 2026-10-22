// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

#if DEBUG
using System.IO;
using VibeSSHMonitoring.Core;

namespace VibeSSHMonitoring.UI;

/// <summary>
/// Debug builds only (VIBESSHMONITORING_DEMO=servers, launch profile Demo): the sample servers of the
/// README screenshots, in a folder of their own that is reset at each launch. Addresses are in
/// 192.0.2.0/24 (TEST-NET-1), which never answers. Runs beside an installed copy.
/// </summary>
internal static class DemoServers
{
    public static bool Enabled => Environment.GetEnvironmentVariable("VIBESSHMONITORING_DEMO") == "servers";

    private static readonly string Home = Path.Combine(Path.GetTempPath(), "VibeSSHMonitoring.DemoServers");

    /// <summary>Points the configuration to the demo folder; must run before AppConfig is used.</summary>
    public static void Prepare()
    {
        Environment.SetEnvironmentVariable("VIBESSHMONITORING_HOME", Home);

        // A restart (language change) keeps what was changed in the demo
        if (Environment.GetCommandLineArgs().Contains(App.WaitForArgument))
            return;

        // Settings or a vault left by a previous run
        try { File.Delete(Path.Combine(Home, "secrets.json")); } catch { }

        new AppConfig
        {
            Language = "en",
            AppUpdateCheck = false,
            Servers =
            [
                new() { Name = "OVH VPS web", Host = "192.0.2.10", Username = "debian", Elevation = ElevationMethod.Sudo, Tags = ["prod", "web"] },
                new() { Name = "Hetzner dedicated", Host = "192.0.2.20", Port = 2222, Username = "admin", Elevation = ElevationMethod.Sudo, Tags = ["prod"] },
                new() { Name = "Home Raspberry Pi", Host = "192.0.2.30", Username = "pi", Elevation = ElevationMethod.Sudo, Tags = ["home"] },
                new() { Name = "Staging", Host = "192.0.2.40", Username = "root" },
            ],
        }.Save();
    }
}
#endif
