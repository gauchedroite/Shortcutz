using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shortcutz;

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
    private readonly ContextMenuStrip _workspaceMenu;
    private readonly TabControl tabs;
    private readonly Board _board = new();
    private bool _showGridDots;
    private float _zoom
    {
        get => (tabs.SelectedTab != null) ? TabFromSelected(tabs).Zoom : 1.0f;
        set { if (tabs.SelectedTab != null) TabFromSelected(tabs).Zoom = value; }
    }
    private const float ZoomMin = 0.25f;
    private const float ZoomMax = 3f;
    private const float ZoomStep = 0.15f;

    public Form1()
    {
        tabs = new TabControl { Dock = DockStyle.Fill };
        SuspendLayout();
        Controls.Add(tabs);
        ClientSize = new Size(1000, 650);
        Text = "Shortcutz";
        try
        {
            using var stream = typeof(Form1).Assembly.GetManifestResourceStream("Shortcutz.app.ico");
            if (stream is not null) Icon = new Icon(stream);
        }
        catch { }
        TopMost = true; // keep above other apps; PromptForm is also topmost so it stays above this
        ResumeLayout(false);

        _tabMenu = new ContextMenuStrip();
        _tabMenu.Items.Add("Add tab", null, (s, e) => { AddTab("New tab", tabs.SelectedIndex + 1); _board.Dirty(); });
        _tabMenu.Items.Add("Rename", null, RenameTab);
        _tabMenu.Items.Add("Delete", null, CloseTab);
        tabs.MouseUp += Tabs_MouseUp;
        tabs.MouseDoubleClick += Tabs_MouseDoubleClick;
        tabs.MouseDown += Tabs_MouseDown;
        tabs.MouseMove += Tabs_MouseMove;
        tabs.MouseUp += Tabs_MouseUpReorder;

        _workspaceMenu = new ContextMenuStrip();
        var showGridItem = new ToolStripMenuItem("Show grid dots") { CheckOnClick = true };
        showGridItem.Click += (s, e) =>
        {
            _showGridDots = showGridItem.Checked;
            if (tabs.SelectedTab is TabPage page)
                WorkspaceFromPage(page).Invalidate();
        };
        _workspaceMenu.Items.Add(showGridItem);

        _stateFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDataDirName, "state.json");
        _stateBak = _stateFile + ".bak";
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        
        tabs.SelectedIndexChanged += Tabs_SelectedIndexChanged;

        LoadState();
        showGridItem.Checked = _showGridDots;
        if (tabs.TabPages.Count == 0)
            AddTab("Board");

        _board.Changed += SaveState;
        FormClosing += (s, e) => SaveState();
        KeyPreview = true;
        KeyDown += Form1_KeyDown;
    }

    private void Tabs_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (tabs.SelectedTab is TabPage page)
            ApplyZoom(WorkspaceFromPage(page));
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
                DeleteSelectedItems(WorkspaceFromPage(page));
            return;
        }
        if (e.KeyCode == Keys.F2)
        {
            e.Handled = true;
            if (tabs.SelectedTab is TabPage page)
                RenameSelected(WorkspaceFromPage(page));
            return;
        }
        if (e.Control)
        {
            if (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add)
            {
                e.Handled = true;
                ZoomIn();
            }
            else if (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract)
            {
                e.Handled = true;
                ZoomOut();
            }
            else if (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0)
            {
                e.Handled = true;
                ResetZoom();
            }
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

    private Tab AddTab(string name, int index = -1)
    {
        var tab = new Tab(name);
        if (index < 0 || index >= _board.Tabs.Count)
            _board.Tabs.Add(tab);
        else
            _board.Tabs.Insert(index, tab);
        CreateTabPage(tab, index);
        return tab;
    }

    private TabPage CreateTabPage(Tab tab, int index = -1)
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
        workspace.ContextMenuStrip = _workspaceMenu;
        workspace.MouseDown += Workspace_MouseDown;
        workspace.MouseMove += Workspace_MouseMove;
        workspace.MouseUp += Workspace_MouseUp;
        workspace.Paint += Workspace_Paint;
        workspace.MouseDoubleClick += Workspace_MouseDoubleClick;
        SetDoubleBuffered(workspace);
        page.Controls.Add(workspace);
        page.Tag = tab;

        if (index < 0 || index >= tabs.TabPages.Count)
            tabs.TabPages.Add(page);
        else
            tabs.TabPages.Insert(index, page);
        tabs.SelectedTab = page;

        foreach (var item in tab.Items)
            CreateView(workspace, item);

        return page;
    }

    private static Panel WorkspaceFromPage(TabPage page) => (Panel)page.Controls[0];
    private static Tab TabFromPage(TabPage page) => (Tab)page.Tag!;
    private static Tab TabFromSelected(TabControl tc) => (Tab)(tc.SelectedTab?.Tag ?? (tc.TabCount > 0 ? tc.TabPages[0].Tag : null))!;
    private static bool ItemExists(string path) => Directory.Exists(path) || File.Exists(path) || IsUrl(path);

    private static readonly Color NoteBackColor = Color.FromArgb(220, 255, 255, 200);
    private static readonly Color NoteSelectionBackColor = Color.FromArgb(255, 255, 240, 150);

    private static Color SelectionTint()
    {
        var c = SystemColors.Highlight;
        return Color.FromArgb(26, c.R, c.G, c.B);
    }

    private static bool IsSelectable(Control c) =>
        (c is Panel && c.Tag is IconItem) || (c is Label && c.Tag is NoteItem);

    private static bool IsSelected(Control c) => c switch
    {
        Panel => c.BackColor != Color.Transparent,
        Label => c.BackColor != NoteBackColor,
        _ => false
    };

    private static void SetSelected(Control c, bool selected)
    {
        if (c is Panel p)
        {
            p.BackColor = selected ? SelectionTint() : Color.Transparent;
            if (p.Controls.Count > 1 && p.Controls[1] is Label title)
                title.ForeColor = SystemColors.WindowText;
        }
        else if (c is Label note)
        {
            note.BackColor = selected ? NoteSelectionBackColor : NoteBackColor;
        }
    }

    private static string GetItemDisplayName(Item item) => item switch
    {
        IconItem i => i.Label ?? Path.GetFileName(i.Path) ?? i.Path,
        NoteItem n => n.Text,
        _ => ""
    };

    private static string? GetItemPath(Item item) => item is IconItem i ? i.Path : null;

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
                    new Point(dropPoint.X + i % 3 * (int)(GridSize * _zoom), dropPoint.Y + i / 3 * (int)(GridSize * _zoom)));
                var item = new IconItem(path, (int)(loc.X / _zoom), (int)(loc.Y / _zoom), null);
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
                    new Point(dropPoint.X + i % 3 * (int)(GridSize * _zoom), dropPoint.Y + i / 3 * (int)(GridSize * _zoom)));
                var item = new IconItem(url, (int)(loc.X / _zoom), (int)(loc.Y / _zoom), label);
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
        if (sender is not Panel workspace) return;
        if (e.Button == MouseButtons.Right)
        {
            if (workspace.GetChildAtPoint(e.Location) == null)
                _workspaceMenu.Show(workspace, e.Location);
            return;
        }
        if (!_selecting || e.Button != MouseButtons.Left) return;
        _selecting = false;
        if (_selRect.Width > 0 && _selRect.Height > 0) workspace.Invalidate(_selRect);
        if (_selRect.Width > 3 && _selRect.Height > 3)
            SelectItems(workspace, _selRect);
    }

    private void Workspace_Paint(object? sender, PaintEventArgs e)
    {
        if (sender is not Panel workspace) return;
        if (_showGridDots)
            DrawGridDots(e.Graphics, workspace);
        if (!_selecting || _selRect.Width <= 0 || _selRect.Height <= 0) return;
        var c = SystemColors.Highlight;
        using var fill = new SolidBrush(Color.FromArgb(32, c.R, c.G, c.B));
        using var pen = new Pen(c, 1);
        e.Graphics.FillRectangle(fill, _selRect);
        e.Graphics.DrawRectangle(pen, _selRect.X, _selRect.Y, _selRect.Width - 1, _selRect.Height - 1);
    }

    private static void DrawGridDots(Graphics g, Panel workspace)
    {
        using var brush = new SolidBrush(Color.FromArgb(80, 128, 128, 128));
        const int r = 1;
        var bounds = workspace.ClientRectangle;
        for (int x = 0; x < bounds.Width; x += GridSize)
            for (int y = 0; y < bounds.Height; y += GridSize)
                g.FillEllipse(brush, x - r, y - r, r * 2, r * 2);
    }

    private static Rectangle SelectionRect(Point a, Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private static void SelectItems(Panel workspace, Rectangle rect)
    {
        foreach (var c in workspace.Controls.OfType<Control>().Where(IsSelectable))
            if (rect.IntersectsWith(c.Bounds))
                SetSelected(c, true);
    }

    private static void ClearHighlights(Panel workspace)
    {
        foreach (var c in workspace.Controls.OfType<Control>().Where(IsSelectable))
            SetSelected(c, false);
    }

    private static void SelectSingle(Panel workspace, Control c)
    {
        ClearHighlights(workspace);
        SetSelected(c, true);
    }

    private static void Toggle(Control c)
    {
        SetSelected(c, !IsSelected(c));
    }

    private void Workspace_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (sender is not Panel workspace) return;
        if (workspace.GetChildAtPoint(e.Location) != null) return;
        var text = Prompt("New note", "Enter note text:", "", multiline: true);
        if (string.IsNullOrWhiteSpace(text)) return;
        var tab = TabFromSelected(tabs);
        var loc = Clamp(workspace, new Size(120, 40), e.Location);
        var item = new NoteItem(text, (int)(loc.X / _zoom), (int)(loc.Y / _zoom));
        tab.Items.Add(item);
        CreateNoteView(workspace, item);
        _board.Dirty();
    }

    private int _dragTabFrom = -1;
    private Point _dragTabStart;
    private bool _dragTabActive;

    private int TabIndexAt(Point p)
    {
        for (int i = 0; i < tabs.TabPages.Count; i++)
            if (tabs.GetTabRect(i).Contains(p)) return i;
        return -1;
    }

    private void Tabs_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragTabFrom = TabIndexAt(e.Location);
        _dragTabStart = e.Location;
        _dragTabActive = false;
    }

    private void Tabs_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragTabFrom < 0 || e.Button != MouseButtons.Left) return;
        if (!_dragTabActive && (Math.Abs(e.X - _dragTabStart.X) > 3 || Math.Abs(e.Y - _dragTabStart.Y) > 3))
            _dragTabActive = true;
        tabs.Cursor = _dragTabActive && TabIndexAt(e.Location) >= 0
            ? Cursors.Hand : Cursors.Default;
    }

    private void Tabs_MouseUpReorder(object? sender, MouseEventArgs e)
    {
        if (_dragTabFrom >= 0 && e.Button == MouseButtons.Left)
        {
            if (_dragTabActive)
            {
                var to = TabIndexAt(e.Location);
                if (to >= 0 && to != _dragTabFrom)
                {
                    var page = tabs.TabPages[_dragTabFrom];
                    var tab = _board.Tabs[_dragTabFrom];
                    tabs.TabPages.RemoveAt(_dragTabFrom);
                    _board.Tabs.RemoveAt(_dragTabFrom);
                    tabs.TabPages.Insert(to, page);
                    _board.Tabs.Insert(to, tab);
                    tabs.SelectedIndex = to;
                    _board.Dirty();
                }
            }
            tabs.Cursor = Cursors.Default;
            _dragTabFrom = -1;
            _dragTabActive = false;
        }
    }

    private void Tabs_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        for (int i = 0; i < tabs.TabPages.Count; i++)
            if (tabs.GetTabRect(i).Contains(e.Location))
            {
                tabs.SelectedIndex = i;
                _tabMenu.Show(tabs, e.Location);
                return;
            }
    }

    private void Tabs_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        for (int i = 0; i < tabs.TabPages.Count; i++)
            if (tabs.GetTabRect(i).Contains(e.Location))
            {
                tabs.SelectedIndex = i;
                RenameTab(null, EventArgs.Empty);
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
                controls[0].BringToFront();
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

    private float NextZoom(int direction)
    {
        var z = (float)Math.Round(_zoom / ZoomStep + direction) * ZoomStep;
        return Math.Clamp(z, ZoomMin, ZoomMax);
    }

    private void ZoomIn()
    {
        var z = NextZoom(1);
        if (Math.Abs(z - _zoom) < 0.001f) return;
        _zoom = z;
        if (tabs.SelectedTab is TabPage page)
            ApplyZoom(WorkspaceFromPage(page));
    }

    private void ZoomOut()
    {
        var z = NextZoom(-1);
        if (Math.Abs(z - _zoom) < 0.001f) return;
        _zoom = z;
        if (tabs.SelectedTab is TabPage page)
            ApplyZoom(WorkspaceFromPage(page));
    }

    private void ResetZoom()
    {
        if (_zoom == 1f) return;
        _zoom = 1f;
        if (tabs.SelectedTab is TabPage page)
            ApplyZoom(WorkspaceFromPage(page));
    }

    private void ApplyZoom(Panel workspace)
    {
        foreach (Control c in workspace.Controls)
            ApplyZoomToControl(c);
        workspace.Invalidate();
    }

    private void ApplyZoomToControl(Control c)
    {
        switch (c.Tag)
        {
            case IconItem iconItem:
                ScaleIconView((Panel)c, iconItem);
                break;
            case NoteItem noteItem:
                ScaleNoteView((Label)c, noteItem);
                break;
        }
    }

    private void ScaleIconView(Panel panel, IconItem item)
    {
        var title = panel.Controls.OfType<Label>().First(l => l.Tag is string);
        var icon = panel.Controls.OfType<Label>().First(l => l.Tag is not null && l.Tag is not string);
        var displayName = title.Tag?.ToString() ?? "";

        var labelWidth = (int)(110 * _zoom);
        var titleFont = new Font(TitleFont.FontFamily, TitleFont.Size * _zoom);
        var titleSize = TextRenderer.MeasureText(
            displayName, titleFont,
            new Size(labelWidth, int.MaxValue), TitleFlags);
        var panelHeight = (int)(6 * _zoom) + (int)(42 * _zoom) + titleSize.Height + (int)(6 * _zoom);

        panel.Size = new Size(labelWidth, panelHeight);
        panel.Location = new Point((int)(item.X * _zoom), (int)(item.Y * _zoom));

        icon.Size = new Size((int)(60 * _zoom), (int)(42 * _zoom));
        icon.Location = new Point((labelWidth - icon.Width) / 2, (int)(6 * _zoom));

        var original = icon.Tag as Image ?? icon.Image;
        if (original is not null)
        {
            var old = icon.Image;
            icon.Image = ScaleBitmap(original, icon.Size);
            if (old is not null && old != original) old.Dispose();
        }

        title.Size = new Size(labelWidth, titleSize.Height);
        title.Location = new Point(0, (int)(48 * _zoom));
        ReplaceFont(title, titleFont);
    }

    private void ScaleNoteView(Label note, NoteItem item)
    {
        note.Padding = new Padding((int)(6 * _zoom));
        ReplaceFont(note, new Font(TitleFont.FontFamily, TitleFont.Size * _zoom));
        int w = (int)(item.Width * _zoom);
        note.Width = w;
        var tsize = TextRenderer.MeasureText(note.Text, note.Font,
            new Size(w - note.Padding.Horizontal, int.MaxValue),
            TextFormatFlags.WordBreak);
        note.Height = tsize.Height + note.Padding.Vertical;
        note.Location = new Point((int)(item.X * _zoom), (int)(item.Y * _zoom));
    }

    private static void ReplaceFont(Control c, Font font)
    {
        var old = c.Font;
        if (old != TitleFont) old.Dispose();
        c.Font = font;
    }

    private static Bitmap ScaleBitmap(Image source, Size fit)
    {
        var bmp = new Bitmap(fit.Width, fit.Height);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);
        var scale = Math.Min((float)fit.Width / source.Width, (float)fit.Height / source.Height);
        var w = (int)(source.Width * scale);
        var h = (int)(source.Height * scale);
        var x = (fit.Width - w) / 2;
        var y = (fit.Height - h) / 2;
        g.DrawImage(source, x, y, w, h);
        return bmp;
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
            }
            icon.Tag = icon.Image;
            _ = FetchUrlFaviconAsync(item.Path, panel, icon);
        }
        else
        {
            icon.Image = GetIconBitmap(item.Path, Directory.Exists(item.Path));
            icon.Tag = icon.Image;
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
            TextRenderer.DrawText(e.Graphics, (string)title.Tag!, title.Font,
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
            if (IsSelected(panel))
                DeleteSelectedItems(workspace);
            else
                DeleteIcon(workspace, item, panel);
        });
        panel.ContextMenuStrip = menu;

        List<(Control c, Item i, Point s, Label g)>? dragGroup = null;
        WireDrag(new Control[] { panel, icon, title },
            onDragStart: () =>
            {
                // Only clear selection if this icon wasn't already selected
                if (!IsSelected(panel))
                    SelectSingle(workspace, panel);
                // Create ghosts for all selected items and send originals behind them
                dragGroup = workspace.Controls.OfType<Control>()
                    .Where(IsSelected)
                    .Select(c =>
                    {
                        var g = CreateDragGhost(c);
                        g.Location = c.Location;
                        workspace.Controls.Add(g);
                        g.BringToFront();
                        c.SendToBack();
                        return (c, (Item)c.Tag!, c.Location, g);
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
                    Toggle(panel);
                else
                    SelectSingle(workspace, panel);
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
                if ((Control.ModifierKeys & Keys.Shift) != 0
                    && !IsUrl(item.Path) && !Directory.Exists(item.Path)
                    && FindNotepadPlus() is string npp)
                {
                    try { Process.Start(new ProcessStartInfo(npp) { UseShellExecute = false, ArgumentList = { item.Path } }); }
                    catch (Exception ex) { MessageBox.Show(ex.Message, "Could not open in Notepad++", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    return;
                }
                OpenItem(item.Path);
            },
            onDragEnd: () =>
            {
                if (dragGroup is null) return;
                foreach (var (c, i, _, g) in dragGroup)
                {
                    c.Location = SnapToGrid(workspace, c.Size, g.Location);
                    i.X = (int)(c.Left / _zoom);
                    i.Y = (int)(c.Top / _zoom);
                    c.BringToFront();
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
        ApplyZoomToControl(panel);
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
                        icon.Tag = cached;
                        old?.Dispose();
                    });
                }
                return;
            }
            catch { }
        }
        try
        {
            var baseUri = new Uri(url);
            var bytes = await DownloadFaviconAsync(baseUri).ConfigureAwait(false);
            if (bytes is null) return;
            try { await File.WriteAllBytesAsync(cache, bytes); } catch { }
            using var ms = new MemoryStream(bytes);
            using var source = Image.FromStream(ms);
            var bmp = ResizeToIcon(source);
            panel.Invoke(() =>
            {
                if (panel.IsDisposed) return;
                var old = icon.Image;
                icon.Image = bmp;
                icon.Tag = bmp;
                old?.Dispose();
            });
        }
        catch { }
    }

    // Favicon ladder: the page's own <link rel="icon"> (handles subdomain /
    // SVG-only sites S2 misses, e.g. fms.yukon.ca -> favicon.svg), then the
    // conventional /favicon.ico, then Google faviconV2 (renders SVG server-side,
    // more reliable than the old s2 endpoint), then legacy s2 per apex walk.
    private async Task<byte[]?> DownloadFaviconAsync(Uri baseUri)
    {
        // 1. <link rel="*icon*"> declared in the page HTML
        string? html = null;
        try { html = await _http.GetStringAsync(baseUri).ConfigureAwait(false); }
        catch { }
        if (html is not null)
        {
            foreach (var href in ExtractIconLinks(html))
            {
                var data = await TryGetImageAsync(new Uri(baseUri, href)).ConfigureAwait(false);
                if (data is not null) return data;
            }
        }

        // 2. Conventional /favicon.ico
        {
            var data = await TryGetImageAsync(new Uri(baseUri, "/favicon.ico")).ConfigureAwait(false);
            if (data is not null) return data;
        }

        // 3. Google faviconV2 — fetches and renders the declared icon (incl. SVG)
        {
            var v2 = new Uri($"https://t1.gstatic.com/faviconV2?client=SOCIAL&type=FAVICON&fallback_opts=TYPE,SIZE,URL&url={Uri.EscapeDataString(baseUri.AbsoluteUri)}&size=64");
            var data = await TryGetImageAsync(v2).ConfigureAwait(false);
            if (data is not null) return data;
        }

        // 4. Legacy s2, walking subdomain -> apex
        var domain = baseUri.Host;
        while (domain is not null)
        {
            var data = await TryGetImageAsync(
                new Uri($"https://www.google.com/s2/favicons?domain={domain}&sz=64")).ConfigureAwait(false);
            if (data is not null) return data;
            domain = ParentDomain(domain);
        }
        return null;
    }

    // Download bytes and verify GDI+ can decode them — rejects SVG/WebP that
    // Image.FromStream cannot read, so the ladder just tries the next candidate.
    private async Task<byte[]?> TryGetImageAsync(Uri uri)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(uri).ConfigureAwait(false);
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            return bytes;
        }
        catch { return null; }
    }

    // <link rel="*icon*"> hrefs, best decodable first. SVG goes last because
    // GDI+ can't render it (it'll be tried, fail decode, and the ladder moves on).
    private static IEnumerable<string> ExtractIconLinks(string html)
    {
        var found = new List<(int priority, string href)>();
        foreach (Match m in Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = m.Value;
            var rel = Regex.Match(tag, @"rel\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (!rel.Success || !rel.Groups[1].Value.Contains("icon", StringComparison.OrdinalIgnoreCase))
                continue;
            var href = Regex.Match(tag, @"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (!href.Success) continue;
            var h = href.Groups[1].Value;
            var priority = h.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? 3
                         : rel.Groups[1].Value.Contains("apple", StringComparison.OrdinalIgnoreCase) ? 0
                         : h.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? 1
                         : 2;
            found.Add((priority, h));
        }
        return found.OrderBy(x => x.priority).Select(x => x.href);
    }

    // ponytail: naive apex walk — "fms.yukon.ca" -> "yukon.ca" -> null.
    // Stops before the TLD; does not handle co.uk-style country SLDs.
    private static string? ParentDomain(string host)
    {
        var parts = host.Split('.');
        return parts.Length >= 3 ? string.Join('.', parts.Skip(1)) : null;
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

    private void UpdateIconLabel(IconItem item, Label title, Panel panel, string newLabel)
    {
        item.Label = newLabel;
        title.Tag = newLabel;
        ApplyZoomToControl(panel);
        title.Invalidate();
    }

    private void RenameIcon(IconItem item, Label title, Panel panel)
    {
        var newLabel = Prompt("Rename icon", "Rename icon:", item.Label ?? Path.GetFileName(item.Path));
        if (string.IsNullOrWhiteSpace(newLabel)) return;
        UpdateIconLabel(item, title, panel, newLabel);
        _board.Dirty();
    }

    private void RenameNote(NoteItem item, Label note)
    {
        var newText = Prompt("Edit note", "Edit note:", item.Text, multiline: true);
        if (string.IsNullOrWhiteSpace(newText)) return;
        item.Text = newText;
        note.Text = newText;
        ScaleNoteView(note, item);
        _board.Dirty();
    }

    private void RenameSelected(Panel workspace)
    {
        var sel = workspace.Controls.OfType<Control>().FirstOrDefault(IsSelected);
        if (sel is null) return;
        switch (sel.Tag)
        {
            case IconItem ii when sel is Panel p:
                var title = p.Controls.OfType<Label>().First(l => l.Tag is string);
                RenameIcon(ii, title, p);
                break;
            case NoteItem ni when sel is Label note:
                RenameNote(ni, note);
                break;
        }
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

    private void DeleteSelectedItems(Panel workspace)
    {
        var selected = workspace.Controls.OfType<Control>().Where(IsSelected).ToList();
        if (selected.Count == 0) return;

        var message = selected.Count == 1
            ? $"Remove '{GetItemDisplayName((Item)selected[0].Tag!)}' from the board?"
            : $"Remove {selected.Count} selected items from the board?";
        var iconPaths = selected
            .Where(c => c.Tag is IconItem)
            .Select(c => ((IconItem)c.Tag!).Path);
        var detail = iconPaths.All(ItemExists)
            ? "\n\nThe original files/folders will not be affected."
            : "";
        if (MessageBox.Show(message + detail, "Remove?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var tab = TabFromSelected(tabs);
        foreach (var c in selected)
        {
            workspace.Controls.Remove(c);
            tab.Items.Remove((Item)c.Tag!);
            if (c.Tag is IconItem iconItem)
                DeleteFaviconCache(iconItem.Path);
            DisposeItemControl(c);
        }
        _board.Dirty();
    }

    private static string GetIconDisplayName(IconItem item) =>
        item.Label ?? Path.GetFileName(item.Path) ?? item.Path;

    private void CreateNoteView(Panel workspace, NoteItem item)
    {
        var note = new Label
        {
            AutoSize = false,
            Size = new Size(item.Width, 40),
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

        void EditNote() => RenameNote(item, note);

        List<(Control c, Item i, Point s, Label g)>? dragGroup = null;
        WireDrag(new[] { note },
            onDragStart: () =>
            {
                if (!IsSelected(note))
                    SelectSingle(workspace, note);
                dragGroup = workspace.Controls.OfType<Control>()
                    .Where(IsSelected)
                    .Select(c =>
                    {
                        var g = CreateDragGhost(c);
                        g.Location = c.Location;
                        workspace.Controls.Add(g);
                        g.BringToFront();
                        c.SendToBack();
                        return (c, (Item)c.Tag!, c.Location, g);
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
                    Toggle(note);
                else
                    SelectSingle(workspace, note);
            },
            onDoubleClick: _ => EditNote(),
            onDragEnd: () =>
            {
                if (dragGroup is null) return;
                foreach (var (c, i, _, g) in dragGroup)
                {
                    c.Location = SnapToGrid(workspace, c.Size, g.Location);
                    i.X = (int)(c.Left / _zoom);
                    i.Y = (int)(c.Top / _zoom);
                    c.BringToFront();
                }
                foreach (var (_, _, _, g) in dragGroup)
                    DisposeDragGhost(g, workspace);
                dragGroup = null;
                _board.Dirty();
            });

        var menu = new ContextMenuStrip();
        menu.Items.Add("Edit", null, (s, e) => EditNote());
        menu.Items.Add("Delete", null, (s, e) =>
        {
            if (IsSelected(note))
            {
                DeleteSelectedItems(workspace);
                return;
            }
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

        // Resize grip: child control in the bottom-right corner, fully separate
        // from the note's drag/select wiring (child consumes its own mouse events).
        const int gripSize = 10;
        var grip = new Panel
        {
            Size = new Size(gripSize, gripSize),
            Location = new Point(item.Width - gripSize, 40 - gripSize),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            BackColor = Color.Transparent,
            Cursor = Cursors.SizeNWSE
        };
        grip.Paint += (s, e) =>
        {
            var g = e.Graphics;
            using var pen = new Pen(Color.FromArgb(120, 80, 80, 80), 1f);
            var r = grip.ClientRectangle;
            // two strokes form a small "\" triangle in the corner
            g.DrawLine(pen, r.Right - 4, r.Bottom - 1, r.Right - 1, r.Bottom - 4);
            g.DrawLine(pen, r.Right - 7, r.Bottom - 1, r.Right - 1, r.Bottom - 7);
        };
        bool resizing = false;
        int resizeStartWidth = 0;
        Point resizeStartMouse = Point.Empty;
        grip.MouseDown += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            resizing = true;
            resizeStartWidth = item.Width;
            resizeStartMouse = grip.PointToScreen(e.Location);
            grip.Capture = true;
            SelectSingle(workspace, note);
        };
        grip.MouseMove += (s, e) =>
        {
            if (!resizing) return;
            var now = grip.PointToScreen(e.Location);
            int delta = (int)((now.X - resizeStartMouse.X) / _zoom);
            item.Width = Math.Clamp(resizeStartWidth + delta, NoteItem.MinWidth, NoteItem.MaxWidth);
            ScaleNoteView(note, item);
        };
        grip.MouseUp += (s, e) =>
        {
            if (!resizing) return;
            resizing = false;
            grip.Capture = false;
            _board.Dirty();
        };
        note.Controls.Add(grip);

        ApplyZoomToControl(note);
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
                    l.Tag = null;
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

    private Point SnapToGrid(Panel workspace, Size size, Point p)
    {
        var grid = (int)(GridSize * _zoom);
        if (grid == 0) grid = GridSize;
        int x = (int)Math.Round((double)p.X / grid) * grid;
        int y = (int)Math.Round((double)p.Y / grid) * grid;
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
                            ? new NoteItem(it.Text ?? "", Math.Max(0, it.X), Math.Max(0, it.Y), it.Width ?? NoteItem.DefaultWidth)
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
        try { if (File.Exists(_stateFile)) File.Copy(_stateFile, _stateBak, overwrite: true); } catch { }
        File.WriteAllText(_stateFile, json);
    }

    private static void OpenItem(string path)
    {
        try
        {
            if (IsUrl(path) && FindChrome() is string chrome)
            {
                var psi = new ProcessStartInfo(chrome) { UseShellExecute = false };
                psi.ArgumentList.Add(path);
                Process.Start(psi);
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

    private string? Prompt(string title, string label, string initial, bool multiline = false)
    {
        using var f = new PromptForm(title, label, initial, multiline);
        return f.ShowDialog(this) == DialogResult.OK ? f.Result : null;
    }
}
