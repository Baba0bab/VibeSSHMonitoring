// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

#if DEBUG
using VibeSSHMonitoring.Core;

namespace VibeSSHMonitoring.UI;

/// <summary>
/// Debug builds only (VIBESSHMONITORING_DEMO=1): a fake server, never saved, replays an upgrade in
/// a loop so the live package list can be checked without a server. Runs beside an installed copy.
/// </summary>
internal static class UpgradeDemo
{
    public static bool Enabled => Environment.GetEnvironmentVariable("VIBESSHMONITORING_DEMO") == "1";

    private static readonly PackageUpdate[] Packages =
    [
        new("libc6", "bookworm-security", "2.36-9+deb12u7", "2.36-9+deb12u4", "amd64"),
        new("openssh-server", "bookworm-security", "1:9.2p1-2+deb12u3", "1:9.2p1-2+deb12u2", "amd64"),
        new("curl", "bookworm", "7.88.1-10+deb12u8", "7.88.1-10+deb12u7", "amd64"),
        new("nginx", "bookworm", "1.22.1-9+deb12u1", "1.22.1-9", "amd64"),
        new("tzdata", "bookworm-updates", "2024b-0+deb12u1", "2024a-0+deb12u1", "all"),
    ];

    // tzdata is kept back, nginx fails to configure, a new kernel comes with the upgrade
    private static readonly string[] Output =
    [
        "Reading package lists...",
        "Get:1 http://deb.debian.org/debian-security bookworm-security/main amd64 libc6 amd64 2.36-9+deb12u7 [2,757 kB]",
        "Get:2 http://deb.debian.org/debian-security bookworm-security/main amd64 openssh-server amd64 1:9.2p1-2+deb12u3 [456 kB]",
        "Get:3 http://deb.debian.org/debian bookworm/main amd64 curl amd64 7.88.1-10+deb12u8 [315 kB]",
        "Get:4 http://deb.debian.org/debian bookworm/main amd64 nginx amd64 1.22.1-9+deb12u1 [527 kB]",
        "Get:5 http://deb.debian.org/debian bookworm/main amd64 linux-image-6.1.0-27-amd64 amd64 6.1.115-1 [68.6 MB]",
        "Fetched 72.6 MB in 4s (18.1 MB/s)",
        "Preparing to unpack .../libc6_2.36-9+deb12u7_amd64.deb ...",
        "Unpacking libc6:amd64 (2.36-9+deb12u7) over (2.36-9+deb12u4) ...",
        "Setting up libc6:amd64 (2.36-9+deb12u7) ...",
        "Preparing to unpack .../openssh-server_1%3a9.2p1-2+deb12u3_amd64.deb ...",
        "Unpacking openssh-server (1:9.2p1-2+deb12u3) over (1:9.2p1-2+deb12u2) ...",
        "Unpacking curl (7.88.1-10+deb12u8) over (7.88.1-10+deb12u7) ...",
        "Unpacking nginx (1.22.1-9+deb12u1) over (1.22.1-9) ...",
        "Selecting previously unselected package linux-image-6.1.0-27-amd64.",
        "Unpacking linux-image-6.1.0-27-amd64 (6.1.115-1) ...",
        "Setting up openssh-server (1:9.2p1-2+deb12u3) ...",
        "Setting up curl (7.88.1-10+deb12u8) ...",
        "Setting up nginx (1.22.1-9+deb12u1) ...",
        "Job for nginx.service failed because the control process exited with error code.",
        "dpkg: error processing package nginx (--configure):",
        "Setting up linux-image-6.1.0-27-amd64 (6.1.115-1) ...",
        "update-initramfs: Generating /boot/initrd.img-6.1.0-27-amd64",
        "Processing triggers for man-db (2.11.2-2) ...",
        "Errors were encountered while processing:",
        " nginx",
    ];

    public static async void Run(Action<ServerViewModel> show, Func<ServerConfig, EffectiveSettings> settingsFor)
    {
        var config = new ServerConfig { Name = "demo", Host = "demo.invalid" };
        var vm = new ServerViewModel(config, settingsFor(config));
        show(vm);
        while (true)
        {
            vm.ClearUpgradeResults();
            vm.Probe =new ProbeResult { HasApt = true, Packages = Packages.ToList() };
            await Task.Delay(2500);
            vm.BeginUpgrade();
            vm.IsBusy = true;
            foreach (string line in Output)
            {
                vm.AppendLogLine(line);
                vm.FeedUpgradeOutput(line + "\n");
                await Task.Delay(line.StartsWith("Setting up linux-image") ? 3000 : 700);
            }
            vm.EndUpgrade(success: false);
            vm.IsBusy = false;
            vm.Probe = new ProbeResult { HasApt = true, Packages = [Packages[3], Packages[4]] };
            await Task.Delay(6000);
            vm.ClearLog();
        }
    }
}
#endif
