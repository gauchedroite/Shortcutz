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

    private void LoadState()
    {
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
                    var tab = new Tab(t.Name) { Zoom = t.Zoom ?? 1.0f };
                    foreach (var it in t.Items ?? new List<ItemState>())
                    {
                        Item item = it.IsNote
                            ? new NoteItem(it.Text ?? "", Math.Max(0, it.X), Math.Max(0, it.Y), it.Width ?? NoteItem.DefaultWidth, it.Color)
                            : new IconItem(it.Path, Math.Max(0, it.X), Math.Max(0, it.Y), it.Label);
                        tab.Items.Add(item);
                    }
                    _board.Tabs.Add(tab);
                }
            }

            _board.ShowGridDots = state.ShowGridDots ?? false;
            _showGridDots = _board.ShowGridDots;

            foreach (var tab in _board.Tabs)
                CreateTabPage(tab);

            if (tabs.TabPages.Count == 0)
                AddTab("Board");
            else if (tabs.TabCount > 0)
                tabs.SelectedIndex = Math.Clamp(state.SelectedTabIndex, 0, tabs.TabCount - 1);

            if (tabs.SelectedTab is TabPage page)
                ApplyZoom(WorkspaceFromPage(page));

            if (state.Window is not null)
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(state.Window.X, state.Window.Y);
                Size = new Size(
                    Math.Max(MinimumSize.Width, state.Window.Width),
                    Math.Max(MinimumSize.Height, state.Window.Height));
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
        var window = new WindowState(Location.X, Location.Y, Size.Width, Size.Height);
        var json = JsonSerializer.Serialize(_board.ToState(window));
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
            var psi = new ProcessStartInfo(_sevenZip, $"a \"{_stateArchive}\" -si\"{name}\"")
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
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var p = Path.Combine(dir, "README.md");
            if (File.Exists(p)) return p;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
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
