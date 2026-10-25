using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shortcutz;

public partial class Form1
{
    // ---------- persistence ----------

    private void SwitchStateFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Switch state file",
            Filter = "JSON state files (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(_stateFile),
            FileName = Path.GetFileName(_stateFile)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var selectedFile = Path.GetFullPath(dialog.FileName);
        if (string.Equals(selectedFile, Path.GetFullPath(_stateFile), StringComparison.OrdinalIgnoreCase)) return;

        BeginInvoke(new Action(() => SwitchToStateFile(selectedFile)));
    }

    private void LoadState()
    {
        DiagnosticLog.Write($"LOAD-STATE file={Path.GetFullPath(_stateFile)}");
        UpdateWindowTitle();
        if (!File.Exists(_stateFile))
        {
            AddTab("Board");
            UpdateWindowTitle();
            return;
        }

        try
        {
            var json = File.ReadAllText(_stateFile);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var state = JsonSerializer.Deserialize<AppState>(json, options)
                ?? new AppState(new List<TabState>());

            if (state.Tabs != null)
            {
                foreach (var t in state.Tabs ?? new List<TabState>())
                {
                    var tab = new Tab(t.Name);
                    if (t.Pages is { Count: > 0 })
                    {
                        foreach (var p in t.Pages)
                        {
                            var legacyCompactIcons = p.CompactIcons ?? state.CompactIcons ?? false;
                            var page = new Page(p.Name) { Zoom = p.Zoom ?? 1.0f, Type = p.Type ?? "Board", Text = p.Text ?? "", Monospace = p.Monospace ?? false, MarkdownPreview = p.MarkdownPreview ?? false };
                            foreach (var it in p.Items ?? new List<ItemState>())
                            {
                                Item item = it.IsNote
                                    ? new NoteItem(it.Text ?? "", Math.Max(0, it.X), Math.Max(0, it.Y), it.Width ?? NoteItem.DefaultWidth, it.Color)
                                    : new IconItem(it.Path, Math.Max(0, it.X), Math.Max(0, it.Y), it.Label, it.Color) { Compact = it.Compact ?? legacyCompactIcons };
                                page.Items.Add(item);
                            }
                            tab.Pages.Add(page);
                        }
                    }
                    else if (t.LegacyItems is { Count: > 0 })
                    {
                        var page = new Page(t.Name) { Zoom = t.LegacyZoom ?? 1.0f, Type = "Board", Text = "" };
                        foreach (var it in t.LegacyItems)
                        {
                            Item item = it.IsNote
                                ? new NoteItem(it.Text ?? "", Math.Max(0, it.X), Math.Max(0, it.Y), it.Width ?? NoteItem.DefaultWidth, it.Color)
                                : new IconItem(it.Path, Math.Max(0, it.X), Math.Max(0, it.Y), it.Label, it.Color) { Compact = it.Compact ?? state.CompactIcons ?? false };
                            page.Items.Add(item);
                        }
                        tab.Pages.Add(page);
                    }
                    else
                    {
                        tab.Pages.Add(new Page("Page 1"));
                    }
                    tab.SelectedPageIndex = Math.Clamp(t.SelectedPageIndex ?? 0, 0, tab.Pages.Count - 1);
                    _board.Tabs.Add(tab);
                }
            }

            _board.ShowGridDots = state.ShowGridDots ?? false;
            _showGridDots = _board.ShowGridDots;

            _suppressPopulate = true;
            foreach (var tab in _board.Tabs)
                CreateTabPage(tab);
            _suppressPopulate = false;

            if (tabs.TabPages.Count == 0)
                AddTab("Board");
            else if (tabs.TabCount > 0)
                tabs.SelectedIndex = Math.Clamp(state.SelectedTabIndex, 0, tabs.TabCount - 1);

            PopulateSubPage(SelectedSubPage);

            if (SelectedWorkspace() is Panel workspace)
                ApplyZoom(workspace);

            if (state.Window is not null && !_hasGlobalWindowState && _isInitialStateLoad)
                ApplyWindowState(state.Window);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteException($"Loading state {_stateFile}", ex);
            // Write log to app directory. If this fails, the app will crash,
            // which is preferable to silent failure while debugging.
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(_stateFile)!, "error.log"), ex.ToString());
            try { File.Copy(_stateFile, _stateFile + ".corrupt", overwrite: true); } catch { }
            MessageBox.Show(
                $"The state file was corrupt and could not be loaded.\nA backup was saved as state.json.corrupt.\n\n{ex.Message}",
                "Load failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _board.Tabs.Clear();
            tabs.TabPages.Clear();
            AddTab("Board");
        }
        UpdateWindowTitle();
    }

    private void RefreshStateFileButtons()
    {
        stateStrip.Items.Clear();
        var homeButton = new ToolStripButton("Shortcutz")
        {
            Alignment = ToolStripItemAlignment.Left,
            DisplayStyle = ToolStripItemDisplayStyle.Text
        };
        homeButton.Click += (_, _) => HideWebView();
        stateStrip.Items.Add(homeButton);
        var webButton = new ToolStripButton("Web")
        {
            Alignment = ToolStripItemAlignment.Left,
            DisplayStyle = ToolStripItemDisplayStyle.Text
        };
        webButton.Click += (_, _) => ShowWebView();
        stateStrip.Items.Add(webButton);
        var printButton = new ToolStripButton("Print")
        {
            Alignment = ToolStripItemAlignment.Left,
            DisplayStyle = ToolStripItemDisplayStyle.Text
        };
        printButton.Click += (_, _) => PrintCurrentPage();
        stateStrip.Items.Add(printButton);
        // shortcut: very long file lists can overflow this row; use a wrapping strip if needed.
        // shortcut: external JSON files appear after a state switch or restart; add a watcher for immediate discovery.
        var files = Directory.GetFiles(_stateDirectory, "*.json")
            .Where(path => !string.Equals(Path.GetFullPath(path), _appSettingsFile, StringComparison.OrdinalIgnoreCase))
            .Append(Path.Combine(_stateDirectory, "state.json"))
            .Append(_stateFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
            .Reverse()
            .Select(Path.GetFullPath)
            // shortcut: duplicate basenames cannot be ordered separately; use unique filenames when an external state file shares a name.
            .OrderByDescending(path =>
            {
                var index = _stateFileOrder.FindIndex(saved => string.Equals(saved, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
                return index < 0 ? int.MaxValue : index;
            });
        foreach (var path in files)
        {
            var button = new ToolStripButton(Path.GetFileName(path))
            {
                Alignment = ToolStripItemAlignment.Right,
                Checked = string.Equals(path, _stateFile, StringComparison.OrdinalIgnoreCase),
                DisplayStyle = ToolStripItemDisplayStyle.Text
            };
            button.Click += (_, _) => BeginInvoke(new Action(() => SwitchToStateFile(path)));
            stateStrip.Items.Add(button);
        }
    }

    private void SwitchToStateFile(string targetFile, bool recordHistory = true)
    {
        targetFile = Path.GetFullPath(targetFile);
        HideWebView();
        if (string.Equals(targetFile, _stateFile, StringComparison.OrdinalIgnoreCase)) return;
        DiagnosticLog.Write($"STATE-SWITCH from={_stateFile} to={targetFile}");
        try { SaveState(); }
        catch (Exception ex)
        {
            DiagnosticLog.WriteException($"Saving state before switch to {targetFile}", ex);
            MessageBox.Show($"Could not save the current state.\n\n{ex.Message}",
                "State switch failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _tabHistoryByStateFile[_stateFile] = (_tabHistoryBack, _tabHistoryForward);
        if (recordHistory)
        {
            _stateFileHistoryBack.Push(_stateFile);
            _stateFileHistoryForward.Clear();
            _tabHistoryForward.Clear();
        }
        _stateFile = targetFile;
        RefreshStateFileButtons();
        try { SaveAppSettings(); }
        catch (Exception ex)
        {
            DiagnosticLog.WriteException($"Saving settings after switch to {targetFile}", ex);
            MessageBox.Show($"Could not remember the selected state file.\n\n{ex.Message}",
                "State file preference failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        ReloadState();
        if (_tabHistoryByStateFile.Remove(_stateFile, out var tabHistory))
        {
            _tabHistoryBack = tabHistory.Back;
            _tabHistoryForward = tabHistory.Forward;
        }
    }

    private void LoadAppSettings()
    {
        if (!File.Exists(_appSettingsFile)) return;
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_appSettingsFile));
            if (settings is null) return;
            _hasGlobalWindowState = true;
            var lastStateFile = Path.GetFileName(settings.LastStateFile);
            if (!string.IsNullOrEmpty(lastStateFile) && File.Exists(Path.Combine(_stateDirectory, lastStateFile)))
                _stateFile = Path.Combine(_stateDirectory, lastStateFile);
            _stateFileOrder = (settings.StateFileOrder ?? new List<string>())
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .ToList();
            ApplyWindowState(new WindowState(settings.X, settings.Y, settings.Width, settings.Height));
        }
        catch { }
    }


    private void ApplyWindowState(WindowState state)
    {
        StartPosition = FormStartPosition.Manual;
        var w = Math.Max(MinimumSize.Width, state.Width);
        var h = Math.Max(MinimumSize.Height, state.Height);
        var screen = Screen.AllScreens.FirstOrDefault(s => s.Bounds.Contains(state.X, state.Y))
                     ?? Screen.PrimaryScreen!;
        var area = screen.WorkingArea;
        Location = new Point(Math.Clamp(state.X, area.Left, area.Right - 80),
            Math.Clamp(state.Y, area.Top, area.Bottom - 40));
        Size = new Size(w, h);
    }

    private void SaveAppSettings()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        var settings = new AppSettings(bounds.X, bounds.Y, bounds.Width, bounds.Height, _stateFileOrder, Path.GetFileName(_stateFile));
        File.WriteAllText(_appSettingsFile, JsonSerializer.Serialize(settings));
        foreach (var preferenceFile in new[] { "_last-state.txt", "last-state.txt" })
        {
            try { File.Delete(Path.Combine(_stateDirectory, preferenceFile)); } catch { }
        }
    }

    private void SaveState()
    {
        _board.SelectedIndex = tabs.SelectedIndex;
        _board.ShowGridDots = _showGridDots;
        for (int i = 0; i < _board.Tabs.Count && i < tabs.TabPages.Count; i++)
        {
            var subTabs = SubTabsFromPage(tabs.TabPages[i]);
            _board.Tabs[i].SelectedPageIndex = subTabs.SelectedIndex;
        }
        var options = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        var json = JsonSerializer.Serialize(_board.ToState(), options);
        File.WriteAllText(_stateFile, json);
        SaveAppSettings();
        ArchiveStateBackup(json);
    }


    private void ArchiveStateBackup(string json)
    {
        // ponytail: archive backups to state.json.7z via 7z; skip silently if 7z missing
        if (_sevenZip is null) return;
        try
        {
            var name = "state_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".json";
            var psi = new ProcessStartInfo(_sevenZip, $"a \"{_stateFile}.7z\" -si\"{name}\"")
            {
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return;
            p.StandardInput.Write(json);
            p.StandardInput.Close();
            p.WaitForExit(5000);
        }
        catch { }
    }

    private static string? Find7z()
    {
        foreach (var p in new[]
        {
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
        })
            if (File.Exists(p)) return p;
        return null;
    }

    private static string? FindNotepadPlus()
    {
        foreach (var p in new[]
        {
            @"C:\Program Files\Notepad++\notepad++.exe",
            @"C:\Program Files (x86)\Notepad++\notepad++.exe",
        })
            if (File.Exists(p)) return p;
        return null;
    }

    private static string? FindReadme()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDataDirName, "README.md");
        return File.Exists(path) ? path : null;
    }

    private static string? FindChrome()
    {
        foreach (var basePath in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        })
        {
            var chrome = Path.Combine(basePath, "Google", "Chrome", "Application", "chrome.exe");
            if (File.Exists(chrome)) return chrome;
        }
        return null;
    }
}
