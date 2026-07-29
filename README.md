# Linux Window Drag

Windows 10/11 tray app for Linux-style window manipulation:

- **Modifier + left-drag**: move window
- **Modifier + middle-drag**: resize from closest corner, anchored at opposite corner

The app ignores desktop/taskbar/tool windows.

## Tray menu

Right-click the tray icon:

- **Modifier Key**
  - `alt`
  - `windows key`
  - `cntrl`
  - persists across restarts
- **Run at Windows Startup** (toggle)
- **Open Log Window**
- **Open Log Folder**
- **About** (shows version)
- **Exit**

## Why this implementation looks the way it does

Several simpler approaches were attempted first and failed on real systems:

1. **`WM_SYSCOMMAND` / `SC_MOVE` and `WM_NCLBUTTONDOWN` simulation**
   - Expected: native window move loop.
   - Problem: unreliable initiation across apps/windows; sometimes no move at all.

2. **Direct `SetWindowPos` from hook events (anchor-from-start)**
   - Expected: deterministic movement.
   - Problem: jitter/snap-back due to event ordering and OS interaction.

3. **Low-level mouse hook with Alt flag only**
   - Problem: modifier detection inconsistency (`LLMHF_ALTDOWN` not always reliable).

4. **Physical cursor APIs + DPI scaling variants**
   - Problem: on some machines this caused under-movement, zero movement, or oscillation.

5. **Aggressive Win-key event swallowing for Start-menu suppression**
   - Problem: could leave Windows key in a "stuck" state for keyboard input.

### What works reliably now

- Mouse hook starts/stops action only.
- A polling loop (`GetCursorPos`) drives movement/resizing at steady intervals.
- Win-key Start-menu suppression is handled without leaving Win-key state stuck.
- Modifier is user-selectable from tray menu and saved in user settings.

## Build

```powershell
dotnet build -c Release
```

## Publish

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

Published EXE:

```text
bin\Release\net9.0-windows\win-x64\publish\LinuxWindowDrag.exe
```
