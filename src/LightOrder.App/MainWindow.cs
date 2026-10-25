using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ControlCenter.Contracts;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LightOrder.App;

public sealed class MainWindow : Window
{
    private const string Origin = "https://lightorder.local";
    private readonly WebView2 view = new();
    private readonly PipeClient client = new();
    private readonly SemaphoreSlim requests = new(4);
    private readonly HashSet<long> pending = [];
    private readonly CancellationTokenSource lifetime = new();
    private System.Windows.Forms.NotifyIcon? tray;
    private bool exiting;
    private ServiceSnapshot? snapshot;
    private readonly string? captureDirectory;
    private bool captureStarted;

    public MainWindow(string? captureDirectory = null)
    {
        this.captureDirectory = captureDirectory;
        Title = "光序 · LightOrder"; Width = 1280; Height = 870; MinWidth = 900; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = System.Windows.Media.Brushes.White;
        Content = view;
        Loaded += async (_, _) => await InitializeAsync();
        Closing += OnClosing;
        Closed += (_, _) => { lifetime.Cancel(); tray?.Dispose(); view.Dispose(); lifetime.Dispose(); };
    }

    private async Task InitializeAsync()
    {
        try
        {
            using var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LightOrder.App.Assets.app-icon.ico")!;
            var ico = new System.Drawing.Icon(iconStream);
            Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            tray = new System.Windows.Forms.NotifyIcon { Icon = ico, Text = "光序 · LightOrder", Visible = true };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("打开光序 / Open", null, (_, _) => Dispatcher.Invoke(ShowWindow));
            menu.Items.Add("退出界面（后台继续） / Exit UI", null, (_, _) => Dispatcher.Invoke(() => { exiting = true; Close(); }));
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightOrder", "WebView2"));
            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NavigationStarting += (_, e) => e.Cancel = e.Uri != Origin + "/index.html";
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += ServeResource;
            core.WebMessageReceived += HandleMessage;
            core.NavigationCompleted += async (_, e) =>
            {
                if (e.IsSuccess && captureDirectory != null && !captureStarted)
                {
                    captureStarted = true;
                    await CapturePagesAsync(captureDirectory);
                }
            };
            core.Navigate(Origin + "/index.html");
        }
        catch (Exception e)
        {
            tray?.Dispose(); tray = null;
            var panel = new StackPanel { Margin = new Thickness(50), VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new TextBlock { Text = "光序需要 Microsoft Edge WebView2 Runtime", FontSize = 24, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "请运行 LightOrder 安装程序检查运行环境。\nPlease run LightOrder setup to check prerequisites.\n\n" + e.Message, Margin = new Thickness(0, 20, 0, 20), TextWrapping = TextWrapping.Wrap });
            var button = new System.Windows.Controls.Button { Content = "打开安装程序 / Open setup", Padding = new Thickness(15), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            button.Click += (_, _) => { try { LaunchSetup(); } catch (Exception error) { System.Windows.MessageBox.Show(error.Message, "LightOrder"); } };
            panel.Children.Add(button); Content = panel;
        }
    }

    // Local CLI-only screenshot workflow. No page message can invoke file writes or supply JavaScript.
    private async Task CapturePagesAsync(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            bool connected = false;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                await Task.Delay(500, lifetime.Token);
                connected = await view.CoreWebView2.ExecuteScriptAsync("typeof state !== 'undefined' && state.connected && !state.loading && preferencesLoaded") == "true";
                if (connected) break;
            }
            string[] pages = connected ? ["overview", "lighting", "cooling", "computer", "settings"] : ["overview"];
            foreach (var page in pages)
            {
                // Page names are fixed literals above, never supplied by the command line or the renderer.
                await view.CoreWebView2.ExecuteScriptAsync($"navigate('{page}'); window.scrollTo(0,0);");
                await Task.Delay(700, lifetime.Token);
                using var stream = File.Create(Path.Combine(directory, connected ? $"lightorder-{page}.png" : "lightorder-setup-required.png"));
                await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "capture-status.json"), JsonSerializer.Serialize(new { connected, pages, capturedUtc = DateTimeOffset.UtcNow }, Wire.Json), lifetime.Token);
        }
        catch (Exception error)
        {
            try { await File.WriteAllTextAsync(Path.Combine(directory, "capture-error.txt"), error.Message); } catch (IOException) { }
        }
        finally { exiting = true; Close(); }
    }

    private void ServeResource(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        var name = uri.AbsolutePath.TrimStart('/');
        var allowed = new Dictionary<string, string> { ["index.html"] = "text/html; charset=utf-8", ["app.js"] = "text/javascript; charset=utf-8", ["charts.js"] = "text/javascript; charset=utf-8", ["editor.js"] = "text/javascript; charset=utf-8", ["icons.js"] = "text/javascript; charset=utf-8", ["design.css"] = "text/css; charset=utf-8", ["production.css"] = "text/css; charset=utf-8", ["app-icon.svg"] = "image/svg+xml" };
        var stream = uri.GetLeftPart(UriPartial.Authority) == Origin && e.Request.Method == "GET" && allowed.ContainsKey(name)
            ? Assembly.GetExecutingAssembly().GetManifestResourceStream("LightOrder.App.Assets." + name) : null;
        var headers = "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self'; connect-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'\r\n";
        e.Response = view.CoreWebView2.Environment.CreateWebResourceResponse(stream ?? new MemoryStream(), stream == null ? 404 : 200, stream == null ? "Not found" : "OK", headers + "Content-Type: " + (allowed.GetValueOrDefault(name) ?? "text/plain"));
    }

    private async void HandleMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != Origin + "/index.html" || e.WebMessageAsJson.Length > 350000) return;
        long id = 0; bool registered = false;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            id = root.GetProperty("id").GetInt64();
            if (id < 1 || id > 9007199254740991 || pending.Count >= 16 || !pending.Add(id)) return;
            registered = true;
            string method = root.GetProperty("method").GetString() ?? "";
            var args = root.TryGetProperty("params", out var p) ? p.Clone() : Wire.Element(new { });
            await requests.WaitAsync(lifetime.Token);
            object? result;
            try
            {
                switch (method)
                {
                    case StandardMethods.Snapshot:
                    case StandardMethods.Refresh:
                        var data = await client.CallAsync(method, new { protocolMajor = Wire.Version }, lifetime.Token);
                        var current = Wire.Read<ServiceSnapshot>(data);
                        if (current.Version.Split('.')[0] != "1" || current.Demo) throw new InvalidDataException("不兼容的服务版本 / Incompatible service version");
                        snapshot = current; result = current; break;
                    case StandardMethods.Plugins:
                    case StandardMethods.ProfilesRead:
                        result = await client.CallAsync(method, new { }, lifetime.Token); break;
                    case StandardMethods.ProfilesWrite:
                        if (args.GetRawText().Length > 262144 || args.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("配置格式无效 / Invalid preferences");
                        result = await client.CallAsync(method, args, lifetime.Token); break;
                    case StandardMethods.Apply:
                        ValidateCommand(Wire.Read<DeviceCommand>(args));
                        result = await client.CallAsync(method, args, lifetime.Token); break;
                    case "setup.open": LaunchSetup(); result = new { launched = true }; break;
                    case "window.hide": Hide(); result = new { }; break;
                    case "window.exit": exiting = true; Close(); return;
                    default: throw new InvalidOperationException("不支持的请求 / Unsupported request");
                }
            }
            finally { requests.Release(); }
            Reply(id, result, null);
        }
        catch (Exception error) { if (registered) Reply(id, null, error.Message); }
        finally { if (registered) pending.Remove(id); }
    }

    private void ValidateCommand(DeviceCommand command)
    {
        var device = snapshot?.Devices.SingleOrDefault(d => d.Id == command.DeviceId);
        if (device == null || !device.Capabilities.Contains(command.Capability)) throw new InvalidOperationException("设备未提供此能力 / Capability unavailable");
        var p = command.Parameters;
        switch (command.Capability)
        {
            case StandardCapabilities.LightingStatic:
                var color = p.GetProperty("color").GetString();
                var brightness = p.GetProperty("brightness").GetDouble();
                if (color == null || !System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$") || !double.IsFinite(brightness) || brightness < 0 || brightness > 1) throw new ArgumentException("颜色或亮度无效 / Invalid color or brightness");
                break;
            case StandardCapabilities.LightingOff: case StandardCapabilities.Release: break;
            case StandardCapabilities.FanProfile: Wire.Read<FanProfile>(p).Validate(); break;
            case StandardCapabilities.WindowsPowerScheme: if (!Guid.TryParse(p.GetProperty("schemeId").GetString(), out _)) throw new ArgumentException("Invalid scheme"); break;
            case StandardCapabilities.ProcessPolicy:
                if (p.GetProperty("pid").GetInt32() <= 0 || !DateTimeOffset.TryParse(p.GetProperty("startTimeUtc").GetString(), out _)) throw new ArgumentException("Invalid process identity");
                break;
            default: throw new InvalidOperationException("不支持的控制能力 / Unsupported control capability");
        }
    }
    private void Reply(long id, object? result, string? error)
    {
        if (!exiting && view.CoreWebView2 != null) view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, result, error }, Wire.Json));
    }
    private static void LaunchSetup()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "LightOrder.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("未找到安装程序，请运行下载的 LightOrder.exe。 / Run the downloaded LightOrder.exe setup.");
        Process.Start(new ProcessStartInfo(path, "--install") { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!exiting && tray != null) { e.Cancel = true; Hide(); } }
}
