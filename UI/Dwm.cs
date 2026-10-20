// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Runtime.InteropServices;

namespace VibeSSHMonitoring.UI;

/// <summary>
/// Attributs DWM du cadre de fenêtre (mode sombre, couleurs de la barre de titre).
/// Les attributs non pris en charge (Windows 10) sont simplement ignorés par le système.
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

    /// <summary>Couleur au format COLORREF (0x00BBGGRR) attendu par DWM.</summary>
    public static int ToColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);
}
