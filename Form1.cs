using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DropFolders;

public partial class Form1 : Form
{
    private static readonly Font TitleFont = new("Segoe UI", 10);
    private const int GridSize = 40;
    // EditControl: break on hyphens and hard-break words longer than the line.
    // Label paints with its own flags (WordBreak only), so titles are drawn by hand below.
    private const TextFormatFlags TitleFlags =
        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl |
        TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private const string AppDataDirName = "Shortcutz";

    private readonly string _stateFile;
    private readonly string _stateBak;
    private readonly ContextMenuStrip _tabMenu;
    private readonly TabControl tabs;
    private readonly Board _board = new();

    public Form1()
    {
        tabs = new TabControl { Dock = DockStyle.Fill };
        SuspendLayout();
        Controls.Add(tabs);
        ClientSize = new Size(1000, 650);
        Text = "Shortcutz";
        ResumeLayout(false);

        _tabMenu = new ContextMenuStrip();
        _tabMenu.Items.Add("Add tab", null, (s, e) => { AddTab("New tab"); _board.Dirty(); });
        _tabMenu.Items.Add("Rename", null, RenameTab);
        _tabMenu.Items.Add("Close", null, CloseTab);
        tabs.MouseUp += Tabs_MouseUp;

        _stateFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDataDirName, "state.json");
        _stateBak = _stateFile + ".bak";
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);

        LoadState();
        if (tabs.TabPages.Count == 0)
            AddTab("Board");

        _board.Changed += SaveState;
        FormClosing += (s, e) => SaveState();
        KeyPreview = true;
        KeyDown += Form1_KeyDown;
    }

    private void Form1_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.R && e.Control)
        {
            e.Handled = true;
            ReloadState();
            return;
        }
        if (e.KeyCode == Keys.Delete)
        {
            e.Handled = true;
            if (tabs.SelectedTab is TabPage page)
                DeleteSelectedIcons(WorkspaceFromPage(page));
        }
    }

    private void ReloadState()
    {
        _board.Changed -= SaveState;
        try
        {
            foreach (TabPage page in tabs.TabPages.Cast<TabPage>().ToList())
            {
                var workspace = WorkspaceFromPage(page);
                foreach (Control c in workspace.Controls) DisposeItemControl(c);
            }
            tabs.TabPages.Clear();
            _board.Tabs.Clear();
            LoadState();
        }
        finally
        {
            _board.Changed += SaveState;
        }
    }

    // ---------- tabs ----------

    private Tab AddTab(string name)
    {
        var tab = new Tab(name);
        _board.Tabs.Add(tab);
        CreateTabPage(tab);
        return tab;
    }

    private TabPage CreateTabPage(Tab tab)
    {
        var page = new TabPage(tab.Name);
        var workspace = new Panel
        {
            Dock = DockStyle.Fill,
            AllowDrop = true,
            BackColor = Color.FromArgb(240, 240, 240)
        };
        workspace.DragEnter += Workspace_DragEnter;
        workspace.DragDrop += Workspace_DragDrop;
        workspace.MouseDown += Workspace_MouseDown;
        workspace.MouseMove += Workspace_MouseMove;
        workspace.MouseUp += Workspace_MouseUp;
        workspace.Paint += Workspace_Paint;
        workspace.MouseDoubleClick += Workspace_MouseDoubleClick;
        SetDoubleBuffered(workspace);
        page.Controls.Add(workspace);
        page.Tag = tab;

        foreach (var item in tab.Items)
            CreateView(workspace, item);

        tabs.TabPages.Add(page);
        tabs.SelectedTab = page;
        return page;
    }

    private static Panel WorkspaceFromPage(TabPage page) => (Panel)page.Controls[0];
    private static Tab TabFromPage(TabPage page) => (Tab)page.Tag!;
    private static Tab TabFromSelected(TabControl tc) => (Tab)tc.SelectedTab!.Tag!;
    private static bool ItemExists(string path) => Directory.Exists(path) || File.Exists(path) || IsUrl(path);

    private static string? ExtractUrlTitle(IDataObject data, string url)
    {
        if (data.GetDataPresent(DataFormats.Html))
        {
            var html = ((string?)data.GetData(DataFormats.Html)) ?? "";
            var htmlIdx = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
            if (htmlIdx >= 0) html = html[htmlIdx..];
            var title = ExtractTitleFromHtml(html);
            if (title is not null) return title;
            var link = Regex.Match(html, @"<a[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (link.Success)
            {
                var text = WebUtility.HtmlDecode(link.Groups[1].Value.Trim());
                if (!string.IsNullOrWhiteSpace(text) && !string.Equals(text, url, StringComparison.OrdinalIgnoreCase))
                    return text;
            }
        }
        foreach (var fmt in new[] { "text/x-moz-url", "text/x-moz-url-desc", "chromium/x-page-title" })
        {
            if (!data.GetDataPresent(fmt)) continue;
            var raw = data.GetData(fmt)?.ToString() ?? "";
            var lines = raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length >= 2)
            {
                var candidate = lines[1].Trim();
                if (!string.IsNullOrWhiteSpace(candidate) && !IsUrl(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private static string? ExtractTitleFromHtml(string html)
    {
        var match = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value)) return null;
        return WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
    }

    private static bool IsUrl(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("www.", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeUrl(string url) =>
        url.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url
            : url;

    private void Workspace_DragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data is null) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop) ?? Array.Empty<string>();
            if (files.Any(ItemExists))
                e.Effect = DragDropEffects.Link;
        }
        else if (e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.Html))
        {
            var text = (string?)e.Data.GetData(DataFormats.Text) ?? "";
            if (IsUrl(text))
                e.Effect = DragDropEffects.Link;
        }
    }

    private void Workspace_DragDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Panel workspace || e.Data is null) return;
        var dropPoint = workspace.PointToClient(new Point(e.X, e.Y));
        var tab = TabFromSelected(tabs);

        int i = 0;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop) ?? Array.Empty<string>();
            foreach (var path in files.Where(ItemExists))
            {
                var loc = SnapToGrid(workspace, new Size(110, 90),
                    new Point(dropPoint.X + i % 3 * GridSize, dropPoint.Y + i / 3 * GridSize));
                var item = new IconItem(path, loc.X, loc.Y, null);
                tab.Items.Add(item);
                CreateIconView(workspace, item);
                i++;
            }
        }
        else if (e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.Html))
        {
            var text = (string?)e.Data.GetData(DataFormats.Text) ?? "";
            if (IsUrl(text))
            {
                var url = NormalizeUrl(text);
                var label = ExtractUrlTitle(e.Data, url);
                var loc = SnapToGrid(workspace, new Size(110, 90),
                    new Point(dropPoint.X + i % 3 * GridSize, dropPoint.Y + i / 3 * GridSize));
                var item = new IconItem(url, loc.X, loc.Y, label);
                tab.Items.Add(item);
                var p = CreateIconView(workspace, item);
                if (label is null && p.Controls[1] is Label titleLabel)
                    _ = FetchUrlTitleAsync(url, item, titleLabel, p);
            }
        }
        _board.Dirty();
    }

    private bool _selecting;
    private Point _selStart;
    private Rectangle _selRect;

    private void Workspace_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.Clicks != 1 || sender is not Panel workspace) return;
        ClearHighlights(workspace);
        _selecting = true;
        _selStart = e.Location;
        _selRect = Rectangle.Empty;
        workspace.Invalidate();
    }

    private void Workspace_MouseMove(object? sender, MouseEventArgs e)
    {
        if (!_selecting || e.Button != MouseButtons.Left || sender is not Panel workspace) return;
        var old = _selRect;
        _selRect = SelectionRect(_selStart, e.Location);
        if (old.Width > 0 && old.Height > 0) workspace.Invalidate(old);
        if (_selRect.Width > 0 && _selRect.Height > 0) workspace.Invalidate(_selRect);
        workspace.Update();
    }

    private void Workspace_MouseUp(object? sender, MouseEventArgs e)
    {
        if (!_selecting || e.Button != MouseButtons.Left || sender is not Panel workspace) return;
        _selecting = false;
        if (_selRect.Width > 0 && _selRect.Height > 0) workspace.Invalidate(_selRect);
        if (_selRect.Width > 3 && _selRect.Height > 3)
            SelectIcons(workspace, _selRect);
    }

    private void Workspace_Paint(object? sender, PaintEventArgs e)
    {
        if (!_selecting || _selRect.Width <= 0 || _selRect.Height <= 0) return;
        var c = SystemColors.Highlight;
        using var fill = new SolidBrush(Color.FromArgb(32, c.R, c.G, c.B));
        using var pen = new Pen(c, 1);
        e.Graphics.FillRectangle(fill, _selRect);
        e.Graphics.DrawRectangle(pen, _selRect.X, _selRect.Y, _selRect.Width - 1, _selRect.Height - 1);
    }

    private static Rectangle SelectionRect(Point a, Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private static void SelectIcons(Panel workspace, Rectangle rect)
    {
        foreach (var panel in workspace.Controls.OfType<Panel>().Where(p => p.Tag is IconItem))
        {
            if (!rect.IntersectsWith(panel.Bounds)) continue;
            var c = SystemColors.Highlight;
            panel.BackColor = Color.FromArgb(26, c.R, c.G, c.B);
            if (panel.Controls.Count > 1 && panel.Controls[1] is Label title)
                title.ForeColor = SystemColors.WindowText;
        }
    }



    private static void ClearHighlights(Panel workspace)
    {
        foreach (var panel in workspace.Controls.OfType<Panel>())
        {
            panel.BackColor = Color.Transparent;
            if (panel.Controls.Count > 1 && panel.Controls[1] is Label title)
                title.ForeColor = SystemColors.WindowText;
        }
    }

    private static void SelectIcon(Panel workspace, Panel panel, Label title)
    {
        ClearHighlights(workspace);
        var c = SystemColors.Highlight;
        panel.BackColor = Color.FromArgb(26, c.R, c.G, c.B);
        title.ForeColor = SystemColors.WindowText;
    }

    private static void ToggleSelection(Panel panel, Label title)
    {
        bool isSelected = panel.BackColor != Color.Transparent;
        if (isSelected)
        {
            panel.BackColor = Color.Transparent;
        }
        else
        {
            var c = SystemColors.Highlight;
            panel.BackColor = Color.FromArgb(26, c.R, c.G, c.B);
        }
        title.ForeColor = SystemColors.WindowText;
    }

    private void Workspace_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (sender is not Panel workspace) return;
        if (workspace.GetChildAtPoint(e.Location) != null) return;
        var text = Prompt("New note", "Enter note text:", "");
        if (string.IsNullOrWhiteSpace(text)) return;
        var tab = TabFromSelected(tabs);
        var loc = Clamp(workspace, new Size(120, 40), e.Location);
        var item = new NoteItem(text, loc.X, loc.Y);
        tab.Items.Add(item);
        CreateNoteView(workspace, item);
        _board.Dirty();
    }

    private void Tabs_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        for (int i = 0; i < tabs.TabPages.Count; i++)
            if (tabs.GetTabRect(i).Contains(e.Location))
            {
                _tabMenu.Show(tabs, e.Location);
                return;
            }
    }

    private void RenameTab(object? sender, EventArgs e)
    {
        if (tabs.SelectedTab is null) return;
        var tab = TabFromPage(tabs.SelectedTab);
        var name = Prompt("Rename tab", "Tab name:", tab.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        tab.Name = name;
        tabs.SelectedTab.Text = name;
        _board.Dirty();
    }

    private void CloseTab(object? sender, EventArgs e)
    {
        if (tabs.TabPages.Count <= 1)
        {
            MessageBox.Show("You must keep at least one tab.", "Cannot close", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (tabs.SelectedTab is null) return;
        var page = tabs.SelectedTab;
        var tab = TabFromPage(page!);
        if (MessageBox.Show(
                $"Close tab '{tab.Name}'? All items on it will be removed.",
                "Close tab?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var workspace = WorkspaceFromPage(page);
        foreach (Control c in workspace.Controls) DisposeItemControl(c);
        _board.Tabs.Remove(tab);
        tabs.TabPages.Remove(page);
        _board.Dirty();
    }

    // ---------- item views ----------

    private void CreateView(Panel workspace, Item item)
    {
        switch (item)
        {
            case IconItem ii:
                CreateIconView(workspace, ii);
                break;
            case NoteItem ni:
                CreateNoteView(workspace, ni);
                break;
        }
    }

    // Shared drag logic for any draggable control (and its child subcontrols that
    // should all act as one draggable region, e.g. icon panel + icon + title).
    private static void WireDrag(Control[] controls, Action<int, int> onDrag, Action onClick, Action<Control>? onDoubleClick = null, Action? onDragStart = null, Action? onDragEnd = null)
    {
        bool dragging = false;
        bool doubleClick = false;
        Point dragOffset = Point.Empty;
        foreach (var c in controls)
        {
            c.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (e.Clicks == 2)
                {
                    doubleClick = true;
                    onDoubleClick?.Invoke(c);
                    return;
                }
                doubleClick = false;
                dragging = false;
                dragOffset = e.Location;
                c.BringToFront();
                c.Capture = true;
            };
            c.MouseMove += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (!dragging && (Math.Abs(e.X - dragOffset.X) > 2 || Math.Abs(e.Y - dragOffset.Y) > 2))
                {
                    dragging = true;
                    onDragStart?.Invoke();
                }
                if (dragging) onDrag(e.X - dragOffset.X, e.Y - dragOffset.Y);
            };
            c.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                c.Capture = false;
                if (doubleClick) { doubleClick = false; return; }
                if (dragging) onDragEnd?.Invoke();
                else onClick();
                dragging = false;
            };
        }
    }

    private static Bitmap CaptureControl(Control c)
    {
        var bmp = new Bitmap(c.Width, c.Height);
        c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
        return bmp;
    }

    private static Bitmap ApplyOpacity(Bitmap source, float opacity)
    {
        var matrix = new ColorMatrix { Matrix33 = opacity };
        var attrs = new ImageAttributes();
        attrs.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
        var dest = new Bitmap(source.Width, source.Height);
        using (var g = Graphics.FromImage(dest))
        {
            g.DrawImage(source, new Rectangle(0, 0, dest.Width, dest.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attrs);
        }
        source.Dispose();
        return dest;
    }

    private static Label CreateDragGhost(Control source)
    {
        var ghost = new Label
        {
            Size = source.Size,
            BackColor = Color.Transparent,
            Image = ApplyOpacity(CaptureControl(source), 0.6f),
            Tag = "ghost"
        };
        return ghost;
    }

    private static void DisposeDragGhost(Label? ghost, Panel workspace)
    {
        if (ghost is null) return;
        workspace.Controls.Remove(ghost);
        if (ghost.Image is not null) { ghost.Image.Dispose(); ghost.Image = null; }
        ghost.Dispose();
    }

    private Panel CreateIconView(Panel workspace, IconItem item)
    {
        var fileName = Path.GetFileName(item.Path);
        var displayName = string.IsNullOrWhiteSpace(item.Label)
            ? (string.IsNullOrEmpty(fileName) ? item.Path : fileName)
            : item.Label;

        const int labelWidth = 110;
        var titleSize = TextRenderer.MeasureText(
            displayName, TitleFont,
            new Size(labelWidth, int.MaxValue),
            TitleFlags);
        var panelHeight = 6 + 42 + titleSize.Height + 6;

        var panel = new Panel
        {
            Size = new Size(labelWidth, panelHeight),
            Location = new Point(item.X, item.Y),
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
            Tag = item
        };
        SetDoubleBuffered(panel);

        var icon = new Label
        {
            AutoSize = false,
            Size = new Size(60, 42),
            Location = new Point((labelWidth - 60) / 2, 6),
            BackColor = Color.Transparent,
            ImageAlign = ContentAlignment.MiddleCenter,
            Text = ""
        };
        if (IsUrl(item.Path))
        {
            var cache = GetFaviconCachePath(item.Path);
            if (File.Exists(cache))
            {
                try { icon.Image = LoadFaviconBitmap(cache); } catch { }
            }
            if (icon.Image is null)
            {
                icon.Image = GetIconBitmap(item.Path, false); // chrome placeholder until favicon loads
                _ = FetchUrlFaviconAsync(item.Path, panel, icon);
            }
        }
        else
        {
            icon.Image = GetIconBitmap(item.Path, Directory.Exists(item.Path));
        }

        var title = new Label
        {
            AutoSize = false,
            Size = new Size(labelWidth, titleSize.Height),
            Location = new Point(0, 48),
            TextAlign = ContentAlignment.TopCenter,
            BackColor = Color.Transparent,
            ForeColor = SystemColors.WindowText,
            Text = "",
            Font = TitleFont,
            Tag = displayName
        };
        title.Paint += (s, e) =>
            TextRenderer.DrawText(e.Graphics, (string)title.Tag!, TitleFont,
                title.ClientRectangle, title.ForeColor, TitleFlags);

        panel.Controls.Add(icon);
        panel.Controls.Add(title);

        if (!ItemExists(item.Path))
            panel.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.Red, 2);
                e.Graphics.DrawRectangle(pen, 1, 1, panel.Width - 2, panel.Height - 2);
            };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Rename", null, (s, e) => RenameIcon(item, title, panel));
        menu.Items.Add("Delete", null, (s, e) =>
        {
            if (panel.BackColor != Color.Transparent)
                DeleteSelectedIcons(workspace);
            else
                DeleteIcon(workspace, item, panel);
        });
        panel.ContextMenuStrip = menu;

        List<(Panel p, IconItem i, Point s, Label g)>? dragGroup = null;
        WireDrag(new Control[] { panel, icon, title },
            onDragStart: () =>
            {
                // Only clear selection if this icon wasn't already selected
                if (panel.BackColor == Color.Transparent)
                    SelectIcon(workspace, panel, title);
                // Create ghosts for all selected icons and send originals behind them
                dragGroup = workspace.Controls.OfType<Panel>()
                    .Where(p => p.Tag is IconItem && p.BackColor != Color.Transparent)
                    .Select(p =>
                    {
                        var g = CreateDragGhost(p);
                        g.Location = p.Location;
                        workspace.Controls.Add(g);
                        g.BringToFront();
                        p.SendToBack();
                        return (p, (IconItem)p.Tag!, p.Location, g);
                    })
                    .ToList();
            },
            onDrag: (dx, dy) =>
            {
                if (dragGroup is null) return;
                foreach (var (_, _, gs, g) in dragGroup)
                    g.Location = Clamp(workspace, g.Size,
                        new Point(gs.X + dx, gs.Y + dy));
            },
            onClick: () =>
            {
                if (dragGroup is not null)
                    foreach (var (_, _, _, g) in dragGroup)
                        DisposeDragGhost(g, workspace);
                dragGroup = null;
                if ((Control.ModifierKeys & Keys.Control) != 0)
                    ToggleSelection(panel, title);
                else
                    SelectIcon(workspace, panel, title);
            },
            onDoubleClick: ctrl =>
            {
                if (dragGroup is not null)
                    foreach (var (_, _, _, g) in dragGroup)
                        DisposeDragGhost(g, workspace);
                dragGroup = null;
                if (title.RectangleToScreen(title.ClientRectangle).Contains(Cursor.Position))
                    return; // title double-click handled by manual detector below
                if (!ItemExists(item.Path))
                {
                    MessageBox.Show(
                        $"The source cannot be found:\n{item.Path}",
                        "Missing target",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }
                OpenItem(item.Path);
            },
            onDragEnd: () =>
            {
                if (dragGroup is null) return;
                foreach (var (gp, gi, gs, g) in dragGroup)
                {
                    gp.Location = SnapToGrid(workspace, gp.Size, g.Location);
                    gi.X = gp.Left;
                    gi.Y = gp.Top;
                }
                foreach (var (_, _, _, g) in dragGroup)
                    DisposeDragGhost(g, workspace);
                dragGroup = null;
                _board.Dirty();
            });

        // Labels don't reliably synthesize WM_LBUTTONDBLCLK, so detect title double-click manually.
        DateTime lastTitleUp = DateTime.MinValue;
        Point lastTitleUpPos = Point.Empty;
        title.MouseUp += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var now = DateTime.UtcNow;
            if ((now - lastTitleUp).TotalMilliseconds <= SystemInformation.DoubleClickTime
                && Math.Abs(e.X - lastTitleUpPos.X) <= SystemInformation.DoubleClickSize.Width
                && Math.Abs(e.Y - lastTitleUpPos.Y) <= SystemInformation.DoubleClickSize.Height)
            {
                lastTitleUp = DateTime.MinValue;
                RenameIcon(item, title, panel);
            }
            else
            {
                lastTitleUp = now;
                lastTitleUpPos = e.Location;
            }
        };

        workspace.Controls.Add(panel);
        return panel;
    }

    private async Task FetchUrlTitleAsync(string url, IconItem item, Label title, Panel panel)
    {
        string? newLabel = null;
        try
        {
            var html = await _http.GetStringAsync(url).ConfigureAwait(false);
            newLabel = ExtractTitleFromHtml(html);
        }
        catch { return; }
        if (string.IsNullOrWhiteSpace(newLabel)) return;
        try
        {
            panel.Invoke(() =>
            {
                if (panel.IsDisposed) return;
                UpdateIconLabel(item, title, panel, newLabel!);
                _board.Dirty();
            });
        }
        catch { }
    }

    private async Task FetchUrlFaviconAsync(string url, Panel panel, Label icon)
    {
        var cache = GetFaviconCachePath(url);
        if (File.Exists(cache))
        {
            try
            {
                var cached = LoadFaviconBitmap(cache);
                if (cached is not null)
                {
                    panel.Invoke(() =>
                    {
                        if (panel.IsDisposed) return;
                        var old = icon.Image;
                        icon.Image = cached;
                        old?.Dispose();
                    });
                }
                return;
            }
            catch { }
        }
        try
        {
            var domain = new Uri(url).Host;
            var faviconUrl = $"https://www.google.com/s2/favicons?domain={domain}&sz=64";
            var bytes = await _http.GetByteArrayAsync(faviconUrl).ConfigureAwait(false);
            try { await File.WriteAllBytesAsync(cache, bytes); } catch { }
            using var ms = new MemoryStream(bytes);
            using var source = Image.FromStream(ms);
            var bmp = ResizeToIcon(source);
            panel.Invoke(() =>
            {
                if (panel.IsDisposed) return;
                var old = icon.Image;
                icon.Image = bmp;
                old?.Dispose();
            });
        }
        catch { }
    }

    private static string GetFaviconCachePath(string url)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDataDirName, "icons");
        Directory.CreateDirectory(dir);
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(url)))[..16];
        return Path.Combine(dir, hash + ".png");
    }

    private static void DeleteFaviconCache(string url)
    {
        try { File.Delete(GetFaviconCachePath(url)); } catch { }
    }

    private static void SetDoubleBuffered(Control c) =>
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(c, true);

    private static Bitmap ResizeToIcon(Image source)
    {
        var bmp = new Bitmap(42, 42);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, 0, 0, 42, 42);
        return bmp;
    }

    private static Bitmap? LoadFaviconBitmap(string path)
    {
        using var ms = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var source = Image.FromStream(ms);
        return ResizeToIcon(source);
    }

    private static void UpdateIconLabel(IconItem item, Label title, Panel panel, string newLabel)
    {
        item.Label = newLabel;
        const int labelWidth = 110;
        var size = TextRenderer.MeasureText(
            newLabel, TitleFont,
            new Size(labelWidth, int.MaxValue),
            TitleFlags);
        title.Tag = newLabel;
        title.Size = new Size(labelWidth, size.Height);
        panel.Size = new Size(labelWidth, 6 + 42 + size.Height + 6);
        title.Invalidate();
    }

    private void RenameIcon(IconItem item, Label title, Panel panel)
    {
        var newLabel = Prompt("Rename icon", "Rename icon:", item.Label ?? Path.GetFileName(item.Path));
        if (string.IsNullOrWhiteSpace(newLabel)) return;
        UpdateIconLabel(item, title, panel, newLabel);
        _board.Dirty();
    }

    private void DeleteIcon(Panel workspace, IconItem item, Panel panel)
    {
        var displayName = item.Label ?? Path.GetFileName(item.Path);
        if (ConfirmRemove(displayName, ItemExists(item.Path)) != DialogResult.Yes) return;
        workspace.Controls.Remove(panel);
        TabFromSelected(tabs).Items.Remove(item);
        DisposeItemControl(panel);
        DeleteFaviconCache(item.Path);
        _board.Dirty();
    }

    private void DeleteSelectedIcons(Panel workspace)
    {
        var selected = workspace.Controls.OfType<Panel>()
            .Where(p => p.Tag is IconItem && p.BackColor != Color.Transparent)
            .ToList();
        if (selected.Count == 0) return;

        var message = selected.Count == 1
            ? $"Remove '{GetIconDisplayName((IconItem)selected[0].Tag!)}' from the board?"
            : $"Remove {selected.Count} selected icons from the board?";
        var detail = selected.All(p => ItemExists(((IconItem)p.Tag!).Path))
            ? "\n\nThe original files/folders will not be affected."
            : "";
        if (MessageBox.Show(message + detail, "Remove?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var tab = TabFromSelected(tabs);
        foreach (var p in selected)
        {
            var iconItem = (IconItem)p.Tag!;
            workspace.Controls.Remove(p);
            tab.Items.Remove(iconItem);
            DisposeItemControl(p);
            DeleteFaviconCache(iconItem.Path);
        }
        _board.Dirty();
    }

    private static string GetIconDisplayName(IconItem item) =>
        item.Label ?? Path.GetFileName(item.Path) ?? item.Path;

    private void CreateNoteView(Panel workspace, NoteItem item)
    {
        var note = new Label
        {
            AutoSize = true,
            Location = new Point(item.X, item.Y),
            BackColor = Color.FromArgb(220, 255, 255, 200),
            ForeColor = SystemColors.WindowText,
            Text = item.Text,
            Font = TitleFont,
            Cursor = Cursors.Hand,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(6),
            Tag = item
        };

        Label? ghost = null;
        var start = Point.Empty;
        void EditNote()
        {
            var newText = Prompt("Edit note", "Edit note:", item.Text);
            if (string.IsNullOrWhiteSpace(newText)) return;
            item.Text = newText;
            note.Text = newText;
            _board.Dirty();
        }
        WireDrag(new[] { note },
            onDragStart: () =>
            {
                start = note.Location;
                ghost = CreateDragGhost(note);
                ghost.Location = start;
                note.BringToFront();
                workspace.Controls.Add(ghost);
                ghost.BringToFront();
            },
            onDrag: (dx, dy) =>
            {
                if (ghost is null) return;
                ghost.Location = Clamp(workspace, ghost.Size,
                    new Point(start.X + dx, start.Y + dy));
            },
            onClick: () =>
            {
                DisposeDragGhost(ghost, workspace);
                ghost = null;
                note.BringToFront();
            },
            onDoubleClick: _ => EditNote(),
            onDragEnd: () =>
            {
                if (ghost is null) return;
                note.Location = ghost.Location;
                item.X = note.Left;
                item.Y = note.Top;
                note.BringToFront();
                DisposeDragGhost(ghost, workspace);
                ghost = null;
                _board.Dirty();
            });

        var menu = new ContextMenuStrip();
        menu.Items.Add("Edit", null, (s, e) => EditNote());
        menu.Items.Add("Delete", null, (s, e) =>
        {
            if (MessageBox.Show(
                    $"Remove note '{item.Text}'?\n\nThis will only remove the note from the board.",
                    "Remove?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            workspace.Controls.Remove(note);
            TabFromSelected(tabs).Items.Remove(item);
            DisposeItemControl(note);
            _board.Dirty();
        });
        note.ContextMenuStrip = menu;

        workspace.Controls.Add(note);
    }

    private static void DisposeItemControl(Control c)
    {
        if (c is Panel p)
            foreach (var child in p.Controls)
                if (child is Label l && l.Image is not null)
                {
                    l.Image.Dispose();
                    l.Image = null;
                }
        c.Dispose();
    }

    private static Point Clamp(Panel workspace, Size size, Point p)
    {
        var r = workspace.ClientRectangle;
        int x = Math.Max(0, Math.Min(p.X, Math.Max(0, r.Width - size.Width)));
        int y = Math.Max(0, Math.Min(p.Y, Math.Max(0, r.Height - size.Height)));
        return new Point(x, y);
    }

    private static Point SnapToGrid(Panel workspace, Size size, Point p)
    {
        int x = (int)Math.Round((double)p.X / GridSize) * GridSize;
        int y = (int)Math.Round((double)p.Y / GridSize) * GridSize;
        return Clamp(workspace, size, new Point(x, y));
    }

    // ---------- shell icons ----------

    private static Bitmap GetIconBitmap(string path, bool isFolder)
    {
        if (IsUrl(path))
        {
            using var urlIcon = FindChrome() is string chrome ? Icon.ExtractAssociatedIcon(chrome) : SystemIcons.Information;
            var bmp = new Bitmap(42, 42);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                if (urlIcon is not null)
                    g.DrawImage(urlIcon.ToBitmap(), 0, 0, 42, 42);
            }
            return bmp;
        }
        const uint SHGFI_ICON = 0x100;
        const uint SHGFI_LARGEICON = 0x0;

        var shinfo = new SHFILEINFO();
        SHGetFileInfo(
            path,
            isFolder ? 0x00000010u : 0u,
            ref shinfo,
            (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_LARGEICON);

        if (shinfo.hIcon == IntPtr.Zero)
            return new Bitmap(32, 32);

        using var icon = Icon.FromHandle(shinfo.hIcon);
        var bitmap = new Bitmap(48, 48, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
            g.DrawImage(icon.ToBitmap(), 0, 0, 48, 48);
        DestroyIcon(shinfo.hIcon);
        return bitmap;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private static DialogResult ConfirmRemove(string label, bool hasSource)
    {
        var msg = hasSource
            ? $"Remove '{label}' from the board?\n\nThe original folder/file will not be affected."
            : $"Remove '{label}' from the board?";
        return MessageBox.Show(msg, "Remove?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
    }

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
            var state = JsonSerializer.Deserialize<AppState>(json)
                ?? new AppState(new List<TabState>());

            foreach (var t in state.Tabs)
            {
                var tab = new Tab(t.Name);
                foreach (var it in t.Items)
                {
                    Item item = it.IsNote
                        ? new NoteItem(it.Text ?? "", Math.Max(0, it.X), Math.Max(0, it.Y))
                        : new IconItem(it.Path, Math.Max(0, it.X), Math.Max(0, it.Y), it.Label);
                    tab.Items.Add(item);
                }
                _board.Tabs.Add(tab);
            }

            foreach (var tab in _board.Tabs)
                CreateTabPage(tab);

            if (tabs.TabPages.Count == 0)
                AddTab("Board");
            else if (_board.Tabs.Count > 0)
                tabs.SelectedIndex = Math.Clamp(state.SelectedTabIndex, 0, tabs.TabPages.Count - 1);

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
        var window = new WindowState(Location.X, Location.Y, Size.Width, Size.Height);
        var json = JsonSerializer.Serialize(_board.ToState(window));
        try { if (File.Exists(_stateFile)) File.Copy(_stateFile, _stateBak, overwrite: true); } catch { }
        File.WriteAllText(_stateFile, json);
    }

    private static void OpenItem(string path)
    {
        try
        {
            if (IsUrl(path) && FindChrome() is string chrome)
            {
                Process.Start(new ProcessStartInfo(chrome, $"\"{path}\"") { UseShellExecute = false });
            }
            else
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not open item", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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

    // ---------- prompt ----------

    private string? Prompt(string title, string label, string initial)
    {
        using var f = new PromptForm(title, label, initial);
        return f.ShowDialog(this) == DialogResult.OK ? f.Result : null;
    }
}
