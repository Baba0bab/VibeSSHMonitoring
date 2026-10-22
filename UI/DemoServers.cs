// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

#if DEBUG
using System.IO;
using VibeSSHMonitoring.Core;

namespace VibeSSHMonitoring.UI;

/// <summary>
/// Debug builds only (VIBESSHMONITORING_DEMO=servers, launch profile Demo): the sample servers of the
/// README screenshots, in a folder of their own that is reset at each launch. Addresses are in
/// 192.0.2.0/24 (TEST-NET-1), which never answers: "OVH VPS web" and "Home Raspberry Pi" get a fake
/// probe instead (home page only: actions and terminals still try to connect), the other two stay
/// unreachable. Runs beside an installed copy.
/// </summary>
internal static class DemoServers
{
    public static bool Enabled => Environment.GetEnvironmentVariable("VIBESSHMONITORING_DEMO") == "servers";

    private static readonly string Home = Path.Combine(Path.GetTempPath(), "VibeSSHMonitoring.DemoServers");

    private const string VpsHost = "192.0.2.10", PiHost = "192.0.2.30";

    /// <summary>Points the configuration to the demo folder; must run before AppConfig is used.</summary>
    public static void Prepare()
    {
        Environment.SetEnvironmentVariable("VIBESSHMONITORING_HOME", Home);

        // A restart (language change) keeps what was changed in the demo
        if (Environment.GetCommandLineArgs().Contains(App.WaitForArgument))
            return;

        // Settings or a vault left by a previous run
        try { File.Delete(Path.Combine(Home, "secrets.json")); } catch { }

        // A recent apt update, so that the automatic one does not try to connect
        var checkedAt = DateTime.UtcNow.AddHours(-2);
        new AppConfig
        {
            Language = "en",
            AppUpdateCheck = false,
            Servers =
            [
                new() { Name = "OVH VPS web", Host = VpsHost, Username = "debian", Elevation = ElevationMethod.Sudo, Tags = ["prod", "web"],
                        LastUpdateCheckUtc = checkedAt },
                new() { Name = "Hetzner dedicated", Host = "192.0.2.20", Port = 2222, Username = "admin", Elevation = ElevationMethod.Sudo, Tags = ["prod"] },
                new() { Name = "Home Raspberry Pi", Host = PiHost, Username = "pi", Elevation = ElevationMethod.Sudo, Tags = ["home"],
                        LastUpdateCheckUtc = checkedAt },
                new() { Name = "Staging", Host = "192.0.2.40", Username = "root" },
            ],
        }.Save();
    }

    /// <summary>Fake probe of a demo server that "answers", with its latency; null for the others.</summary>
    public static (ProbeResult Probe, double LatencyMs)? ProbeFor(ServerConfig config)
    {
        if (!Enabled)
            return null;
        // CPU and memory move a little at each refresh, like a real server
        double Jitter(double value, double spread) => Math.Max(0, value + (Random.Shared.NextDouble() - 0.5) * 2 * spread);
        const long Gb = 1024 * 1024;
        switch (config.Host)
        {
            case VpsHost:
                return (new ProbeResult
                {
                    Hostname = "vps-web", Os = "Debian GNU/Linux 12 (bookworm)", Kernel = "6.1.0-26-amd64", Arch = "x86_64",
                    Uptime = TimeSpan.FromDays(23.4), Load = "0.32 0.27 0.21", Cpus = 2, CpuPercent = Jitter(14, 6),
                    MemTotalKb = 4 * Gb, MemAvailableKb = (long)Jitter(1.6 * Gb, 0.1 * Gb), SwapTotalKb = Gb, SwapFreeKb = 900 * 1024,
                    DiskTotalKb = 80 * Gb, DiskUsedKb = 31 * Gb, HasApt = true,
                    Packages =
                    [
                        new("libc6", "bookworm-security", "2.36-9+deb12u7", "2.36-9+deb12u4", "amd64"),
                        new("openssh-server", "bookworm-security", "1:9.2p1-2+deb12u3", "1:9.2p1-2+deb12u2", "amd64"),
                        new("curl", "bookworm", "7.88.1-10+deb12u8", "7.88.1-10+deb12u7", "amd64"),
                    ],
                }, Jitter(18, 3));
            case PiHost:
                var pi = new ProbeResult
                {
                    Hostname = "raspberrypi", Os = "Debian GNU/Linux 12 (bookworm)", Kernel = "6.6.51+rpt-rpi-2712", Arch = "aarch64",
                    Uptime = TimeSpan.FromDays(61.2), Load = "0.08 0.12 0.10", Cpus = 4, CpuPercent = Jitter(5, 3),
                    MemTotalKb = 8 * Gb, MemAvailableKb = (long)Jitter(6.1 * Gb, 0.1 * Gb), SwapTotalKb = 512 * 1024, SwapFreeKb = 512 * 1024,
                    DiskTotalKb = 58 * Gb, DiskUsedKb = 12 * Gb, HasApt = true, Packages = [], RebootRequired = true,
                };
                pi.RebootPackages.Add("linux-image-rpi-2712");
                return (pi, Jitter(4, 1.5));
            default:
                return null;
        }
    }
}
#endif
