using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace LinuxWindowDrag;

internal static class NativeMethods
{
    internal const int WmNcLButtonDown = 0x00A1;
    internal const int WmSysCommand = 0x0112;
    private const int IdcHand = 32649;
    private const int IdcSizeAll = 32646;
    private const uint OcrNormal = 32512;
    private const uint SpiSetcursors = 0x0057;

    internal delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);
    internal delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointStruct
    {
        internal int X;
        internal int Y;

        internal Point ToPoint() => new(X, Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MsllHookStruct
    {
        internal PointStruct Pt;
        internal int MouseData;
        internal int Flags;
        internal int Time;
        internal nint DwExtraInfo;

        internal Point Point => Pt.ToPoint();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KbdllHookStruct
    {
        internal int VkCode;
        internal int ScanCode;
        internal int Flags;
        internal int Time;
        internal nint DwExtraInfo;
    }

    internal static IntPtr SetHook(LowLevelMouseProc proc, int hookId)
    {
        using var currentProcess = Environment.ProcessId != 0
            ? System.Diagnostics.Process.GetCurrentProcess()
            : throw new InvalidOperationException("Unable to access the current process.");
        using var currentModule = currentProcess.MainModule
            ?? throw new InvalidOperationException("Unable to access the current module.");

        var moduleHandle = GetModuleHandle(currentModule.ModuleName);
        var hook = SetWindowsHookEx(hookId, proc, moduleHandle, 0);
        if (hook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to install the Windows hook.");
        }

        return hook;
    }

    internal static IntPtr SetKeyboardHook(LowLevelKeyboardProc proc, int hookId)
    {
        using var currentProcess = Environment.ProcessId != 0
            ? System.Diagnostics.Process.GetCurrentProcess()
            : throw new InvalidOperationException("Unable to access the current process.");
        using var currentModule = currentProcess.MainModule
            ?? throw new InvalidOperationException("Unable to access the current module.");

        var moduleHandle = GetModuleHandle(currentModule.ModuleName);
        var hook = SetWindowsHookExKeyboard(hookId, proc, moduleHandle, 0);
        if (hook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to install the keyboard hook.");
        }

        return hook;
    }

    internal static string GetClassName(IntPtr window)
    {
        var builder = new StringBuilder(256);
        _ = GetClassName(window, builder, builder.Capacity);
        return builder.ToString();
    }

    internal static IntPtr MakeLParam(Point point)
    {
        var x = point.X & 0xFFFF;
        var y = point.Y & 0xFFFF;
        return (IntPtr)(x | (y << 16));
    }

    internal static bool IsOwnedByCurrentProcess(IntPtr window)
    {
        _ = GetWindowThreadProcessId(window, out var processId);
        return processId == Environment.ProcessId;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern short GetAsyncKeyState(int vKey);

    private const int KeyeventfKeyup = 0x0002;
    private const byte VkControl = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointStruct lpPoint);

    internal static bool TryGetCursorPos(out Point point)
    {
        if (GetCursorPos(out var cursor))
        {
            point = cursor.ToPoint();
            return true;
        }

        point = Point.Empty;
        return false;
    }

    internal static void TapControlKey()
    {
        keybd_event(VkControl, 0, 0, UIntPtr.Zero);
        keybd_event(VkControl, 0, KeyeventfKeyup, UIntPtr.Zero);
    }

    // Replace the system arrow cursor with the hand cursor so the change
    // persists across WM_SETCURSOR resets from windows under the pointer.
    internal static void SetSystemHandCursor()
    {
        ReplaceSystemArrow(IdcHand);
    }

    // Replace the system arrow cursor with the 4-directional resize cursor.
    internal static void SetSystemResizeCursor()
    {
        ReplaceSystemArrow(IdcSizeAll);
    }

    private static void ReplaceSystemArrow(int idcCursor)
    {
        var cursor = LoadCursor(IntPtr.Zero, (IntPtr)idcCursor);
        if (cursor == IntPtr.Zero) return;
        // SetSystemCursor takes ownership of its argument, so pass a copy.
        var copy = CopyIcon(cursor);
        if (copy != IntPtr.Zero)
        {
            SetSystemCursor(copy, OcrNormal);
        }
    }

    // Restore all system cursors to their defaults.
    internal static void RestoreSystemCursor()
    {
        SystemParametersInfo(SpiSetcursors, 0, IntPtr.Zero, 0);
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookEx")]
    private static extern IntPtr SetWindowsHookExKeyboard(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll")]
    private static extern IntPtr CopyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);
}
