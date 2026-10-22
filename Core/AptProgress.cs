// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Text;
using System.Text.RegularExpressions;

namespace VibeSSHMonitoring.Core;

/// <summary>Where a package stands during an upgrade (ordered: a package only moves forward).</summary>
public enum PackageStage { Pending, Downloading, Installing, Done, Failed }

/// <summary>A change seen in the upgrade output. Versions are only known from some lines.</summary>
public sealed record PackageProgress(string Name, PackageStage Stage, string? NewVersion = null, string? OldVersion = null);

/// <summary>
/// Follows the output of "apt-get upgrade" (C locale, see <see cref="RemoteCommands.AptUpgrade"/>)
/// and tells what happens to each package: downloaded, unpacked, then configured. dpkg configures
/// one package at a time, so a "Setting up" ends when the next dpkg step starts. Not thread-safe.
/// </summary>
public sealed partial class AptProgressParser
{
    private readonly StringBuilder _line = new();
    private string? _configuring;

    [GeneratedRegex(@"^Get:\d+\s+\S+\s+\S+\s+(?:\S+\s+)?(?<name>[^\s:]+)(?::\S+)?\s+\S+\s+(?<ver>\S+)\s+\[")]
    private static partial Regex GetRegex();

    [GeneratedRegex(@"^Unpacking (?<name>[^\s:]+)(?::\S+)? \((?<ver>[^)]+)\)(?: over \((?<old>[^)]+)\))?")]
    private static partial Regex UnpackRegex();

    [GeneratedRegex(@"^Setting up (?<name>[^\s:]+)(?::\S+)? \((?<ver>[^)]+)\)")]
    private static partial Regex SetupRegex();

    [GeneratedRegex(@"^dpkg: error processing package (?<name>[^\s:]+)")]
    private static partial Regex ErrorRegex();

    [GeneratedRegex(@"^(Preparing to unpack |Selecting previously unselected package |Processing triggers for |Removing |Purging configuration files for )")]
    private static partial Regex OtherStepRegex();

    /// <summary>Reads a piece of output (any split) and returns the changes it completes.</summary>
    public List<PackageProgress> Feed(string chunk)
    {
        var changes = new List<PackageProgress>();
        foreach (char c in chunk)
        {
            if (c is '\n' or '\r')
            {
                ReadLine(RemoteExec.StripAnsi(_line.ToString()).Trim(), changes);
                _line.Clear();
            }
            else
            {
                _line.Append(c);
            }
        }
        return changes;
    }

    /// <summary>End of the command: the package being configured is done if apt succeeded.</summary>
    public List<PackageProgress> Complete(bool success)
    {
        var changes = Feed("\n");
        if (_configuring is not null && success)
            changes.Add(new PackageProgress(_configuring, PackageStage.Done));
        _configuring = null;
        return changes;
    }

    private void ReadLine(string line, List<PackageProgress> changes)
    {
        if (line.Length == 0)
            return;
        Match m;
        if ((m = GetRegex().Match(line)).Success)
        {
            changes.Add(new PackageProgress(m.Groups["name"].Value, PackageStage.Downloading, m.Groups["ver"].Value));
        }
        else if ((m = UnpackRegex().Match(line)).Success)
        {
            EndConfiguring(changes);
            changes.Add(new PackageProgress(m.Groups["name"].Value, PackageStage.Installing, m.Groups["ver"].Value,
                m.Groups["old"].Success ? m.Groups["old"].Value : null));
        }
        else if ((m = SetupRegex().Match(line)).Success)
        {
            EndConfiguring(changes);
            _configuring = m.Groups["name"].Value;
            changes.Add(new PackageProgress(_configuring, PackageStage.Installing, m.Groups["ver"].Value));
        }
        else if ((m = ErrorRegex().Match(line)).Success)
        {
            string name = m.Groups["name"].Value;
            if (_configuring == name)
                _configuring = null;
            changes.Add(new PackageProgress(name, PackageStage.Failed));
        }
        else if (OtherStepRegex().IsMatch(line))
        {
            EndConfiguring(changes);
        }
    }

    private void EndConfiguring(List<PackageProgress> changes)
    {
        if (_configuring is null)
            return;
        changes.Add(new PackageProgress(_configuring, PackageStage.Done));
        _configuring = null;
    }
}
