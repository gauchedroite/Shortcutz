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
    private const int GridSize = 20;
    // EditControl: break on hyphens and hard-break words longer than the line.
    // Label paints with its own flags (WordBreak only), so titles are drawn by hand below.
    private const TextFormatFlags TitleFlags =
        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl |
        TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private const string AppDataDirName = "Shortcutz";

    private readonly string _stateFile;
    private readonly string _stateArchive;
    private static readonly string? _sevenZip = Find7z();
    private readonly ContextMenuStrip _tabMenu;
    private readonly ContextMenuStrip _subTabMenu;
    private readonly ContextMenuStrip _workspaceMenu;
    private readonly TabControl tabs;
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

    public Form1()
    {
        tabs = new TabControl { Dock = DockStyle.Fill, DrawMode = TabDrawMode.OwnerDrawFixed };
        tabs.DrawItem += Tabs_DrawItem;
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
        tabs.ContextMenuStrip = _tabMenu;
        tabs.MouseUp += Tabs_MouseUp;
        tabs.MouseDoubleClick += Tabs_MouseDoubleClick;
        tabs.MouseDown += Tabs_MouseDown;
        tabs.MouseMove += Tabs_MouseMove;
        tabs.MouseUp += Tabs_MouseUpReorder;

        _subTabMenu = new ContextMenuStrip();
        _subTabMenu.Items.Add("Add page", null, (s, e) => { AddPage("New page", SelectedSubTabs?.SelectedIndex + 1 ?? -1); _board.Dirty(); });
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

        _stateFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDataDirName, "state.json");
        _stateArchive = _stateFile + ".7z";
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        tabs.Tag = _board.Tabs;
        
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
        if (SelectedWorkspace() is Panel workspace)
            ApplyZoom(workspace);
    }

    private void Form1_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F1)
        {
            e.Handled = true;
            try { Process.Start(new ProcessStartInfo(FindReadme() ?? "README.md") { UseShellExecute = true }); }
            catch { }
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
        _board.Changed -= SaveState;
        try
        {
            foreach (TabPage page in tabs.TabPages.Cast<TabPage>().ToList())
            {
                var subTabs = SubTabsFromPage(page);
                foreach (TabPage sub in subTabs.TabPages.Cast<TabPage>().ToList())
                {
                    var workspace = WorkspaceFromSubPage(sub);
                    foreach (Control c in workspace.Controls) DisposeItemControl(c);
                }
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
        foreach (var item in page.Items)
            CreateView(workspace, item);
        subPage.Controls.Add(workspace);
        subPage.Tag = page;
        return subPage;
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
        if (SelectedWorkspace() is Panel workspace)
            ApplyZoom(workspace);
    }

    private static TabControl SubTabsFromPage(TabPage page) => (TabControl)page.Controls[0];
    private static Panel WorkspaceFromSubPage(TabPage sub) => (Panel)sub.Controls[0];
    private static Page PageFromSubPage(TabPage sub) => (Page)sub.Tag!;
    private static Page PageFromWorkspace(Panel workspace) => (Page)workspace.Tag!;
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
