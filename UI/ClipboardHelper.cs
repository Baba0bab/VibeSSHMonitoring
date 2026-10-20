// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace VibeSSHMonitoring.UI;

/// <summary>
/// Clipboard. Secrets stay there for 30 s at most and are excluded from the Windows clipboard
/// history (Win+V) and cloud sync.
/// </summary>
public static class ClipboardHelper
{
    private static DispatcherTimer? _clearTimer;
    private static string? _secret;

    public static void SetSecret(string text, TimeSpan clearAfter)
    {
        var data = new DataObject();
        data.SetText(text);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        if (!TryClipboard(() => Clipboard.SetDataObject(data, copy: true)))
            return;

        _secret = text;
        _clearTimer?.Stop();
        _clearTimer = new DispatcherTimer { Interval = clearAfter };
        _clearTimer.Tick += (_, _) =>
        {
            _clearTimer.Stop();
            // Only clears it if the user has not copied anything else in the meantime
            TryClipboard(() =>
            {
                if (Clipboard.ContainsText() && Clipboard.GetText() == _secret)
                    Clipboard.Clear();
            });
            _secret = null;
        };
        _clearTimer.Start();
    }

    public static void SetText(string text) => TryClipboard(() => Clipboard.SetText(text));

    public static string GetText()
    {
        string text = "";
        TryClipboard(() => text = Clipboard.ContainsText() ? Clipboard.GetText() : "");
        return text;
    }

    /// <summary>The clipboard may be held by another application: a few attempts.</summary>
    private static bool TryClipboard(Action action)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
            catch (ExternalException)
            {
                Thread.Sleep(40);
            }
        }
        return false;
    }
}
