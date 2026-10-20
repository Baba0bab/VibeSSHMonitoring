// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Runtime.InteropServices;

namespace VibeSSHMonitoring.UI;

/// <summary>
/// DWM attributes of the window frame (dark mode, title bar colors).
/// Unsupported attributes (Windows 10) are simply ignored by the system.
/// </summary>
internal static class Dwm
{
    public const int UseImmersiveDarkMode = 20;
    public const int CaptionColor = 35;
    public const int TextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void SetAttribute(IntPtr hwnd, int attribute, int value)
    {
        if (hwnd != IntPtr.Zero)
            DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
    }

    /// <summary>Color in the COLORREF format (0x00BBGGRR) expected by DWM.</summary>
    public static int ToColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);
}
