using System.Diagnostics;
using System.Drawing;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace LinuxWindowDrag;

internal sealed class LinuxDragApplicationContext : ApplicationContext
{
    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkLMenu = 0xA4;
    private const int VkRMenu = 0xA5;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int GaRoot = 2;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int ResizeMinWidth = 120;
    private const int ResizeMinHeight = 80;
    private const string StartupRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupRunValueName = "LinuxWindowDrag";

    private enum DragMode
    {
        None,
        Move,
        Resize,
    }

    private enum ResizeCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    private enum ModifierKey
    {
        Alt,
        WindowsKey,
        Cntrl,
    }

    // Static storage so hook stays alive for the lifetime of the app
    private static NativeMethods.LowLevelMouseProc? s_mouseProc;
    private static NativeMethods.LowLevelKeyboardProc? s_keyboardProc;
    private static IntPtr s_mouseHook;
    private static IntPtr s_keyboardHook;
    private static DebugForm? s_debugForm;
    private static string? s_logPath;
    
    private readonly NativeMethods.LowLevelMouseProc _mouseProc;
    private readonly NativeMethods.LowLevelKeyboardProc _keyboardProc;
    private readonly Icon _trayAppIcon;
    private readonly NotifyIcon _trayIcon;
    private readonly DebugForm _debugForm;
    private readonly string _logPath;
    private readonly System.Threading.Timer _dragPollTimer;
    private readonly object _dragStateLock = new();
    private DragMode _dragMode = DragMode.None;
    private IntPtr _dragWindow = IntPtr.Zero;
    private Point _dragCursorOffset = Point.Empty;
    private Point _resizeAnchor = Point.Empty;
    private Point _resizeCursorOffset = Point.Empty;
    private ResizeCorner _resizeCorner = ResizeCorner.TopLeft;
    private bool _ignoreMovesUntilNextDown;
    private bool _suppressNextWinKeyUp;
    private long _lastPollHeartbeatTick;
    private ModifierKey _modifierKey = ModifierKey.WindowsKey;
    private ToolStripMenuItem? _modifierAltMenuItem;
    private ToolStripMenuItem? _modifierWindowsMenuItem;
    private ToolStripMenuItem? _modifierCntrlMenuItem;
    private ToolStripMenuItem? _runAtStartupMenuItem;
    
    internal LinuxDragApplicationContext()
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LinuxWindowDrag");
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, "linux-window-drag.log");
        s_logPath = _logPath;

        _debugForm = new DebugForm();
        s_debugForm = _debugForm;

        _debugForm.FormClosing += DebugForm_FormClosing;

        // Keep the debug window hidden by default; open it from tray menu on demand.
        _ = _debugForm.Handle;
        _debugForm.BeginInvoke((MethodInvoker)InstallHooks);

        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;
        _trayAppIcon = GetExecutableIcon();

        _trayIcon = new NotifyIcon
        {
            Icon = _trayAppIcon,
            Text = "Linux Window Drag",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        _dragPollTimer = new System.Threading.Timer(
            PollDragMove,
            null,
            System.Threading.Timeout.Infinite,
            System.Threading.Timeout.Infinite);

        Log("Application started. Left-drag moves, middle-drag resizes (with selected modifier).");
        Log($"Log file: {_logPath}");
    }

    private void InstallHooks()
    {
        // This fires after the message pump has started.
        Log("Initializing hooks.");
        
        try
        {
            s_mouseProc = MouseHookCallback;
            s_mouseHook = NativeMethods.SetHook(s_mouseProc, WhMouseLl);
            s_keyboardProc = KeyboardHookCallback;
            s_keyboardHook = NativeMethods.SetKeyboardHook(s_keyboardProc, WhKeyboardLl);
             
            if (s_mouseHook == IntPtr.Zero)
            {
                Log("ERROR: SetHook returned zero");
            }
            else
            {
                Log($"Mouse hook installed: 0x{s_mouseHook:X}");
                Log($"Keyboard hook installed: 0x{s_keyboardHook:X}");
            }
        }
        catch (Exception ex)
        {
            Log($"ERROR installing hook: {ex.Message}");
        }
    }

    private void DebugForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            _debugForm.Hide();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Log("Application stopping.");
            _dragPollTimer.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayAppIcon.Dispose();
            _debugForm?.Dispose();
        }

        if (s_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(s_mouseHook);
            s_mouseHook = IntPtr.Zero;
        }
        if (s_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(s_keyboardHook);
            s_keyboardHook = IntPtr.Zero;
        }

        base.Dispose(disposing);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var modifierMenu = new ToolStripMenuItem("Modifier Key");

        _modifierAltMenuItem = new ToolStripMenuItem("alt");
        _modifierAltMenuItem.Click += (_, _) => SetModifierKey(ModifierKey.Alt);
        modifierMenu.DropDownItems.Add(_modifierAltMenuItem);

        _modifierWindowsMenuItem = new ToolStripMenuItem("windows key");
        _modifierWindowsMenuItem.Click += (_, _) => SetModifierKey(ModifierKey.WindowsKey);
        modifierMenu.DropDownItems.Add(_modifierWindowsMenuItem);

        _modifierCntrlMenuItem = new ToolStripMenuItem("cntrl");
        _modifierCntrlMenuItem.Click += (_, _) => SetModifierKey(ModifierKey.Cntrl);
        modifierMenu.DropDownItems.Add(_modifierCntrlMenuItem);

        menu.Items.Add(modifierMenu);
        menu.Items.Add(new ToolStripSeparator());
        _runAtStartupMenuItem = new ToolStripMenuItem("Run at Windows Startup");
        _runAtStartupMenuItem.Click += (_, _) => ToggleRunAtStartup();
        menu.Items.Add(_runAtStartupMenuItem);
        menu.Items.Add("Open Log Window", null, (_, _) => OpenLogWindow());
        menu.Items.Add("Open Log Folder", null, (_, _) => OpenLogFolder());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        UpdateModifierMenuChecks();
        UpdateRunAtStartupMenuCheck();
        return menu;
    }

    private IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code < 0)
            {
                return NativeMethods.CallNextHookEx(s_mouseHook, code, wParam, lParam);
            }

            var hookInfo = Marshal.PtrToStructure<NativeMethods.MsllHookStruct>(lParam);
            var msgType = (int)wParam;
            var cursorPoint = GetCursorPoint(hookInfo.Point);
            var modifierPressed = IsModifierPressed();

            if (msgType != WmLButtonDown &&
                msgType != WmLButtonUp &&
                msgType != WmMButtonDown &&
                msgType != WmMButtonUp)
            {
                // Never swallow non-button messages; doing so can freeze cursor updates.
                return NativeMethods.CallNextHookEx(s_mouseHook, code, wParam, lParam);
            }

            lock (_dragStateLock)
            {
                if ((msgType == WmLButtonUp || msgType == WmMButtonUp) && _dragWindow != IntPtr.Zero)
                {
                    var endedMode = _dragMode;
                    ResetDragState();
                    _ignoreMovesUntilNextDown = true;
                    Log(endedMode == DragMode.Resize ? "    ✓ Resize ended" : "    ✓ Drag ended");
                    return (IntPtr)1;
                }

                // Modifier+left or modifier+middle down starts action.
                if (msgType == WmLButtonDown || msgType == WmMButtonDown)
                {
                    if (!modifierPressed)
                    {
                        return NativeMethods.CallNextHookEx(s_mouseHook, code, wParam, lParam);
                    }

                    _ignoreMovesUntilNextDown = false;
                    s_debugForm?.ClearLog();
                    Log($"LEFT_DOWN cursor=({cursorPoint.X},{cursorPoint.Y}) modifier={_modifierKey}");
                    var window = NativeMethods.WindowFromPoint(cursorPoint);
                    Log($"    Window under cursor: 0x{window:X}");
                    if (IsValidMoveTarget(window))
                    {
                        var rootWindow = NativeMethods.GetAncestor(window, GaRoot);
                        if (NativeMethods.IsZoomed(rootWindow))
                        {
                            _ = NativeMethods.ShowWindow(rootWindow, 9); // SW_RESTORE
                        }
                        if (!NativeMethods.GetWindowRect(rootWindow, out var rect))
                        {
                            Log($"    ERROR: GetWindowRect failed for 0x{rootWindow:X}");
                            return NativeMethods.CallNextHookEx(s_mouseHook, code, wParam, lParam);
                        }

                        _dragWindow = rootWindow;
                        _suppressNextWinKeyUp = _modifierKey == ModifierKey.WindowsKey;
                        if (msgType == WmLButtonDown)
                        {
                            _dragMode = DragMode.Move;
                            _dragCursorOffset = new Point(rect.Left - cursorPoint.X, rect.Top - cursorPoint.Y);
                        }
                        else
                        {
                            _dragMode = DragMode.Resize;
                            InitializeResizeState(rect, cursorPoint);
                        }

                        _dragPollTimer.Change(0, 8);
                        if (_dragMode == DragMode.Move)
                        {
                            Log($"    ✓ Drag started window=0x{_dragWindow:X} startCursor=({cursorPoint.X},{cursorPoint.Y}) startWindow=({rect.Left},{rect.Top}) offset=({_dragCursorOffset.X},{_dragCursorOffset.Y})");
                        }
                        else
                        {
                            Log($"    ✓ Resize started window=0x{_dragWindow:X} cursor=({cursorPoint.X},{cursorPoint.Y}) rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) corner={_resizeCorner} anchor=({_resizeAnchor.X},{_resizeAnchor.Y})");
                        }
                        return (IntPtr)1;
                    }
                    else
                    {
                        Log("    ✗ Not a valid move target");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"  ERROR in hook: {ex.Message}");
        }

        return NativeMethods.CallNextHookEx(s_mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code < 0)
            {
                return NativeMethods.CallNextHookEx(s_keyboardHook, code, wParam, lParam);
            }

            if (_modifierKey != ModifierKey.WindowsKey)
            {
                return NativeMethods.CallNextHookEx(s_keyboardHook, code, wParam, lParam);
            }

            var msgType = (int)wParam;
            if (msgType != WmKeyDown && msgType != WmKeyUp && msgType != WmSysKeyDown && msgType != WmSysKeyUp)
            {
                return NativeMethods.CallNextHookEx(s_keyboardHook, code, wParam, lParam);
            }

            var keyInfo = Marshal.PtrToStructure<NativeMethods.KbdllHookStruct>(lParam);
            var vk = keyInfo.VkCode;
            if (vk != VkLWin && vk != VkRWin)
            {
                return NativeMethods.CallNextHookEx(s_keyboardHook, code, wParam, lParam);
            }

            lock (_dragStateLock)
            {
                if (_suppressNextWinKeyUp && (msgType == WmKeyUp || msgType == WmSysKeyUp))
                {
                    _suppressNextWinKeyUp = false;
                    NativeMethods.TapControlKey();
                    Log("    Neutralized Win key up to prevent Start menu.");
                    return NativeMethods.CallNextHookEx(s_keyboardHook, code, wParam, lParam);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"  ERROR in keyboard hook: {ex.Message}");
        }

        return NativeMethods.CallNextHookEx(s_keyboardHook, code, wParam, lParam);
    }

    private void PollDragMove(object? _)
    {
        lock (_dragStateLock)
        {
            if (_dragWindow == IntPtr.Zero || _ignoreMovesUntilNextDown)
            {
                return;
            }

            if (!NativeMethods.TryGetCursorPos(out var cursorPoint))
            {
                Log("    ERROR: GetCursorPos failed during drag poll.");
                return;
            }

            if (!NativeMethods.GetWindowRect(_dragWindow, out var rect))
            {
                Log("    ERROR: GetWindowRect failed during drag poll.");
                ResetDragState();
                _ignoreMovesUntilNextDown = true;
                return;
            }

            if (_dragMode == DragMode.Move)
            {
                var newX = cursorPoint.X + _dragCursorOffset.X;
                var newY = cursorPoint.Y + _dragCursorOffset.Y;
                if (rect.Left == newX && rect.Top == newY)
                {
                    var nowNoMove = Environment.TickCount64;
                    if (nowNoMove - _lastPollHeartbeatTick >= 250)
                    {
                        _lastPollHeartbeatTick = nowNoMove;
                        Log($"    POLL no-move cursor=({cursorPoint.X},{cursorPoint.Y}) rect=({rect.Left},{rect.Top})");
                    }
                    return;
                }

                _ = NativeMethods.SetWindowPos(_dragWindow, IntPtr.Zero, newX, newY, 0, 0, 0x0001);
                Log($"    MOVE poll cursor=({cursorPoint.X},{cursorPoint.Y}) rect=({rect.Left},{rect.Top}) -> new=({newX},{newY})");
                return;
            }

            if (_dragMode == DragMode.Resize)
            {
                var dragPoint = new Point(cursorPoint.X + _resizeCursorOffset.X, cursorPoint.Y + _resizeCursorOffset.Y);
                var newRect = BuildResizedRect(dragPoint);
                var newWidth = newRect.Width;
                var newHeight = newRect.Height;
                if (newWidth < ResizeMinWidth || newHeight < ResizeMinHeight)
                {
                    return;
                }

                var currentWidth = rect.Right - rect.Left;
                var currentHeight = rect.Bottom - rect.Top;
                if (rect.Left == newRect.Left && rect.Top == newRect.Top && currentWidth == newWidth && currentHeight == newHeight)
                {
                    return;
                }

                _ = NativeMethods.SetWindowPos(_dragWindow, IntPtr.Zero, newRect.Left, newRect.Top, newWidth, newHeight, 0x0004);
                Log($"    RESIZE poll cursor=({cursorPoint.X},{cursorPoint.Y}) drag=({dragPoint.X},{dragPoint.Y}) rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) -> new=({newRect.Left},{newRect.Top},{newRect.Right},{newRect.Bottom})");
            }
        }
    }

    private void ResetDragState()
    {
        _dragMode = DragMode.None;
        _dragWindow = IntPtr.Zero;
        _dragCursorOffset = Point.Empty;
        _resizeAnchor = Point.Empty;
        _resizeCursorOffset = Point.Empty;
        _resizeCorner = ResizeCorner.TopLeft;
        _lastPollHeartbeatTick = 0;
        _dragPollTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    private void InitializeResizeState(NativeMethods.Rect rect, Point cursorPoint)
    {
        var topLeft = new Point(rect.Left, rect.Top);
        var topRight = new Point(rect.Right, rect.Top);
        var bottomLeft = new Point(rect.Left, rect.Bottom);
        var bottomRight = new Point(rect.Right, rect.Bottom);

        var closest = topLeft;
        _resizeCorner = ResizeCorner.TopLeft;
        var minDist = DistanceSquared(cursorPoint, topLeft);

        var topRightDist = DistanceSquared(cursorPoint, topRight);
        if (topRightDist < minDist)
        {
            minDist = topRightDist;
            closest = topRight;
            _resizeCorner = ResizeCorner.TopRight;
        }

        var bottomLeftDist = DistanceSquared(cursorPoint, bottomLeft);
        if (bottomLeftDist < minDist)
        {
            minDist = bottomLeftDist;
            closest = bottomLeft;
            _resizeCorner = ResizeCorner.BottomLeft;
        }

        var bottomRightDist = DistanceSquared(cursorPoint, bottomRight);
        if (bottomRightDist < minDist)
        {
            closest = bottomRight;
            _resizeCorner = ResizeCorner.BottomRight;
        }

        _resizeCursorOffset = new Point(closest.X - cursorPoint.X, closest.Y - cursorPoint.Y);
        _resizeAnchor = _resizeCorner switch
        {
            ResizeCorner.TopLeft => bottomRight,
            ResizeCorner.TopRight => bottomLeft,
            ResizeCorner.BottomLeft => topRight,
            ResizeCorner.BottomRight => topLeft,
            _ => bottomRight,
        };
    }

    private Rectangle BuildResizedRect(Point dragPoint)
    {
        var left = Math.Min(dragPoint.X, _resizeAnchor.X);
        var right = Math.Max(dragPoint.X, _resizeAnchor.X);
        var top = Math.Min(dragPoint.Y, _resizeAnchor.Y);
        var bottom = Math.Max(dragPoint.Y, _resizeAnchor.Y);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static long DistanceSquared(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return ((long)dx * dx) + ((long)dy * dy);
    }

    private bool IsValidMoveTarget(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return false;
        }

        var rootWindow = NativeMethods.GetAncestor(window, GaRoot);
        if (rootWindow == IntPtr.Zero || NativeMethods.IsOwnedByCurrentProcess(rootWindow))
        {
            return false;
        }

        if (!NativeMethods.IsWindowVisible(rootWindow) || NativeMethods.IsIconic(rootWindow))
        {
            return false;
        }

        var className = NativeMethods.GetClassName(rootWindow);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd")
        {
            return false;
        }

        var style = NativeMethods.GetWindowLong(rootWindow, GwlExStyle);
        if ((style & WsExToolWindow) == WsExToolWindow)
        {
            return false;
        }

        return true;
    }

    private void OpenLogFolder()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.GetDirectoryName(_logPath)!,
            UseShellExecute = true,
        });
    }

    private void ToggleRunAtStartup()
    {
        try
        {
            var enabled = IsRunAtStartupEnabled();
            if (enabled)
            {
                DisableRunAtStartup();
                Log("Run at startup disabled.");
            }
            else
            {
                EnableRunAtStartup();
                Log("Run at startup enabled.");
            }

            UpdateRunAtStartupMenuCheck();
        }
        catch (UnauthorizedAccessException ex)
        {
            Log($"ERROR toggling startup setting: {ex.Message}");
            MessageBox.Show("Unable to update startup setting due to permissions.", "Linux Window Drag", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (InvalidOperationException ex)
        {
            Log($"ERROR toggling startup setting: {ex.Message}");
            MessageBox.Show("Unable to determine executable path for startup setting.", "Linux Window Drag", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool IsRunAtStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRunKeyPath, false);
        var configuredValue = key?.GetValue(StartupRunValueName) as string;
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return false;
        }

        var expectedPath = GetStartupExecutablePath();
        var normalizedConfigured = configuredValue.Trim().Trim('"');
        return string.Equals(normalizedConfigured, expectedPath, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnableRunAtStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupRunKeyPath, true)
            ?? throw new InvalidOperationException("Unable to open startup registry key.");
        var executablePath = GetStartupExecutablePath();
        key.SetValue(StartupRunValueName, $"\"{executablePath}\"", RegistryValueKind.String);
    }

    private static void DisableRunAtStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRunKeyPath, true);
        key?.DeleteValue(StartupRunValueName, false);
    }

    private static string GetStartupExecutablePath()
    {
        return Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to determine executable path.");
    }

    private void OpenLogWindow()
    {
        if (!_debugForm.Visible)
        {
            _debugForm.Show();
        }

        _debugForm.WindowState = FormWindowState.Normal;
        _debugForm.BringToFront();
        _debugForm.Activate();
    }

    private void UpdateRunAtStartupMenuCheck()
    {
        if (_runAtStartupMenuItem != null)
        {
            _runAtStartupMenuItem.Checked = IsRunAtStartupEnabled();
        }
    }

    private static Icon GetExecutableIcon()
    {
        var exePath = GetStartupExecutablePath();
        var associatedIcon = Icon.ExtractAssociatedIcon(exePath);
        if (associatedIcon != null)
        {
            return (Icon)associatedIcon.Clone();
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private void Log(string message)
    {
        File.AppendAllText(_logPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        // Also log to debug window if it exists
        if (s_debugForm != null)
        {
            s_debugForm.Log(message);
        }
    }

    private bool IsModifierPressed()
    {
        return _modifierKey switch
        {
            ModifierKey.Alt => IsKeyDown(VkLMenu, VkRMenu),
            ModifierKey.WindowsKey => IsKeyDown(VkLWin, VkRWin),
            ModifierKey.Cntrl => IsKeyDown(VkLControl, VkRControl),
            _ => false,
        };
    }

    private static bool IsKeyDown(int leftKey, int rightKey)
    {
        short left = NativeMethods.GetAsyncKeyState(leftKey);
        short right = NativeMethods.GetAsyncKeyState(rightKey);
        return (left & 0x8000) != 0 || (right & 0x8000) != 0;
    }

    private void SetModifierKey(ModifierKey modifierKey)
    {
        if (_modifierKey == modifierKey)
        {
            return;
        }

        _modifierKey = modifierKey;
        _suppressNextWinKeyUp = false;
        UpdateModifierMenuChecks();
        Log($"Modifier key set to {modifierKey}");
    }

    private void UpdateModifierMenuChecks()
    {
        if (_modifierAltMenuItem != null)
        {
            _modifierAltMenuItem.Checked = _modifierKey == ModifierKey.Alt;
        }

        if (_modifierWindowsMenuItem != null)
        {
            _modifierWindowsMenuItem.Checked = _modifierKey == ModifierKey.WindowsKey;
        }

        if (_modifierCntrlMenuItem != null)
        {
            _modifierCntrlMenuItem.Checked = _modifierKey == ModifierKey.Cntrl;
        }
    }

    private static Point GetCursorPoint(Point fallback)
    {
        return NativeMethods.TryGetCursorPos(out var cursorPoint) ? cursorPoint : fallback;
    }
}
