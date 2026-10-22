// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Runtime.InteropServices;
using System.Windows.Threading;

using VibeSSHMonitoring.Core;

namespace VibeSSHMonitoring.UI;

/// <summary>State shown by the tray menu, read again each time it opens.</summary>
public record TrayState(string Status, bool CanLock, bool IsLocked, bool HasServers, string? UpdateVersion,
    IReadOnlyList<TrayServer>? Servers);

/// <summary>A server row of the tray menu (when the per-server status is enabled).</summary>
public record TrayServer(Guid Id, string Name, TrayDot Dot);

/// <summary>Colored dot in front of a server of the tray menu.</summary>
public enum TrayDot { Neutral, Ok, Updates, Problem }

/// <summary>
/// Notification area icon: left click = open (or hide) the window, right click = status of each
/// server (optional) and quick actions (check, refresh, vault) without opening the window,
/// Windows notifications (updates available, unreachable server). Pure Win32 (Shell_NotifyIcon,
/// message window, native dark menu): neither WinForms nor the WPF rendering engine (~65 MB,
/// never released once started) are loaded for the icon or its menu.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    // ===== Interop: icon, message window, menu =====

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MENUITEMINFO
    {
        public int cbSize;
        public int fMask;
        public int fType;
        public int fState;
        public int wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public int cch;
        public IntPtr hbmpItem;
    }

    private delegate int SetPreferredAppModeDelegate(int mode);
    private delegate void FlushMenuThemesDelegate();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    // Unicode like the window class: the ANSI version would truncate texts (title cut down to "V")
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, int flags, IntPtr idOrSubmenu, string? text);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr menu, int item, int byPosition);

    [DllImport("user32.dll")]
    private static extern bool SetMenuItemInfo(IntPtr menu, int item, bool byPosition, ref MENUITEMINFO info);

    // The header alone is a valid BITMAPINFO for 32-bit pixels (no color table)
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, int usage, out IntPtr bits,
        IntPtr section, int offset);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadImage(IntPtr instance, IntPtr name, int type, int cx, int cy, int flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("comctl32.dll")]
    private static extern int LoadIconWithScaleDown(IntPtr instance, IntPtr name, int cx, int cy, out IntPtr icon);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(IntPtr name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr reserved, int flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);

    private const string WindowClass = "VibeSSHMonitoringTray";
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const int NIIF_INFO = 1, NIIF_WARNING = 2, NIIF_RESPECT_QUIET_TIME = 0x80;
    private const int NIN_BALLOONUSERCLICK = 0x0405;
    private const int WM_NULL = 0, WM_APP = 0x8000;
    private const int CallbackMessage = WM_APP + 1;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int SM_CXSMICON = 49, SM_MENUDROPALIGNMENT = 40;
    private const int IMAGE_ICON = 1;
    private const int MF_STRING = 0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
    private const int MIIM_BITMAP = 0x80;
    private const int TPM_RIGHTALIGN = 0x8, TPM_RIGHTBUTTON = 0x2, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;
    private const int LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x800;
    private static readonly IntPtr AppIconId = 32512;   // IDI_APPLICATION: icon embedded by the .NET apphost
    private static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    // Menu command identifiers
    private const int CmdOpen = 1, CmdCheckAll = 2, CmdRefreshAll = 3, CmdLock = 4, CmdExit = 5, CmdInstallUpdate = 6;
    private const int CmdServerFirst = 1000;   // + index in TrayState.Servers

    // ===== State =====

    private readonly Func<TrayState> _getState;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly WndProcDelegate _wndProc;    // kept in a field: called from native code
    private readonly IntPtr _module = GetModuleHandle(IntPtr.Zero);
    private readonly IntPtr _window;              // invisible window that receives the icon clicks
    private readonly IntPtr _icon;
    private readonly Dictionary<TrayDot, IntPtr> _dots = new();   // menu bitmaps, created on first use
    private string _tooltip = "Vibe SSH Monitoring";

    public event Action? OpenRequested;
    /// <summary>Left click on the icon.</summary>
    public event Action? Clicked;
    /// <summary>Click on a server row of the menu.</summary>
    public event Action<Guid>? ServerRequested;
    public event Action? CheckAllRequested;
    public event Action? RefreshAllRequested;
    public event Action? LockToggled;
    public event Action? InstallUpdateRequested;
    public event Action? ExitRequested;

    public TrayIcon(Func<TrayState> getState)
    {
        _getState = getState;
        EnableDarkMenus();

        // Win32 window that is never shown, outside the taskbar and Alt+Tab. Top-level rather
        // than "message-only", to receive TaskbarCreated. No HwndSource: it would start the
        // WPF rendering engine at launch.
        _wndProc = WndProc;
        var windowClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = _module,
            lpszClassName = WindowClass
        };
        RegisterClassEx(ref windowClass);
        _window = CreateWindowEx(WS_EX_TOOLWINDOW, WindowClass, WindowClass, 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, _module, IntPtr.Zero);

        _icon = LoadAppIcon();
        Notify(NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP);
    }

    /// <summary>
    /// Dark native menus, matching the interface: SetPreferredAppMode(ForceDark) and
    /// FlushMenuThemes from uxtheme (ordinals 135 and 136). Undocumented but stable since
    /// Windows 10 1903; when missing, the menu simply stays light.
    /// </summary>
    private static void EnableDarkMenus()
    {
        IntPtr uxtheme = LoadLibraryEx("uxtheme.dll", IntPtr.Zero, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (uxtheme == IntPtr.Zero)
            return;
        IntPtr setMode = GetProcAddress(uxtheme, 135);
        IntPtr flush = GetProcAddress(uxtheme, 136);
        if (setMode != IntPtr.Zero)
            Marshal.GetDelegateForFunctionPointer<SetPreferredAppModeDelegate>(setMode)(2);   // ForceDark
        if (flush != IntPtr.Zero)
            Marshal.GetDelegateForFunctionPointer<FlushMenuThemesDelegate>(flush)();
    }

    /// <summary>
    /// The exe icon, scaled down by Windows to the exact notification area size (sharp on HiDPI).
    /// LoadIconWithScaleDown comes from Common Controls v6 (see app.manifest); otherwise
    /// LoadImage does the same with a less careful downscale.
    /// </summary>
    private IntPtr LoadAppIcon()
    {
        int size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
        try
        {
            if (LoadIconWithScaleDown(_module, AppIconId, size, size, out IntPtr icon) == 0)
                return icon;
        }
        catch (EntryPointNotFoundException)
        {
        }
        return LoadImage(_module, AppIconId, IMAGE_ICON, size, size, 0);
    }

    /// <summary>Icon tooltip (limited to 127 characters by Windows).</summary>
    public void SetStatus(string status)
    {
        string text = $"Vibe SSH Monitoring — {status}";
        text = text.Length > 127 ? text[..127] : text;
        if (text == _tooltip)
            return;
        _tooltip = text;
        Notify(NIM_MODIFY, NIF_TIP);
    }

    /// <summary>Windows notification (balloon, or toast on Windows 10/11); a click opens the window.</summary>
    public void ShowNotification(string title, string text, bool warning = false)
    {
        Notify(NIM_MODIFY, NIF_INFO, title.Length > 63 ? title[..63] : title, text.Length > 255 ? text[..255] : text,
            (warning ? NIIF_WARNING : NIIF_INFO) | NIIF_RESPECT_QUIET_TIME);
    }

    private void Notify(int message, int flags, string infoTitle = "", string info = "", int infoFlags = 0)
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _window,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = _tooltip,
            szInfo = info,
            szInfoTitle = infoTitle,
            dwInfoFlags = infoFlags
        };
        Shell_NotifyIcon(message, ref data);
    }

    /// <summary>
    /// Called from native code: actions go back through the Dispatcher, so that an exception
    /// follows the normal WPF path instead of crossing native frames.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)(long)lParam)   // version d'origine du protocole : lParam = message souris
            {
                case WM_LBUTTONUP:
                    _dispatcher.BeginInvoke(() => Clicked?.Invoke());
                    break;
                case NIN_BALLOONUSERCLICK:
                    _dispatcher.BeginInvoke(() => OpenRequested?.Invoke());
                    break;
                case WM_RBUTTONUP:
                    _dispatcher.BeginInvoke(ShowMenu);
                    break;
            }
            return IntPtr.Zero;
        }
        if (msg == TaskbarCreatedMessage)
        {
            // Explorer restarted: the notification area is empty, add the icon again
            Notify(NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP);
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Menu rebuilt from the current state each time it opens, then destroyed.</summary>
    private void ShowMenu()
    {
        var state = _getState();
        IntPtr menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MF_STRING | MF_GRAYED, IntPtr.Zero, $"Vibe SSH Monitoring · {state.Status}");
            AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
            if (state.Servers is { Count: > 0 } servers)
            {
                for (int i = 0; i < servers.Count; i++)
                {
                    AppendMenu(menu, MF_STRING, CmdServerFirst + i, servers[i].Name.Replace("&", "&&"));
                    var item = new MENUITEMINFO
                    {
                        cbSize = Marshal.SizeOf<MENUITEMINFO>(),
                        fMask = MIIM_BITMAP,
                        hbmpItem = Dot(servers[i].Dot)
                    };
                    SetMenuItemInfo(menu, CmdServerFirst + i, false, ref item);
                }
                AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
            }
            AppendMenu(menu, MF_STRING, CmdOpen, Loc.T("Tray.Open"));
            AppendMenu(menu, MF_STRING | (state.HasServers ? 0 : MF_GRAYED), CmdCheckAll, Loc.T("Tray.CheckAll"));
            AppendMenu(menu, MF_STRING | (state.HasServers ? 0 : MF_GRAYED), CmdRefreshAll, Loc.T("Tray.RefreshAll"));
            if (state.UpdateVersion is not null)
            {
                AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
                AppendMenu(menu, MF_STRING, CmdInstallUpdate, Loc.T("Tray.InstallUpdate", state.UpdateVersion));
            }
            if (state.CanLock)
            {
                AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
                AppendMenu(menu, MF_STRING, CmdLock, Loc.T(state.IsLocked ? "Tray.Unlock" : "Tray.Lock"));
            }
            AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, CmdExit, Loc.T("Tray.Exit"));
            SetMenuDefaultItem(menu, CmdOpen, 0);   // bold, like the left click

            // Foreground while the menu is open, otherwise it does not close when clicking
            // elsewhere; the following WM_NULL is the documented sequence for tray menus
            SetForegroundWindow(_window);
            GetCursorPos(out POINT cursor);
            int flags = TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY |
                        (GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : 0);
            int command = TrackPopupMenuEx(menu, flags, cursor.X, cursor.Y, _window, IntPtr.Zero);
            PostMessage(_window, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (command >= CmdServerFirst && state.Servers is { } list && command - CmdServerFirst < list.Count)
                ServerRequested?.Invoke(list[command - CmdServerFirst].Id);
            else
                Execute(command);
        }
        finally
        {
            DestroyMenu(menu);   // also destroys the attached submenus
        }
    }

    /// <summary>
    /// Colored dot of a server row, in the theme colors: a 32-bit bitmap with premultiplied
    /// alpha, which themed menus (dark included) blend onto their background. Drawn here with
    /// 4×4 supersampling for smooth edges, at the small icon size of the screen.
    /// </summary>
    private IntPtr Dot(TrayDot dot)
    {
        if (_dots.TryGetValue(dot, out IntPtr cached))
            return cached;
        uint rgb = dot switch
        {
            TrayDot.Ok => 0x3DD68C,        // SuccessColor
            TrayDot.Updates => 0xF5B94B,   // WarningColor
            TrayDot.Problem => 0xF2615C,   // DangerColor
            _ => 0x8A8A99                  // NeutralColor
        };
        int size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size,   // top-down
            biPlanes = 1,
            biBitCount = 32
        };
        IntPtr bitmap = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero)
            return IntPtr.Zero;   // no dot rather than no menu

        var pixels = new int[size * size];
        double center = size / 2.0, radius = size * 0.3;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int covered = 0;
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                    {
                        double dx = x + (sx + 0.5) / 4 - center, dy = y + (sy + 0.5) / 4 - center;
                        if (dx * dx + dy * dy <= radius * radius)
                            covered++;
                    }
                uint alpha = (uint)(covered * 255 / 16);
                uint r = ((rgb >> 16) & 0xFF) * alpha / 255, g = ((rgb >> 8) & 0xFF) * alpha / 255, b = (rgb & 0xFF) * alpha / 255;
                pixels[y * size + x] = (int)(alpha << 24 | r << 16 | g << 8 | b);
            }
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        _dots[dot] = bitmap;
        return bitmap;
    }

    private void Execute(int command)
    {
        switch (command)
        {
            case CmdOpen:
                OpenRequested?.Invoke();
                break;
            case CmdCheckAll:
                CheckAllRequested?.Invoke();
                break;
            case CmdRefreshAll:
                RefreshAllRequested?.Invoke();
                break;
            case CmdLock:
                LockToggled?.Invoke();
                break;
            case CmdInstallUpdate:
                InstallUpdateRequested?.Invoke();
                break;
            case CmdExit:
                ExitRequested?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        Notify(NIM_DELETE, 0);
        DestroyWindow(_window);
        UnregisterClass(WindowClass, _module);
        if (_icon != IntPtr.Zero)
            DestroyIcon(_icon);
        foreach (IntPtr bitmap in _dots.Values)
            DeleteObject(bitmap);
    }
}
