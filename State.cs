namespace Shortcutz;

// Serialization DTOs
public sealed record ItemState(string Path, int X, int Y, bool IsNote = false, string? Text = null, string? Label = null, int? Width = null);
public sealed record TabState(string Name, List<ItemState> Items, float? Zoom = 1.0f);
public sealed record WindowState(int X, int Y, int Width, int Height);
public sealed record AppState(List<TabState> Tabs, int SelectedTabIndex = 0, WindowState? Window = null, bool? ShowGridDots = false);

// Model (source of truth; controls are views over this)
public abstract class Item(int x, int y)
{
    public int X = x, Y = y;
    public abstract ItemState ToState();
}

public sealed class IconItem(string path, int x, int y, string? label) : Item(x, y)
{
    public string Path = path;
    public string? Label = label;
    public override ItemState ToState() => new(Path, X, Y, false, null, Label);
}

public sealed class NoteItem(string text, int x, int y, int width = NoteItem.DefaultWidth) : Item(x, y)
{
    public const int DefaultWidth = 160;
    public const int MinWidth = 40;
    public const int MaxWidth = 600;
    public string Text = text;
    public int Width = width;
    public override ItemState ToState() => new("", X, Y, true, Text, null, Width);
}

public sealed class Tab(string name)
{
    public string Name = name;
    public List<Item> Items = new();
    public float Zoom = 1.0f;
    public TabState ToState() => new(Name, Items.ConvertAll(i => i.ToState()), Zoom);
}

public sealed class Board
{
    public List<Tab> Tabs = new();
    public int SelectedIndex;
    public bool ShowGridDots;
    public event Action? Changed;
    public void Dirty() => Changed?.Invoke();
    public AppState ToState(WindowState? window = null) => new(Tabs.ConvertAll(t => t.ToState()), SelectedIndex, window, ShowGridDots);
}
