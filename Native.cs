using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace MeuPlayer;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public Rect Bounds, Work; public uint Flags; }
    internal record Display(nint Handle, Rect Bounds, Rect Work, bool Primary);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(long point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    private delegate bool MonitorCallback(nint monitor, nint dc, nint bounds, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, nint name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, nint name);

    internal static (int X, int Y)? WindowPosition(nint window) =>
        GetWindowRect(window, out var rect) ? (rect.Left, rect.Top) : null;
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out uint value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetFrameBounds(nint window, int attribute, out Rect value, int size);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint window, int index, int style);

    internal static (Rect Bounds, Rect Work)? MainScreen()
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(MonitorFromPoint(0, 1), ref info) ? (info.Bounds, info.Work) : null;
    }

    internal static System.Collections.Generic.List<Display> Displays()
    {
        var displays = new System.Collections.Generic.List<Display>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
                displays.Add(new Display(monitor, info.Bounds, info.Work, (info.Flags & 1) != 0));
            return true;
        }, 0);
        return displays;
    }

    internal static nint TaskbarOn(nint monitor)
    {
        if (monitor == 0 || monitor == MonitorFromPoint(0, 1)) return FindWindow("Shell_TrayWnd", 0);
        nint bar = 0;
        while ((bar = FindWindowEx(0, bar, "Shell_SecondaryTrayWnd", 0)) != 0)
            if (MonitorFromWindow(bar, 2) == monitor) return bar;
        return 0;
    }

    internal static bool FullScreenOn(nint ownWindow, Rect screen) =>
        IsFullScreenWindow(GetForegroundWindow(), ownWindow, screen);

    private static bool IsFullScreenWindow(nint foreground, nint ownWindow, Rect screen)
    {
        if (foreground == 0 || foreground == ownWindow || foreground == GetShellWindow() ||
            !IsWindowVisible(foreground) || IsIconic(foreground) ||
            (DwmGetWindowAttribute(foreground, 14, out uint cloaked, sizeof(uint)) == 0 && cloaked != 0)) return false;
        bool covers = GetWindowRect(foreground, out var rect) && CoversMonitor(rect, screen);
        // Visible frame bounds avoid invisible resize borders and use physical pixels.
        covers |= DwmGetFrameBounds(foreground, 9, out var frame, Marshal.SizeOf<Rect>()) == 0 && CoversMonitor(frame, screen);
        return covers && !IsShellSurface(foreground);
    }

    internal static bool CoversMonitor(Rect window, Rect screen)
    {
        const int tolerance = 2;
        return window.Right > window.Left && window.Bottom > window.Top &&
            screen.Right > screen.Left && screen.Bottom > screen.Top &&
            (long)window.Left <= (long)screen.Left + tolerance && (long)window.Top <= (long)screen.Top + tolerance &&
            (long)window.Right >= (long)screen.Right - tolerance && (long)window.Bottom >= (long)screen.Bottom - tolerance;
    }

    private static bool IsShellSurface(nint window)
    {
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or
            "DV2ControlHost" or "ImmersiveLauncher") return true;

        return IsStartSurface(window);
    }

    private static bool IsStartSurface(nint window)
    {
        if (window == 0) return false;

        GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return IsShellProcess(process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool IsShellProcess(string processName) => processName.ToUpperInvariant() is
        "STARTMENUEXPERIENCEHOST" or "SHELLEXPERIENCEHOST" or "SHELLHOST" or "SEARCHHOST" or "SEARCHAPP" or "SEARCHUI";

    private static bool LeftAligned()
    {
        try
        {
            object? alignment = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAl", null);
            if (alignment is int value) return value == 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException) { }
        return Environment.OSVersion.Version.Build < 22000;
    }

    internal static (int X, int Y) Dock(Rect screen, Rect work, int width, int height, double scale, nint taskbar)
    {
        int margin = (int)(8 * scale);
        if (work.Bottom < screen.Bottom)
        {
            int x = screen.Left + margin;
            if (LeftAligned())
            {
                nint tray = taskbar == 0 ? 0 : FindWindowEx(taskbar, 0, "TrayNotifyWnd", 0);
                if (tray != 0 && GetWindowRect(tray, out var rect) && rect.Left >= screen.Left + width + margin)
                    x = rect.Left - width - margin;
                else if (taskbar != 0 && taskbar != FindWindow("Shell_TrayWnd", 0))
                    x = Math.Max(x, screen.Right - width - (int)(125 * scale));
                else
                    return (x, work.Bottom - height - margin);
            }
            return (x, work.Bottom + (screen.Bottom - work.Bottom - height) / 2);
        }

        if (work.Top > screen.Top)
            return (screen.Left + margin, screen.Top + (work.Top - screen.Top - height) / 2);
        if (work.Left > screen.Left) return (work.Left + margin, screen.Bottom - height - margin);
        if (work.Right < screen.Right) return (work.Right - width - margin, screen.Bottom - height - margin);
        return (work.Left + margin, work.Bottom - height - margin);
    }

    internal static void KeepDockVisible(nint window, Rect screen, Rect work, int width, int height, double scale, nint monitor, TaskbarSpace.Slot? slot = null, bool right = false, bool behind = false)
    {
        nint taskbar = TaskbarOn(monitor);
        var (x, y) = Dock(screen, work, width, height, scale, taskbar);
        if (right) x = screen.Right - width - (int)(8 * scale);
        if (slot is { } available) x = available.X;
        var (safeX, safeY) = InsideWorkArea(work, x, y, width, height, Math.Max(2, (int)(4 * scale)));
        if (behind)
        {
            PresentDockBehind(window, safeX, safeY, GetForegroundWindow());
            return;
        }
        const int cloakedAttribute = 14;
        nint foreground = GetForegroundWindow();
        bool foregroundIsVisible = IsWindowVisible(foreground) &&
            (DwmGetWindowAttribute(foreground, cloakedAttribute, out uint cloaked, sizeof(uint)) != 0 || cloaked == 0);
        if (foregroundIsVisible && IsStartSurface(foreground) &&
            MonitorFromWindow(foreground, 2) == (monitor == 0 ? MonitorFromPoint(0, 1) : monitor))
        {
            PresentDocked(window, safeX, safeY);
            return;
        }

        PresentDocked(window, slot is null ? safeX : x, slot is null ? safeY : y);
        if (taskbar != 0 && CoveredByWindow(window, taskbar))
            PresentDocked(window, safeX, safeY);
    }

    private static (int X, int Y) InsideWorkArea(Rect work, int x, int y, int width, int height, int margin)
    {
        int left = work.Left + margin;
        int top = work.Top + margin;
        return (
            Math.Clamp(x, left, Math.Max(left, work.Right - width - margin)),
            Math.Clamp(y, top, Math.Max(top, work.Bottom - height - margin)));
    }

    private static bool CoveredByWindow(nint window, nint coveringWindow)
    {
        if (!GetWindowRect(window, out var rect)) return false;
        var point = new Point { X = rect.Left + (rect.Right - rect.Left) / 2, Y = rect.Top + (rect.Bottom - rect.Top) / 2 };
        nint hit = WindowFromPoint(point);
        return hit == coveringWindow || (hit != 0 && GetAncestor(hit, 2) == coveringWindow);
    }

    internal static void SetToolWindow(nint window)
    {
        const int extendedStyle = -20;
        const int toolWindow = 0x80;
        SetWindowLong(window, extendedStyle, GetWindowLong(window, extendedStyle) | toolWindow);
    }

    internal static void PresentDocked(nint window, int x, int y)
    {
        const int showNormalWithoutActivation = 4;
        const uint noSize = 0x0001, noActivate = 0x0010, showWindow = 0x0040;
        if (IsIconic(window)) ShowWindow(window, showNormalWithoutActivation);
        SetWindowPos(window, -1, x, y, 0, 0, noSize | noActivate | showWindow);
    }

    internal static void KeepBehind(nint window) => SetWindowPos(window, 1, 0, 0, 0, 0, 0x13);

    internal static void BringPopupForward(nint window)
    {
        if (window != 0 && IsWindowVisible(window))
            SetWindowPos(window, -1, 0, 0, 0, 0, 0x13); // TOPMOST, NOSIZE, NOMOVE, NOACTIVATE
    }

    internal static void PresentDockBehind(nint window, int x, int y, nint foreground)
    {
        const uint flags = 0x0001 | 0x0010 | 0x0040; // NOSIZE, NOACTIVATE, SHOWWINDOW
        if (IsIconic(window)) ShowWindow(window, 4);
        // Demote first: inserting after a topmost foreground would promote us again.
        if ((GetWindowLong(window, -20) & 8) != 0)
            SetWindowPos(window, -2, x, y, 0, 0, flags);
        if (foreground != 0 && foreground != window && IsWindowVisible(foreground) &&
            !IsIconic(foreground) && !IsShellSurface(foreground))
        {
            if ((GetWindowLong(foreground, -20) & 8) == 0)
            {
                SetWindowPos(window, foreground, x, y, 0, 0, flags);
                return;
            }
        }
        SetWindowPos(window, 0, x, y, 0, 0, flags | 0x0004); // preserve normal Z-order
    }

    internal static void Move(nint window, int x, int y) => SetWindowPos(window, 0, x, y, 0, 0, 0x15);
}















