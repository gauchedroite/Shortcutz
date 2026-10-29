namespace Shortcutz;

public partial class Form1
{
    private readonly Panel _searchPanel = new() { Dock = DockStyle.Top, Height = 34, Visible = false, BackColor = SystemColors.Control, Padding = new Padding(4) };
    private readonly TextBox _searchBox = new() { Dock = DockStyle.Fill, Font = TitleFont };
    private readonly ListBox _searchResults = new() { Dock = DockStyle.Top, Height = 150, Visible = false, Font = TitleFont };
    private readonly List<(Tab Tab, Page Page, Item? Item)> _searchMatches = new();

    private void InitSearch()
    {
        _searchPanel.Controls.Add(_searchBox);
        Controls.Add(_searchResults);
        Controls.Add(_searchPanel);
        _searchResults.BringToFront();
        _searchPanel.BringToFront();

        _searchBox.TextChanged += (s, e) => RunSearch();
        _searchBox.KeyDown += SearchBox_KeyDown;
        _searchResults.DoubleClick += (s, e) => ActivateSearchResult();
        // Live navigation: arrow keys and single clicks jump straight to the result
        _searchResults.SelectedIndexChanged += (s, e) => ActivateSearchResult();
        _searchResults.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; ActivateSearchResult(); }
            else if (e.KeyCode == Keys.Escape) { e.Handled = true; HideSearch(); }
        };
    }

    private void ToggleSearch()
    {
        if (_searchPanel.Visible) { HideSearch(); return; }
        _searchPanel.Visible = true;
        RunSearch(); // refresh stale results from a previous search session
        _searchBox.Focus();
        _searchBox.SelectAll();
    }

    private void HideSearch()
    {
        _searchPanel.Visible = false;
        _searchResults.Visible = false;
        ActiveControl = null;
    }

    private static bool Matches(string query, string? text) =>
        text?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ");

    private void RunSearch()
    {
        _searchResults.Items.Clear();
        _searchMatches.Clear();
        var query = _searchBox.Text.Trim();
        if (query.Length > 0)
        {
            foreach (var tab in _board.Tabs)
                foreach (var page in tab.Pages)
                {
                    if (page.Type == "Text")
                    {
                        if (Matches(query, tab.Name) || Matches(query, page.Name) || Matches(query, page.Text))
                            AddMatch(tab, page, null, $"{tab.Name} > {page.Name}");
                        continue;
                    }
                    if (Matches(query, tab.Name) || Matches(query, page.Name))
                        AddMatch(tab, page, null, $"{tab.Name} > {page.Name}");
                    foreach (var item in page.Items)
                    {
                        var text = item switch
                        {
                            IconItem i => (i.Label ?? "") + "\n" + i.Path,
                            NoteItem n => n.Text,
                            ImageItem i => (i.Label ?? "") + "\n" + i.Path,
                            _ => ""
                        };
                        if (!Matches(query, text)) continue;
                        var display = OneLine(GetItemDisplayName(item));
                        // Show the path when the hit is in the path, not in the displayed name
                        if (item is IconItem icon && Matches(query, icon.Path) && !Matches(query, display))
                            display += $"  ({icon.Path})";
                        if (item is ImageItem image && Matches(query, image.Path) && !Matches(query, display))
                            display += $"  ({image.Path})";
                        AddMatch(tab, page, item, $"{tab.Name} > {page.Name} > {display}");
                    }
                }
        }
        _searchResults.Visible = _searchMatches.Count > 0;
    }

    private void AddMatch(Tab tab, Page page, Item? item, string display)
    {
        _searchMatches.Add((tab, page, item));
        _searchResults.Items.Add(display);
    }

    private void RefocusSearchBox()
    {
        if (!_searchPanel.Visible || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (!_searchPanel.Visible || ActiveControl == _searchBox) return;
            _searchBox.Focus();
            if (ActiveControl != _searchBox) RefocusSearchBox();
        });
    }

    private void MoveSearchSelection(int delta)
    {
        if (_searchMatches.Count == 0) return;
        _searchResults.SelectedIndex = Math.Clamp(_searchResults.SelectedIndex + delta, 0, _searchMatches.Count - 1);
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Down or Keys.Up)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            // focus stays in the box; move the list selection, which triggers navigation
            MoveSearchSelection(e.KeyCode == Keys.Down ? 1 : -1);
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            HideSearch();
        }
        else if (e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (_searchResults.SelectedIndex < 0 && _searchResults.Items.Count > 0)
                _searchResults.SelectedIndex = 0;
            ActivateSearchResult();
        }
    }

    private void ActivateSearchResult()
    {
        var idx = _searchResults.SelectedIndex;
        if (idx < 0 || idx >= _searchMatches.Count) return;
        var (tab, page, item) = _searchMatches[idx];
        var tabIndex = _board.Tabs.IndexOf(tab);
        if (tabIndex < 0 || tabIndex >= tabs.TabPages.Count) return;
        tabs.SelectedIndex = tabIndex;
        var subTabs = SubTabsFromPage(tabs.TabPages[tabIndex]);
        var subIndex = tab.Pages.IndexOf(page);
        if (subIndex < 0 || subIndex >= subTabs.TabPages.Count) return;
        if (subTabs.SelectedIndex != subIndex)
            subTabs.SelectedIndex = subIndex;
        PopulateSubPage(subTabs.TabPages[subIndex]);
        // Tab/page selection steals focus after the selection events settle, so
        // refocus now and keep retrying until it sticks.
        _searchBox.Focus();
        RefocusSearchBox();
        if (item is null) return;
        var workspace = WorkspaceFromSubPage(subTabs.TabPages[subIndex]);
        var control = workspace.Controls.OfType<Control>().FirstOrDefault(c => c.Tag == item);
        if (control is not null)
            SelectSingle(workspace, control);
        // shortcut: no scroll-into-view for items outside the visible area, upgrade if boards get larger
    }
}
