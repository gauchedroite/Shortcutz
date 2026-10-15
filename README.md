# Shortcutz

A lightweight Windows desktop tool for organizing shortcuts, files, and notes
on a grid of tabbed boards. Stays on top of all windows so your shortcuts are
always one click away.

Built with .NET 10 Windows Forms.

## Features

- **Tabbed boards** — organize shortcuts across multiple tabs. Rename, delete,
  and drag-reorder tabs. Right-click a tab for its context menu.
- **Drag-and-drop anything** — drop files, folders, URLs onto a board. URLs fetch their page title and favicon
  automatically.
- **Desktop-style multi-select** — rubber-band select, group-drag, and delete
  multiple items at once.
- **Notes** — drop text notes on a board. Resize the note width with the grip,
  double-click to edit, Shift+Enter for line breaks.
- **Zoom** — Ctrl + `+` / `-` to zoom in/out, Ctrl + `0` to reset. Per-tab zoom
  is persisted.
- **Compact icons** — select one or more icons and press Ctrl + `M` to toggle their compact layout.
- **Open in Notepad++** — Shift+double-click a file item to open it in
  Notepad++ regardless of its extension.
- **Always on top** — the window stays above other apps.
- **Grid dots** — optional visual alignment grid (right-click workspace).

## Shortcuts

| Action | Shortcut |
|---|---|
| Rename selected item / tab | Double-click, or `F2` |
| Delete selected items | `Delete` |
| Search all content | `Ctrl+F` |
| Toggle compact layout for selected icons | `Ctrl+M` |
| Reload state from disk | `Ctrl+R` |
| Zoom in / out | `Ctrl` + `+` / `Ctrl` + `-` |
| Reset zoom | `Ctrl` + `0` |
| Open file in Notepad++ | Shift + double-click |
| Edit note | Double-click |
| New tab right of selected | Right-click tab → *Add tab* |

## State

State is persisted as JSON by default under:

```
%LocalAppData%\Shortcutz\state.json
```

Right-click an empty board and choose **Switch state file...** to load another
`.json` state file. Changes are saved to the active file, and the last selected
file is used the next time the app starts. When 7-Zip is
installed, compressed backups are stored beside each active state file. If a
state file cannot be loaded, a `.corrupt` copy is saved beside it.

## Build & run

Requires the .NET 10 SDK and Windows.

```bash
dotnet build
cmd //c "start bin/Debug/net10.0-windows/Shortcutz.exe"
```

Rebuild and restart (kills any running instance first):

```bash
cmd //c "taskkill /F /IM Shortcutz.exe >nul 2>&1"
dotnet build
cmd //c "start bin/Debug/net10.0-windows/Shortcutz.exe"
```

Quick restart only (no build):

```bash
cmd //c "taskkill /F /IM Shortcutz.exe >nul 2>&1 & start bin/Debug/net10.0-windows/Shortcutz.exe"
```
