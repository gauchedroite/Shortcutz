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
    private static readonly Font MonospaceFont = new(FontFamily.GenericMonospace, TitleFont.Size);
    private const int GridSize = 20;
    // EditControl: break on hyphens and hard-break words longer than the line.
    // Label paints with its own flags (WordBreak only), so titles are drawn by hand below.
    private const TextFormatFlags TitleFlags =
        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl |
        TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix;
    private const TextFormatFlags CompactTitleFlags =
        TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private const string AppDataDirName = "Shortcutz";

    private string _stateFile;
    private readonly string _lastStateFilePath;
    private static readonly string? _sevenZip = Find7z();
    private readonly ContextMenuStrip _tabMenu;
    private readonly ContextMenuStrip _subTabMenu;
    private readonly ContextMenuStrip _workspaceMenu;
    private readonly ToolStrip stateStrip;
    private readonly Stack<(int Top, int Sub)> _tabHistoryBack = new();
    private readonly Stack<(int Top, int Sub)> _tabHistoryForward = new();
    private (int Top, int Sub) _currentTabPair = (-1, -1);
    private bool _tabHistoryNavigating;
    private bool _tabPairInitialized;
    private readonly TabControl tabs;
    private readonly string _stateDirectory;
    private readonly string _appSettingsFile;
    private bool _isInitialStateLoad = true;
    private bool _hasGlobalWindowState;
    private readonly Board _board = new();
    private static readonly List<Item> _clipboard = new();
    private Point _lastWorkspaceMenuLocation;
    private bool _showGridDots;
    private float _zoom
    {
        get => (SelectedSubPage != null) ? PageFromSubPage(SelectedSubPage).Zoom : 1.0f;
        set { if (SelectedSubPage != null) PageFromSubPage(SelectedSubPage).Zoom = value; }
    }
    private const float ZoomMin = 0.25f;
    private const float ZoomMax = 3f;
    private const float ZoomStep = 0.15f;

    // Lazy page population: build a page's controls only when it becomes visible.
    private bool _suppressPopulate;
    private readonly HashSet<TabPage> _populatedSubPages = new();

    public Form1()
    {
        stateStrip = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
        tabs = new TabControl { Dock = DockStyle.Fill, DrawMode = TabDrawMode.OwnerDrawFixed };
        tabs.DrawItem += Tabs_DrawItem;
        SuspendLayout();
        Controls.Add(tabs);
        Controls.Add(stateStrip);
        InitSearch();
        ClientSize = new Size(1000, 650);
        MinimumSize = new Size(400, 300);
        Text = "Shortcutz";
        try
        {
            using var stream = typeof(Form1).Assembly.GetManifestResourceStream("Shortcutz.app.ico");
            if (stream is not null) Icon = new Icon(stream);
        }
        catch { }
        ResumeLayout(false);

        _tabMenu = new ContextMenuStrip();
        _tabMenu.Items.Add("Add tab", null, (s, e) => { AddTab("New tab", tabs.SelectedIndex + 1); _board.Dirty(); });
        _tabMenu.Items.Add("Rename", null, RenameTab);
        _tabMenu.Items.Add("Delete", null, CloseTab);
        tabs.ContextMenuStrip = _tabMenu;
        tabs.MouseUp += Tabs_MouseUp;
        tabs.MouseDoubleClick += Tabs_MouseDoubleClick;
        tabs.MouseDown += Tabs_MouseDown;
        tabs.MouseMove += Tabs_MouseMove;
        tabs.MouseUp += Tabs_MouseUpReorder;

        _subTabMenu = new ContextMenuStrip();
        var moveSubTabMenu = new ToolStripMenuItem("Move to");
        _subTabMenu.Items.Add("Add page", null, (s, e) => { AddPage("New page", SelectedSubTabs?.SelectedIndex + 1 ?? -1); _board.Dirty(); });
        _subTabMenu.Items.Add("Add text page", null, (s, e) => { AddTextPage("New text page", SelectedSubTabs?.SelectedIndex + 1 ?? -1); _board.Dirty(); });
        var monospaceItem = new ToolStripMenuItem("Monospace font") { CheckOnClick = true };
        monospaceItem.Click += (s, e) =>
        {
            if (SelectedSubPage is not TabPage subPage) return;
            var page = PageFromSubPage(subPage);
            page.Monospace = monospaceItem.Checked;
            if (WorkspaceFromSubPage(subPage).Controls.OfType<TextBox>().FirstOrDefault() is TextBox box)
                box.Font = page.Monospace ? MonospaceFont : TitleFont;
            _board.Dirty();
        };
        _subTabMenu.Items.Add(monospaceItem);
        var previewItem = new ToolStripMenuItem("Markdown preview") { CheckOnClick = true };
        previewItem.Click += (s, e) => ToggleMarkdownPreview();
        _subTabMenu.Items.Add(previewItem);
        _subTabMenu.Opening += (s, e) =>
        {
            var page = SelectedSubPage is TabPage subPage ? PageFromSubPage(subPage) : null;
            var parent = tabs.SelectedTab is TabPage parentPage ? TabFromPage(parentPage) : null;
            moveSubTabMenu.DropDownItems.Clear();
            if (parent is not null)
                foreach (var target in _board.Tabs.Where(tab => tab != parent))
                    moveSubTabMenu.DropDownItems.Add(target.Name, null, (_, _) => MoveSubTabTo(target));
            moveSubTabMenu.Enabled = moveSubTabMenu.DropDownItems.Count > 0;
            monospaceItem.Enabled = page?.Type == "Text" && page.MarkdownPreview == false;
            previewItem.Enabled = page?.Type == "Text";
            monospaceItem.Checked = page?.Monospace == true;
            previewItem.Checked = page?.MarkdownPreview == true;
        };
        _subTabMenu.Items.Add(moveSubTabMenu);
        _subTabMenu.Items.Add("Rename", null, RenameSubTab);
        _subTabMenu.Items.Add("Delete", null, CloseSubTab);

        _workspaceMenu = new ContextMenuStrip();
        var showGridItem = new ToolStripMenuItem("Show grid dots") { CheckOnClick = true };
        showGridItem.Click += (s, e) =>
        {
            _showGridDots = showGridItem.Checked;
            if (SelectedWorkspace() is Panel workspace)
                workspace.Invalidate();
        };
        _workspaceMenu.Items.Add(showGridItem);
        _workspaceMenu.Items.Add("Paste", null, (s, e) => PasteItems(_lastWorkspaceMenuLocation));
        _workspaceMenu.Items.Add(new ToolStripSeparator());
        _workspaceMenu.Items.Add("Switch state file...", null, (s, e) =>
        {
            SwitchStateFile();
            showGridItem.Checked = _showGridDots;
        });

        _stateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDataDirName);
        Directory.CreateDirectory(_stateDirectory);
        var defaultStateFile = Path.Combine(_stateDirectory, "state.json");
        _appSettingsFile = Path.Combine(_stateDirectory, "_window-state.json");
        _lastStateFilePath = Path.Combine(_stateDirectory, "_last-state.txt");
        _stateFile = defaultStateFile;
        var oldPreferencePath = Path.Combine(_stateDirectory, "last-state.txt");
        try
        {
            if (!File.Exists(_lastStateFilePath) && File.Exists(oldPreferencePath))
                File.Move(oldPreferencePath, _lastStateFilePath);
            var lastStateFile = File.ReadAllText(_lastStateFilePath).Trim();
            if (!string.IsNullOrEmpty(lastStateFile) && File.Exists(lastStateFile))
                _stateFile = Path.GetFullPath(lastStateFile);
        }
        catch { }
        RefreshStateFileButtons();
        tabs.Tag = _board.Tabs;
        tabs.SelectedIndexChanged += Tabs_SelectedIndexChanged;

        LoadAppSettings();
        LoadState();
        _isInitialStateLoad = false;
        showGridItem.Checked = _showGridDots;
        if (tabs.TabPages.Count == 0)
            AddTab("Board");

        _currentTabPair = GetCurrentTabPair();
        _tabPairInitialized = true;

        FormClosing += (s, e) => SaveState();
        KeyPreview = true;
        KeyDown += Form1_KeyDown;
    }

    private void Tabs_SelectedIndexChanged(object? sender, EventArgs e)
    {
        UpdateWindowTitle();
        RecordTabPair();
        PopulateSubPage(SelectedSubPage);
        if (SelectedWorkspace() is Panel workspace)
            ApplyZoom(workspace);
    }

    private void Form1_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.T)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            TopMost = !TopMost;
            return;
        }
        if (e.Control && e.KeyCode == Keys.S)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            SaveState();
            return;
        }
        if (e.KeyCode == Keys.Escape && _searchPanel.Visible)
        {
            e.Handled = true;
            HideSearch();
            return;
        }
        // While search is open, Up/Down stay usable even when tab navigation stole focus
        if (_searchPanel.Visible && ActiveControl is not TextBox && e.KeyCode is Keys.Up or Keys.Down)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            MoveSearchSelection(e.KeyCode == Keys.Down ? 1 : -1);
            return;
        }
        if (e.Control && e.KeyCode == Keys.F)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ToggleSearch();
            return;
        }
        if (e.KeyCode == Keys.F5 && SelectedSubPage is TabPage textSubPage && PageFromSubPage(textSubPage).Type == "Text")
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ToggleMarkdownPreview();
            return;
        }
        if (e.Control && e.KeyCode == Keys.M)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (SelectedWorkspace() is Panel workspace)
            {
                var icons = workspace.Controls.OfType<Panel>()
                    .Where(p => p.Tag is IconItem && IsSelected(p))
                    .ToList();
                if (icons.Count == 0) return;
                var compact = icons.Any(p => !((IconItem)p.Tag!).Compact);
                foreach (var panel in icons)
                {
                    ((IconItem)panel.Tag!).Compact = compact;
                    ApplyZoomToControl(panel);
                }
                if (compact)
                {
                    var moving = icons.Cast<Control>().ToHashSet();
                    var placed = new List<Rectangle>();
                    foreach (var panel in icons)
                    {
                        panel.Location = SnapCompactIconToList(workspace, panel, panel.Location, moving, placed);
                        var item = (IconItem)panel.Tag!;
                        item.X = (int)(panel.Left / _zoom);
                        item.Y = (int)(panel.Top / _zoom);
                        placed.Add(panel.Bounds);
                    }
                }
                _board.Dirty();
            }
            return;
        }
        if (e.KeyCode == Keys.F1)
        {
            e.Handled = true;
            try { Process.Start(new ProcessStartInfo(FindReadme() ?? "README.md") { UseShellExecute = true }); }
            catch { }
            return;
        }
        if (ActiveControl is TextBox) return;

        if (e.Control && e.KeyCode == Keys.Left)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            NavigateTabPairHistory(back: true);
            return;
        }
        if (e.Control && e.KeyCode == Keys.Right)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            NavigateTabPairHistory(back: false);
            return;
        }

        if (e.KeyCode == Keys.R && e.Control)
        {
            e.Handled = true;
            ReloadState();
            return;
        }
        if (e.KeyCode == Keys.Delete)
        {
            e.Handled = true;
            if (SelectedWorkspace() is Panel workspace)
                DeleteSelectedItems(workspace);
            return;
        }
        if (e.KeyCode == Keys.F2)
        {
            e.Handled = true;
            if (SelectedWorkspace() is Panel workspace)
                RenameSelected(workspace);
            return;
        }
        if (e.Control)
        {
            if (e.KeyCode == Keys.C)
            {
                e.Handled = true;
                if (SelectedWorkspace() is Panel workspace)
                    CopySelectedItems(workspace);
                return;
            }
            if (e.KeyCode == Keys.X)
            {
                e.Handled = true;
                if (SelectedWorkspace() is Panel workspace)
                    CutSelectedItems(workspace);
                return;
            }
            if (e.KeyCode == Keys.V)
            {
                e.Handled = true;
                PasteItems();
                return;
            }
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
        foreach (TabPage page in tabs.TabPages.Cast<TabPage>().ToList())
        {
            var subTabs = SubTabsFromPage(page);
            foreach (TabPage sub in subTabs.TabPages.Cast<TabPage>().ToList())
            {
                var workspace = WorkspaceFromSubPage(sub);
                ClearDragGhosts(workspace);
                foreach (Control c in workspace.Controls.Cast<Control>().ToList()) DisposeItemControl(c);
            }
        }
        tabs.TabPages.Clear();
        _board.Tabs.Clear();
        _populatedSubPages.Clear();
        _tabHistoryBack.Clear();
        _tabHistoryForward.Clear();
        _currentTabPair = (-1, -1);
        _tabPairInitialized = false;
        LoadState();
        _currentTabPair = GetCurrentTabPair();
        _tabPairInitialized = true;
    }

    private void NavigateTabPairHistory(bool back)
    {
        var source = back ? _tabHistoryBack : _tabHistoryForward;
        var dest = back ? _tabHistoryForward : _tabHistoryBack;
        var current = GetCurrentTabPair();
        while (source.Count > 0)
        {
            var candidate = source.Peek();
            if (IsTabPairValid(candidate) && candidate != current) break;
            source.Pop();
        }
        if (source.Count == 0) return;
        var target = source.Pop();
        dest.Push(current);
        _tabHistoryNavigating = true;
        tabs.SelectedIndex = target.Top;
        if (SelectedSubTabs is TabControl subTabs && target.Sub >= 0 && target.Sub < subTabs.TabPages.Count)
            subTabs.SelectedIndex = target.Sub;
        _currentTabPair = GetCurrentTabPair();
        _tabHistoryNavigating = false;
    }

    private void RecordTabPair()
    {
        if (_tabHistoryNavigating || !_tabPairInitialized) return;
        var pair = GetCurrentTabPair();
        if (pair != _currentTabPair)
        {
            _tabHistoryBack.Push(_currentTabPair);
            _tabHistoryForward.Clear();
            _currentTabPair = pair;
        }
    }

    private (int Top, int Sub) GetCurrentTabPair() =>
        (tabs.SelectedIndex, SelectedSubTabs?.SelectedIndex ?? -1);

    private bool IsTabPairValid((int Top, int Sub) pair) =>
        pair.Top >= 0 && pair.Top < tabs.TabPages.Count &&
        SubTabsFromPage(tabs.TabPages[pair.Top]) is TabControl subTabs &&
        pair.Sub >= 0 && pair.Sub < subTabs.TabPages.Count;

    // ---------- tabs ----------

    private Tab AddTab(string name, int index = -1)
    {
        var tab = new Tab(name);
        tab.Pages.Add(new Page("Page 1"));
        if (index < 0 || index >= _board.Tabs.Count)
            _board.Tabs.Add(tab);
        else
            _board.Tabs.Insert(index, tab);
        CreateTabPage(tab, index);
        return tab;
    }

    private Page AddPage(string name, int index = -1)
    {
        var tab = TabFromPage(tabs.SelectedTab!);
        var page = new Page(name);
        var subTabs = SelectedSubTabs!;
        if (index < 0 || index >= subTabs.TabPages.Count)
        {
            tab.Pages.Add(page);
            subTabs.TabPages.Add(CreateSubPage(page));
        }
        else
        {
            tab.Pages.Insert(index, page);
            subTabs.TabPages.Insert(index, CreateSubPage(page));
        }
        subTabs.SelectedIndex = index < 0 ? subTabs.TabPages.Count - 1 : index;

        var newName = Prompt("Rename page", "Page name:", page.Name);
        if (!string.IsNullOrWhiteSpace(newName))
        {
            page.Name = newName;
            subTabs.SelectedTab!.Text = newName;
        }
        return page;
    }

    private Page AddTextPage(string name, int index = -1)
    {
        var page = AddPage(name, index);
        page.Type = "Text";
        if (SelectedWorkspace() is Panel workspace)
            UpdateTextPageView(workspace, page);
        return page;
    }

    private TabPage CreateTabPage(Tab tab, int index = -1)
    {
        var page = new TabPage(tab.Name);
        var subTabs = new TabControl { Dock = DockStyle.Fill, Tag = tab.Pages, DrawMode = TabDrawMode.OwnerDrawFixed };
        subTabs.DrawItem += Tabs_DrawItem;
        subTabs.MouseUp += SubTabs_MouseUp;
        subTabs.MouseDoubleClick += SubTabs_MouseDoubleClick;
        subTabs.MouseDown += SubTabs_MouseDown;
        subTabs.MouseMove += SubTabs_MouseMove;
        subTabs.MouseUp += SubTabs_MouseUpReorder;
        subTabs.ContextMenuStrip = _subTabMenu;
        subTabs.SelectedIndexChanged += SubTabs_SelectedIndexChanged;
        page.Controls.Add(subTabs);
        page.Tag = tab;

        if (index < 0 || index >= tabs.TabPages.Count)
            tabs.TabPages.Add(page);
        else
            tabs.TabPages.Insert(index, page);
        tabs.SelectedTab = page;

        foreach (var p in tab.Pages)
            subTabs.TabPages.Add(CreateSubPage(p));

        if (subTabs.TabPages.Count > 0)
            subTabs.SelectedIndex = Math.Clamp(tab.SelectedPageIndex, 0, subTabs.TabPages.Count - 1);

        return page;
    }

    private TabPage CreateSubPage(Page page)
    {
        var subPage = new TabPage(page.Name);
        var workspace = CreateWorkspace(page);
        subPage.Controls.Add(workspace);
        subPage.Tag = page;
        return subPage;
    }

    private void PopulateSubPage(TabPage? subPage)
    {
        if (_suppressPopulate || subPage is null || _populatedSubPages.Contains(subPage)) return;
        _populatedSubPages.Add(subPage);
        var page = PageFromSubPage(subPage);
        var workspace = WorkspaceFromSubPage(subPage);
        if (page.Type == "Text")
            UpdateTextPageView(workspace, page);
        else
            foreach (var item in page.Items)
                CreateView(workspace, item);
    }

    private void ToggleMarkdownPreview()
    {
        if (SelectedSubPage is not TabPage subPage) return;
        var page = PageFromSubPage(subPage);
        if (page.Type != "Text") return;
        page.MarkdownPreview = !page.MarkdownPreview;
        UpdateTextPageView(WorkspaceFromSubPage(subPage), page);
        _board.Dirty();
    }

    private void UpdateTextPageView(Panel workspace, Page page)
    {
        var editor = workspace.Controls.OfType<TextBox>().FirstOrDefault();
        if (editor is not null)
        {
            page.TextSelectionStart = editor.SelectionStart;
            page.TextSelectionLength = editor.SelectionLength;
        }
        bool restoreEditor = !page.MarkdownPreview && workspace.Controls.OfType<Panel>()
            .Any(panel => panel.Controls.OfType<MarkdownTextView>().Any());
        foreach (Control control in workspace.Controls.Cast<Control>().ToList())
        {
            workspace.Controls.Remove(control);
            control.Dispose();
        }
        if (page.MarkdownPreview)
        {
            var preview = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = SystemColors.Window };
            var markdown = new MarkdownTextView
            {
                Location = new Point(12, 12),
                Font = TitleFont,
                ForeColor = SystemColors.WindowText,
                Markdown = page.Text
            };
            preview.Controls.Add(markdown);
            preview.Resize += (s, e) => ResizeMarkdownPreview(preview, markdown);
            workspace.Controls.Add(preview);
            ResizeMarkdownPreview(preview, markdown);
        }
        else
        {
            var box = CreateTextBox(workspace, page);
            if (restoreEditor)
            {
                box.SelectionStart = Math.Clamp(page.TextSelectionStart, 0, box.TextLength);
                box.SelectionLength = Math.Clamp(page.TextSelectionLength, 0, box.TextLength - box.SelectionStart);
                box.Focus();
            }
        }
    }

    private static void ResizeMarkdownPreview(Panel preview, MarkdownTextView markdown)
    {
        int width = Math.Max(1, preview.ClientSize.Width - 24);
        markdown.Size = new Size(width, markdown.ContentHeight(width));
    }

    private TextBox CreateTextBox(Panel workspace, Page page)
    {
        var box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            Font = page.Monospace ? MonospaceFont : TitleFont,
            Text = page.Text
        };
        box.TextChanged += (s, e) => page.Text = box.Text;
        workspace.Controls.Add(box);
        return box;
    }

    private Panel CreateWorkspace(Page page)
    {
        var workspace = new Panel
        {
            Dock = DockStyle.Fill,
            AllowDrop = true,
            BackColor = WorkspaceBackColor,
            Tag = page
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
        return workspace;
    }

    private void SubTabs_SelectedIndexChanged(object? sender, EventArgs e)
    {
        UpdateWindowTitle();
        RecordTabPair();
        PopulateSubPage(SelectedSubPage);
        if (SelectedWorkspace() is Panel workspace)
            ApplyZoom(workspace);
    }

    private static TabControl SubTabsFromPage(TabPage page) => (TabControl)page.Controls[0];
    private static Panel WorkspaceFromSubPage(TabPage sub) => (Panel)sub.Controls[0];
    private static Page PageFromSubPage(TabPage sub) => (Page)sub.Tag!;
    private static Page PageFromWorkspace(Panel workspace) => (Page)workspace.Tag!;
    private void UpdateWindowTitle()
    {
        var title = $"Shortcutz - {Path.GetFileName(_stateFile)}";
        if (tabs.SelectedTab is TabPage parentPage && SubTabsFromPage(parentPage).SelectedTab is TabPage childPage)
            title += $"/{TabFromPage(parentPage).Name}/{PageFromSubPage(childPage).Name}";
        Text = title;
    }
    private TabPage? SelectedSubPage => tabs.SelectedTab is TabPage p && SubTabsFromPage(p).SelectedTab is TabPage s ? s : null;
    private TabControl? SelectedSubTabs => tabs.SelectedTab is TabPage p ? SubTabsFromPage(p) : null;
    private Panel? SelectedWorkspace() => SelectedSubPage is TabPage sub ? WorkspaceFromSubPage(sub) : null;
    private static Tab TabFromPage(TabPage page) => (Tab)page.Tag!;
    private static bool ItemExists(string path) => Directory.Exists(path) || File.Exists(path) || IsUrl(path);

    // ---------- prompt ----------

    private string? Prompt(string title, string label, string initial, bool multiline = false)
    {
        using var f = new PromptForm(title, label, initial, multiline);
        return f.ShowDialog(this) == DialogResult.OK ? f.Result : null;
    }
}
