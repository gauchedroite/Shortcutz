# Shortcutz

Shortcutz is a Windows desktop app for shortcuts, files, notes, and Outlook messages. Press `Ctrl+T` to toggle whether its window stays above other apps.

Built with .NET 10 Windows Forms.

## Features

- Boards and pages. Each top-level tab contains one or more pages. Pages can hold board items or plain text.
- State files. Buttons for all board JSON files in `%LocalAppData%\Shortcutz` appear at the top-right; click one to switch files. Choose a file elsewhere from the empty-board context menu.
- Tabs and pages. Add, rename, delete, drag them to reorder, or right-click a page tab and choose **Move to** to place it under another tab.
- Drag and drop. Add files, folders, and URLs to a board. URLs show their page title and favicon when available.
- Outlook messages. Drop one or more messages onto a board. Shortcutz saves each message as a `.msg` file in `%LocalAppData%\Shortcutz\outlook`. The state file stores only the path to each saved message.
- Notes. Double-click an empty part of a board to add a note. Drag notes to move them. Use the grip to change their width. Double-click a note to edit it. Press `Shift+Enter` to add a line break while editing.
- Text pages. Add a text page from the page-tab menu. Press `F5` or right-click its tab to toggle Markdown preview. Preview supports headings, lists, quotes, code, links, and inline emphasis. The monospace option applies in Edit mode only. See [Markdown in Shortcutz](#markdown-in-shortcutz) for supported syntax and limits.
- Notes render Markdown; double-click to edit the source. Icon labels support bold with `**text**`.
- Multi-select. Drag on an empty part of a board to select items. Move, copy, cut, or delete selected items together.
- Search. Search tab names, page names, notes, text pages, item labels, and file paths.
- Zoom. Each page keeps its own zoom level.
- Compact icons. Select icons and press `Ctrl+M` to change their layout.
- Notepad++. Hold `Shift` and double-click a file item to open it in Notepad++.
- Grid dots. Right-click an empty part of a board to show or hide the grid.

## Markdown in Shortcutz

Shortcutz supports a basic set of Markdown formats. Text pages show the source in Edit mode and formatted text in Preview mode. Press `F5` to switch modes. You can also right-click the page tab and select **Markdown preview**. The preview mode is saved with the page.

Notes always show formatted text. Double-click a note to edit its Markdown source. Icon labels support bold text only.

### Supported formats

| Format | Example |
|---|---|
| Heading | `## Heading` |
| Bullet list | `- Item` |
| Nested bullet | Indent a child `- Item` by two spaces |
| Numbered list | `1. Item` |
| Quote | `> Quoted text` |
| Horizontal rule | `---` |
| Bold | `**bold**` or `__bold__` |
| Italic | `*italic*` or `_italic_` |
| Inline code | `` `code` `` |
| Code block | Put `` ``` `` on a line before and after the code |
| Strikethrough | `~~text~~` |
| Link | `[label](https://example.com)` or `[file](file:///~/Documents/file.txt)` |

Clicking an absolute link URI passes it to Windows to open with its registered handler. In `file:` links, `~/` expands to the current user's profile folder. Relative links are not opened.

Icon labels support only `**bold**`. Other Markdown marks appear as typed.

### Limits

This is not a full CommonMark parser. Nested bullet lists support one level only; nested ordered lists and nested formatting are not supported. Tables and images are not rendered. Raw HTML and Markdown escape sequences are not interpreted. Put a blank line between paragraphs. Line breaks inside a paragraph display as spaces.

## Shortcuts

| Action | Shortcut |
|---|---|
| Save state | `Ctrl+S` |
| Toggle always on top | `Ctrl+T` |
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
| Toggle Markdown preview on a text page | `F5` |
| Open a file in Notepad++ | `Shift` + double-click |
| Open this README | `F1` |

## State

Shortcutz stores board data in a JSON file. The default file is:

```text
%LocalAppData%\Shortcutz\state.json
```

Shortcutz saves state when you press `Ctrl+S`, close the app, or switch state files. Right-click an empty board and choose **Switch state file...** to use another JSON file. Shortcutz saves the current state before it switches files.

The state stores paths to files and folders. Shortcutz does not copy those items, except for dropped `.msg` files. It saves those files in `%LocalAppData%\Shortcutz\outlook` and stores their paths in the state. Removing a board item does not delete its source file or its saved `.msg` file.

Shortcutz stores the last selected state file, button order, and shared window position and size in `%LocalAppData%\Shortcutz\_window-state.json`. `LastStateFile` contains only the file name and resolves under `%LocalAppData%\Shortcutz`; files selected from elsewhere cannot be remembered across restarts. With Shortcutz closed, set the button order by editing `StateFileOrder` in `%LocalAppData%\Shortcutz\_window-state.json`; list file names only, in the desired order (for example, `state.json`, `work.json`). Unlisted files use the default order. If 7-Zip is installed, Shortcutz stores compressed backups beside the active state file. If a state file cannot load, Shortcutz saves a `.corrupt` copy beside it.

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
