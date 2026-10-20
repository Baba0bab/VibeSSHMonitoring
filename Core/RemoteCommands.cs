// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeSSHMonitoring.Core;

/// <summary>Server state read by the probe (null fields = not requested or unavailable).</summary>
public sealed class ProbeResult
{
    public string? Hostname { get; set; }
    public string? Os { get; set; }
    public string? Kernel { get; set; }
    public string? Arch { get; set; }
    public TimeSpan? Uptime { get; set; }
    public string? Load { get; set; }
    public int? Cpus { get; set; }
    public double? CpuPercent { get; set; }
    public long? MemTotalKb { get; set; }
    public long? MemAvailableKb { get; set; }
    public long? SwapTotalKb { get; set; }
    public long? SwapFreeKb { get; set; }
    public long? DiskTotalKb { get; set; }
    public long? DiskUsedKb { get; set; }
    public bool RebootRequired { get; set; }
    public bool HasApt { get; set; }
    public List<PackageUpdate>? Packages { get; set; }
}

/// <summary>Commands sent to Debian / Ubuntu servers, and parsing of their output.</summary>
public static partial class RemoteCommands
{
    /// <summary>
    /// Probe in a single command, without root rights. "apt list --upgradable" reads the package
    /// lists already downloaded: "Check updates" (apt update, as root) is what refreshes them.
    /// </summary>
    public static string BuildProbe(bool system, bool resources, bool updates)
    {
        var sb = new StringBuilder("export LC_ALL=C\n");
        sb.Append("echo \"hostname=$(hostname 2>/dev/null)\"\n");
        if (system)
        {
            sb.Append("echo \"os=$(. /etc/os-release 2>/dev/null; echo \"$PRETTY_NAME\")\"\n");
            sb.Append("echo \"kernel=$(uname -r)\"\n");
            sb.Append("echo \"arch=$(uname -m)\"\n");
            sb.Append("echo \"uptime=$(cut -d' ' -f1 /proc/uptime 2>/dev/null)\"\n");
        }
        if (resources)
        {
            sb.Append("echo \"load=$(cut -d' ' -f1-3 /proc/loadavg 2>/dev/null)\"\n");
            sb.Append("echo \"cpus=$(nproc 2>/dev/null || grep -c ^processor /proc/cpuinfo)\"\n");
            sb.Append("echo \"cpu1=$(head -n1 /proc/stat)\"; sleep 0.5; echo \"cpu2=$(head -n1 /proc/stat)\"\n");
            sb.Append("awk '/^(MemTotal|MemAvailable|MemFree|Buffers|Cached|SwapTotal|SwapFree):/ {sub(\":\",\"\",$1); print \"mem_\" $1 \"=\" $2}' /proc/meminfo\n");
            sb.Append("df -P -k / 2>/dev/null | awk 'NR==2 {print \"disk=\" $2 \" \" $3}'\n");
        }
        sb.Append("[ -f /var/run/reboot-required ] && echo reboot=1 || echo reboot=0\n");
        sb.Append("command -v apt-get >/dev/null 2>&1 && echo apt=1 || echo apt=0\n");
        if (updates)
            sb.Append("apt list --upgradable 2>/dev/null | grep -F '[upgradable from' | sed 's/^/pkg=/'\n");
        return sb.ToString();
    }

    private const string AptOptions = "-o DPkg::Lock::Timeout=120";

    /// <summary>"Check updates": refreshes the package lists (root).</summary>
    public static string AptUpdate => $"export DEBIAN_FRONTEND=noninteractive; apt-get {AptOptions} update";

    /// <summary>
    /// "Update": non-interactive upgrade. Locally modified configuration files are kept
    /// (confold); needrestart restarts the affected services.
    /// </summary>
    public static string AptUpgrade(UpgradeKind kind, bool autoRemove)
    {
        string verb = kind == UpgradeKind.FullUpgrade ? "full-upgrade" : "upgrade";
        var sb = new StringBuilder("export DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=a\n");
        sb.Append($"apt-get {AptOptions} update || exit $?\n");
        sb.Append($"apt-get -y {AptOptions} -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold ")
          .Append($"-o Dpkg::Progress-Fancy=0 {verb} || exit $?\n");
        if (autoRemove)
            sb.Append($"apt-get -y {AptOptions} autoremove --purge\n");
        return sb.ToString();
    }

    /// <summary>Detached reboot: the command returns before the connection drops.</summary>
    public static string Reboot =>
        "nohup sh -c 'sleep 2; systemctl reboot || /sbin/reboot' >/dev/null 2>&1 &\necho Reboot scheduled.";

    [GeneratedRegex(@"^(?<name>[^/\s]+)/(?<suites>\S+)\s+(?<new>\S+)\s+(?<arch>\S+)\s+\[upgradable from:\s*(?<old>[^\]]+)\]")]
    private static partial Regex PackageRegex();

    public static PackageUpdate? ParsePackage(string line)
    {
        var m = PackageRegex().Match(line.Trim());
        return m.Success
            ? new PackageUpdate(m.Groups["name"].Value, m.Groups["suites"].Value, m.Groups["new"].Value,
                m.Groups["old"].Value.Trim(), m.Groups["arch"].Value)
            : null;
    }

    public static ProbeResult ParseProbe(string output, bool updates)
    {
        var result = new ProbeResult();
        if (updates)
            result.Packages = new List<PackageUpdate>();
        string? cpu1 = null, cpu2 = null;
        long? memFree = null, buffers = null, cached = null;

        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            string key = line[..eq];
            string value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "hostname": result.Hostname = NullIfEmpty(value); break;
                case "os": result.Os = NullIfEmpty(value); break;
                case "kernel": result.Kernel = NullIfEmpty(value); break;
                case "arch": result.Arch = NullIfEmpty(value); break;
                case "uptime":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double up))
                        result.Uptime = TimeSpan.FromSeconds(up);
                    break;
                case "load": result.Load = NullIfEmpty(value); break;
                case "cpus":
                    if (int.TryParse(value, out int cpus)) result.Cpus = cpus;
                    break;
                case "cpu1": cpu1 = value; break;
                case "cpu2": cpu2 = value; break;
                case "mem_MemTotal": result.MemTotalKb = ParseLong(value); break;
                case "mem_MemAvailable": result.MemAvailableKb = ParseLong(value); break;
                case "mem_MemFree": memFree = ParseLong(value); break;
                case "mem_Buffers": buffers = ParseLong(value); break;
                case "mem_Cached": cached = ParseLong(value); break;
                case "mem_SwapTotal": result.SwapTotalKb = ParseLong(value); break;
                case "mem_SwapFree": result.SwapFreeKb = ParseLong(value); break;
                case "disk":
                    var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        result.DiskTotalKb = ParseLong(parts[0]);
                        result.DiskUsedKb = ParseLong(parts[1]);
                    }
                    break;
                case "reboot": result.RebootRequired = value == "1"; break;
                case "apt": result.HasApt = value == "1"; break;
                case "pkg":
                    if (ParsePackage(value) is { } package)
                        result.Packages?.Add(package);
                    break;
            }
        }

        // Kernels older than 3.14 have no MemAvailable: classic estimate
        if (result.MemAvailableKb is null && memFree is not null)
            result.MemAvailableKb = memFree + (buffers ?? 0) + (cached ?? 0);
        if (cpu1 is not null && cpu2 is not null)
            result.CpuPercent = CpuUsage(cpu1, cpu2);
        if (!result.HasApt)
            result.Packages = null;
        return result;
    }

    /// <summary>CPU usage between two readings of the "cpu" line of /proc/stat.</summary>
    private static double? CpuUsage(string first, string second)
    {
        static long[]? Fields(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || parts[0] != "cpu")
                return null;
            return parts.Skip(1).Take(8).Select(p => long.TryParse(p, out long v) ? v : 0).ToArray();
        }

        var a = Fields(first);
        var b = Fields(second);
        if (a is null || b is null || a.Length < 4 || b.Length < 4)
            return null;
        long idleA = a[3] + (a.Length > 4 ? a[4] : 0);   // idle + iowait
        long idleB = b[3] + (b.Length > 4 ? b[4] : 0);
        long total = b.Sum() - a.Sum();
        if (total <= 0)
            return null;
        return Math.Clamp(100.0 * (1 - (double)(idleB - idleA) / total), 0, 100);
    }

    private static long? ParseLong(string s) => long.TryParse(s, out long v) ? v : null;

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
