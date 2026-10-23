// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace VibeSSHMonitoring.Core;

/// <summary>
/// Gives physical memory back to Windows once the window is gone. Most of it belongs to the WPF
/// rendering engine, which stays loaded: its pages are released from RAM (63 MB → about 8 MB in
/// the notification area) and come back on demand when the window is shown again.
/// </summary>
public static class ProcessMemory
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

    /// <summary>Compacts the managed heap, then empties the working set (only what is in use comes back).</summary>
    public static void Release()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var process = Process.GetCurrentProcess();
        // (-1, -1): removes as many pages as possible from the working set
        SetProcessWorkingSetSize(process.Handle, -1, -1);
    }
}
