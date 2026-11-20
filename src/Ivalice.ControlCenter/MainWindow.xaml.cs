using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Ivalice.ControlCenter;

public partial class MainWindow : Window
{
    private const string Host = "app.ivalice";
    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await StartAsync();
    }

    private static bool IsOwnPage(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == Host;

    private async Task StartAsync()
    {
        Bridge bridge;
        try
        {
            bridge = new Bridge();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Katalog mod (catalog/mods.json) tidak bisa dibaca.\n\n" + ex.Message,
                "Ivalice Control Center", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IvaliceControlCenter");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
            await Web.EnsureCoreWebView2Async(env);

            var core = Web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(Host, Path.Combine(AppContext.BaseDirectory, "ui"),
                CoreWebView2HostResourceAccessKind.Deny);

            // The UI is local-only: any attempt to navigate away from it is cancelled.
            core.NavigationStarting += (_, e) =>
            {
                if (!IsOwnPage(e.Uri)) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;

            core.WebMessageReceived += (_, e) =>
            {
                if (!IsOwnPage(e.Source)) return;
                core.PostWebMessageAsJson(bridge.Handle(e.WebMessageAsJson));
            };

            core.Navigate($"https://{Host}/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Ivalice Control Center membutuhkan Microsoft Edge WebView2 Runtime.\n\n" + ex.Message,
                "Ivalice Control Center", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }
}
