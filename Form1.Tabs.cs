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

}
