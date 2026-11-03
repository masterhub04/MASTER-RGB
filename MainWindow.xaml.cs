using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Input;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using MasterRgb.Services;
using MasterRgb.Controls;
using Velopack;
using Velopack.Sources;
using Microsoft.Web.WebView2.Core;
using Forms = System.Windows.Forms;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using CheckBox = System.Windows.Controls.CheckBox;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using System.Runtime.InteropServices;

namespace MasterRgb;

public partial class MainWindow : Window
{
    private FrameworkElement? _previousPage;
    private FrameworkElement? _currentPage;
    private readonly BoostGameService _boostGame = new();
    private readonly OptimizeSystemService _optimizeSystem = new();
    private readonly OpenRgbSdkClient _openRgb = new();
    private readonly OpenRgbEngineHost _openRgbEngine = new();
    private readonly ObservableCollection<DeviceRow> _devices = new();
    private readonly ObservableCollection<DeviceRow> _selectableDevices = new();
    private readonly ObservableCollection<DeviceDisplayRow> _displayDevices = new();
    private bool _updatingColorText;
    private Ellipse? _hueMarker;
    private bool _hueArcDrawn;
    private string _currentTheme = "Blue";
    private string _uiAccentColor = "#0000FF";
    private bool _startWithWindows;
    private bool _startMinimized;
    private bool _minimizeToTray;
    private bool _allowExit;
    private int _mouseRedGain = 100, _mouseGreenGain = 100, _mouseBlueGain = 100;
    private string? _lastMouseColorHex;
    private int _lastMouseColorBrightness = 100;
    private bool _hasSavedMouseColor;
    private Forms.NotifyIcon? _trayIcon;
    private readonly ObservableCollection<string> _notifications = new();
    private readonly ObservableCollection<FavoriteColorRow> _favoriteColors = new();
    private readonly Dictionary<string, int> _zoneLedOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _userSettingsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MASTER RGB", "user-settings.json");

    public MainWindow()
    {
        InitializeComponent();
        _currentPage = DashboardPage;
        DeviceList.ItemsSource = _displayDevices;
        DeviceSelector.ItemsSource = _selectableDevices;
        MultiDeviceList.ItemsSource = _devices;
        MultiDeviceList.AddHandler(CheckBox.CheckedEvent, new RoutedEventHandler(MultiDeviceCheckChanged));
        MultiDeviceList.AddHandler(CheckBox.UncheckedEvent, new RoutedEventHandler(MultiDeviceCheckChanged));
        FavoriteColorsList.ItemsSource = _favoriteColors;
        LoadUserSettings();
        StartWithWindowsCheckBox.IsChecked = _startWithWindows;
        StartMinimizedCheckBox.IsChecked = _startMinimized;
        MinimizeToTrayCheckBox.IsChecked = _minimizeToTray;
        ApplyTheme(_currentTheme, false);
        ConfigureTrayIcon();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += (_, _) => { MasterMicView.Shutdown(); _trayIcon?.Dispose(); _openRgbEngine.Dispose(); };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
            WindowState = WindowState.Minimized;

        await InitializeHtmlInterfaceAsync();

        // O mecanismo OpenRGB continua sendo iniciado pelo código nativo.
        await DetectOpenRgbDevicesAsync();
    }

    private async Task InitializeHtmlInterfaceAsync()
    {
        try
        {
            await HtmlInterface.EnsureCoreWebView2Async();
            HtmlInterface.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            HtmlInterface.CoreWebView2.Settings.IsZoomControlEnabled = false;
            HtmlInterface.CoreWebView2.WebMessageReceived += HtmlInterface_WebMessageReceived;

            string htmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, "HTML", "index.html");
            if (!File.Exists(htmlPath))
            {
                System.Windows.MessageBox.Show($"A interface HTML não foi encontrada:\n{htmlPath}", "MASTER HUB",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            HtmlInterface.Source = new Uri(htmlPath);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Não foi possível iniciar a interface WebView2. Verifique se o WebView2 Runtime está instalado.\n\n" + ex.Message,
                "MASTER HUB", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void HtmlInterface_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            string? action = root.TryGetProperty("action", out var value) ? value.GetString() : null;

            switch (action)
            {
                case "window-minimize": WindowState = WindowState.Minimized; break;
                case "window-close": CloseWindow_Click(this, new RoutedEventArgs()); break;
                case "window-drag": BeginNativeWindowDrag(); break;
                case "check-updates": CheckUpdatesFromHtml(); break;
                case "install-update": CheckUpdatesFromHtml(true); break;
                case "mic-get-state": MasterMicView.HtmlInitialize(); SendHtmlMessage(new { action = "mic-state", state = MasterMicView.GetHtmlState() }); break;
                case "mic-refresh-audio": MasterMicView.HtmlRefreshAudio(); SendHtmlMessage(new { action = "mic-state", state = MasterMicView.GetHtmlState() }); break;
                case "mic-select-audio": MasterMicView.HtmlSelectAudio(root.GetProperty("id").GetString() ?? ""); SendHtmlMessage(new { action = "mic-state", state = MasterMicView.GetHtmlState() }); break;
                case "mic-volume": MasterMicView.HtmlSetVolume(root.GetProperty("value").GetDouble()); SendHtmlMessage(new { action = "mic-state", state = MasterMicView.GetHtmlState() }); break;
                case "mic-mute": MasterMicView.HtmlSetMute(root.GetProperty("muted").GetBoolean()); SendHtmlMessage(new { action = "mic-state", state = MasterMicView.GetHtmlState() }); break;
                case "mic-effect": MasterMicView.HtmlSetEffect(root.GetProperty("effect").GetString() ?? "", root.GetProperty("enabled").GetBoolean()); SendHtmlMessage(new { action = "mic-state", state = MasterMicView.GetHtmlState() }); break;
                case "mic-parameter": MasterMicView.HtmlSetParameter(root.GetProperty("parameter").GetString() ?? "", root.GetProperty("value").GetDouble()); SendHtmlMessage(new { action = "mic-state", state = MasterMicView.GetHtmlState() }); break;
                case "mic-monitor": MasterMicView.HtmlToggleMonitor(); SendHtmlMessage(new { action = "mic-state", state = MasterMicView.GetHtmlState() }); break;
                case "mic-refresh-usb": MasterMicView.HtmlRefreshUsb(root.GetProperty("showAll").GetBoolean()); SendHtmlMessage(new { action = "mic-state", state = MasterMicView.GetHtmlState() }); break;
                case "mic-select-usb": MasterMicView.HtmlSelectUsb(root.GetProperty("id").GetString() ?? ""); SendHtmlMessage(new { action = "mic-state", state = MasterMicView.GetHtmlState() }); break;
                case "mic-export-usb": MasterMicView.HtmlExportUsb(); break;
                case "rgb-get-devices": await SendRgbDevicesToHtmlAsync(); break;
                case "rgb-refresh-devices": await DetectOpenRgbDevicesAsync(); await SendRgbDevicesToHtmlAsync(); break;
                case "rgb-run-diagnostic": RunDiagnostic_Click(this, new RoutedEventArgs()); break;
                case "rgb-apply-color": await ApplyHtmlRgbColorAsync(root); break;
                case "rgb-apply-group": await ApplyHtmlRgbGroupAsync(root); break;
                case "boost-dashboard": SendHtmlMessage(new { action = "boost-dashboard", data = _boostGame.GetDashboard() }); break;
                case "optimize-startup-list": SendHtmlMessage(new { action = "optimize-startup-list", items = _optimizeSystem.GetStartupItems() }); break;
                case "optimize-services-list": SendHtmlMessage(new { action = "optimize-services-list", items = _optimizeSystem.GetOptionalServices() }); break;
                case "optimize-service-toggle": SendHtmlMessage(new { action = "optimize-services-list", items = _optimizeSystem.SetOptionalService(root.GetProperty("name").GetString() ?? "", root.GetProperty("disable").GetBoolean()) }); break;
                case "optimize-startup-toggle": SendHtmlMessage(new { action = "optimize-startup-list", items = _optimizeSystem.ToggleStartup(root.GetProperty("id").GetString() ?? "", root.GetProperty("enabled").GetBoolean()) }); break;
                case "optimize-clean-preview": SendHtmlMessage(new { action = "optimize-clean-preview", data = _optimizeSystem.GetCleanupPreview() }); break;
                case "optimize-clean-run": SendHtmlMessage(new { action = "optimize-clean-result", data = _optimizeSystem.CleanTemp() }); break;
                case "boost-power-plans": SendHtmlMessage(new { action = "boost-power-plans", data = _boostGame.GetPowerPlans() }); break;
                case "boost-power-set": SendHtmlMessage(new { action = "boost-power-plans", data = _boostGame.SetPowerPlan(root.GetProperty("id").GetString() ?? "") }); break;
                case "boost-game-settings": SendHtmlMessage(new { action = "boost-game-settings", data = _boostGame.GetGameSettings() }); break;
                case "boost-game-setting": SendHtmlMessage(new { action = "boost-game-settings", data = _boostGame.SetGameSetting(root.GetProperty("setting").GetString() ?? "", root.GetProperty("enabled").GetBoolean()) }); break;
            }
        }
        catch (Exception ex)
        {
            SendHtmlMessage(new { action = "host-error", message = ex.Message });
            System.Diagnostics.Debug.WriteLine("WebView2 message error: " + ex);
        }
    }

    private void SendHtmlMessage(object payload)
    {
        if (HtmlInterface.CoreWebView2 is null) return;
        HtmlInterface.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
    }

    private async Task SendRgbDevicesToHtmlAsync()
    {
        try
        {
            if (_devices.Count == 0) await DetectOpenRgbDevicesAsync();
            var devices = _devices.Select(d => new {
                index = d.Index, name = d.Name,
                zones = d.Zones.Select((z, i) => new { index = i, name = z.Name, ledCount = GetConfiguredLedCount(d, new ZoneRow(i, z.Name, z.LedCount)) })
            }).ToArray();
            SendHtmlMessage(new { action = "rgb-devices", devices });
        }
        catch (Exception ex) { SendHtmlMessage(new { action = "host-error", message = ex.Message }); }
    }

    private async Task ApplyHtmlRgbColorAsync(JsonElement root)
    {
        int deviceIndex = root.GetProperty("deviceIndex").GetInt32();
        int zoneIndex = root.GetProperty("zoneIndex").GetInt32();
        int ledCount = root.GetProperty("ledCount").GetInt32();
        string hex = root.GetProperty("hex").GetString()?.Trim().TrimStart('#') ?? "";
        int brightness = root.TryGetProperty("brightness", out var b) ? Math.Clamp(b.GetInt32(), 0, 100) : 100;
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            throw new InvalidOperationException("Cor inválida. Use #RRGGBB.");
        var device = _devices.FirstOrDefault(d => d.Index == deviceIndex) ?? throw new InvalidOperationException("Dispositivo não encontrado. Atualize a lista.");
        if (zoneIndex < 0 || zoneIndex >= device.Zones.Count) throw new InvalidOperationException("Zona não encontrada. Atualize a lista.");
        var zone = device.Zones[zoneIndex];
        if (ledCount < 1 || ledCount > ushort.MaxValue || zone.LedCount < 1) throw new InvalidOperationException("Quantidade de LEDs inválida para a zona.");
        byte red = (byte)Math.Round(((rgb >> 16) & 0xFF) * brightness / 100.0);
        byte green = (byte)Math.Round(((rgb >> 8) & 0xFF) * brightness / 100.0);
        byte blue = (byte)Math.Round((rgb & 0xFF) * brightness / 100.0);
        (red, green, blue) = ApplyControllerColorQuirks(device.Name, zone.Name, red, green, blue);
        if (ledCount != zone.LedCount)
            await _openRgb.ResizeZoneAsync(deviceIndex, zoneIndex, ledCount);
        await _openRgb.ApplyDeviceZonesColorAsync(deviceIndex, new[] { (ZoneIndex: zoneIndex, LedCount: ledCount, Red: red, Green: green, Blue: blue) });
        SendHtmlMessage(new { action = "rgb-result", ok = true, message = $"Cor #{hex.ToUpperInvariant()} enviada para {device.Name} / {zone.Name}." });
    }

    private async Task ApplyHtmlRgbGroupAsync(JsonElement root)
    {
        string hex = root.GetProperty("hex").GetString()?.Trim().TrimStart('#') ?? "";
        int brightness = root.TryGetProperty("brightness", out var b) ? Math.Clamp(b.GetInt32(), 0, 100) : 100;
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            throw new InvalidOperationException("Cor inválida. Use #RRGGBB.");
        var indices = root.GetProperty("deviceIndices").EnumerateArray().Select(x => x.GetInt32()).Distinct().ToHashSet();
        var selected = _devices.Where(d => indices.Contains(d.Index)).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("Selecione pelo menos um dispositivo.");
        byte red = (byte)Math.Round(((rgb >> 16) & 0xFF) * brightness / 100.0);
        byte green = (byte)Math.Round(((rgb >> 8) & 0xFF) * brightness / 100.0);
        byte blue = (byte)Math.Round((rgb & 0xFF) * brightness / 100.0);
        var batches = selected.Select(device =>
        {
            var zones = device.Zones.Select((zone, zoneIndex) =>
            {
                var adjusted = ApplyControllerColorQuirks(device.Name, zone.Name, red, green, blue);
                var zoneRow = new ZoneRow(zoneIndex, zone.Name, zone.LedCount);
                return (ZoneIndex: zoneIndex, LedCount: GetConfiguredLedCount(device, zoneRow), Red: adjusted.red, Green: adjusted.green, Blue: adjusted.blue);
            }).Where(zone => zone.LedCount > 0).ToArray();
            return (Device: device, Zones: zones);
        }).Where(batch => batch.Zones.Length > 0).ToArray();

        // Cada dispositivo recebe sua cor em paralelo. Dentro de cada dispositivo,
        // todas as zonas seguem pela mesma conexão do SDK para evitar atrasos em cascata.
        await Task.WhenAll(batches.Select(async batch =>
        {
            foreach (var zone in batch.Zones)
            {
                var detected = batch.Device.Zones[zone.ZoneIndex];
                if (zone.LedCount != detected.LedCount)
                    await _openRgb.ResizeZoneAsync(batch.Device.Index, zone.ZoneIndex, zone.LedCount);
            }
            await _openRgb.ApplyDeviceZonesColorAsync(batch.Device.Index, batch.Zones);
        }));

        int applied = batches.Sum(batch => batch.Zones.Length);
        SendHtmlMessage(new { action = "rgb-result", ok = true, message = $"Cor #{hex.ToUpperInvariant()} enviada para {applied} zona(s) em {selected.Count} dispositivo(s)." });
    }

    private static (byte red, byte green, byte blue) ApplyControllerColorQuirks(string deviceName, string zoneName, byte red, byte green, byte blue)
    {
        // MSI MPG B550 GAMING PLUS JRAINBOW2 uses the two least-significant
        // bits of each channel as control flags in affected OpenRGB builds.
        if (deviceName.Contains("MSI", StringComparison.OrdinalIgnoreCase)
            && deviceName.Contains("B550", StringComparison.OrdinalIgnoreCase)
            && zoneName.Contains("JRAINBOW2", StringComparison.OrdinalIgnoreCase))
        {
            red = (byte)(red & 0xFC);
            green = (byte)(green & 0xFC);
            blue = (byte)(blue & 0xFC);
        }
        return (red, green, blue);
    }

    private async void CheckUpdatesFromHtml(bool installUpdate = false)
    {
        string currentVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "desconhecida";
        try
        {
            var source = new GithubSource("https://github.com/masterhub04/MASTER-RGB", null, false);
            var manager = new UpdateManager(source);
            if (!manager.IsInstalled)
            {
                SendHtmlMessage(new { action = "update-check-result", currentVersion, available = false,
                    message = "Atualização automática disponível somente na versão instalada pelo instalador." });
                return;
            }

            SendHtmlMessage(new { action = "update-checking", currentVersion });
            var checkTask = manager.CheckForUpdatesAsync();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
            if (await Task.WhenAny(checkTask, timeoutTask) != checkTask)
            {
                _ = checkTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw new TimeoutException("A verificação demorou mais de 30 segundos. Confira sua conexão e tente novamente.");
            }

            var update = await checkTask;
            if (update is null)
            {
                SendHtmlMessage(new { action = "update-check-result", currentVersion, available = false,
                    message = "Você já está usando a versão mais recente." });
                return;
            }

            string availableVersion = update.TargetFullRelease.Version.ToString();
            if (!installUpdate)
            {
                SendHtmlMessage(new { action = "update-check-result", currentVersion, available = true,
                    availableVersion, message = $"Nova versão disponível: v{availableVersion}" });
                return;
            }

            SendHtmlMessage(new { action = "update-installing", currentVersion, availableVersion,
                message = $"Baixando a versão v{availableVersion}..." });
            await manager.DownloadUpdatesAsync(update);
            SendHtmlMessage(new { action = "update-installing", currentVersion, availableVersion,
                message = "Download concluído. Fechando o MASTER HUB para aplicar a atualização..." });
            manager.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            SendHtmlMessage(new { action = "update-error", currentVersion, message = "Não foi possível concluir: " + ex.Message });
        }
    }

    private void NavigateDashboard_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(DashboardPage, DashboardNavButton);
    }

    private void EnterMasterMic_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(MasterMicPage, DashboardNavButton);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        FrameworkElement target = _previousPage ?? DashboardPage;
        Button active = target == LightingPage ? LightingNavButton
            : target == DevicesPage ? DevicesNavButton
            : target == ProfilesPage ? ProfilesNavButton
            : target == SettingsPage ? SettingsNavButton
            : DashboardNavButton;
        ShowPage(target, active);
    }

    private void EnterMasterRgb_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(LightingPage, LightingNavButton);
    }
    private void NavigateLighting_Click(object sender, RoutedEventArgs e) => ShowPage(LightingPage, LightingNavButton);
    private void NavigateDevices_Click(object sender, RoutedEventArgs e) => ShowPage(DevicesPage, DevicesNavButton);
    private void NavigateProfiles_Click(object sender, RoutedEventArgs e) => ShowPage(ProfilesPage, ProfilesNavButton);
    private void NavigateSettings_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage, SettingsNavButton);
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (CheckUpdatesButton.IsEnabled == false) return;

        try
        {
            CheckUpdatesButton.IsEnabled = false;
            UpdateStatusText.Text = "Verificando a versão mais recente...";
            var source = new GithubSource("https://github.com/masterhub04/MASTER-RGB", null, false);
            var manager = new UpdateManager(source);
            if (!manager.IsInstalled)
            {
                UpdateStatusText.Text = "A atualização automática está disponível apenas na versão instalada pelo instalador do MASTER HUB.";
                return;
            }

            // O Velopack 1.2.161 não oferece CancellationToken nesse método.
            // Task.WhenAny limita o tempo de espera da interface sem usar uma API inexistente.
            UpdateStatusText.Text = "Conectando ao GitHub para verificar atualizações...";
            var checkTask = manager.CheckForUpdatesAsync();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
            if (await Task.WhenAny(checkTask, timeoutTask) != checkTask)
            {
                _ = checkTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                UpdateStatusText.Text = "A verificação demorou mais de 30 segundos. Confira sua conexão e tente novamente.";
                return;
            }
            var update = await checkTask;

            if (update is null)
            {
                UpdateStatusText.Text = "Você já está usando a versão mais recente.";
                return;
            }

            UpdateStatusText.Text = $"Baixando a versão {update.TargetFullRelease.Version}...";
            await manager.DownloadUpdatesAsync(update);
            UpdateStatusText.Text = "Download concluído. Reiniciando para aplicar a atualização...";
            manager.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Não foi possível atualizar o MASTER HUB: " + ex.Message;
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }


    private void ShowPage(FrameworkElement page, Button activeButton)
    {
        if (_currentPage is not null && !ReferenceEquals(_currentPage, page))
            _previousPage = _currentPage;
        _currentPage = page;

        bool showRgbSidebar = page == LightingPage || page == DevicesPage || page == ProfilesPage;
        AppSidebar.Visibility = showRgbSidebar ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(MainContentHost, showRgbSidebar ? 1 : 0);
        Grid.SetColumnSpan(MainContentHost, showRgbSidebar ? 1 : 2);
        if (AppSidebar.Parent is Grid root)
            root.ColumnDefinitions[0].Width = showRgbSidebar ? new GridLength(245) : new GridLength(0);
        DashboardPage.Visibility = Visibility.Collapsed;
        LightingPage.Visibility = Visibility.Collapsed;
        DevicesPage.Visibility = Visibility.Collapsed;
        ProfilesPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        MasterMicPage.Visibility = Visibility.Collapsed;
        DashboardPage.Visibility = ReferenceEquals(page, DashboardPage) ? Visibility.Visible : Visibility.Collapsed;
        LightingPage.Visibility = ReferenceEquals(page, LightingPage) ? Visibility.Visible : Visibility.Collapsed;
        DevicesPage.Visibility = ReferenceEquals(page, DevicesPage) ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = ReferenceEquals(page, ProfilesPage) ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = ReferenceEquals(page, SettingsPage) ? Visibility.Visible : Visibility.Collapsed;
        MasterMicPage.Visibility = ReferenceEquals(page, MasterMicPage) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { DashboardNavButton, LightingNavButton, DevicesNavButton, ProfilesNavButton, SettingsNavButton })
        {
            button.Background = ReferenceEquals(button, activeButton) ? new SolidColorBrush(Color.FromRgb(23, 54, 77)) : Brushes.Transparent;
            button.BorderBrush = ReferenceEquals(button, activeButton) ? new SolidColorBrush(Color.FromRgb(35, 74, 104)) : Brushes.Transparent;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WmNcLButtonDown = 0x00A1;
    private static readonly IntPtr HtCaption = new(2);

    private void BeginNativeWindowDrag()
    {
        try
        {
            if (WindowState == WindowState.Maximized) return;
            ReleaseCapture();
            SendMessage(new System.Windows.Interop.WindowInteropHelper(this).Handle,
                WmNcLButtonDown, HtCaption, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Falha ao arrastar a janela: {ex.Message}");
        }
    }

    private void HeaderBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        // Os botões e seus ícones não devem iniciar o arraste da janela.
        if (e.OriginalSource is DependencyObject source &&
            FindVisualAncestor<Button>(source) is not null) return;
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) BeginNativeWindowDrag();
    }

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void TrayOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || MinimizeToTrayCheckBox is null) return;
        _minimizeToTray = MinimizeToTrayCheckBox.IsChecked == true;
        ConfigureTrayIcon();
        SaveUserSettings();
    }

    private void ConfigureTrayIcon()
    {
        if (!_minimizeToTray)
        {
            if (_trayIcon is not null) _trayIcon.Visible = false;
            return;
        }

        _trayIcon ??= new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "MASTER HUB",
            Visible = true
        };
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick -= TrayIcon_DoubleClick;
        _trayIcon.DoubleClick += TrayIcon_DoubleClick;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir MASTER HUB", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Encerrar", null, (_, _) => { _allowExit = true; _trayIcon!.Visible = false; Close(); });
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.ContextMenuStrip = menu;
    }

    private void TrayIcon_DoubleClick(object? sender, EventArgs e) => RestoreFromTray();

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowExit || !_minimizeToTray) return;
        e.Cancel = true;
        Hide();
        ConfigureTrayIcon();
        _trayIcon?.ShowBalloonTip(1800, "MASTER HUB", "O aplicativo continua em execução na bandeja.", Forms.ToolTipIcon.Info);
    }

    private void Notifications_Click(object sender, RoutedEventArgs e)
    {
        NotificationList.ItemsSource = _notifications;
        EmptyNotificationsText.Visibility = _notifications.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        NotificationList.Visibility = _notifications.Count == 0
            ? Visibility.Collapsed : Visibility.Visible;
        NotificationsPopup.IsOpen = !NotificationsPopup.IsOpen;
    }

    private void StartupOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _startWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        _startMinimized = StartMinimizedCheckBox.IsChecked == true;
        try
        {
            ConfigureWindowsStartup(_startWithWindows, _startMinimized);
            SaveUserSettings();
        }
        catch (Exception ex) { StatusText.Text = "Não foi possível atualizar a inicialização do Windows: " + ex.Message; }
    }

    private static void ConfigureWindowsStartup(bool enabled, bool minimized)
    {
        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string appValueName = "MASTER RGB";
        using var runKey = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true);
        if (!enabled)
        {
            runKey.DeleteValue(appValueName, throwOnMissingValue: false);
            return;
        }
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("Não foi possível localizar o executável do MASTER RGB.");
        runKey.SetValue(appValueName, $"\"{executable}\"{(minimized ? " --minimized" : string.Empty)}");
    }

    private bool TryReadSelectedColor(out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        string hex = HexColorBox.Text.Trim().TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)) return false;
        red = (byte)(value >> 16); green = (byte)(value >> 8); blue = (byte)value;
        return true;
    }

    private async void ConnectOpenRgb_Click(object sender, RoutedEventArgs e)
    {
        await DetectOpenRgbDevicesAsync();
    }

    private async Task DetectOpenRgbDevicesAsync()
    {
        ConnectButtonState(true);
        EngineStatusText.Text = "Iniciando mecanismo OpenRGB SDK...";
        EngineStatusText.Foreground = Brushes.LightSkyBlue;
        StatusText.Text = "Detectando controladores RGB pelo SDK integrado...";
        _devices.Clear();
        _selectableDevices.Clear();
        _displayDevices.Clear();
        ZoneSelector.ItemsSource = null;
        EmptyDevicesText.Visibility = Visibility.Visible;
        EmptyDevicesText.Text = "Procurando controladores RGB compatíveis...";
        DeviceCountText.Text = "Detectando...";

        try
        {
            await _openRgbEngine.EnsureStartedAsync();
            var devices = await _openRgb.ReadDevicesAsync();

            foreach (var device in devices)
            {
                string vendor = string.IsNullOrWhiteSpace(device.Vendor) ? "Fabricante não informado" : device.Vendor;
                string details = $"{vendor} • {device.LedCount} LED(s) • {device.ZoneCount} zona(s)";
                string zones = device.Zones.Count == 0
                    ? "Sem zonas informadas"
                    : "Zonas: " + string.Join(" • ", device.Zones.Select(z => $"{z.Name} ({z.LedCount})"));

                var row = new DeviceRow(device.Index, device.Name, details, zones, device.Zones);
                _devices.Add(row);
                _selectableDevices.Add(row);
                _displayDevices.Add(new DeviceDisplayRow(device.Name, details, zones, "OPENRGB"));
            }

            EmptyDevicesText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyDevicesText.Text = devices.Count == 0
                ? "Nenhum dispositivo RGB compatível foi encontrado pelo OpenRGB SDK."
                : string.Empty;
            DeviceCountText.Text = $"{devices.Count} dispositivo(s)";
            MultiSelectInfo.Text = $"{devices.Count} dispositivo(s) disponível(is) para seleção em grupo.";
            EngineStatusText.Text = "SDK conectado • pronto para controlar";
            EngineStatusText.Foreground = Brushes.LightGreen;

            if (_selectableDevices.Count > 0)
                DeviceSelector.SelectedIndex = 0;

            StatusText.Text = devices.Count == 0
                ? "O mecanismo iniciou e a conexão SDK foi estabelecida, mas nenhum controlador foi retornado."
                : $"Detecção concluída: {devices.Count} dispositivo(s) encontrado(s) pelo OpenRGB SDK.";
        }
        catch (Exception ex)
        {
            _devices.Clear();
            _selectableDevices.Clear();
            _displayDevices.Clear();
            DeviceCountText.Text = "0 dispositivo(s)";
            EmptyDevicesText.Visibility = Visibility.Visible;
            EmptyDevicesText.Text = "Não foi possível detectar os dispositivos RGB pelo mecanismo integrado.";
            EngineStatusText.Text = "Mecanismo RGB indisponível";
            EngineStatusText.Foreground = Brushes.Orange;
            StatusText.Text = "Falha ao iniciar ou consultar o OpenRGB SDK: " + ex.Message;
        }
        finally
        {
            ConnectButtonState(false);
        }
    }

    private async void RunDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        RunDiagnosticButton.IsEnabled = false;
        ConnectButtonState(true);
        StatusText.Text = "Executando diagnóstico exclusivamente pelo OpenRGB SDK (somente leitura)...";
        EngineStatusText.Text = "Diagnóstico em andamento";
        EngineStatusText.Foreground = Brushes.LightSkyBlue;
        try
        {
            await _openRgbEngine.EnsureStartedAsync();
            var devices = await _openRgb.ReadDevicesAsync();
            var report = new
            {
                Report = new
                {
                    Application = "MASTER RGB",
                    ApplicationVersion = "0.9.7",
                    GeneratedAtLocal = DateTimeOffset.Now,
                    DiagnosticMode = "Read-only OpenRGB SDK metadata; no lighting mode or LED color commands are sent",
                    Computer = Environment.MachineName,
                    OperatingSystem = Environment.OSVersion.ToString(),
                    Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                    SdkEndpoint = "127.0.0.1:6742",
                    ClientProtocolVersion = 5,
                    ProtocolNegotiation = "SDK protocol version is recorded per controller response; legacy servers may use protocol 0",
                    ReadOnlySdkPackets = new[]
                    {
                        new { Id = 40, Name = "RequestProtocolVersion" },
                        new { Id = 50, Name = "SetClientName (MASTER RGB)" },
                        new { Id = 0, Name = "RequestControllerCount" },
                        new { Id = 1, Name = "RequestControllerData" }
                    },
                    KnownLightingWritePackets = new[]
                    {
                        new { Id = 1100, Name = "SetCustomMode", Note = "Not sent by this diagnostic" },
                        new { Id = 1051, Name = "UpdateZoneLEDs", Note = "Not sent by this diagnostic" }
                    },
                    DiscoveryMethod = "OpenRGB SDK only. No Windows PnP, direct HID, USB, PCI, SMBus or I2C discovery is performed.",
                    ImportantLimit = "The SDK exposes controller metadata and logical zones. It does not necessarily reveal the USB/HID/SMBus transactions or internal driver implementation performed inside OpenRGB."
                },
                DeviceCount = devices.Count,
                Devices = devices.Select(d => new
                {
                    d.Index,
                    d.Name,
                    d.Vendor,
                    d.Description,
                    d.Version,
                    d.DeviceType,
                    d.ProtocolVersion,
                    d.ZoneCount,
                    d.LedCount,
                    Zones = d.Zones.Select((z, index) => new { Index = index, z.Name, z.LedCount }),
                    RawControllerPacketHex = d.RawDataHex,
                    RawDataLengthBytes = d.RawDataHex.Length / 2
                }).ToList()
            };

            var dialog = new SaveFileDialog
            {
                Title = "Salvar diagnóstico do MASTER RGB",
                FileName = $"MASTER_RGB_Diagnostico_{DateTime.Now:yyyyMMdd_HHmmss}.json",
                Filter = "Relatório JSON (*.json)|*.json",
                DefaultExt = ".json",
                AddExtension = true
            };
            if (dialog.ShowDialog() != true)
            {
                StatusText.Text = "Diagnóstico coletado, mas o salvamento foi cancelado. Nenhuma configuração RGB foi alterada.";
                return;
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(report, options));
            EngineStatusText.Text = "Diagnóstico concluído";
            EngineStatusText.Foreground = Brushes.LightGreen;
            StatusText.Text = $"Relatório salvo em: {dialog.FileName}\n{devices.Count} dispositivo(s) analisado(s) pelo SDK.";
        }
        catch (Exception ex)
        {
            EngineStatusText.Text = "Falha no diagnóstico";
            EngineStatusText.Foreground = Brushes.Orange;
            StatusText.Text = "Não foi possível gerar o diagnóstico pelo mecanismo OpenRGB integrado: " + ex.Message;
        }
        finally
        {
            RunDiagnosticButton.IsEnabled = true;
            ConnectButtonState(false);
            SendHtmlMessage(new { action = "rgb-diagnostic-finished" });
        }
    }

    private void MultiDeviceCheckChanged(object sender, RoutedEventArgs e)
    {
        if (MultiSelectInfo is null) return;
        int count = _devices.Count(d => d.IsSelected);
        MultiSelectInfo.Text = count == 0
            ? "Nenhum dispositivo selecionado para aplicação em grupo."
            : $"{count} dispositivo(s) selecionado(s). A aplicação em grupo alcança todas as zonas controláveis.";
    }

    private void SelectAllDevices_Click(object sender, RoutedEventArgs e)
    {
        foreach (var device in _devices) device.IsSelected = true;
        MultiDeviceList.Items.Refresh();
        MultiDeviceCheckChanged(sender, e);
    }

    private void ClearDeviceSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var device in _devices) device.IsSelected = false;
        MultiDeviceList.Items.Refresh();
        MultiDeviceCheckChanged(sender, e);
    }

    private void HueArcCanvas_Loaded(object sender, RoutedEventArgs e)
    {
        if (_hueArcDrawn) return;
        DrawHueArc();
    }

    private void DrawHueArc()
    {
        HueArcCanvas.Children.Clear();
        const double cx = 130, cy = 130, radius = 105, thickness = 25;

        // Roda completa de 360 graus, com transição contínua entre as tonalidades.
        for (int i = 0; i < 360; i++)
        {
            double a1 = i * Math.PI / 180.0;
            double a2 = (i + 1.2) * Math.PI / 180.0;
            var p1 = new Point(cx + radius * Math.Cos(a1), cy + radius * Math.Sin(a1));
            var p2 = new Point(cx + radius * Math.Cos(a2), cy + radius * Math.Sin(a2));
            var path = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(HsvColor(i, 1, 1)),
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = new LineGeometry(p1, p2),
                IsHitTestVisible = false
            };
            HueArcCanvas.Children.Add(path);
        }

        _hueMarker = new Ellipse { Width = 19, Height = 19, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(20, 35, 48)), StrokeThickness = 4, IsHitTestVisible = false };
        HueArcCanvas.Children.Add(_hueMarker);
        _hueArcDrawn = true;
        if (ColorConverter.ConvertFromString(HexColorBox.Text) is Color initialColor)
            UpdateHueMarker(GetHueFromRgb(initialColor));
    }

    private void HueArcCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Point point = e.GetPosition(HueArcCanvas);
        const double cx = 130, cy = 130;
        double dx = point.X - cx;
        double dy = point.Y - cy;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        // Aceita cliques próximos ao anel colorido, sem exigir precisão milimétrica.
        if (distance < 65 || distance > 145) return;

        double hue = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 360.0) % 360.0;
        Color color = HsvColor(hue, 1, 1);
        HexColorBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        UpdateHueMarker(hue);
        e.Handled = true;
    }

    private static double GetHueFromRgb(Color color)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        if (delta == 0) return 0;

        double hue;
        if (max == r) hue = 60.0 * (((g - b) / delta) % 6.0);
        else if (max == g) hue = 60.0 * (((b - r) / delta) + 2.0);
        else hue = 60.0 * (((r - g) / delta) + 4.0);

        return (hue + 360.0) % 360.0;
    }

    private void UpdateHueMarker(double hue)
    {
        if (_hueMarker is null) return;
        const double cx = 130, cy = 130, radius = 105;
        double angle = (hue % 360.0) * Math.PI / 180.0;
        Canvas.SetLeft(_hueMarker, cx + radius * Math.Cos(angle) - _hueMarker.Width / 2);
        Canvas.SetTop(_hueMarker, cy + radius * Math.Sin(angle) - _hueMarker.Height / 2);
    }

    private static Color HsvColor(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        double c = value * saturation;
        double x = c * (1 - Math.Abs((hue / 60.0 % 2) - 1));
        double m = value - c;
        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private async void ApplyToSelectedDevices_Click(object sender, RoutedEventArgs e)
    {
        var selected = _devices.Where(d => d.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "Marque pelo menos um dispositivo na seção Aplicação em grupo.";
            return;
        }
        string hex = HexColorBox.Text.Trim().TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            StatusText.Text = "Cor inválida. Use #RRGGBB.";
            return;
        }
        double brightness = Math.Clamp(BrightnessSlider.Value, 0, 100) / 100.0;
        byte red = (byte)Math.Round(((rgb >> 16) & 0xFF) * brightness);
        byte green = (byte)Math.Round(((rgb >> 8) & 0xFF) * brightness);
        byte blue = (byte)Math.Round((rgb & 0xFF) * brightness);
        var targets = selected.SelectMany(d => d.Zones.Select(z => (Device: d, Zone: z)))
            .Where(t => t.Zone.LedCount > 0).ToList();
        if (targets.Count == 0)
        {
            StatusText.Text = "Os dispositivos selecionados não possuem zonas com LEDs controláveis.";
            return;
        }

        ApplyControlsEnabled(false);
        int succeeded = 0;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        try
        {
            // Uma conexão por dispositivo: ativa o modo Custom uma vez e envia
            // todas as zonas sem repetir as pausas de cada operação.
            // Dispositivos diferentes continuam sendo atualizados em paralelo.
            var deviceGroups = targets.GroupBy(t => t.Device).ToList();
            var updateTasks = deviceGroups.Select(async deviceGroup =>
            {
                var groupTargets = deviceGroup.ToList();
                var zoneCommands = groupTargets.Select(target => (
                    ZoneIndex: target.Zone.Index,
                    LedCount: GetConfiguredLedCount(target.Device, target.Zone))).ToList();
                try
                {
                    foreach (var command in zoneCommands)
                    {
                        var detected = deviceGroup.Key.Zones[command.ZoneIndex];
                        if (command.LedCount != detected.LedCount)
                            await _openRgb.ResizeZoneAsync(deviceGroup.Key.Index, command.ZoneIndex, command.LedCount);
                    }
                    await _openRgb.ApplyDeviceZonesColorAsync(deviceGroup.Key.Index, zoneCommands, red, green, blue);
                    System.Threading.Interlocked.Add(ref succeeded, groupTargets.Count);
                }
                catch (Exception ex)
                {
                    foreach (var target in groupTargets)
                        failures.Add($"{target.Device.Name}/{target.Zone.Name}: {ex.Message}");
                }
            });

            await Task.WhenAll(updateTasks);
            EngineStatusText.Text = failures.IsEmpty ? "Aplicação em grupo concluída" : "Aplicação em grupo parcial";
            EngineStatusText.Foreground = failures.IsEmpty ? Brushes.LightGreen : Brushes.Orange;
            StatusText.Text = $"Aplicação em grupo: {succeeded}/{targets.Count} zona(s) atualizada(s) em {selected.Count} dispositivo(s), cor #{hex.ToUpperInvariant()}, brilho {(int)Math.Round(BrightnessSlider.Value)}%.";
            StatusText.Text += " Envio otimizado por dispositivo e em paralelo.";
            if (!failures.IsEmpty) StatusText.Text += " Falhas: " + string.Join(" | ", failures.Take(3));
        }
        finally { ApplyControlsEnabled(true); }
    }

    private void DeviceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceSelector.SelectedItem is not DeviceRow device)
        {
            ZoneSelector.ItemsSource = null;
            ZoneLedCountBox.Text = "0";
            ZoneLedCountInfo.Text = "Quantidade detectada pelo OpenRGB";
            return;
        }
        ZoneSelector.ItemsSource = device.Zones.Select((zone, index) => new ZoneRow(index, zone.Name, zone.LedCount)).ToList();
        if (ZoneSelector.Items.Count > 0) ZoneSelector.SelectedIndex = 0;
        ZoneLedCountInfo.Text = "Quantidade detectada pelo OpenRGB";
    }

    private void ZoneSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ZoneSelector.SelectedItem is not ZoneRow zone || DeviceSelector.SelectedItem is not DeviceRow device)
        {
            ZoneLedCountBox.Text = "0";
            ZoneLedCountInfo.Text = "Quantidade detectada pelo OpenRGB";
            return;
        }
        string key = GetZoneKey(device, zone);
        int configured = GetConfiguredLedCount(device, zone);
        ZoneLedCountBox.Text = configured.ToString(CultureInfo.InvariantCulture);
        ZoneLedCountInfo.Text = $"Detectados pelo OpenRGB: {zone.LedCount} • Ajuste conforme a quantidade física";
    }

    private async void ApplyColor_Click(object sender, RoutedEventArgs e) => await ApplySelectedColorAsync();

    private async void TurnOffZone_Click(object sender, RoutedEventArgs e)
    {
        HexColorBox.Text = "#000000";
        await ApplySelectedColorAsync();
    }

    private void QuickColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string color) HexColorBox.Text = color;
    }

    private void HexColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingColorText || ColorPreview is null) return;
        string value = HexColorBox.Text.Trim();
        if (value.Length == 7 && value[0] == '#' && uint.TryParse(value[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            if (RedSlider is null || GreenSlider is null || BlueSlider is null || RedValueText is null || GreenValueText is null || BlueValueText is null) return;
            try
            {
                Color selectedColor = (Color)ColorConverter.ConvertFromString(value);
                ColorPreview.Background = new SolidColorBrush(selectedColor);
                ApplyUiAccentColor(value);
                UpdateHueMarker(GetHueFromRgb(selectedColor));
                _updatingColorText = true;
                RedSlider.Value = (rgb >> 16) & 0xFF;
                GreenSlider.Value = (rgb >> 8) & 0xFF;
                BlueSlider.Value = rgb & 0xFF;
                RedValueText.Text = ((int)RedSlider.Value).ToString();
                GreenValueText.Text = ((int)GreenSlider.Value).ToString();
                BlueValueText.Text = ((int)BlueSlider.Value).ToString();
            }
            catch { }
            finally { _updatingColorText = false; }
        }
    }

    private void CustomColorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingColorText || HexColorBox is null || RedSlider is null || GreenSlider is null || BlueSlider is null || RedValueText is null || GreenValueText is null || BlueValueText is null) return;
        int r = (int)Math.Round(RedSlider.Value);
        int g = (int)Math.Round(GreenSlider.Value);
        int b = (int)Math.Round(BlueSlider.Value);
        RedValueText.Text = r.ToString();
        GreenValueText.Text = g.ToString();
        BlueValueText.Text = b.ToString();
        string hex = $"#{r:X2}{g:X2}{b:X2}";
        _updatingColorText = true;
        HexColorBox.Text = hex;
        ApplyUiAccentColor(hex);
        Color selectedColor = Color.FromRgb((byte)r, (byte)g, (byte)b);
        ColorPreview.Background = new SolidColorBrush(selectedColor);
        UpdateHueMarker(GetHueFromRgb(selectedColor));
        _updatingColorText = false;
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessValueText is null) return;
        BrightnessValueText.Text = $"{(int)Math.Round(BrightnessSlider.Value)}%";
    }

    private async Task ApplySelectedColorAsync()
    {
        if (DeviceSelector.SelectedItem is not DeviceRow device || ZoneSelector.SelectedItem is not ZoneRow zone)
        {
            StatusText.Text = "Selecione um dispositivo e uma zona antes de aplicar a cor.";
            return;
        }
        string hex = HexColorBox.Text.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            StatusText.Text = "Cor inválida. Use o formato hexadecimal #RRGGBB, por exemplo #35C8FF.";
            return;
        }
        if (zone.LedCount <= 0)
        {
            StatusText.Text = "A zona selecionada não informa LEDs controláveis.";
            return;
        }
        if (!int.TryParse(ZoneLedCountBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int configuredLedCount) || configuredLedCount < 1 || configuredLedCount > ushort.MaxValue)
        {
            StatusText.Text = $"Informe uma quantidade entre 1 e 65535 LEDs para esta zona. O OpenRGB detectou {zone.LedCount}.";
            return;
        }
        _zoneLedOverrides[GetZoneKey(device, zone)] = configuredLedCount;
        SaveUserSettings();

        ApplyControlsEnabled(false);
        try
        {
            double brightness = Math.Clamp(BrightnessSlider.Value, 0, 100) / 100.0;
            byte red = (byte)Math.Round(((rgb >> 16) & 0xFF) * brightness);
            byte green = (byte)Math.Round(((rgb >> 8) & 0xFF) * brightness);
            byte blue = (byte)Math.Round((rgb & 0xFF) * brightness);
            if (configuredLedCount != zone.LedCount)
                await _openRgb.ResizeZoneAsync(device.Index, zone.Index, configuredLedCount);
            await _openRgb.ApplyZoneColorAsync(device.Index, zone.Index, configuredLedCount, red, green, blue);
            EngineStatusText.Text = "Cor enviada ao OpenRGB";
            EngineStatusText.Foreground = Brushes.LightGreen;
            StatusText.Text = $"Comando enviado: {device.Name} → {zone.Name} • {configuredLedCount}/{zone.LedCount} LED(s) • #{hex.ToUpperInvariant()} • brilho {(int)Math.Round(BrightnessSlider.Value)}%. Confira o resultado físico do dispositivo.";
        }
        catch (Exception ex)
        {
            EngineStatusText.Text = "Falha ao aplicar cor";
            EngineStatusText.Foreground = Brushes.Orange;
            StatusText.Text = $"Não foi possível aplicar a cor: {ex.Message}";
        }
        finally { ApplyControlsEnabled(true); }
    }

    private void AddFavorite_Click(object sender, RoutedEventArgs e)
    {
        string value = HexColorBox.Text.Trim().ToUpperInvariant();
        if (!value.StartsWith('#')) value = "#" + value;
        if (value.Length != 7 || !uint.TryParse(value[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            StatusText.Text = "Escolha uma cor HEX válida antes de salvá-la como favorita.";
            return;
        }
        if (_favoriteColors.Any(c => c.Hex.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = $"A cor {value} já está nas favoritas.";
            return;
        }
        _favoriteColors.Add(new FavoriteColorRow(value));
        SaveUserSettings();
        StatusText.Text = $"Cor {value} adicionada às favoritas.";
    }

    private void RemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string hex)
        {
            var item = _favoriteColors.FirstOrDefault(c => c.Hex.Equals(hex, StringComparison.OrdinalIgnoreCase));
            if (item is not null)
            {
                _favoriteColors.Remove(item);
                SaveUserSettings();
                StatusText.Text = $"Cor {hex} removida das favoritas.";
            }
        }
    }

    private static bool IsMsiArgbHeader(DeviceRow device, ZoneRow zone) =>
        device.Name.Contains("MSI", StringComparison.OrdinalIgnoreCase)
        && device.Name.Contains("B550", StringComparison.OrdinalIgnoreCase)
        && (zone.Name.Contains("JRAINBOW1", StringComparison.OrdinalIgnoreCase)
            || zone.Name.Contains("JRAINBOW2", StringComparison.OrdinalIgnoreCase));

    private int GetConfiguredLedCount(DeviceRow device, ZoneRow zone)
    {
        if (_zoneLedOverrides.TryGetValue(GetZoneKey(device, zone), out int configured))
            return configured;

        // Default físico solicitado para os dois headers ARGB da MSI MPG B550 Gaming Plus.
        return IsMsiArgbHeader(device, zone) ? 8 : zone.LedCount;
    }

    private static string GetZoneKey(DeviceRow device, ZoneRow zone) => $"{device.Name}|{zone.Name}|{zone.Index}";

    private void BlueTheme_Click(object sender, RoutedEventArgs e) => ApplyTheme("Blue");
    private void LightTheme_Click(object sender, RoutedEventArgs e) => ApplyTheme("Light");
    private void DarkTheme_Click(object sender, RoutedEventArgs e) => ApplyTheme("Dark");

    private void ApplyTheme(string theme, bool save = true)
    {
        _currentTheme = theme is "Light" or "Dark" ? theme : "Blue";

        (string background, string panel, string card, string text, string muted, string accent) = _currentTheme switch
        {
            "Light" => ("#F2F5F9", "#FFFFFF", "#E7EDF4", "#172334", "#607286", "#087EBD"),
            "Dark" => ("#080B10", "#11161F", "#1A222E", "#F1F5F9", "#9AA8B8", "#35C8FF"),
            _ => ("#0A101A", "#111D2B", "#142334", "#EAF6FF", "#8FA9BD", "#35C8FF")
        };

        Application.Current.Resources["BgBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background));
        Application.Current.Resources["PanelBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(panel));
        Application.Current.Resources["CardBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(card));
        Application.Current.Resources["TextBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text));
        Application.Current.Resources["MutedBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(muted));
        Application.Current.Resources["AccentBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_uiAccentColor));

        Background = (Brush)Application.Current.Resources["BgBrush"];
        Foreground = (Brush)Application.Current.Resources["TextBrush"];

        if (BlueThemeButton is not null)
        {
            BlueThemeButton.Content = (_currentTheme == "Blue" ? "●" : "○") + "   Azul elétrico";
            LightThemeButton.Content = (_currentTheme == "Light" ? "●" : "○") + "   Claro";
            DarkThemeButton.Content = (_currentTheme == "Dark" ? "●" : "○") + "   Escuro";
        }

        if (save) SaveUserSettings();
    }

    private void LoadUserSettings()
    {
        try
        {
            if (!File.Exists(_userSettingsPath)) return;
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_userSettingsPath));
            if (settings is null) return;
            foreach (string color in settings.FavoriteColors ?? new())
            {
                string normalized = color.StartsWith('#') ? color.ToUpperInvariant() : "#" + color.ToUpperInvariant();
                if (normalized.Length == 7 && uint.TryParse(normalized[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) && !_favoriteColors.Any(c => c.Hex == normalized))
                    _favoriteColors.Add(new FavoriteColorRow(normalized));
            }
            foreach (var pair in settings.ZoneLedOverrides ?? new Dictionary<string, int>())
                if (pair.Value > 0 && pair.Value <= ushort.MaxValue) _zoneLedOverrides[pair.Key] = pair.Value;
            BrightnessSlider.Value = Math.Clamp(settings.Brightness, 0, 100);
            _currentTheme = settings.Theme ?? "Blue";
            _uiAccentColor = NormalizeHexColor(settings.UiAccentColor) ?? "#0000FF";
            _startWithWindows = settings.StartWithWindows;
            _startMinimized = settings.StartMinimized;
            _minimizeToTray = settings.MinimizeToTray;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível carregar as preferências: {ex.Message}";
        }
    }

    private void SaveUserSettings()
    {
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(_userSettingsPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var settings = new UserSettings(_favoriteColors.Select(c => c.Hex).ToList(), _zoneLedOverrides, BrightnessSlider.Value, _currentTheme, _startWithWindows, _startMinimized, _uiAccentColor, _minimizeToTray);
            File.WriteAllText(_userSettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível salvar as preferências: {ex.Message}";
        }
    }

    private void ApplyControlsEnabled(bool enabled)
    {
        DeviceSelector.IsEnabled = enabled;
        ZoneSelector.IsEnabled = enabled;
        HexColorBox.IsEnabled = enabled;
        RedSlider.IsEnabled = enabled;
        GreenSlider.IsEnabled = enabled;
        BlueSlider.IsEnabled = enabled;
        BrightnessSlider.IsEnabled = enabled;
        ZoneLedCountBox.IsEnabled = enabled;
        foreach (var button in FindVisualChildren<Button>(this))
            if (button.Content?.ToString() is "Aplicar cor" or "Desligar zona (preto)" or "Aplicar aos selecionados") button.IsEnabled = enabled;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private void ConnectButtonState(bool busy)
    {
        // O rótulo permanece fixo; apenas bloqueia cliques durante a leitura.
        ConnectOpenRgbButton.IsEnabled = !busy;
        ConnectOpenRgbButton.Content = "Detectar dispositivos RGB";
    }

    private sealed record DeviceDisplayRow(string Name, string Details, string ZonesText, string Status);
    private sealed class DeviceRow(int index, string name, string details, string zonesText, IReadOnlyList<OpenRgbZone> sourceZones)
    {
        public int Index { get; } = index;
        public string Name { get; } = name;
        public string Details { get; } = details;
        public string ZonesText { get; } = zonesText;
        public IReadOnlyList<OpenRgbZone> SourceZones { get; } = sourceZones;
        public bool IsSelected { get; set; }
        public IReadOnlyList<ZoneRow> Zones => SourceZones.Select((zone, zoneIndex) => new ZoneRow(zoneIndex, zone.Name, zone.LedCount)).ToList();
    }
    private sealed record ZoneRow(int Index, string Name, int LedCount)
    {
        public string DisplayName => $"{Name} • {LedCount} LED(s)";
    }
    private sealed record FavoriteColorRow(string Hex)
    {
        public Brush Brush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Hex));
    }
    private static string? NormalizeHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim().ToUpperInvariant();
        if (!normalized.StartsWith('#')) normalized = "#" + normalized;
        return normalized.Length == 7 && uint.TryParse(normalized[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ? normalized : null;
    }

    private void ApplyUiAccentColor(string hex, bool save = true)
    {
        string? normalized = NormalizeHexColor(hex);
        if (normalized is null) return;
        _uiAccentColor = normalized;
        Application.Current.Resources["AccentBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_uiAccentColor));
        if (save) SaveUserSettings();
    }

    private sealed record UserSettings(List<string>? FavoriteColors, Dictionary<string, int>? ZoneLedOverrides, double Brightness = 100, string? Theme = "Blue", bool StartWithWindows = false, bool StartMinimized = false, string? UiAccentColor = "#0000FF", bool MinimizeToTray = false);
}
