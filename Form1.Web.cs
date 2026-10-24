using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Shortcutz;

public partial class Form1
{
    private WebView2? _webView;

    private async void ShowWebView(string? url = null)
    {
        if (_webView is null)
        {
            _webView = new WebView2 { Dock = DockStyle.Fill };
            try
            {
                await _webView.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(
                    userDataFolder: Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "GaucheDroite", "Shortcutz", "webview")));
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("Starting WebView2", ex);
                _webView.Dispose();
                _webView = null;
                MessageBox.Show(this, ex.Message, "Could not open web view",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Controls.Add(_webView);
            _webView.BringToFront();
        }
        _webView.Visible = true;
        // Navigate only when the view is fresh or a different URL was asked for,
        // so later clicks keep the loaded page.
        var current = _webView.Source.ToString();
        if (current == "about:blank" || (url is not null && !string.Equals(current, url, StringComparison.OrdinalIgnoreCase)))
            _webView.CoreWebView2.Navigate(url ?? "http://localhost:9341/");
    }

    private void HideWebView()
    {
        if (_webView is not null) _webView.Visible = false;
    }
}
