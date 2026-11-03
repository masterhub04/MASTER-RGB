using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RNNoise.NET;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using MessageBox = System.Windows.MessageBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace MasterRgb.Controls;

public partial class MasterMicControl : System.Windows.Controls.UserControl
{
    private readonly ObservableCollection<DeviceInfo> _devices = new();
    private readonly List<AudioDeviceItem> _audioDevices = new();
    private List<DeviceInfo> _allDevices = new();
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _selectedAudioDevice;
    private WasapiCapture? _monitorCapture;
    private WasapiOut? _monitorOutput;
    private BufferedWaveProvider? _monitorBuffer;
    private Denoiser? _noiseSuppressor;
    private readonly float[] _noiseFrame = new float[480];
    private int _noiseFramePosition;
    private int _noiseChannels;
    private int _noiseSampleBytes;
    private bool _noiseInputIsFloat;
    private bool _noiseGateEnabled;
    private bool _compressorEnabled;
    private float _gateThresholdDb = -45f;
    private float _compressorRatio = 3f;
    private float _presenceAmountDb = 0f;
    private float _gateEnvelope;
    private float _gateGain = 1f;
    private float _compressorEnvelope;
    private float _compressorGain = 1f;
    private float _presenceLowpass;
    private readonly object _monitorProcessingLock = new();
    private readonly DispatcherTimer _monitorTimer;
    private bool _updatingAudioUi;
    private bool _isClosing;

    public MasterMicControl()
    {
        InitializeComponent();
        DevicesGrid.ItemsSource = _devices;

        _monitorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _monitorTimer.Tick += (_, _) => UpdateAudioState();

        Loaded += (_, _) =>
        {
            RefreshAudioDevices();
            LoadUsbDevices();
            _monitorTimer.Start();
        };

    }

    // CONTROLE DE AUDIO DO WINDOWS

    private void RefreshAudio_Click(object sender, RoutedEventArgs e) =>
        RefreshAudioDevices();

    private void RefreshAudioDevices()
    {
        string? previousId = _selectedAudioDevice?.ID;
        StopMicrophoneMonitor();
        _updatingAudioUi = true;

        try
        {
            MicrophoneCombo.ItemsSource = null;

            foreach (var item in _audioDevices)
                item.Device.Dispose();

            _audioDevices.Clear();
            _selectedAudioDevice = null;

            _enumerator ??= new MMDeviceEnumerator();

            var endpoints = _enumerator.EnumerateAudioEndPoints(
                DataFlow.Capture, DeviceState.Active);

            foreach (var endpoint in endpoints)
                _audioDevices.Add(new AudioDeviceItem(endpoint));

            MicrophoneCombo.ItemsSource = _audioDevices;

            var selected = _audioDevices.FirstOrDefault(
                d => d.Device.ID == previousId)
                ?? _audioDevices.FirstOrDefault(
                    d => d.FriendlyName.Contains("fifine",
                        StringComparison.OrdinalIgnoreCase))
                ?? _audioDevices.FirstOrDefault();

            if (selected is null)
            {
                DeviceStatusText.Text =
                    "Nenhum dispositivo de captura ativo foi encontrado.";
                VolumeSlider.IsEnabled = false;
                MuteButton.IsEnabled = false;
                VolumeValueText.Text = "--%";
                MuteStatusText.Text = "Estado do mute: indisponível";
                return;
            }

            MicrophoneCombo.SelectedItem = selected;
            _selectedAudioDevice = selected.Device;
            DeviceStatusText.Text =
                $"Dispositivo ativo: {selected.FriendlyName}";
            VolumeSlider.IsEnabled = true;
            MuteButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            DeviceStatusText.Text =
                "Falha ao listar dispositivos: " + ex.Message;
            VolumeSlider.IsEnabled = false;
            MuteButton.IsEnabled = false;
        }
        finally
        {
            _updatingAudioUi = false;
        }

        UpdateAudioState();
    }

    private void MicrophoneCombo_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAudioUi ||
            MicrophoneCombo.SelectedItem is not AudioDeviceItem item)
            return;

        StopMicrophoneMonitor();
        _selectedAudioDevice = item.Device;
        DeviceStatusText.Text =
            $"Dispositivo selecionado: {item.FriendlyName}";
        VolumeSlider.IsEnabled = true;
        MuteButton.IsEnabled = true;
        UpdateAudioState();
    }

    private void VolumeSlider_ValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingAudioUi || _selectedAudioDevice is null)
            return;

        try
        {
            _selectedAudioDevice.AudioEndpointVolume
                .MasterVolumeLevelScalar =
                (float)(VolumeSlider.Value / 100.0);

            VolumeValueText.Text =
                $"{Math.Round(VolumeSlider.Value)}%";
        }
        catch (Exception ex)
        {
            DeviceStatusText.Text =
                "Não foi possível alterar o volume: " + ex.Message;
        }
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAudioDevice is null)
            return;

        try
        {
            var endpoint = _selectedAudioDevice.AudioEndpointVolume;
            endpoint.Mute = !endpoint.Mute;
            UpdateAudioState();
        }
        catch (Exception ex)
        {
            DeviceStatusText.Text =
                "Não foi possível alterar o mute: " + ex.Message;
        }
    }

    private void UpdateAudioState()
    {
        if (_isClosing || _selectedAudioDevice is null ||
            _updatingAudioUi)
            return;

        try
        {
            var endpoint = _selectedAudioDevice.AudioEndpointVolume;
            float volume = endpoint.MasterVolumeLevelScalar * 100f;
            bool muted = endpoint.Mute;

            _updatingAudioUi = true;
            VolumeSlider.Value = Math.Clamp(volume, 0f, 100f);
            VolumeValueText.Text = $"{Math.Round(volume)}%";
            MuteStatusText.Text = muted
                ? "Estado do mute: ATIVADO"
                : "Estado do mute: DESATIVADO";
            MuteButton.Content = muted
                ? "ATIVAR MICROFONE"
                : "MUTAR MICROFONE";
            MuteButton.Background = muted
                ? Brushes.DarkRed
                : new SolidColorBrush(Color.FromRgb(18, 50, 74));
            MuteButton.IsEnabled = true;
            VolumeSlider.IsEnabled = true;
        }
        catch (Exception ex)
        {
            DeviceStatusText.Text =
                "O dispositivo não está disponível: " + ex.Message;
            VolumeSlider.IsEnabled = false;
            MuteButton.IsEnabled = false;
        }
        finally
        {
            _updatingAudioUi = false;
        }
    }

    private void NoiseSuppressionCheck_Changed(
        object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _updatingAudioUi)
            return;

        UpdateEffectSettingsFromUi();
        RestartMonitorForEffectChange();
    }

    private void VoiceEffects_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _updatingAudioUi)
            return;

        UpdateEffectSettingsFromUi();
        RestartMonitorForEffectChange();
    }

    private void VoiceEffectsSlider_Changed(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;

        if (GateThresholdValue is null)
            return;

        GateThresholdValue.Text = $"{GateThresholdSlider.Value:0} dB";
        CompressorRatioValue.Text = $"{CompressorRatioSlider.Value:0}:1";
        PresenceValue.Text = $"+{PresenceSlider.Value:0} dB";

        UpdateEffectSettingsFromUi();
        RestartMonitorForEffectChange();
    }

    private void UpdateEffectSettingsFromUi()
    {
        _noiseGateEnabled = NoiseGateCheck.IsChecked == true;
        _compressorEnabled = CompressorCheck.IsChecked == true;
        _gateThresholdDb = (float)GateThresholdSlider.Value;
        _compressorRatio = (float)CompressorRatioSlider.Value;
        _presenceAmountDb = (float)PresenceSlider.Value;
    }

    private void RestartMonitorForEffectChange()
    {
        if (_monitorCapture is null)
            return;

        StopMicrophoneMonitor();
        MonitorStatusText.Text =
            "Efeitos alterados. Clique em OUVIR MICROFONE para aplicar.";
    }

    private void MonitorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_monitorCapture is not null)
        {
            StopMicrophoneMonitor();
            return;
        }

        if (_selectedAudioDevice is null)
        {
            MonitorStatusText.Text = "Selecione um dispositivo de captura primeiro.";
            return;
        }

        try
        {
            UpdateEffectSettingsFromUi();
            _monitorCapture = new WasapiCapture(_selectedAudioDevice);
            var captureFormat = _monitorCapture.WaveFormat;

            bool useRnNoise = NoiseSuppressionCheck.IsChecked == true;
            bool useVoiceEffects = _noiseGateEnabled ||
                _compressorEnabled || _presenceAmountDb > 0.01f;

            if (useRnNoise || useVoiceEffects)
                InitializeAudioProcessing(captureFormat, useRnNoise);

            _monitorBuffer = new BufferedWaveProvider(captureFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };

            _monitorOutput = new WasapiOut(AudioClientShareMode.Shared, false, 50);
            _monitorOutput.Init(_monitorBuffer);

            _monitorCapture.DataAvailable += MonitorCapture_DataAvailable;

            _monitorOutput.Play();
            _monitorCapture.StartRecording();

            MonitorButton.Content = "PARAR MONITORAMENTO";
            MonitorButton.Background = new SolidColorBrush(Color.FromRgb(20, 105, 78));
            string activeEffects = string.Join(" + ",
                new[]
                {
                    _noiseSuppressor is not null ? "RNNoise" : null,
                    _noiseGateEnabled ? "Noise Gate" : null,
                    _compressorEnabled ? "Compressor" : null,
                    _presenceAmountDb > 0.01f ? "Voice Clarity" : null
                }.Where(effect => effect is not null));

            MonitorStatusText.Text = activeEffects.Length > 0
                ? $"Monitoramento ativo ({activeEffects}) — {_selectedAudioDevice.FriendlyName}"
                : "Monitoramento ativo — ouvindo " + _selectedAudioDevice.FriendlyName;
        }
        catch (Exception ex)
        {
            StopMicrophoneMonitor();
            MonitorStatusText.Text = "Falha ao iniciar: " + ex.Message;
            MessageBox.Show(
                "Não foi possível iniciar o monitoramento do microfone.\n\n" +
                ex.Message,
                "MASTER MIC",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void InitializeAudioProcessing(WaveFormat format, bool enableRnNoise)
    {
        if (format.SampleRate != 48000)
            throw new NotSupportedException(
                $"O processamento de voz precisa de áudio a 48 kHz. O dispositivo está em {format.SampleRate} Hz. " +
                "Altere o formato do microfone nas Configurações de Som do Windows para 48.000 Hz.");

        if (format.Channels is < 1 or > 2)
            throw new NotSupportedException(
                "Nesta versão, o supressor aceita microfone mono ou estéreo.");

        // SubFormat GUIDs definidos pela especificação WAVEFORMATEXTENSIBLE.
        // IEEE Float: KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
        // PCM:        KSDATAFORMAT_SUBTYPE_PCM
        Guid ieeeFloatSubtype = new("00000003-0000-0010-8000-00AA00389B71");
        Guid pcmSubtype = new("00000001-0000-0010-8000-00AA00389B71");

        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            (format is WaveFormatExtensible extFloat &&
             extFloat.SubFormat == ieeeFloatSubtype);

        bool isPcm16 = format.BitsPerSample == 16 &&
            (format.Encoding == WaveFormatEncoding.Pcm ||
             (format is WaveFormatExtensible extPcm &&
              extPcm.SubFormat == pcmSubtype));

        if (!isFloat || format.BitsPerSample != 32)
        {
            if (!isPcm16)
                throw new NotSupportedException(
                    $"Formato de áudio não suportado pelo processamento de voz: {format}. " +
                    "São aceitos PCM 16-bit ou IEEE Float 32-bit em 48 kHz.");
        }

        _noiseChannels = format.Channels;
        _noiseSampleBytes = format.BitsPerSample / 8;
        _noiseInputIsFloat = isFloat;
        _noiseFramePosition = 0;

        _gateEnvelope = 0f;
        _gateGain = 1f;
        _compressorEnvelope = 0f;
        _compressorGain = 1f;
        _presenceLowpass = 0f;

        _noiseSuppressor?.Dispose();
        _noiseSuppressor = enableRnNoise ? new Denoiser() : null;
    }

    private void MonitorCapture_DataAvailable(object? sender, WaveInEventArgs args)
    {
        lock (_monitorProcessingLock)
        {
            if (_monitorBuffer is null || args.BytesRecorded <= 0)
                return;

            bool effectsActive = _noiseSuppressor is not null ||
                _noiseGateEnabled || _compressorEnabled ||
                _presenceAmountDb > 0.01f;

            if (!effectsActive)
            {
                _monitorBuffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
                return;
            }

            ProcessAudioEffects(args.Buffer, args.BytesRecorded);
        }
    }

    private void ProcessAudioEffects(byte[] input, int bytesRecorded)
    {
        if (_monitorBuffer is null)
            return;

        int blockAlign = _noiseChannels * _noiseSampleBytes;
        int outputCapacity = bytesRecorded + (480 * blockAlign);
        byte[] output = new byte[outputCapacity];
        int outputOffset = 0;

        for (int frameOffset = 0;
             frameOffset + blockAlign <= bytesRecorded;
             frameOffset += blockAlign)
        {
            float monoSample = 0f;

            for (int channel = 0; channel < _noiseChannels; channel++)
            {
                int sampleOffset = frameOffset + (channel * _noiseSampleBytes);
                float sample = _noiseInputIsFloat
                    ? BitConverter.ToSingle(input, sampleOffset)
                    : BitConverter.ToInt16(input, sampleOffset) / 32768f;

                monoSample += Math.Clamp(sample, -1f, 1f);
            }

            _noiseFrame[_noiseFramePosition++] = monoSample / _noiseChannels;

            if (_noiseFramePosition < _noiseFrame.Length)
                continue;

            _noiseSuppressor?.Denoise(_noiseFrame.AsSpan());

            for (int i = 0; i < _noiseFrame.Length; i++)
            {
                float cleanedSample = ProcessVoiceEffects(_noiseFrame[i]);
                cleanedSample = Math.Clamp(cleanedSample, -1f, 1f);

                for (int channel = 0; channel < _noiseChannels; channel++)
                {
                    if (_noiseInputIsFloat)
                    {
                        BitConverter.TryWriteBytes(
                            output.AsSpan(outputOffset, 4), cleanedSample);
                        outputOffset += 4;
                    }
                    else
                    {
                        short pcmSample = (short)Math.Clamp(
                            (int)Math.Round(cleanedSample * 32767f),
                            short.MinValue, short.MaxValue);

                        BitConverter.TryWriteBytes(
                            output.AsSpan(outputOffset, 2), pcmSample);
                        outputOffset += 2;
                    }
                }
            }

            _noiseFramePosition = 0;
        }

        if (outputOffset > 0)
            _monitorBuffer.AddSamples(output, 0, outputOffset);
    }

    private float ProcessVoiceEffects(float sample)
    {
        float magnitude = Math.Abs(sample);

        if (_noiseGateEnabled)
        {
            float gateAttack = magnitude > _gateEnvelope ? 0.025f : 0.0015f;
            _gateEnvelope += gateAttack * (magnitude - _gateEnvelope);

            float threshold = MathF.Pow(10f, _gateThresholdDb / 20f);
            float targetGain = _gateEnvelope < threshold ? 0.06f : 1f;
            float gateSmoothing = targetGain < _gateGain ? 0.015f : 0.0015f;
            _gateGain += gateSmoothing * (targetGain - _gateGain);
            sample *= _gateGain;
        }

        if (_compressorEnabled)
        {
            magnitude = Math.Abs(sample);
            float compressorAttack = magnitude > _compressorEnvelope
                ? 0.02f : 0.001f;
            _compressorEnvelope += compressorAttack *
                (magnitude - _compressorEnvelope);

            const float threshold = 0.1259f; // aproximadamente -18 dBFS
            if (_compressorEnvelope > threshold)
            {
                float compressedLevel = threshold +
                    (_compressorEnvelope - threshold) /
                    Math.Max(1f, _compressorRatio);
                float targetGain = compressedLevel /
                    Math.Max(_compressorEnvelope, 0.0001f);

                float smoothing = targetGain < _compressorGain
                    ? 0.02f : 0.001f;
                _compressorGain += smoothing *
                    (targetGain - _compressorGain);
            }
            else
            {
                _compressorGain += 0.001f * (1f - _compressorGain);
            }

            sample *= _compressorGain * 1.15f;
        }

        if (_presenceAmountDb > 0.01f)
        {
            _presenceLowpass += 0.12f * (sample - _presenceLowpass);
            float highFrequencyDetail = sample - _presenceLowpass;
            float presenceGain = MathF.Pow(10f, _presenceAmountDb / 20f);
            sample = _presenceLowpass +
                (highFrequencyDetail * presenceGain);
        }

        return sample;
    }

    private void StopMicrophoneMonitor()
    {
        try
        {
            if (_monitorCapture is not null)
            {
                _monitorCapture.DataAvailable -= MonitorCapture_DataAvailable;
                try { _monitorCapture.StopRecording(); } catch { }
                _monitorCapture.Dispose();
                _monitorCapture = null;
            }

            if (_monitorOutput is not null)
            {
                try { _monitorOutput.Stop(); } catch { }
                _monitorOutput.Dispose();
                _monitorOutput = null;
            }

            _monitorBuffer = null;

            lock (_monitorProcessingLock)
            {
                _noiseSuppressor?.Dispose();
                _noiseSuppressor = null;
                _noiseFramePosition = 0;
                _gateEnvelope = 0f;
                _gateGain = 1f;
                _compressorEnvelope = 0f;
                _compressorGain = 1f;
                _presenceLowpass = 0f;
            }

            if (MonitorButton is not null)
            {
                MonitorButton.Content = "OUVIR MICROFONE";
                MonitorButton.Background =
                    new SolidColorBrush(Color.FromRgb(18, 50, 74));
            }

            if (MonitorStatusText is not null)
                MonitorStatusText.Text = "Monitoramento desligado";
        }
        catch (Exception ex)
        {
            MonitorStatusText.Text = "Erro ao parar monitoramento: " + ex.Message;
        }
    }

    // API pública consumida pela interface HTML do MASTER HUB.
    public object GetHtmlState()
    {
        var devices = _audioDevices.Select(d => new { id = d.Device.ID, name = d.FriendlyName }).ToArray();
        var endpoint = _selectedAudioDevice?.AudioEndpointVolume;
        return new {
            devices, selectedId = _selectedAudioDevice?.ID ?? "",
            volume = endpoint is null ? 0 : (int)Math.Round(endpoint.MasterVolumeLevelScalar * 100),
            muted = endpoint?.Mute ?? false, deviceStatus = DeviceStatusText.Text,
            monitoring = _monitorCapture is not null, monitorStatus = MonitorStatusText.Text,
            rnnoise = NoiseSuppressionCheck.IsChecked == true, gate = NoiseGateCheck.IsChecked == true,
            gateThreshold = GateThresholdSlider.Value, compressor = CompressorCheck.IsChecked == true,
            compressorRatio = CompressorRatioSlider.Value, presence = PresenceSlider.Value,
            usbStatus = StatusText.Text,
            usbDevices = _devices.Select(d => new { name=d.Name, manufacturer=d.Manufacturer, pnpClass=d.PnpClass, status=d.Status, service=d.Service, errorCode=d.ErrorCode, pnpDeviceId=d.PnpDeviceId, isFifine=d.IsFifine }).ToArray(),
            usbDetails = DetailsText.Text
        };
    }
    public void HtmlInitialize() { if (_audioDevices.Count == 0) RefreshAudioDevices(); if (_allDevices.Count == 0) LoadUsbDevices(); if (!_monitorTimer.IsEnabled) _monitorTimer.Start(); }
    public void HtmlRefreshAudio() => RefreshAudioDevices();
    public void HtmlSelectAudio(string id) { var item = _audioDevices.FirstOrDefault(d => d.Device.ID == id); if (item is not null) MicrophoneCombo.SelectedItem = item; }
    public void HtmlSetVolume(double value) { if (_selectedAudioDevice is not null) VolumeSlider.Value = Math.Clamp(value, 0, 100); }
    public void HtmlSetMute(bool muted) { if (_selectedAudioDevice is not null) { _selectedAudioDevice.AudioEndpointVolume.Mute = muted; UpdateAudioState(); } }
    public void HtmlSetEffect(string effect, bool enabled) { switch(effect) { case "rnnoise": NoiseSuppressionCheck.IsChecked=enabled; break; case "gate": NoiseGateCheck.IsChecked=enabled; break; case "compressor": CompressorCheck.IsChecked=enabled; break; } UpdateEffectSettingsFromUi(); RestartMonitorForEffectChange(); }
    public void HtmlSetParameter(string parameter, double value) { switch(parameter) { case "gateThreshold": GateThresholdSlider.Value=Math.Clamp(value,-60,-20); break; case "compressorRatio": CompressorRatioSlider.Value=Math.Clamp(value,1,6); break; case "presence": PresenceSlider.Value=Math.Clamp(value,0,6); break; } UpdateEffectSettingsFromUi(); RestartMonitorForEffectChange(); }
    public void HtmlToggleMonitor() { if (_monitorCapture is null) MonitorButton_Click(MonitorButton,new RoutedEventArgs()); else StopMicrophoneMonitor(); }
    public void HtmlRefreshUsb(bool showAll) { ShowAllCheck.IsChecked=showAll; LoadUsbDevices(); }
    public void HtmlSelectUsb(string id) { var item=_devices.FirstOrDefault(d=>d.PnpDeviceId==id); if(item is not null) DevicesGrid.SelectedItem=item; }
    public void HtmlExportUsb() => Export_Click(this,new RoutedEventArgs());

    public void Shutdown() => CleanupAudio();

    private void CleanupAudio()
    {
        _isClosing = true;
        StopMicrophoneMonitor();
        _monitorTimer.Stop();

        foreach (var item in _audioDevices)
            item.Device.Dispose();

        _audioDevices.Clear();
        _selectedAudioDevice = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    // DIAGNOSTICO USB / PNP

    private void RefreshUsb_Click(object sender, RoutedEventArgs e) =>
        LoadUsbDevices();

    private void ShowAllCheck_Changed(
        object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            ApplyFilter();
    }

    private void LoadUsbDevices()
    {
        StatusText.Text =
            "Investigando interfaces USB, HID e áudio...";

        try
        {
            var found = new List<DeviceInfo>();

            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Manufacturer, PNPClass, PNPDeviceID, " +
                "HardwareID, CompatibleID, ClassGuid, Service, Status, " +
                "ConfigManagerErrorCode FROM Win32_PnPEntity");

            using var results = searcher.Get();

            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var info = new DeviceInfo
                    {
                        Name = GetString(item, "Name"),
                        Manufacturer = GetString(item, "Manufacturer"),
                        PnpClass = GetString(item, "PNPClass"),
                        PnpDeviceId = GetString(item, "PNPDeviceID"),
                        Service = GetString(item, "Service"),
                        Status = GetString(item, "Status"),
                        ClassGuid = GetString(item, "ClassGuid"),
                        HardwareIds = GetStringArray(item, "HardwareID"),
                        CompatibleIds = GetStringArray(item, "CompatibleID"),
                        ErrorCode = GetString(item, "ConfigManagerErrorCode")
                    };

                    if (string.IsNullOrWhiteSpace(info.Name) &&
                        string.IsNullOrWhiteSpace(info.PnpDeviceId))
                        continue;

                    info.IsFifine = IsFifine(info);
                    info.IsRelevant = IsRelevant(info);
                    found.Add(info);
                }
            }

            _allDevices = found
                .OrderByDescending(d => d.IsFifine)
                .ThenByDescending(d => d.IsRelevant)
                .ThenBy(d => d.Name)
                .ToList();

            ApplyFilter();

            StatusText.Text =
                $"{_allDevices.Count} dispositivos encontrados. " +
                $"{_allDevices.Count(d => d.IsFifine)} interfaces relacionadas ao FIFINE.";
        }
        catch (Exception ex)
        {
            StatusText.Text =
                "Erro na investigação: " + ex.Message;

            MessageBox.Show(
                "Não foi possível consultar os dispositivos.\n\n" +
                ex.Message,
                "MASTER MIC",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ApplyFilter()
    {
        string? selectedId =
            (DevicesGrid.SelectedItem as DeviceInfo)?.PnpDeviceId;

        var list = ShowAllCheck.IsChecked == true
            ? _allDevices
            : _allDevices.Where(d => d.IsRelevant).ToList();

        _devices.Clear();

        foreach (var device in list)
            _devices.Add(device);

        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            DevicesGrid.SelectedItem = _devices.FirstOrDefault(
                d => d.PnpDeviceId == selectedId);
        }

        if (DevicesGrid.SelectedItem is null)
        {
            DetailsText.Text =
                "Selecione um dispositivo para visualizar interfaces, " +
                "IDs USB, IDs compatíveis e informações PnP.";
        }
    }

    private static bool IsFifine(DeviceInfo d)
    {
        string text =
            $"{d.Name} {d.Manufacturer} {d.PnpDeviceId} " +
            $"{string.Join(" ", d.HardwareIds)} " +
            $"{string.Join(" ", d.CompatibleIds)}";

        return text.Contains("3142", StringComparison.OrdinalIgnoreCase)
            || text.Contains("0A80", StringComparison.OrdinalIgnoreCase)
            || text.Contains("FIFINE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRelevant(DeviceInfo d)
    {
        if (d.IsFifine)
            return true;

        string text =
            $"{d.Name} {d.Manufacturer} {d.PnpClass} " +
            $"{d.PnpDeviceId} {d.Service} " +
            $"{string.Join(" ", d.HardwareIds)}";

        string[] terms =
        {
            "usb audio", "audio", "microphone",
            "microfone", "hid", "usb\\vid_"
        };

        return terms.Any(term =>
            text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private void DevicesGrid_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not DeviceInfo d)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("NOME: " + d.Name);
        sb.AppendLine("FABRICANTE: " + d.Manufacturer);
        sb.AppendLine("CLASSE PNP: " + d.PnpClass);
        sb.AppendLine("STATUS: " + d.Status);
        sb.AppendLine("SERVIÇO: " + d.Service);
        sb.AppendLine("CÓDIGO DE ERRO: " + d.ErrorCode);
        sb.AppendLine("GUID DA CLASSE: " + d.ClassGuid);
        sb.AppendLine();
        sb.AppendLine("PNP DEVICE ID:");
        sb.AppendLine(d.PnpDeviceId);
        sb.AppendLine();
        sb.AppendLine("HARDWARE IDS:");

        foreach (var id in d.HardwareIds)
            sb.AppendLine("  " + id);

        sb.AppendLine();
        sb.AppendLine("COMPATIBLE IDS:");

        foreach (var id in d.CompatibleIds)
            sb.AppendLine("  " + id);

        sb.AppendLine();
        sb.AppendLine(d.IsFifine
            ? "IDENTIFICAÇÃO: INTERFACE FIFINE"
            : "IDENTIFICAÇÃO: OUTRO DISPOSITIVO");

        DetailsText.Text = sb.ToString();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Salvar relatório do MASTER MIC",
            Filter = "Arquivo de texto (*.txt)|*.txt",
            FileName = "MASTER_MIC_FIFINE_Report.txt"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("MASTER MIC V0.4.0 — FIFINE USB DIAGNOSTIC REPORT");
            sb.AppendLine("Gerado em: " +
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Computador: " + Environment.MachineName);
            sb.AppendLine(new string('=', 80));

            foreach (var d in _allDevices)
            {
                sb.AppendLine();
                sb.AppendLine("NOME: " + d.Name);
                sb.AppendLine("FABRICANTE: " + d.Manufacturer);
                sb.AppendLine("CLASSE: " + d.PnpClass);
                sb.AppendLine("STATUS: " + d.Status);
                sb.AppendLine("SERVIÇO: " + d.Service);
                sb.AppendLine("CÓDIGO DE ERRO: " + d.ErrorCode);
                sb.AppendLine("GUID DA CLASSE: " + d.ClassGuid);
                sb.AppendLine("PNP DEVICE ID: " + d.PnpDeviceId);
                sb.AppendLine("HARDWARE IDS:");

                foreach (var id in d.HardwareIds)
                    sb.AppendLine("  " + id);

                sb.AppendLine("COMPATIBLE IDS:");

                foreach (var id in d.CompatibleIds)
                    sb.AppendLine("  " + id);

                sb.AppendLine("INTERFACE FIFINE: " + d.IsFifine);
                sb.AppendLine(new string('-', 80));
            }

            File.WriteAllText(
                dialog.FileName, sb.ToString(), Encoding.UTF8);

            StatusText.Text =
                "Relatório exportado: " + dialog.FileName;

            MessageBox.Show(
                "Relatório salvo com sucesso.",
                "MASTER MIC",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Erro ao salvar relatório:\n\n" + ex.Message,
                "MASTER MIC",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static string GetString(
        ManagementBaseObject item, string property) =>
        item[property]?.ToString() ?? "";

    private static string[] GetStringArray(
        ManagementBaseObject item, string property) =>
        item[property] as string[] ?? Array.Empty<string>();
}

public sealed class AudioDeviceItem
{
    public MMDevice Device { get; }
    public string FriendlyName => Device.FriendlyName;

    public AudioDeviceItem(MMDevice device) => Device = device;
    public override string ToString() => FriendlyName;
}

public sealed class DeviceInfo
{
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string PnpClass { get; set; } = "";
    public string PnpDeviceId { get; set; } = "";
    public string Service { get; set; } = "";
    public string Status { get; set; } = "";
    public string ClassGuid { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public string[] HardwareIds { get; set; } = Array.Empty<string>();
    public string[] CompatibleIds { get; set; } = Array.Empty<string>();
    public bool IsRelevant { get; set; }
    public bool IsFifine { get; set; }
}
