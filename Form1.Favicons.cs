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
    private async Task FetchUrlTitleAsync(string url, IconItem item, Label title, Panel panel)
    {
        string? newLabel = null;
        try
        {
            var html = await _http.GetStringAsync(url).ConfigureAwait(false);
            newLabel = ExtractTitleFromHtml(html);
        }
        catch { return; }
        if (string.IsNullOrWhiteSpace(newLabel)) return;
        try
        {
            panel.Invoke(() =>
            {
                if (panel.IsDisposed) return;
                UpdateIconLabel(item, title, panel, newLabel!);
                _board.Dirty();
            });
        }
        catch { }
    }

    private async Task FetchUrlFaviconAsync(string url, Panel panel, Label icon)
    {
        var cache = GetFaviconCachePath(url);
        if (File.Exists(cache))
        {
            try
            {
                var cached = LoadFaviconBitmap(cache);
                if (cached is not null)
                {
                    panel.Invoke(() =>
                    {
                        if (panel.IsDisposed) return;
                        var old = icon.Image;
                        icon.Image = cached;
                        icon.Tag = cached;
                        old?.Dispose();
                    });
                }
                return;
            }
            catch { }
        }
        try
        {
            var baseUri = new Uri(url);
            var bytes = await DownloadFaviconAsync(baseUri).ConfigureAwait(false);
            if (bytes is null) return;
            try { await File.WriteAllBytesAsync(cache, bytes); } catch { }
            using var ms = new MemoryStream(bytes);
            using var source = Image.FromStream(ms);
            var bmp = ResizeToIcon(source);
            panel.Invoke(() =>
            {
                if (panel.IsDisposed) return;
                var old = icon.Image;
                icon.Image = bmp;
                icon.Tag = bmp;
                old?.Dispose();
            });
        }
        catch { }
    }

    // Favicon ladder: the page's own <link rel="icon"> (handles subdomain /
    // SVG-only sites S2 misses, e.g. fms.yukon.ca -> favicon.svg), then the
    // conventional /favicon.ico, then Google faviconV2 (renders SVG server-side,
    // more reliable than the old s2 endpoint), then legacy s2 per apex walk.
    private async Task<byte[]?> DownloadFaviconAsync(Uri baseUri)
    {
        // 1. <link rel="*icon*"> declared in the page HTML
        string? html = null;
        try { html = await _http.GetStringAsync(baseUri).ConfigureAwait(false); }
        catch { }
        if (html is not null)
        {
            foreach (var href in ExtractIconLinks(html))
            {
                var data = await TryGetImageAsync(new Uri(baseUri, href)).ConfigureAwait(false);
                if (data is not null) return data;
            }
        }

        // 2. Conventional /favicon.ico
        {
            var data = await TryGetImageAsync(new Uri(baseUri, "/favicon.ico")).ConfigureAwait(false);
            if (data is not null) return data;
        }

        // 3. Google faviconV2 — fetches and renders the declared icon (incl. SVG)
        {
            var v2 = new Uri($"https://t1.gstatic.com/faviconV2?client=SOCIAL&type=FAVICON&fallback_opts=TYPE,SIZE,URL&url={Uri.EscapeDataString(baseUri.AbsoluteUri)}&size=64");
            var data = await TryGetImageAsync(v2).ConfigureAwait(false);
            if (data is not null) return data;
        }

        // 4. Legacy s2, walking subdomain -> apex
        var domain = baseUri.Host;
        while (domain is not null)
        {
            var data = await TryGetImageAsync(
                new Uri($"https://www.google.com/s2/favicons?domain={domain}&sz=64")).ConfigureAwait(false);
            if (data is not null) return data;
            domain = ParentDomain(domain);
        }
        return null;
    }

    // Download bytes and verify GDI+ can decode them — rejects SVG/WebP that
    // Image.FromStream cannot read, so the ladder just tries the next candidate.
    private async Task<byte[]?> TryGetImageAsync(Uri uri)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(uri).ConfigureAwait(false);
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            return bytes;
        }
        catch { return null; }
    }

    // <link rel="*icon*"> hrefs, best decodable first. SVG goes last because
    // GDI+ can't render it (it'll be tried, fail decode, and the ladder moves on).
    private static IEnumerable<string> ExtractIconLinks(string html)
    {
        var found = new List<(int priority, string href)>();
        foreach (Match m in Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = m.Value;
            var rel = Regex.Match(tag, @"rel\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (!rel.Success || !rel.Groups[1].Value.Contains("icon", StringComparison.OrdinalIgnoreCase))
                continue;
            var href = Regex.Match(tag, @"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (!href.Success) continue;
            var h = href.Groups[1].Value;
            var priority = h.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? 3
                         : rel.Groups[1].Value.Contains("apple", StringComparison.OrdinalIgnoreCase) ? 0
                         : h.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? 1
                         : 2;
            found.Add((priority, h));
        }
        return found.OrderBy(x => x.priority).Select(x => x.href);
    }

    // ponytail: naive apex walk — "fms.yukon.ca" -> "yukon.ca" -> null.
    // Stops before the TLD; does not handle co.uk-style country SLDs.
    private static string? ParentDomain(string host)
    {
        var parts = host.Split('.');
        return parts.Length >= 3 ? string.Join('.', parts.Skip(1)) : null;
    }

    private static string GetFaviconCachePath(string url)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDataDirName, "icons");
        Directory.CreateDirectory(dir);
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(url)))[..16];
        return Path.Combine(dir, hash + ".png");
    }

    private static void DeleteFaviconCache(string url)
    {
        try { File.Delete(GetFaviconCachePath(url)); } catch { }
    }

}
