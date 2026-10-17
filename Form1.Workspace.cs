using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shortcutz;

public partial class Form1
{
    private static readonly Color NoteBackColor = Color.FromArgb(220, 255, 255, 200);
    private static readonly Color NoteSelectionBackColor = Color.FromArgb(255, 255, 240, 150);

    private static Color WorkspaceBackColor => Color.FromArgb(240, 240, 240);

    private static Color? ParseColor(string? name) => name?.ToLowerInvariant() switch
    {
        "yellow" => Color.FromArgb(220, 255, 255, 150),
        "green" => Color.FromArgb(220, 200, 255, 200),
        "red" => Color.FromArgb(220, 255, 200, 200),
        "gray" => WorkspaceBackColor,
        "transparent" => Color.Transparent,
        _ => null
    };

    private static Color NoteBaseColor(NoteItem item) => ParseColor(item.Color) ?? NoteBackColor;

    private static Color IconBaseColor(IconItem item) => ParseColor(item.Color) ?? Color.Transparent;

    private static Color IconSelectionColor(IconItem item)
    {
        var baseColor = IconBaseColor(item);
        return baseColor.A == 0 ? SelectionTint() : ControlPaint.Light(baseColor, 0.7f);
    }

    private static Color SelectionTint()
    {
        var c = SystemColors.Highlight;
        return Color.FromArgb(26, c.R, c.G, c.B);
    }

    private static bool IsSelectable(Control c) =>
        (c is Panel && c.Tag is IconItem) || (c is Label && c.Tag is NoteItem);

    private static bool IsSelected(Control c) => c switch
    {
        Panel p when p.Tag is IconItem icon => p.BackColor != IconBaseColor(icon),
        Label note when note.Tag is NoteItem item => note.BackColor != NoteBaseColor(item),
        _ => false
    };

    private static void SetSelected(Control c, bool selected)
    {
        if (c is Panel p)
        {
            if (p.Tag is IconItem icon)
                p.BackColor = selected ? IconSelectionColor(icon) : IconBaseColor(icon);
            else
                p.BackColor = selected ? SelectionTint() : Color.Transparent;
            if (p.Controls.Count > 1 && p.Controls[1] is Label title)
                title.ForeColor = SystemColors.WindowText;
        }
        else if (c is Label note)
        {
            note.BackColor = selected ? NoteSelectionBackColor : NoteBaseColor((NoteItem)note.Tag!);
            note.Invalidate(true);
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
        var files = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? (string[]?)e.Data.GetData(DataFormats.FileDrop) ?? Array.Empty<string>()
            : Array.Empty<string>();
        if (files.Any(ItemExists))
            e.Effect = DragDropEffects.Link;
        else if (TryGetOutlookMessageNames(e.Data, out _))
            e.Effect = DragDropEffects.Link;
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
        var page = PageFromWorkspace(workspace);
        var iconSize = new Size(110, 90);

        int i = 0;
        var files = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? (string[]?)e.Data.GetData(DataFormats.FileDrop) ?? Array.Empty<string>()
            : Array.Empty<string>();
        if (files.Any(ItemExists))
        {
            foreach (var path in files.Where(ItemExists))
            {
                try
                {
                    var itemPath = path;
                    if (Path.GetExtension(path).Equals(".msg", StringComparison.OrdinalIgnoreCase))
                    {
                        using var contents = File.OpenRead(path);
                        itemPath = StoreOutlookMessage(Path.GetFileName(path), contents);
                    }
                    var loc = SnapToGrid(workspace, iconSize,
                        new Point(dropPoint.X + i % 3 * (int)(GridSize * _zoom), dropPoint.Y + i / 3 * (int)(GridSize * _zoom)));
                    var label = File.Exists(path) ? Path.GetFileNameWithoutExtension(path) : null;
                    var item = new IconItem(itemPath, (int)(loc.X / _zoom), (int)(loc.Y / _zoom), label);
                    page.Items.Add(item);
                    CreateIconView(workspace, item);
                    i++;
                }
                catch (Exception ex)
                {
                    ShowOutlookImportError(ex, e.Data);
                }
            }
        }
        else if (TryGetOutlookMessageNames(e.Data, out var messages))
        {
            int? indexBase = null;
            foreach (var (descriptorIndex, messageName) in messages)
            {
                try
                {
                    using var contents = GetOutlookMessageContents(e.Data, descriptorIndex, indexBase, out var detectedBase);
                    if (contents is null)
                        throw new InvalidDataException($"Outlook did not provide contents for '{messageName}'.");
                    indexBase ??= detectedBase;
                    var path = StoreOutlookMessage(messageName, contents);
                    var loc = SnapToGrid(workspace, iconSize,
                        new Point(dropPoint.X + i % 3 * (int)(GridSize * _zoom), dropPoint.Y + i / 3 * (int)(GridSize * _zoom)));
                    var item = new IconItem(path, (int)(loc.X / _zoom), (int)(loc.Y / _zoom), Path.GetFileNameWithoutExtension(messageName));
                    page.Items.Add(item);
                    CreateIconView(workspace, item);
                    i++;
                }
                catch (Exception ex)
                {
                    ShowOutlookImportError(ex, e.Data);
                    break;
                }
            }
        }
        else if (e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.Html))
        {
            var text = (string?)e.Data.GetData(DataFormats.Text) ?? "";
            if (IsUrl(text))
            {
                var url = NormalizeUrl(text);
                var label = ExtractUrlTitle(e.Data, url);
                var loc = SnapToGrid(workspace, iconSize,
                    new Point(dropPoint.X + i % 3 * (int)(GridSize * _zoom), dropPoint.Y + i / 3 * (int)(GridSize * _zoom)));
                var item = new IconItem(url, (int)(loc.X / _zoom), (int)(loc.Y / _zoom), label);
                page.Items.Add(item);
                var p = CreateIconView(workspace, item);
                if (label is null && p.Controls[1] is Label titleLabel)
                    _ = FetchUrlTitleAsync(url, item, titleLabel, p);
            }
        }
        _board.Dirty();
    }

    private static bool TryGetOutlookMessageNames(IDataObject data, out List<(int Index, string Name)> names)
    {
        names = new();
        var wide = data.GetDataPresent("FileGroupDescriptorW", false);
        var format = wide ? "FileGroupDescriptorW" : "FileGroupDescriptor";
        if (!wide && !data.GetDataPresent(format, false)) return false;
        using var descriptor = GetDataStream(data, format);
        if (descriptor is null) return false;
        using var buffer = new MemoryStream();
        descriptor.CopyTo(buffer);
        var bytes = buffer.ToArray();
        const int descriptorHeaderSize = 4;
        const int nameOffset = 72;
        const int maxPath = 260;
        var nameBytes = maxPath * (wide ? 2 : 1);
        var descriptorSize = nameOffset + nameBytes;
        var count = bytes.Length >= descriptorHeaderSize ? BitConverter.ToInt32(bytes, 0) : 0;
        if (count < 1 || bytes.Length < descriptorHeaderSize + (long)count * descriptorSize)
            return false;
        for (var index = 0; index < count; index++)
        {
            var offset = descriptorHeaderSize + index * descriptorSize + nameOffset;
            string descriptorName;
            if (wide)
            {
                descriptorName = Encoding.Unicode.GetString(bytes, offset, nameBytes);
                var end = descriptorName.IndexOf('\0');
                if (end >= 0) descriptorName = descriptorName[..end];
            }
            else
            {
                var end = Array.IndexOf(bytes, (byte)0, offset, maxPath);
                if (end < 0) continue;
                var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try { descriptorName = Marshal.PtrToStringAnsi(IntPtr.Add(handle.AddrOfPinnedObject(), offset), end - offset) ?? ""; }
                finally { handle.Free(); }
            }
            var name = Path.GetFileName(descriptorName);
            if (!string.IsNullOrWhiteSpace(name) && Path.GetExtension(name).Equals(".msg", StringComparison.OrdinalIgnoreCase))
                names.Add((index, name));
        }
        return names.Count > 0;
    }

    private static Stream? GetDataStream(IDataObject data, string format)
    {
        var value = data.GetData(format, false) ?? data.GetData(format, true);
        return value switch
        {
            Stream stream => stream,
            byte[] bytes => new MemoryStream(bytes, writable: false),
            System.Runtime.InteropServices.ComTypes.IStream stream => CopyComStream(stream),
            _ => null
        };
    }

    private static Stream? GetOutlookMessageContents(IDataObject data, int descriptorIndex, int? indexBase, out int? detectedBase)
    {
        detectedBase = indexBase;
        if (GetNativeOleDataObject(data) is not { } oleData)
            return GetDataStream(data, "FileContents");

        foreach (var candidateBase in indexBase is int known ? new[] { known } : new[] { 0, 1 })
        {
            if (GetIndexedFileContents(oleData, descriptorIndex + candidateBase) is not Stream contents)
                continue;
            detectedBase = candidateBase;
            return contents;
        }
        return descriptorIndex == 0 && data.GetDataPresent("FileNameW", false)
            ? GetOutlookFileNameContents(oleData)
            : null;
    }

    private static Stream? GetIndexedFileContents(System.Runtime.InteropServices.ComTypes.IDataObject oleData, int index)
    {
        foreach (var tymed in new[]
        {
            System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ISTREAM,
            System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ISTORAGE,
            System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
        })
        {
            var format = new System.Runtime.InteropServices.ComTypes.FORMATETC
            {
                cfFormat = unchecked((short)DataFormats.GetFormat("FileContents").Id),
                dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
                lindex = index,
                ptd = IntPtr.Zero,
                tymed = tymed
            };
            System.Runtime.InteropServices.ComTypes.STGMEDIUM medium = default;
            try
            {
                oleData.GetData(ref format, out medium);
                if (medium.tymed == System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ISTREAM)
                {
                    var stream = (System.Runtime.InteropServices.ComTypes.IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                    try { return CopyComStream(stream); }
                    finally { Marshal.ReleaseComObject(stream); }
                }
                if (medium.tymed == System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ISTORAGE)
                {
                    var storage = (NativeIStorage)Marshal.GetObjectForIUnknown(medium.unionmember);
                    try { return CopyComStorageToTempStream(storage); }
                    finally { Marshal.ReleaseComObject(storage); }
                }
                if (medium.tymed == System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL)
                    return CopyGlobalMemory(medium.unionmember);
            }
            catch (COMException ex) when (ex.HResult is unchecked((int)0x80040064) or unchecked((int)0x80040068)) { }
            finally
            {
                if (medium.tymed != System.Runtime.InteropServices.ComTypes.TYMED.TYMED_NULL)
                    ReleaseStgMedium(ref medium);
            }
        }
        return null;
    }

    // shortcut: reaches through WinForms' private data wrapper for indexed virtual-file access; replace if its internals change.
    private static System.Runtime.InteropServices.ComTypes.IDataObject? GetNativeOleDataObject(IDataObject data)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var wrapper = typeof(DataObject).GetField("_innerData", flags)?.GetValue(data);
        return wrapper?.GetType().GetField("_runtimeDataObject", flags)?.GetValue(wrapper)
            as System.Runtime.InteropServices.ComTypes.IDataObject;
    }

    private static Stream? GetOutlookFileNameContents(System.Runtime.InteropServices.ComTypes.IDataObject oleData)
    {
        var format = new System.Runtime.InteropServices.ComTypes.FORMATETC
        {
            cfFormat = unchecked((short)DataFormats.GetFormat("FileNameW").Id),
            dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            ptd = IntPtr.Zero,
            tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_FILE
        };
        oleData.GetData(ref format, out var medium);
        try
        {
            if (medium.tymed != System.Runtime.InteropServices.ComTypes.TYMED.TYMED_FILE) return null;
            var path = Marshal.PtrToStringUni(medium.unionmember);
            if (path is null || !File.Exists(path)) return null;
            using var file = File.OpenRead(path);
            var contents = new MemoryStream();
            file.CopyTo(contents);
            contents.Position = 0;
            return contents;
        }
        finally { ReleaseStgMedium(ref medium); }
    }

    private static Stream CopyGlobalMemory(IntPtr handle)
    {
        var size = GlobalSize(handle).ToUInt64();
        if (size > int.MaxValue) throw new InvalidDataException("The Outlook message is too large to import.");
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("Could not read Outlook message contents.");
        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return new MemoryStream(bytes, writable: false);
        }
        finally { GlobalUnlock(handle); }
    }

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium);

    [ComImport]
    [Guid("0000000B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface NativeIStorage
    {
        [PreserveSig] int CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name, int mode, int reserved1, int reserved2, out System.Runtime.InteropServices.ComTypes.IStream stream);
        [PreserveSig] int OpenStream([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr reserved1, int mode, int reserved2, out System.Runtime.InteropServices.ComTypes.IStream stream);
        [PreserveSig] int CreateStorage([MarshalAs(UnmanagedType.LPWStr)] string name, int mode, int reserved1, int reserved2, out NativeIStorage storage);
        [PreserveSig] int OpenStorage([MarshalAs(UnmanagedType.LPWStr)] string name, NativeIStorage? priority, int mode, IntPtr exclude, int reserved, out NativeIStorage storage);
        [PreserveSig] int CopyTo(int excludeCount, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] Guid[]? excludedInterfaces, IntPtr excludeNames, NativeIStorage destination);
        [PreserveSig] int MoveElementTo([MarshalAs(UnmanagedType.LPWStr)] string name, NativeIStorage destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, int flags);
        [PreserveSig] int Commit(int flags);
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void StgCreateDocfile(string path, int mode, int reserved, out NativeIStorage storage);

    private static Stream CopyComStorageToTempStream(NativeIStorage source)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".msg");
        NativeIStorage? destination = null;
        try
        {
            StgCreateDocfile(path, 0x1012, 0, out destination);
            Marshal.ThrowExceptionForHR(source.CopyTo(0, Array.Empty<Guid>(), IntPtr.Zero, destination));
            Marshal.ThrowExceptionForHR(destination.Commit(0));
            Marshal.ReleaseComObject(destination);
            destination = null;
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                4096, FileOptions.DeleteOnClose);
        }
        catch
        {
            if (destination is not null) Marshal.ReleaseComObject(destination);
            try { File.Delete(path); } catch { }
            throw;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr handle);

    private static Stream CopyComStream(System.Runtime.InteropServices.ComTypes.IStream source)
    {
        var output = new MemoryStream();
        const int chunkSize = 64 * 1024;
        var buffer = new byte[chunkSize];
        var bytesRead = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            while (true)
            {
                Marshal.WriteInt32(bytesRead, 0);
                source.Read(buffer, chunkSize, bytesRead);
                var count = Marshal.ReadInt32(bytesRead);
                if (count == 0) break;
                output.Write(buffer, 0, count);
            }
            output.Position = 0;
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(bytesRead);
        }
    }

    private void ShowOutlookImportError(Exception ex, IDataObject data)
    {
        string? logPath = null;
        string? logError = null;
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDataDirName, "outlook");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "import-errors.log");
            TryGetOutlookMessageNames(data, out var messages);
            File.AppendAllText(path,
                $"[{DateTimeOffset.Now:O}]\nFormats: {string.Join(", ", data.GetFormats(false))}\nMessages: {string.Join("; ", messages.Select(m => $"{m.Index}:{m.Name}"))}\nOLE formats: {DescribeOleFormats(data)}\nWrapper: {DescribeDataObject(data)}\nNative IDataObject: {GetNativeOleDataObject(data)?.GetType().FullName ?? "<not found>"}\n{ex}\n\n");
            logPath = path;
        }
        catch (Exception logException) { logError = logException.Message; }
        var details = logPath is not null
            ? $"Full details saved to:\n{logPath}"
            : $"Could not save the error log: {logError}";
        MessageBox.Show($"{ex.Message}\n\n{details}", "Could not import Outlook message", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static string DescribeOleFormats(IDataObject data)
    {
        try
        {
            var enumerator = GetNativeOleDataObject(data)?.EnumFormatEtc(System.Runtime.InteropServices.ComTypes.DATADIR.DATADIR_GET);
            if (enumerator is null) return "<unavailable>";
            var format = new System.Runtime.InteropServices.ComTypes.FORMATETC[1];
            var fetched = new int[1];
            var names = new List<string>();
            while (enumerator.Next(1, format, fetched) == 0 && fetched[0] == 1)
            {
                var item = format[0];
                names.Add($"{DataFormats.GetFormat(item.cfFormat).Name}[index={item.lindex}, tymed={item.tymed}, aspect={item.dwAspect}]");
            }
            var oleData = GetNativeOleDataObject(data);
            if (oleData is not null)
            {
                var maxIndex = TryGetOutlookMessageNames(data, out var messages) ? Math.Max(2, messages.Count + 1) : 4;
                foreach (var index in Enumerable.Range(0, Math.Min(maxIndex, 16)).Prepend(-1))
                    foreach (var tymed in new[]
                    {
                        System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ISTREAM,
                        System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ISTORAGE,
                        System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
                    })
                    {
                        var query = new System.Runtime.InteropServices.ComTypes.FORMATETC
                        {
                            cfFormat = unchecked((short)DataFormats.GetFormat("FileContents").Id),
                            dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
                            lindex = index,
                            ptd = IntPtr.Zero,
                            tymed = tymed
                        };
                        names.Add($"Query[{index},{tymed}]=0x{unchecked((uint)oleData.QueryGetData(ref query)):X8}");
                    }
            }
            return string.Join("; ", names);
        }
        catch (Exception ex) { return $"<error: {ex.Message}>"; }
    }

    private static string DescribeDataObject(IDataObject data)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var chain = new List<string>();
        object? current = data;
        for (var depth = 0; current is not null && depth < 4; depth++)
        {
            var type = current.GetType();
            chain.Add(type.FullName ?? type.Name);
            var field = type.GetField("_innerData", flags) ?? type.GetField("_runtimeDataObject", flags);
            current = field?.GetValue(current);
        }
        return string.Join(" -> ", chain);
    }

    private static string StoreOutlookMessage(string fileName, Stream contents)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDataDirName, "outlook");
        Directory.CreateDirectory(directory);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (var suffix = 1; ; suffix++)
        {
            var candidateName = suffix == 1 ? fileName : $"{stem} ({suffix}).msg";
            var path = Path.Combine(directory, candidateName);
            FileStream output;
            try { output = new FileStream(path, FileMode.CreateNew, FileAccess.Write); }
            catch (IOException) when (File.Exists(path)) { continue; }
            using (output)
            {
                try { contents.CopyTo(output); }
                catch
                {
                    output.Dispose();
                    try { File.Delete(path); } catch { }
                    throw;
                }
            }
            return path;
        }
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
            {
                _lastWorkspaceMenuLocation = e.Location;
                _workspaceMenu.Show(workspace, e.Location);
            }
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
        var loc = Clamp(workspace, new Size(120, 40), e.Location);
        var item = new NoteItem(text, (int)(loc.X / _zoom), (int)(loc.Y / _zoom));
        PageFromWorkspace(workspace).Items.Add(item);
        CreateNoteView(workspace, item);
        _board.Dirty();
    }

}
