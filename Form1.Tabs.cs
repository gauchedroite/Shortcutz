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
    private int _dragTabFrom = -1;
    private Point _dragTabStart;
    private bool _dragTabActive;

    private static int TabIndexAt(TabControl tc, Point p)
    {
        for (int i = 0; i < tc.TabPages.Count; i++)
            if (tc.GetTabRect(i).Contains(p)) return i;
        return -1;
    }

    private static System.Collections.IList? ModelListFor(TabControl tc) => tc.Tag as System.Collections.IList;

    private void Tabs_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || sender is not TabControl tc) return;
        _dragTabFrom = TabIndexAt(tc, e.Location);
        _dragTabStart = e.Location;
        _dragTabActive = false;
    }

    private void Tabs_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragTabFrom < 0 || e.Button != MouseButtons.Left || sender is not TabControl tc) return;
        if (!_dragTabActive && (Math.Abs(e.X - _dragTabStart.X) > 3 || Math.Abs(e.Y - _dragTabStart.Y) > 3))
            _dragTabActive = true;
        tc.Cursor = _dragTabActive && TabIndexAt(tc, e.Location) >= 0
            ? Cursors.Hand : Cursors.Default;
    }

    private void Tabs_MouseUpReorder(object? sender, MouseEventArgs e)
    {
        if (_dragTabFrom >= 0 && e.Button == MouseButtons.Left && sender is TabControl tc)
        {
            if (_dragTabActive)
            {
                var to = TabIndexAt(tc, e.Location);
                if (to >= 0 && to != _dragTabFrom)
                {
                    var page = tc.TabPages[_dragTabFrom];
                    var models = ModelListFor(tc);
                    var model = models?[_dragTabFrom];
                    tc.TabPages.RemoveAt(_dragTabFrom);
                    models?.RemoveAt(_dragTabFrom);
                    tc.TabPages.Insert(to, page);
                    if (model is not null && models is not null)
                        models.Insert(to, model);
                    tc.SelectedIndex = to;
                    _board.Dirty();
                }
            }
            tc.Cursor = Cursors.Default;
            _dragTabFrom = -1;
            _dragTabActive = false;
        }
    }

    private void Tabs_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || sender is not TabControl tc) return;
        for (int i = 0; i < tc.TabPages.Count; i++)
            if (tc.GetTabRect(i).Contains(e.Location))
            {
                tc.SelectedIndex = i;
                tc.ContextMenuStrip?.Show(tc, e.Location);
                return;
            }
    }

    private void Tabs_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || sender is not TabControl tc) return;
        for (int i = 0; i < tc.TabPages.Count; i++)
            if (tc.GetTabRect(i).Contains(e.Location))
            {
                tc.SelectedIndex = i;
                RenameTabIn(tc);
                return;
            }
    }

    private void SubTabs_MouseDown(object? sender, MouseEventArgs e) => Tabs_MouseDown(sender, e);
    private void SubTabs_MouseMove(object? sender, MouseEventArgs e) => Tabs_MouseMove(sender, e);
    private void SubTabs_MouseUpReorder(object? sender, MouseEventArgs e) => Tabs_MouseUpReorder(sender, e);
    private void SubTabs_MouseUp(object? sender, MouseEventArgs e) => Tabs_MouseUp(sender, e);
    private void SubTabs_MouseDoubleClick(object? sender, MouseEventArgs e) => Tabs_MouseDoubleClick(sender, e);

    private void RenameTab(object? sender, EventArgs e) => RenameTabIn(tabs);
    private void RenameSubTab(object? sender, EventArgs e) => RenameTabIn(SelectedSubTabs!);

    private void RenameTabIn(TabControl tc)
    {
        if (tc.SelectedTab is null) return;
        var model = tc.SelectedTab.Tag;
        var currentName = model switch
        {
            Tab tab => tab.Name,
            Page page => page.Name,
            _ => tc.SelectedTab.Text
        };
        var name = Prompt("Rename", "Name:", currentName);
        if (string.IsNullOrWhiteSpace(name)) return;
        switch (model)
        {
            case Tab tab: tab.Name = name; break;
            case Page page: page.Name = name; break;
        }
        tc.SelectedTab.Text = name;
        _board.Dirty();
    }

    private void CloseTab(object? sender, EventArgs e) => CloseTabIn(tabs);
    private void CloseSubTab(object? sender, EventArgs e) => CloseTabIn(SelectedSubTabs!);

    private void CloseTabIn(TabControl tc)
    {
        if (tc.TabPages.Count <= 1)
        {
            MessageBox.Show("You must keep at least one tab.", "Cannot close", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (tc.SelectedTab is null) return;
        var page = tc.SelectedTab;
        var model = page.Tag;
        var name = model switch
        {
            Tab tab => tab.Name,
            Page p => p.Name,
            _ => page.Text
        };
        if (MessageBox.Show(
                $"Close '{name}'? All items on it will be removed.",
                "Close?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        if (model is Page pageModel)
        {
            var workspace = WorkspaceFromSubPage(page);
            foreach (Control c in workspace.Controls) DisposeItemControl(c);
            var tab = TabFromPage(tabs.SelectedTab!);
            tab.Pages.Remove(pageModel);
            tc.TabPages.Remove(page);
        }
        else if (model is Tab tabModel)
        {
            var subTabs = SubTabsFromPage(page);
            foreach (TabPage sub in subTabs.TabPages.Cast<TabPage>().ToList())
            {
                var workspace = WorkspaceFromSubPage(sub);
                foreach (Control c in workspace.Controls) DisposeItemControl(c);
            }
            _board.Tabs.Remove(tabModel);
            tabs.TabPages.Remove(page);
        }
        _board.Dirty();
    }
}
