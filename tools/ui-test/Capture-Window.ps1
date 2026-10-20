# SPDX-License-Identifier: GPL-3.0-or-later
# Capture une fenêtre de l'application en PNG (PrintWindow), même masquée par d'autres fenêtres.
#   ./tools/ui-test/Capture-Window.ps1 -Out shot.png                     # fenêtre principale
#   ./tools/ui-test/Capture-Window.ps1 -Out dlg.png -Title "Ajouter"     # fenêtre dont le titre contient « Ajouter »
param(
    [Parameter(Mandatory)] [string] $Out,
    [string] $Title = "",
    [string] $ProcessName = "VibeSSHMonitoring"
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class VsmWin {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@

# Coordonnées physiques : sinon la capture est tronquée sur un écran à 150 % / 175 %
[void][VsmWin]::SetProcessDpiAwarenessContext([IntPtr](-4))
$proc = Get-Process $ProcessName -ErrorAction Stop | Select-Object -First 1
$found = New-Object System.Collections.ArrayList
$callback = [VsmWin+EnumProc] {
    param($h, $l)
    $windowPid = 0
    [void][VsmWin]::GetWindowThreadProcessId($h, [ref]$windowPid)
    if ($windowPid -eq $proc.Id -and [VsmWin]::IsWindowVisible($h)) {
        $sb = New-Object System.Text.StringBuilder 256
        [void][VsmWin]::GetWindowText($h, $sb, 256)
        [void]$found.Add(@{ Handle = $h; Title = $sb.ToString() })
    }
    return $true
}
[void][VsmWin]::EnumWindows($callback, [IntPtr]::Zero)

$target = $found | Where-Object { $_.Title -ne "" -and ($Title -eq "" -or $_.Title -like "*$Title*") } | Select-Object -First 1
if (-not $target) {
    Write-Error ("Fenêtre introuvable. Fenêtres visibles : " + (($found | ForEach-Object { $_.Title }) -join " | "))
    exit 1
}

$rect = New-Object VsmWin+RECT
[void][VsmWin]::GetWindowRect($target.Handle, [ref]$rect)
$bitmap = New-Object System.Drawing.Bitmap ($rect.R - $rect.L), ($rect.B - $rect.T)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()
[void][VsmWin]::PrintWindow($target.Handle, $hdc, 2)   # PW_RENDERFULLCONTENT : contenu WPF / WebView2 compris
$graphics.ReleaseHdc($hdc)
$bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose(); $bitmap.Dispose()
"OK $($target.Title) -> $Out"
