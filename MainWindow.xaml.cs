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
using MasterRgb.Services.DirectHardware;
using MasterRgb.Services.DirectHardware.Drivers;
using Velopack;
using Velopack.Sources;
using Forms = System.Windows.Forms;

namespace MasterRgb;

public partial class MainWindow : Window
{
    private readonly OpenRgbSdkClient _openRgb = new();
    private readonly OpenRgbEngineHost _openRgbEngine = new();
    private UpdateManager? _updateManager;
    private UpdateInfo? _pendingUpdate;
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
    private Forms.NotifyIcon? _trayIcon;
    private readonly List<string> _notifications = new();
    private readonly ObservableCollection<FavoriteColorRow> _favoriteColors = new();
    private readonly Dictionary<string, int> _zoneLedOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _userSettingsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MASTER RGB", "user-settings.json");

    public MainWindow()
    {
        InitializeComponent();
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
        RefreshDirectHardwareDisplay();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += (_, _) => { _trayIcon?.Dispose(); _openRgbEngine.Dispose(); };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
            WindowState = WindowState.Minimized;
        try
        {
            await _openRgbEngine.EnsureStartedAsync();
            EngineStatusText.Text = "Mecanismo RGB integrado • inicializado";
            EngineStatusText.Foreground = Brushes.LightGreen;
        }
        catch (Exception ex)
        {
            EngineStatusText.Text = "Mecanismo RGB indisponível";
            EngineStatusText.Foreground = Brushes.Orange;
            StatusText.Text = "Não foi possível iniciar a compatibilidade RGB integrada: " + ex.Message;
        }
        await CheckForUpdatesAsync();
    }

    private void NavigateDashboard_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(DashboardPage, DashboardNavButton);
    }

    private void EnterMasterRgb_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(LightingPage, LightingNavButton);
    }
    private void NavigateLighting_Click(object sender, RoutedEventArgs e) => ShowPage(LightingPage, LightingNavButton);
    private void NavigateDevices_Click(object sender, RoutedEventArgs e) => ShowPage(DevicesPage, DevicesNavButton);
    private void NavigateProfiles_Click(object sender, RoutedEventArgs e) => ShowPage(ProfilesPage, ProfilesNavButton);
    private void NavigateSettings_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage, SettingsNavButton);

    private void ShowPage(FrameworkElement page, Button activeButton)
    {
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
        DashboardPage.Visibility = ReferenceEquals(page, DashboardPage) ? Visibility.Visible : Visibility.Collapsed;
        LightingPage.Visibility = ReferenceEquals(page, LightingPage) ? Visibility.Visible : Visibility.Collapsed;
        DevicesPage.Visibility = ReferenceEquals(page, DevicesPage) ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = ReferenceEquals(page, ProfilesPage) ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = ReferenceEquals(page, SettingsPage) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { DashboardNavButton, LightingNavButton, DevicesNavButton, ProfilesNavButton, SettingsNavButton })
        {
            button.Background = ReferenceEquals(button, activeButton) ? new SolidColorBrush(Color.FromRgb(23, 54, 77)) : Brushes.Transparent;
            button.BorderBrush = ReferenceEquals(button, activeButton) ? new SolidColorBrush(Color.FromRgb(35, 74, 104)) : Brushes.Transparent;
        }
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
        var menu = new Forms.ContextMenuStrip();
        if (_notifications.Count == 0)
            menu.Items.Add("Nenhuma notificação no momento");
        else
            foreach (string message in _notifications.TakeLast(8).Reverse())
                menu.Items.Add(message);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Verificar atualizações", null, async (_, _) => await CheckForUpdatesAsync());
        menu.Show(Cursor.Position.X, Cursor.Position.Y);
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

    private void RefreshDirectHardwareDisplay()
    {
        // A enumeração HID é somente leitura: não abre o mouse e não envia comandos RGB.
        // A entrada é acrescentada à lista visual separada para não interferir nos
        // seletores/grupos que operam exclusivamente sobre controladores OpenRGB.
        try
        {
            // Identificação independente da placa MSI pelo inventário PnP do Windows.
            // Esta consulta lê somente metadados do Registro; não abre HID nem envia comandos.
            var pnpDevices = new WindowsHardwareInventory().Collect();
            var msiBoard = pnpDevices.FirstOrDefault(d =>
                (d.Source is "HID" or "USB") &&
                (d.HardwareIds.Contains("VID_1462&PID_7C56", StringComparison.OrdinalIgnoreCase) ||
                 d.CompatibleIds.Contains("VID_1462&PID_7C56", StringComparison.OrdinalIgnoreCase)));

            var previousBoard = _displayDevices.FirstOrDefault(d => d.Name == "MSI MPG B550 Gaming Plus (MS-7C56)");
            if (previousBoard is not null) _displayDevices.Remove(previousBoard);
            var previousMsiGroup = _devices.FirstOrDefault(d => d.Index == -2);
            if (previousMsiGroup is not null) _devices.Remove(previousMsiGroup);
            var previousMsiSelectable = _selectableDevices.FirstOrDefault(d => d.Index == -2);
            if (previousMsiSelectable is not null) _selectableDevices.Remove(previousMsiSelectable);
            var previousMsiDisplay = _displayDevices.FirstOrDefault(d => d.Name == "MSI MPG B550 Gaming Plus (MS-7C56)");
            if (previousMsiDisplay is not null) _displayDevices.Remove(previousMsiDisplay);
            if (msiBoard is not null)
            {
                const string msiName = "MSI MPG B550 Gaming Plus (MS-7C56)";
                _displayDevices.Add(new DeviceDisplayRow(
                    msiName,
                    "MSI • Windows PnP • VID_1462 / PID_7C56 • Identificação independente; controle nativo ainda bloqueado.",
                    "Zonas de referência: JRGB1 • JRAINBOW1 • JRAINBOW2 • ONBOARD",
                    "DETECTADO"));
                var msiZones = new[]
                {
                    new OpenRgbZone("JRGB1 • RGB 12V", 1),
                    new OpenRgbZone("JRAINBOW1 • ARGB 5V", 8),
                    new OpenRgbZone("JRAINBOW2 • ARGB 5V", 8),
                    new OpenRgbZone("ONBOARD", 6)
                };
                _selectableDevices.Add(new DeviceRow(-2, msiName,
                    "MSI Mystic Light • HID 185 bytes • somente seleção/inspeção",
                    "Controlador RGB da placa-mãe (controle nativo em validação)",
                    msiZones, false, true));
                _devices.Add(new DeviceRow(-2, msiName,
                    "MSI • seleção em grupo disponível; aplicação RGB aguardando validação do protocolo",
                    "Zonas de referência MSI",
                    msiZones, false, true));
            }

            // Identifica a GPU NVIDIA pelo inventário PCI do Windows.
            // A GPU aparece na lista como hardware detectado, mas não é tratada
            // como controlador RGB sem uma interface de iluminação confirmada.
            const string gpuName = "Gainward GeForce RTX 3060 Ghost 12GB";
            var previousGpu = _displayDevices.FirstOrDefault(d => d.Name == gpuName);
            if (previousGpu is not null) _displayDevices.Remove(previousGpu);
            var previousGpuSelectable = _selectableDevices.FirstOrDefault(d => d.Index == -3);
            if (previousGpuSelectable is not null) _selectableDevices.Remove(previousGpuSelectable);
            var previousGpuGroup = _devices.FirstOrDefault(d => d.Index == -3);
            if (previousGpuGroup is not null) _devices.Remove(previousGpuGroup);

            var gpu = pnpDevices.FirstOrDefault(d =>
                d.Source == "PCI" &&
                (d.HardwareIds.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) ||
                 d.CompatibleIds.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) &&
                (d.HardwareIds.Contains("CC_0300", StringComparison.OrdinalIgnoreCase) ||
                 d.CompatibleIds.Contains("CC_0300", StringComparison.OrdinalIgnoreCase) ||
                 d.CompatibleIds.Contains("CC_0302", StringComparison.OrdinalIgnoreCase) ||
                 d.HardwareIds.Contains("CC_0302", StringComparison.OrdinalIgnoreCase)));

            if (gpu is not null)
            {
                string detectedGpuName = string.IsNullOrWhiteSpace(gpu.FriendlyName) ? gpuName : gpu.FriendlyName;
                string gpuDetails = $"NVIDIA • PCI • {gpu.RedactedInstanceId} • Identificação somente leitura";
                _displayDevices.Add(new DeviceDisplayRow(detectedGpuName, gpuDetails,
                    "GPU detectada • controlador RGB ainda não identificado", "DETECTADO"));

                var gpuRow = new DeviceRow(-3, detectedGpuName, gpuDetails,
                    "GPU detectada • sem zonas RGB confirmadas",
                    new[] { new OpenRgbZone("RGB não identificado", 0) }, false, true);
                _selectableDevices.Add(gpuRow);
                _devices.Add(gpuRow);
            }

            var detection = new LogitechG203HidDriver().DetectConnectedDevice();
            var previous = _displayDevices.FirstOrDefault(d => d.Name == "Logitech G203 Prodigy");
            if (previous is not null) _displayDevices.Remove(previous);
            var previousSelectable = _selectableDevices.FirstOrDefault(d => d.IsNative);
            if (previousSelectable is not null) _selectableDevices.Remove(previousSelectable);
            if (detection.Found)
            {
                const string name = "Logitech G203 Prodigy";
                _displayDevices.Add(new DeviceDisplayRow(
                    name,
                    "Logitech • USB HID • 046D:C084 • Controle direto",
                    detection.Details,
                    "NATIVO"));
                _selectableDevices.Add(new DeviceRow(-1, name,
                    "Logitech • USB HID • Controle direto",
                    "Iluminação RGB do mouse (1 zona)",
                    new[] { new OpenRgbZone("Iluminação RGB", 1) }, true));
                var mouseRow = _selectableDevices.Last();
                _devices.Add(mouseRow);
            }
            DeviceCountText.Text = $"{_displayDevices.Count} dispositivo(s)";
            EmptyDevicesText.Visibility = _displayDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_displayDevices.Count > 0) EmptyDevicesText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Não foi possível atualizar a detecção HID do G203: " + ex.Message;
        }
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
        ConnectButtonState(true);
        EngineStatusText.Text = "Lendo dispositivos...";
        EngineStatusText.Foreground = Brushes.LightSkyBlue;
        StatusText.Text = "Conectando ao SDK e solicitando a lista real de controladores...";
        _devices.Clear();
        _selectableDevices.Clear();
        _displayDevices.Clear();
        ZoneSelector.ItemsSource = null;
        EmptyDevicesText.Visibility = Visibility.Visible;
        DeviceCountText.Text = "Lendo...";
        try
        {
            await _openRgbEngine.EnsureStartedAsync();
            var devices = await _openRgb.ReadDevicesAsync();
            foreach (var device in devices)
            {
                string vendor = string.IsNullOrWhiteSpace(device.Vendor) ? "Fabricante não informado" : device.Vendor;
                string details = $"{vendor} • {device.LedCount} LED(s) • {device.ZoneCount} zona(s)";
                string zones = device.Zones.Count == 0 ? "Sem zonas informadas" : "Zonas: " + string.Join(" • ", device.Zones.Select(z => $"{z.Name} ({z.LedCount})"));
                var row = new DeviceRow(device.Index, device.Name, details, zones, device.Zones);
                _devices.Add(row);
                _selectableDevices.Add(row);
                _displayDevices.Add(new DeviceDisplayRow(device.Name, details, zones, "OPENRGB"));
            }
            RefreshDirectHardwareDisplay();
            EmptyDevicesText.Visibility = _displayDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyDevicesText.Text = _displayDevices.Count == 0 ? "Nenhum dispositivo RGB compatível foi encontrado." : string.Empty;
            DeviceCountText.Text = $"{_displayDevices.Count} dispositivo(s)";
            MultiSelectInfo.Text = $"{devices.Count} dispositivo(s) disponível(is) para seleção em grupo.";
            EngineStatusText.Text = "SDK conectado • pronto para controlar";
            EngineStatusText.Foreground = Brushes.LightGreen;
            if (_selectableDevices.Count > 0) DeviceSelector.SelectedIndex = 0;
            StatusText.Text = devices.Count == 0
                ? "Conexão estabelecida. Nenhum controlador foi retornado pelo OpenRGB."
                : $"Leitura concluída: {devices.Count} dispositivo(s). Selecione uma zona no Lighting Studio para aplicar uma cor estática.";
        }
        catch (Exception ex)
        {
            EngineStatusText.Text = "Falha na leitura SDK";
            EngineStatusText.Foreground = Brushes.Orange;
            RefreshDirectHardwareDisplay();
            DeviceCountText.Text = $"{_displayDevices.Count} dispositivo(s) • OpenRGB indisponível";
            EmptyDevicesText.Visibility = _displayDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyDevicesText.Text = _displayDevices.Count == 0 ? "Não foi possível ler a lista pelo mecanismo RGB integrado. Verifique se o Windows bloqueou o componente ou se faltam permissões de driver." : string.Empty;
            StatusText.Text = $"Erro ao consultar o mecanismo RGB integrado: {ex.Message}";
        }
        finally { ConnectButtonState(false); }
    }

    private async void RunDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        RunDiagnosticButton.IsEnabled = false;
        ConnectOpenRgbButton.IsEnabled = false;
        StatusText.Text = "Executando diagnóstico do OpenRGB + inventário PnP do Windows (somente leitura)...";
        EngineStatusText.Text = "Diagnóstico em andamento";
        EngineStatusText.Foreground = Brushes.LightSkyBlue;
        try
        {
            var devices = await _openRgb.ReadDevicesAsync();
            var pnpDevices = new WindowsHardwareInventory().Collect();
            var msiHidProbe = new MsiHidReadOnlyProbe().Collect();
            var report = new
            {
                Report = new
                {
                    Application = "MASTER RGB",
                    ApplicationVersion = "0.9.1",
                    GeneratedAtLocal = DateTimeOffset.Now,
                    DiagnosticMode = "Read-only; no lighting mode or LED color commands are sent",
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
                        new { Id = 1100, Name = "SetCustomMode", Note = "Known from the normal color-control path; not sent by this diagnostic" },
                        new { Id = 1051, Name = "UpdateZoneLEDs", Note = "Known from the normal color-control path; not sent by this diagnostic" }
                    },
                    ImportantLimit = "The SDK exposes controller metadata and logical zones. It does not necessarily reveal the USB/HID/SMBus transactions or internal driver implementation performed inside OpenRGB.",
                    DirectDiscovery = "Read-only Windows Plug and Play inventory plus HID descriptor enumeration and source-backed MSI Mystic Light protocol correlation. No device handles are opened and no USB/HID/SMBus commands are sent.",
                    DirectControlStatus = "Logitech G203 uses its validated native HID Output Report driver. MSI protocol is mapped, but native writes remain disabled pending board-specific mode and recovery validation; OpenRGB remains the motherboard/GPU control path."
                },
                MotherboardAnalysis = BuildMotherboardAnalysis(pnpDevices, devices),
                MsiHidReadOnlyProbe = msiHidProbe,
                MsiProtocolResearch = new MsiOpenRgbProtocolEvidence().Build(pnpDevices, devices),
                GpuAnalysis = BuildGpuAnalysis(pnpDevices, devices),
                WindowsPnPInventory = new
                {
                    ReadOnly = true,
                    Sources = new[] { @"HKLM\SYSTEM\CurrentControlSet\Enum\USB", @"HKLM\SYSTEM\CurrentControlSet\Enum\HID", @"HKLM\SYSTEM\CurrentControlSet\Enum\PCI" },
                    MatchingDeviceCount = pnpDevices.Count,
                    Devices = pnpDevices
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
            StatusText.Text = $"Relatório salvo em: {dialog.FileName}\n{devices.Count} dispositivo(s) analisado(s). Envie esse JSON aqui para avaliarmos os dados.";
        }
        catch (Exception ex)
        {
            EngineStatusText.Text = "Falha no diagnóstico";
            EngineStatusText.Foreground = Brushes.Orange;
            StatusText.Text = "Não foi possível gerar o diagnóstico. Confirme se o OpenRGB está aberto e se o SDK Server está ativo na porta 6742. Erro: " + ex.Message;
        }
        finally
        {
            RunDiagnosticButton.IsEnabled = true;
            ConnectOpenRgbButton.IsEnabled = true;
        }
    }

    private static object BuildMotherboardAnalysis(
        IReadOnlyList<PnpDeviceRecord> pnpDevices,
        IReadOnlyList<OpenRgbDevice> openRgbDevices)
    {
        // SMBIOS/BIOS metadata is read from the Windows registry; no hardware bus is accessed.
        string ReadBiosValue(string name)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\BIOS", false);
                return key?.GetValue(name)?.ToString()?.Trim() ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        var board = new
        {
            Manufacturer = ReadBiosValue("BaseBoardManufacturer"),
            Product = ReadBiosValue("BaseBoardProduct"),
            Version = ReadBiosValue("BaseBoardVersion"),
            BiosVendor = ReadBiosValue("BIOSVendor"),
            BiosVersion = ReadBiosValue("BIOSVersion")
        };

        var candidates = pnpDevices
            .Where(d => d.DeviceFamily.Contains("MSI", StringComparison.OrdinalIgnoreCase)
                || d.DeviceFamily.Contains("SMBus", StringComparison.OrdinalIgnoreCase)
                || d.FriendlyName.Contains("SMBus", StringComparison.OrdinalIgnoreCase)
                || d.HardwareIds.Contains("VEN_1462", StringComparison.OrdinalIgnoreCase)
                || d.HardwareIds.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase))
            .Select(d => new
            {
                d.Source,
                d.FriendlyName,
                d.Manufacturer,
                d.Service,
                d.HardwareIds,
                d.CompatibleIds,
                d.RedactedInstanceId,
                d.DeviceFamily,
                d.RgbRelevance,
                d.RecommendedNextStep
            }).ToArray();

        var controllers = openRgbDevices
            .Where(d => d.Name.Contains("MSI", StringComparison.OrdinalIgnoreCase)
                || d.Vendor.Contains("MSI", StringComparison.OrdinalIgnoreCase)
                || d.Description.Contains("Mystic", StringComparison.OrdinalIgnoreCase)
                || d.Name.Contains("Mystic", StringComparison.OrdinalIgnoreCase))
            .Select(d => new
            {
                d.Index,
                d.Name,
                d.Vendor,
                d.Description,
                d.DeviceType,
                d.ProtocolVersion,
                d.ZoneCount,
                d.LedCount,
                Zones = d.Zones.Select(z => new { z.Name, z.LedCount }).ToArray(),
                RawControllerPacketHex = d.RawDataHex
            }).ToArray();

        return new
        {
            ReadOnly = true,
            Method = "SMBIOS registry metadata + Windows PnP inventory + OpenRGB SDK controller metadata",
            Motherboard = board,
            PnpCandidateCount = candidates.Length,
            PnpCandidates = candidates,
            AmdSmbusControllers = pnpDevices
                .Where(d => d.Source == "PCI" &&
                    d.HardwareIds.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase) &&
                    (d.HardwareIds.Contains("CC_0C05", StringComparison.OrdinalIgnoreCase) ||
                     d.FriendlyName.Contains("SMBus", StringComparison.OrdinalIgnoreCase) ||
                     d.Service.Contains("SMBus", StringComparison.OrdinalIgnoreCase)))
                .Select(d => new
                {
                    d.FriendlyName, d.Manufacturer, d.Service,
                    d.HardwareIds, d.CompatibleIds, d.RedactedInstanceId,
                    Classification = d.HardwareIds.Contains("CC_0C05", StringComparison.OrdinalIgnoreCase)
                        ? "Controlador SMBus confirmado pela classe PCI 0C05"
                        : "Dispositivo AMD relacionado; classe SMBus não confirmada"
                }).ToArray(),
            OpenRgbMysticLightControllerCount = controllers.Length,
            OpenRgbMysticLightControllers = controllers,
            Safety = "No direct USB/HID/PCI/SMBus/I2C commands were sent. No RGB modes or colors were changed.",
            NextStep = "Use the exact motherboard model, PnP candidates, SMBus controller IDs and OpenRGB controller metadata to study the matching open-source driver before implementing any direct write operation."
        };
    }


    private static object BuildGpuAnalysis(
        IReadOnlyList<PnpDeviceRecord> pnpDevices,
        IReadOnlyList<OpenRgbDevice> openRgbDevices)
    {
        // Separa o adaptador gráfico NVIDIA do dispositivo de áudio HDMI da mesma GPU.
        // A identificação permanece somente leitura.
        var nvidiaPnp = pnpDevices
            .Where(d => d.Source == "PCI" &&
                (d.HardwareIds.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) ||
                 d.CompatibleIds.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var graphicsAdapters = nvidiaPnp
            .Where(d => ContainsId(d.HardwareIds + " " + d.CompatibleIds, "CC_0300") ||
                        ContainsId(d.HardwareIds + " " + d.CompatibleIds, "CC_0302"))
            .ToArray();

        var audioFunctions = nvidiaPnp
            .Where(d => ContainsId(d.HardwareIds + " " + d.CompatibleIds, "CC_0403"))
            .ToArray();

        var lightingControllers = openRgbDevices
            .Where(d => ContainsGpuMarker(d.Name) || ContainsGpuMarker(d.Vendor) ||
                        ContainsGpuMarker(d.Description))
            .Select(d => new
            {
                d.Index, d.Name, d.Vendor, d.Description, d.Version,
                d.DeviceType, d.ProtocolVersion, d.ZoneCount, d.LedCount,
                Zones = d.Zones.Select((z, index) => new { Index = index, z.Name, z.LedCount }).ToArray(),
                RawControllerPacketHex = d.RawDataHex
            }).ToArray();

        static string ExtractIdValue(string ids, string marker)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                ids, marker + @"([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : string.Empty;
        }

        var pnpIdentity = graphicsAdapters.Select(d =>
        {
            var ids = d.HardwareIds + " " + d.CompatibleIds;
            return new
            {
                d.FriendlyName,
                d.Manufacturer,
                HardwareIds = d.HardwareIds,
                d.RedactedInstanceId,
                VendorId = ExtractIdValue(ids, @"VEN_"),
                DeviceId = ExtractIdValue(ids, @"DEV_"),
                SubsystemDeviceId = ExtractIdValue(ids, @"SUBSYS_"),
                SubsystemVendorId = ExtractIdValue(
                    System.Text.RegularExpressions.Regex.Match(ids, @"SUBSYS_([0-9A-F]{8})",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value,
                    @"(?<=^)(?:.{4})")
            };
        }).ToArray();

        bool hasPalitController = lightingControllers.Any(d =>
            (d.Name + " " + d.Vendor + " " + d.Description).Contains("Palit", StringComparison.OrdinalIgnoreCase));
        bool hasGainwardController = lightingControllers.Any(d =>
            (d.Name + " " + d.Vendor + " " + d.Description).Contains("Gainward", StringComparison.OrdinalIgnoreCase));
        bool hasGainwardPnpText = graphicsAdapters.Any(d =>
            (d.Manufacturer + " " + d.FriendlyName).Contains("Gainward", StringComparison.OrdinalIgnoreCase));

        // Nome Palit no OpenRGB não é prova de que a placa física seja Palit.
        // Se o PnP não confirma Gainward e o controlador é nomeado Palit, marcar como não verificado.
        bool identityUnverified = hasPalitController && !hasGainwardController;
        bool possibleNameMismatch = identityUnverified &&
            (graphicsAdapters.Length > 0 || hasGainwardPnpText);

        return new
        {
            ReadOnly = true,
            Method = "Windows PCI Plug and Play inventory (separando classe gráfica e áudio) + OpenRGB SDK controller metadata",
            ExpectedUserHardware = new
            {
                Manufacturer = "Gainward",
                Model = "GeForce RTX 3060 Ghost 12GB",
                KnownPciId = "PCI\\VEN_10DE&DEV_2504&SUBSYS_250410DE",
                Note = "Identificador esperado informado pelo usuário; o PnP em tempo real é a fonte de identificação observada."
            },
            NvidiaPnpDeviceCount = nvidiaPnp.Length,
            GraphicsAdapterCount = graphicsAdapters.Length,
            GraphicsAdapters = pnpIdentity,
            NvidiaAudioFunctionCount = audioFunctions.Length,
            NvidiaAudioFunctions = audioFunctions.Select(d => new
            {
                d.FriendlyName, d.Manufacturer, d.HardwareIds, d.RedactedInstanceId
            }).ToArray(),
            OpenRgbLightingControllerCount = lightingControllers.Length,
            OpenRgbLightingControllers = lightingControllers,
            ManufacturerCorrelation = new
            {
                GainwardFoundInPnp = hasGainwardPnpText,
                GainwardFoundInOpenRgb = hasGainwardController,
                PalitFoundInOpenRgb = hasPalitController,
                ControllerIdentityVerified = !identityUnverified,
                PossibleNameMismatch = possibleNameMismatch,
                Assessment = identityUnverified
                    ? "O OpenRGB apresenta um nome Palit, mas o nome do controlador não comprova o fabricante físico. A associação permanece não verificada."
                    : "Não foi detectada divergência nominal que possa ser avaliada com os metadados disponíveis.",
                Warning = identityUnverified
                    ? "Não associar este controlador à GPU física nem enviar comandos NVAPI até confirmar o fabricante, SUBSYS e a correspondência do controlador."
                    : "A identificação por nome ainda não substitui a confirmação física do modelo e do subsistema PCI."
            },
            Safety = "Somente leitura. Nenhum comando NVAPI, PCI, HID, I2C ou SMBus foi enviado; nenhum efeito ou cor foi alterado.",
            NextStep = "Confirmar o fabricante físico e a correspondência do controlador RGB antes de implementar controle nativo."
        };

        static bool ContainsId(string ids, string marker) =>
            ids.Contains(marker, StringComparison.OrdinalIgnoreCase);

        static bool ContainsGpuMarker(string value) =>
            value.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("RTX 3060", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Gainward", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Palit", StringComparison.OrdinalIgnoreCase);
    }

    private void MultiDeviceCheckChanged(object sender, RoutedEventArgs e)
    {
        if (MultiSelectInfo is null) return;
        int count = _devices.Count(d => d.IsSelected);
        int pending = _devices.Count(d => d.IsSelected && d.IsReadOnlyOnly);
        MultiSelectInfo.Text = count == 0
            ? "Nenhum dispositivo selecionado para aplicação em grupo."
            : pending > 0
                ? $"{count} selecionado(s). {pending} aguardando controle nativo validado; os demais recebem a cor."
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
        UpdateHueMarker(180);
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
        var writableSelected = selected.Where(d => !d.IsReadOnlyOnly).ToList();
        int readOnlySelected = selected.Count - writableSelected.Count;
        if (writableSelected.Count == 0)
        {
            StatusText.Text = "A MSI está marcada, mas o controle RGB nativo ainda está bloqueado até validar o protocolo da placa. Nenhum comando foi enviado.";
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
        var targets = writableSelected.SelectMany(d => d.Zones.Select(z => (Device: d, Zone: z)))
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
                    LedCount: _zoneLedOverrides.TryGetValue(GetZoneKey(target.Device, target.Zone), out int configured)
                        ? configured : target.Zone.LedCount)).ToList();
                try
                {
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
            StatusText.Text = $"Aplicação em grupo: {succeeded}/{targets.Count} zona(s) atualizada(s) em {writableSelected.Count} dispositivo(s), cor #{hex.ToUpperInvariant()}, brilho {(int)Math.Round(BrightnessSlider.Value)}%.";
            if (readOnlySelected > 0) StatusText.Text += $" {readOnlySelected} dispositivo(s) selecionado(s) ignorado(s): controle nativo ainda não validado.";
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
        if (device.IsReadOnlyOnly)
            ZoneLedCountInfo.Text = "MSI detectada • seleção informativa • envio RGB desativado até validar o protocolo";
        else if (device.IsNative)
            ZoneLedCountInfo.Text = "Controle direto do Logitech G203 • zona única • sem OpenRGB";
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
        int configured = _zoneLedOverrides.TryGetValue(key, out int value) ? value : zone.LedCount;
        ZoneLedCountBox.Text = configured.ToString(CultureInfo.InvariantCulture);
        ZoneLedCountInfo.Text = device.IsReadOnlyOnly
            ? "Quantidade de referência; ainda não lida diretamente do controlador MSI"
            : device.IsNative
                ? "Controle direto do Logitech G203 • zona única • quantidade fixa"
                : $"Detectados pelo OpenRGB: {zone.LedCount} • Ajuste conforme a quantidade física";
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
                ColorPreview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
                ApplyUiAccentColor(value);
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
        ColorPreview.Background = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));
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
        if (device.IsReadOnlyOnly)
        {
            EngineStatusText.Text = "MSI detectada • controle bloqueado";
            EngineStatusText.Foreground = Brushes.Orange;
            StatusText.Text = "A placa-mãe MSI já pode ser selecionada e inspecionada. O envio de comandos RGB permanece bloqueado até concluir a validação específica do protocolo MS-7C56.";
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
            if (device.IsNative)
            {
                var result = await Task.Run(() => new LogitechG203HidDriver().ApplyStaticColor(red, green, blue));
                EngineStatusText.Text = result.Success ? "G203 • cor enviada pelo controle direto" : "Falha no controle direto do G203";
                EngineStatusText.Foreground = result.Success ? Brushes.LightGreen : Brushes.Orange;
                StatusText.Text = result.Message + (result.Success ? $" Cor #{hex.ToUpperInvariant()} • brilho {(int)Math.Round(BrightnessSlider.Value)}%." : " Feche Logitech G HUB/OpenRGB se estiverem disputando o mouse.");
            }
            else
            {
                await _openRgb.ApplyZoneColorAsync(device.Index, zone.Index, configuredLedCount, red, green, blue);
                EngineStatusText.Text = "Cor enviada ao OpenRGB";
                EngineStatusText.Foreground = Brushes.LightGreen;
                StatusText.Text = $"Comando enviado: {device.Name} → {zone.Name} • {configuredLedCount}/{zone.LedCount} LED(s) • #{hex.ToUpperInvariant()} • brilho {(int)Math.Round(BrightnessSlider.Value)}%. Confira o resultado físico do dispositivo.";
            }
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
        ConnectOpenRgbButton.IsEnabled = !busy;
        ConnectOpenRgbButton.Content = busy ? "Lendo dispositivos..." : "Conectar ao OpenRGB";
    }

    private bool _checkingUpdates;
    private bool _updatesAvailable;

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingUpdates) return;
        if (_pendingUpdate is not null)
        {
            await DownloadAndApplyUpdateAsync();
            return;
        }
        await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_checkingUpdates) return;
        _checkingUpdates = true;
        UpdatesButton.IsEnabled = false;
        UpdatesButton.Content = "↻   Verificando...";
        UpdateText.Text = "Consultando atualizações oficiais...";
        try
        {
            _updateManager ??= new UpdateManager(
                new GithubSource("https://github.com/masterhub04/MASTER-RGB", null, false));

            if (!_updateManager.IsInstalled)
            {
                _updatesAvailable = false;
                UpdatesButton.Content = "↻   Verificar atualização";
                UpdateText.Text = "Atualizações automáticas disponíveis após instalar o MASTER RGB pelo instalador oficial.";
                return;
            }

            var update = await _updateManager.CheckForUpdatesAsync();
            _pendingUpdate = update;
            _updatesAvailable = update is not null;

            if (update is not null)
            {
                UpdatesButton.Content = "⬇   Atualizar";
                UpdateText.Text = $"Nova versão {update.TargetFullRelease.Version} disponível. Clique em Atualizar para baixar e instalar.";
                string notification = $"Nova atualização disponível: versão {update.TargetFullRelease.Version}.";
                if (!_notifications.Contains(notification)) _notifications.Add(notification);
            }
            else
            {
                UpdatesButton.Content = "↻   Verificar atualização";
                UpdateText.Text = $"MASTER RGB atualizado. Versão instalada: {_updateManager.CurrentVersion}.";
            }
        }
        catch (Exception ex)
        {
            _pendingUpdate = null;
            _updatesAvailable = false;
            UpdatesButton.Content = "↻   Verificar atualização";
            UpdateText.Text = "Não foi possível verificar atualizações: " + ex.Message;
        }
        finally
        {
            UpdatesButton.IsEnabled = true;
            _checkingUpdates = false;
        }
    }

    private async Task DownloadAndApplyUpdateAsync()
    {
        if (_pendingUpdate is null || _updateManager is null || _checkingUpdates) return;

        _checkingUpdates = true;
        UpdatesButton.IsEnabled = false;
        UpdatesButton.Content = "⬇   Baixando...";
        try
        {
            UpdateText.Text = "Baixando a atualização. Não feche o programa...";
            await _updateManager.DownloadUpdatesAsync(_pendingUpdate, progress =>
                Dispatcher.Invoke(() => UpdateText.Text = $"Baixando atualização... {progress}%"));

            UpdateText.Text = "Download concluído. O MASTER RGB será reiniciado para concluir a instalação...";
            await Task.Delay(700);
            _updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
        }
        catch (Exception ex)
        {
            UpdatesButton.Content = "↻   Verificar atualização";
            UpdatesButton.IsEnabled = true;
            _pendingUpdate = null;
            _updatesAvailable = false;
            UpdateText.Text = "Falha ao baixar/aplicar a atualização: " + ex.Message;
            _checkingUpdates = false;
        }
    }

    private sealed record DeviceDisplayRow(string Name, string Details, string ZonesText, string Status);
    private sealed class DeviceRow(int index, string name, string details, string zonesText, IReadOnlyList<OpenRgbZone> sourceZones, bool isNative = false, bool isReadOnlyOnly = false)
    {
        public int Index { get; } = index;
        public bool IsNative { get; } = isNative;
        public bool IsReadOnlyOnly { get; } = isReadOnlyOnly;
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
