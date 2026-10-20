// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.IO;
using Microsoft.Win32;

namespace VibeSSHMonitoring.Core;

/// <summary>Start with Windows through the Run registry key (HKCU).</summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VibeSSHMonitoring";
    private const string LegacyValueName = "SshVibeMonitoring";   // former application name

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is not null;
    }

    /// <summary>Startup entry created under the former name: replaced by the new one.</summary>
    public static void MigrateLegacyEntry(bool startMinimized)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(LegacyValueName) is null)
            return;
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        SetEnabled(true, startMinimized);
    }

    public static void SetEnabled(bool enabled, bool startMinimized)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null)
            return;

        if (enabled)
        {
            string exePath = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "VibeSSHMonitoring.exe");
            string args = startMinimized ? " --minimized" : "";
            key.SetValue(ValueName, $"\"{exePath}\"{args}");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
