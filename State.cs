namespace Shortcutz;

// Serialization DTOs
public sealed record ItemState(string Path, int X, int Y, bool IsNote = false, string? Text = null, string? Label = null, int? Width = null, string? Color = null);
public sealed record PageState(string Name, List<ItemState>? Items = null, float? Zoom = 1.0f, string? Type = "Board", string? Text = null);
public sealed record TabState(string Name, List<PageState>? Pages = null,
    int? SelectedPageIndex = 0,
    [property: System.Text.Json.Serialization.JsonPropertyName("Items")]
    List<ItemState>? LegacyItems = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("Zoom")]
    float? LegacyZoom = null);
public sealed record WindowState(int X, int Y, int Width, int Height);
public sealed record AppState(List<TabState> Tabs, int SelectedTabIndex = 0, WindowState? Window = null, bool? ShowGridDots = false);

// Model (source of truth; controls are views over this)
public abstract class Item(int x, int y)
{
    public int X = x, Y = y;
    public abstract ItemState ToState();
}

public sealed class IconItem(string path, int x, int y, string? label, string? color = null) : Item(x, y)
{
    public string Path = path;
    public string? Label = label;
    public string? Color = color;
    public override ItemState ToState() => new(Path, X, Y, false, null, Label, null, Color);
}

public sealed class NoteItem(string text, int x, int y, int width = NoteItem.DefaultWidth, string? color = null) : Item(x, y)
{
    public const int DefaultWidth = 160;
    public const int MinWidth = 40;
    public const int MaxWidth = 600;
    public string Text = text;
    public int Width = width;
    public string? Color = color;
    public override ItemState ToState() => new("", X, Y, true, Text, null, Width, Color);
}

public sealed class Page(string name)
{
    public string Name = name;
    public string Type = "Board";
    public string Text = "";
    public List<Item> Items = new();
    public float Zoom = 1.0f;
    public PageState ToState() => new(Name, Items.ConvertAll(i => i.ToState()), Zoom, Type, string.IsNullOrWhiteSpace(Text) ? null : Text);
}

public sealed class Tab(string name)
{
    public string Name = name;
    public List<Page> Pages = new();
    public int SelectedPageIndex;
    public TabState ToState() => new(Name, Pages.ConvertAll(p => p.ToState()), SelectedPageIndex);
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
