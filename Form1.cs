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
        _stateArchive = _stateFile + ".7z";
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
            BackColor = WorkspaceBackColor
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

    // ---------- prompt ----------

    private string? Prompt(string title, string label, string initial, bool multiline = false)
    {
        using var f = new PromptForm(title, label, initial, multiline);
        return f.ShowDialog(this) == DialogResult.OK ? f.Result : null;
    }
}
