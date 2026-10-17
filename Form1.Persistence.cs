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

        SaveState();
        _stateFile = selectedFile;
        try { File.WriteAllText(_lastStateFilePath, _stateFile); }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not remember the selected state file.\n\n{ex.Message}",
                "State file preference failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        ReloadState();
    }

    private void LoadState()
    {
        Text = $"Shortcutz - {Path.GetFileName(_stateFile)}";
        if (!File.Exists(_stateFile))
        {
            AddTab("Board");
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

            if (state.Window is not null)
            {
                StartPosition = FormStartPosition.Manual;
                var w = Math.Max(MinimumSize.Width, state.Window.Width);
                var h = Math.Max(MinimumSize.Height, state.Window.Height);
                // Clamp onto a real screen: a saved position from a now-disconnected
                // monitor would otherwise strand the window off-screen (taskbar icon only).
                var screen = Screen.AllScreens.FirstOrDefault(s => s.Bounds.Contains(state.Window.X, state.Window.Y))
                             ?? Screen.PrimaryScreen!;
                var area = screen.WorkingArea;
                var x = Math.Clamp(state.Window.X, area.Left, area.Right - 80);
                var y = Math.Clamp(state.Window.Y, area.Top, area.Bottom - 40);
                Location = new Point(x, y);
                Size = new Size(w, h);
            }
        }
        catch (Exception ex)
        {
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
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        var window = new WindowState(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        var options = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        var json = JsonSerializer.Serialize(_board.ToState(window), options);
        File.WriteAllText(_stateFile, json);
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
