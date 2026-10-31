namespace Shortcutz;

internal static class StateTimestamp
{
    public static DateTimeOffset UtcNow => DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    public static DateTimeOffset? WholeSeconds(DateTimeOffset? value) => value is { } date ? DateTimeOffset.FromUnixTimeSeconds(date.ToUnixTimeSeconds()) : null;
}

// Serialization DTOs
public sealed record ItemState(string Path, int X, int Y, bool IsNote = false, string? Text = null, string? Label = null, int? Width = null, string? Color = null, bool? Compact = null, bool? IsImage = false, int? Height = null, int? ImageBaseWidth = null, int? ImageBaseHeight = null, DateTimeOffset? CreatedAt = null, DateTimeOffset? TextUpdatedAt = null);
public sealed record PageState(string Name, List<ItemState>? Items = null, float? Zoom = 1.0f, string? Type = "Board", string? Text = null, bool? CompactIcons = null, bool? Monospace = false, bool? MarkdownPreview = false, DateTimeOffset? CreatedAt = null, DateTimeOffset? TextUpdatedAt = null);
public sealed record TabState(string Name, List<PageState>? Pages = null,
    int? SelectedPageIndex = 0,
    [property: System.Text.Json.Serialization.JsonPropertyName("Items")]
    List<ItemState>? LegacyItems = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("Zoom")]
    float? LegacyZoom = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? TextUpdatedAt = null);
public sealed record WindowState(int X, int Y, int Width, int Height);
public sealed record AppSettings(int X, int Y, int Width, int Height, List<string>? StateFileOrder = null, string? LastStateFile = null);
// CompactIcons fields are kept only to migrate older saved states.
public sealed record AppState(List<TabState> Tabs, int SelectedTabIndex = 0, WindowState? Window = null, bool? ShowGridDots = false, bool? CompactIcons = null);

// Model (source of truth; controls are views over this)
public abstract class Item(int x, int y)
{
    public int X = x, Y = y;
    public DateTimeOffset? CreatedAt = StateTimestamp.UtcNow;
    public DateTimeOffset? TextUpdatedAt = StateTimestamp.UtcNow;
    public abstract ItemState ToState();
}

public sealed class IconItem(string path, int x, int y, string? label, string? color = null) : Item(x, y)
{
    public string Path = path;
    public string? Label = label;
    public string? Color = color;
    public bool Compact;
    public override ItemState ToState() => new(Path, X, Y, false, null, Label, null, Color, Compact, CreatedAt: StateTimestamp.WholeSeconds(CreatedAt), TextUpdatedAt: StateTimestamp.WholeSeconds(TextUpdatedAt));
}

public sealed class ImageItem(string path, int x, int y, int width, int height) : Item(x, y)
{
    public const int DefaultWidth = 400;
    public const int DefaultHeight = 300;
    public const int MaxScale = 5;
    public const float MinScale = 0.1f;
    public string Path = path;
    public string? Label;
    public bool Selected;
    public int Width = width;
    public int Height = height;
    public int BaseWidth = width;
    public int BaseHeight = height;
    public override ItemState ToState() => new(Path, X, Y, Label: Label, Width: Width, IsImage: true, Height: Height, ImageBaseWidth: BaseWidth, ImageBaseHeight: BaseHeight, CreatedAt: StateTimestamp.WholeSeconds(CreatedAt), TextUpdatedAt: StateTimestamp.WholeSeconds(TextUpdatedAt));
}

public sealed class NoteItem(string text, int x, int y, int width = NoteItem.DefaultWidth, string? color = null) : Item(x, y)
{
    public const int DefaultWidth = 240;
    public const int MinWidth = 40;
    public const int MaxWidth = 600;
    public string Text = text;
    public int Width = width;
    public string? Color = color;
    public override ItemState ToState() => new("", X, Y, true, Text, null, Width, Color, CreatedAt: StateTimestamp.WholeSeconds(CreatedAt), TextUpdatedAt: StateTimestamp.WholeSeconds(TextUpdatedAt));
}

public sealed class Page(string name)
{
    public string Name = name;
    public string Type = "Board";
    public string Text = "";
    public bool Monospace;
    public bool MarkdownPreview;
    public int TextSelectionStart;
    public int TextSelectionLength;
    public DateTimeOffset? CreatedAt = StateTimestamp.UtcNow;
    public DateTimeOffset? TextUpdatedAt = StateTimestamp.UtcNow;
    public string TextAtLastUpdate = "";
    public List<Item> Items = new();
    public float Zoom = 1.0f;
    public PageState ToState() => new(Name, Items.ConvertAll(i => i.ToState()), Zoom, Type, string.IsNullOrWhiteSpace(Text) ? null : Text, Monospace: Monospace, MarkdownPreview: MarkdownPreview, CreatedAt: StateTimestamp.WholeSeconds(CreatedAt), TextUpdatedAt: StateTimestamp.WholeSeconds(TextUpdatedAt));
}

public sealed class Tab(string name)
{
    public string Name = name;
    public DateTimeOffset? CreatedAt = StateTimestamp.UtcNow;
    public DateTimeOffset? TextUpdatedAt = StateTimestamp.UtcNow;
    public List<Page> Pages = new();
    public int SelectedPageIndex;
    public TabState ToState() => new(Name, Pages.ConvertAll(p => p.ToState()), SelectedPageIndex, CreatedAt: StateTimestamp.WholeSeconds(CreatedAt), TextUpdatedAt: StateTimestamp.WholeSeconds(TextUpdatedAt));
}

public sealed class Board
{
    public List<Tab> Tabs = new();
    public int SelectedIndex;
    public bool ShowGridDots;
    public event Action? Changed;
    public void Dirty() => Changed?.Invoke();
    public AppState ToState() => new(Tabs.ConvertAll(t => t.ToState()), SelectedIndex, ShowGridDots: ShowGridDots);
}
