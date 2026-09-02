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
    // ---------- shell icons ----------

    private static Bitmap GetIconBitmap(string path, bool isFolder)
    {
        if (IsUrl(path))
        {
            using var urlIcon = FindChrome() is string chrome ? Icon.ExtractAssociatedIcon(chrome) : SystemIcons.Information;
            var bmp = new Bitmap(42, 42);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                if (urlIcon is not null)
                    g.DrawImage(urlIcon.ToBitmap(), 0, 0, 42, 42);
            }
            return bmp;
        }
        const uint SHGFI_ICON = 0x100;
        const uint SHGFI_LARGEICON = 0x0;

        var shinfo = new SHFILEINFO();
        SHGetFileInfo(
            path,
            isFolder ? 0x00000010u : 0u,
            ref shinfo,
            (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_LARGEICON);

        if (shinfo.hIcon == IntPtr.Zero)
            return new Bitmap(32, 32);

        using var icon = Icon.FromHandle(shinfo.hIcon);
        var bitmap = new Bitmap(48, 48, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
            g.DrawImage(icon.ToBitmap(), 0, 0, 48, 48);
        DestroyIcon(shinfo.hIcon);
        return bitmap;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private static DialogResult ConfirmRemove(string label, bool hasSource)
    {
        var msg = hasSource
            ? $"Remove '{label}' from the board?\n\nThe original folder/file will not be affected."
            : $"Remove '{label}' from the board?";
        return MessageBox.Show(msg, "Remove?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
    }

    private static void OpenItem(string path)
    {
        try
        {
            if (IsUrl(path) && FindChrome() is string chrome)
            {
                var psi = new ProcessStartInfo(chrome) { UseShellExecute = false };
                psi.ArgumentList.Add(path);
                Process.Start(psi);
            }
            else
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not open item", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
