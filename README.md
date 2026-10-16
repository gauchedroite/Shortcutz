# Shortcutz

Shortcutz is a Windows desktop app for shortcuts, files, notes, and Outlook messages. It keeps its window above other apps.

Built with .NET 10 Windows Forms.

## Features

- Boards and pages. Each top-level tab contains one or more pages. Pages can hold board items or plain text.
- State file switching. Choose another JSON board file from the empty-board context menu.
- Tabs and pages. Add, rename, delete, and drag them to reorder. Right-click a tab to see its actions.
- Drag and drop. Add files, folders, and URLs to a board. URLs show their page title and favicon when available.
- Outlook messages. Drop one or more messages onto a board. Shortcutz saves each message as a `.msg` file in `%LocalAppData%\Shortcutz\outlook`. The state file stores only the path to each saved message.
- Notes. Double-click an empty part of a board to add a note. Drag notes to move them. Use the grip to change their width. Double-click a note to edit it. Press `Shift+Enter` to add a line break while editing.
- Text pages. Add a text page from the page-tab menu.
- Multi-select. Drag on an empty part of a board to select items. Move, copy, cut, or delete selected items together.
- Search. Search tab names, page names, notes, text pages, item labels, and file paths.
- Zoom. Each page keeps its own zoom level.
- Compact icons. Select icons and press `Ctrl+M` to change their layout.
- Notepad++. Hold `Shift` and double-click a file item to open it in Notepad++.
- Grid dots. Right-click an empty part of a board to show or hide the grid.

## Shortcuts

| Action | Shortcut |
|---|---|
| Save state | `Ctrl+S` |
| Search | `Ctrl+F` |
| Move through search results | `Up` / `Down` |
| Go to the selected result or close search | `Enter` / `Escape` |
| Rename a selected item | `F2` |
| Rename a tab or page | Double-click its tab |
| Add a note | Double-click an empty part of a board |
| Edit a note | Double-click the note |
| Copy, cut, or paste selected items | `Ctrl+C`, `Ctrl+X`, `Ctrl+V` |
| Remove selected items from the board | `Delete` |
| Toggle compact layout | `Ctrl+M` |
| Zoom in or out | `Ctrl` + `+` / `Ctrl` + `-` |
| Reset zoom | `Ctrl` + `0` |
| Move through tab and page history | `Ctrl` + `Left` / `Ctrl` + `Right` |
| Reload state from disk | `Ctrl+R` |
| Open a file in Notepad++ | `Shift` + double-click |
| Open this README | `F1` |

## State

Shortcutz stores board data in a JSON file. The default file is:

```text
%LocalAppData%\Shortcutz\state.json
```

Shortcutz saves state when you press `Ctrl+S`, close the app, or switch state files. Right-click an empty board and choose **Switch state file...** to use another JSON file. Shortcutz saves the current state before it switches files.

The state stores paths to files and folders. Shortcutz does not copy those items, except for dropped `.msg` files. It saves those files in `%LocalAppData%\Shortcutz\outlook` and stores their paths in the state. Removing a board item does not delete its source file or its saved `.msg` file.

Shortcutz uses the last selected state file when it starts. If 7-Zip is installed, it stores compressed backups beside the active state file. If a state file cannot load, Shortcutz saves a `.corrupt` copy beside it.

## Build and run

Requires the .NET 10 SDK and Windows. Each build copies `README.md` to `%LocalAppData%\Shortcutz\README.md`. Press `F1` in the app to open that copy.

```bash
dotnet build
cmd //c "start bin/Debug/net10.0-windows/Shortcutz.exe"
```

To rebuild and restart, stop the running app first:

```bash
cmd //c "taskkill /F /IM Shortcutz.exe >nul 2>&1"
dotnet build
cmd //c "start bin/Debug/net10.0-windows/Shortcutz.exe"
```

To restart without a build:

```bash
cmd //c "taskkill /F /IM Shortcutz.exe >nul 2>&1 & start bin/Debug/net10.0-windows/Shortcutz.exe"
```
