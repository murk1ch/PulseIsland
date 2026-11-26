using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PulseIsland.Audio;
using PulseIsland.Core;
using PulseIsland.Interop;
using PulseIsland.Services;
using PulseIsland.Translation;

namespace PulseIsland.UI;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly MouseBatteryService? _mouseBatteryService;
    private readonly OfflineModelCatalogService _modelCatalogService = new();
    private readonly LocalLlmCatalogService _aiCatalogService = new();
    private readonly WhisperVoiceService _whisperService = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<CustomAiPromptChip> _customPrompts = new();
    private CancellationTokenSource? _downloadCts;
    private bool _isSettingsMouseBoltBlinking;
    private string _recordingHotkeyTarget = "None"; // "None", "Lasso", "Clipboard"
    private string _selectedOfflineModelId = "opus-mt-en-ru";
    private bool _clearApiKeyRequested;
    private bool _isInitializingUi = true;
    private bool _isSaved;
    private bool _isClosing;
    private AppLanguage _initialLanguage;
    private bool _suppressHexSync;
    private string _activeColorTarget = "Primary";
    private double _pickerHue = 198.0;
    private double _pickerSat = 0.77;
    private double _pickerVal = 0.97;
    private bool _isDraggingSv;
    private bool _isDraggingHue;

    private static readonly string[] PresetWaveColors =
    [
        "#FF7DD3FC", "#FF38BDF8", "#FFA78BFA", "#FFF43F5E",
        "#FFF59E0B", "#FFEC4899", "#FF14B8A6", "#FFFB923C",
        "#FF22C55E", "#FFE2B778", "#FFEF4444", "#FFFFFFFF"
    ];

    public SettingsWindow(SettingsService settingsService, MouseBatteryService? mouseBatteryService = null)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _mouseBatteryService = mouseBatteryService;
        _initialLanguage = _settingsService.Current.UiLanguage;

        if (_mouseBatteryService is not null)
            _mouseBatteryService.BatteryUpdated += OnServiceMouseBatteryUpdated;

        Closed += (_, _) =>
        {
            _downloadCts?.Cancel();
            _whisperService.Dispose();
            if (!_isSaved)
            {
                LocalizationService.ApplyLanguage(_initialLanguage);
                ThemeService.ApplyTheme(_settingsService.Current.ThemeStyle);
            }
            if (_mouseBatteryService is not null)
                _mouseBatteryService.BatteryUpdated -= OnServiceMouseBatteryUpdated;
        };
        CustomPromptsListControl.ItemsSource = _customPrompts;
        PopulateStaticLists();
        LoadFromSettings(_settingsService.Current);
        _isInitializingUi = false;
        UpdateSettingsMouseBatteryPreview(_mouseBatteryService?.LastSnapshot ?? default);

        // Load heavy disk/COM/WMI probes asynchronously after window renders so Settings opens instantaneously
        _ = LoadHeavySettingsDataAsync();
    }

    private async Task LoadHeavySettingsDataAsync()
    {
        try
        {
            string? savedAudioId = _settingsService.Current.AudioDeviceId;
            string selectedOfflineId = _selectedOfflineModelId;
            string selectedAiId = GetSelectedAiModelId();

            var audioTask = Task.Run(() =>
            {
                var options = new List<AudioDeviceOptionDto>
                {
                    new(null, LocalizationService.Pick("System Default Audio Device (Default)", "Системное устройство по умолчанию (Default)"))
                };
                foreach (var dev in WasapiAudioCaptureService.GetRenderDevices())
                {
                    options.Add(new AudioDeviceOptionDto(dev.Id, $"{dev.Name} ({dev.Id[..Math.Min(12, dev.Id.Length)]}...)"));
                }
                return options;
            });

            var modelsTask = Task.Run(() =>
            {
                var disk = _modelCatalogService.GetDiskSpaceSummary();
                var statuses = _modelCatalogService.GetModelStatuses(selectedOfflineId);
                var aiStatus = _aiCatalogService.GetStatus(selectedAiId);
                var whisperReady = WhisperVoiceService.IsReady();
                var whisperStatus = WhisperVoiceService.GetStatusText();
                return (disk, statuses, aiStatus, whisperReady, whisperStatus);
            });

            var audioOptions = await audioTask;
            if (!IsLoaded && !IsVisible) return;

            AudioDeviceCombo.ItemsSource = audioOptions;
            AudioDeviceCombo.SelectedValue = savedAudioId;
            if (AudioDeviceCombo.SelectedIndex < 0 && audioOptions.Count > 0)
                AudioDeviceCombo.SelectedIndex = 0;

            var (disk, statuses, aiStatus, whisperReady, whisperStatus) = await modelsTask;
            if (!IsLoaded && !IsVisible) return;

            ApplyOfflineModelsSnapshot(disk, statuses, aiStatus);
            ApplyWhisperStatusSnapshot(whisperReady, whisperStatus);
        }
        catch (Exception ex)
        {
            LogService.Warn($"SettingsWindow async data load warning: {ex.Message}");
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(280);

        RootAnimatedGrid.Opacity = 0.0;
        WindowScaleTransform.ScaleX = 0.92;
        WindowScaleTransform.ScaleY = 0.92;
        WindowBlurEffect.Radius = 8.0;

        RootAnimatedGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, dur) { EasingFunction = ease });
        WindowScaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1.0, dur) { EasingFunction = ease });
        WindowScaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1.0, dur) { EasingFunction = ease });
        WindowBlurEffect.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, new DoubleAnimation(8.0, 0.0, dur) { EasingFunction = ease });

        DwmBackdropService.ApplyToCard(this, MainSettingsCard, _settingsService.Current.EnableDwmBackdropBlur, 26);
    }

    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && !IsInsideInteractive(e.OriginalSource as DependencyObject))
        {
            try
            {
                DragMove();
            }
            catch
            {
                // Ignore if released mid-drag
            }
        }
    }

    private static bool IsInsideInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.Primitives.TextBoxBase
                or PasswordBox
                or ComboBox
                or Slider
                or System.Windows.Controls.Primitives.Thumb
                or System.Windows.Controls.Primitives.ScrollBar
                or TabItem)
            {
                return true;
            }
            source = DwmBackdropService.GetParentSafe(source);
        }
        return false;
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // Ignore if released mid-drag
            }
        }
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl || SettingsTabControl.SelectedContent is not UIElement content)
            return;

        content.Opacity = 0.0;
        var dur = TimeSpan.FromMilliseconds(200);
        content.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, dur)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void PopulateStaticLists()
    {
        TargetLangCombo.ItemsSource = LanguageCatalog.SupportedLanguages;
        ManualSourceLangCombo.ItemsSource = LanguageCatalog.SupportedLanguages;
        AiModelCombo.ItemsSource = LocalLlmCatalogService.BuiltInModels;

        RefreshMonitorsList();
        AudioDeviceCombo.ItemsSource = new List<AudioDeviceOptionDto>
        {
            new(null, LocalizationService.Pick("System Default Audio Device (Default)", "Системное устройство по умолчанию (Default)"))
        };
        AudioDeviceCombo.SelectedIndex = 0;
        PopulatePresetColorSwatches();
    }

    private void PopulatePresetColorSwatches()
    {
        PresetSwatchesPanel.Children.Clear();
        foreach (string hex in PresetWaveColors)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var btn = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 7, 7),
                Tag = hex,
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(color),
                ToolTip = hex
            };
            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
            borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            borderFactory.SetResourceReference(Border.BorderBrushProperty, "ThemeGlassBorderBrush");
            template.VisualTree = borderFactory;
            btn.Template = template;
            btn.Click += OnPresetSwatchClick;
            PresetSwatchesPanel.Children.Add(btn);
        }
    }

    private void RefreshMonitorsList()
    {
        int prevIndex = MonitorCombo.Items.Count > 0 && MonitorCombo.SelectedIndex >= 0
            ? MonitorCombo.SelectedIndex
            : _settingsService.Current.MonitorIndex;
        var monitors = MonitorService.GetAllMonitors(this);
        MonitorCombo.Items.Clear();
        foreach (var m in monitors)
        {
            string primaryTag = m.Index == 0 ? LocalizationService.Pick(" [Primary]", " [Основной]") : string.Empty;
            MonitorCombo.Items.Add(LocalizationService.Pick(
                $"Monitor #{m.Index + 1}: {m.PhysicalBounds.Width}×{m.PhysicalBounds.Height}{primaryTag}",
                $"Монитор #{m.Index + 1}: {m.PhysicalBounds.Width}×{m.PhysicalBounds.Height}{primaryTag}"));
        }
        if (MonitorCombo.Items.Count > 0)
            MonitorCombo.SelectedIndex = Math.Clamp(prevIndex, 0, MonitorCombo.Items.Count - 1);
    }

    private void RefreshAudioDevicesListAsync()
    {
        object? prevSelected = AudioDeviceCombo.SelectedValue;
        string defaultLabel = LocalizationService.Pick("System Default Audio Device (Default)", "Системное устройство по умолчанию (Default)");
        _ = Task.Run(() =>
        {
            var options = new List<AudioDeviceOptionDto>
            {
                new(null, defaultLabel)
            };

            foreach (var dev in WasapiAudioCaptureService.GetRenderDevices())
            {
                options.Add(new AudioDeviceOptionDto(dev.Id, $"{dev.Name} ({dev.Id[..Math.Min(12, dev.Id.Length)]}...)"));
            }

            Dispatcher.InvokeAsync(() =>
            {
                if (!IsLoaded && !IsVisible) return;
                AudioDeviceCombo.ItemsSource = options;
                AudioDeviceCombo.SelectedValue = prevSelected;
                if (AudioDeviceCombo.SelectedIndex < 0 && options.Count > 0)
                    AudioDeviceCombo.SelectedIndex = 0;
            });
        });
    }

    private void RefreshLanguageDropdownLabels()
    {
        var target = TargetLangCombo.SelectedValue;
        var source = ManualSourceLangCombo.SelectedValue;
        TargetLangCombo.ItemsSource = null;
        ManualSourceLangCombo.ItemsSource = null;
        TargetLangCombo.ItemsSource = LanguageCatalog.SupportedLanguages;
        ManualSourceLangCombo.ItemsSource = LanguageCatalog.SupportedLanguages;
        if (target is not null) TargetLangCombo.SelectedValue = target;
        if (source is not null) ManualSourceLangCombo.SelectedValue = source;
    }

    private static void RefreshComboBoxSelectionBox(ComboBox? combo)
    {
        if (combo is null || combo.SelectedItem is null)
            return;
        var selected = combo.SelectedItem;
        combo.SelectedItem = null;
        combo.SelectedItem = selected;
    }

    private AppLanguage GetSelectedUiLanguage()
    {
        if (UiLanguageCombo?.SelectedItem is ComboBoxItem langItem &&
            Enum.TryParse<AppLanguage>(langItem.Tag?.ToString(), out var lang))
        {
            return lang;
        }
        return LocalizationService.CurrentLanguage;
    }

    private void UpdateHotkeyRecorderButtons()
    {
        string pressKeys = LocalizationService.Pick("Press keys... (Esc)", "Нажмите клавиши... (Esc)");
        string pressShort = LocalizationService.Pick("Press... (Esc)", "Нажмите... (Esc)");
        RecordHotkeyBtn.Content = _recordingHotkeyTarget == "Lasso"
            ? pressKeys
            : LocalizationService.Get("Loc_Btn_RecordHotkey");
        RecordClipboardHotkeyBtn.Content = _recordingHotkeyTarget == "Clipboard"
            ? pressKeys
            : LocalizationService.Get("Loc_Btn_RecordHotkey");
        RecordAiHotkeyBtn.Content = _recordingHotkeyTarget == "AiFeed"
            ? pressShort
            : LocalizationService.Get("Loc_Btn_RecordShort");
    }

    private void RefreshDynamicLocalizedTexts()
    {
        PortableRootText.Text = LocalizationService.Pick(
            "Control panel for modules, translation, AI, and appearance",
            "Панель управления модулями, переводом и оформлением");

        bool hasSavedKey = !string.IsNullOrWhiteSpace(_settingsService.Current.EncryptedDeepLApiKey);
        if (_clearApiKeyRequested)
        {
            DeepLKeyLabel.Text = LocalizationService.Pick(
                "DeepL API key (will be removed on save):",
                "API-ключ DeepL (будет удалён при сохранении):");
        }
        else
        {
            DeepLKeyLabel.Text = hasSavedKey
                ? LocalizationService.Pick("DeepL API key (saved via DPAPI, enter new to replace):", "API-ключ DeepL (сохранён в DPAPI, введите новый для замены):")
                : LocalizationService.Pick("DeepL API key (DPAPI encrypted):", "API-ключ DeepL (DPAPI шифрование):");
        }

        bool mouseExpanded = MouseBatteryDetailsPanel?.Visibility == Visibility.Visible;
        if (ToggleMouseBatteryDetailsBtn is not null)
        {
            ToggleMouseBatteryDetailsBtn.Content = mouseExpanded
                ? LocalizationService.Get("Loc_Btn_CollapseSettings")
                : LocalizationService.Get("Loc_Btn_MoreSettings");
        }

        UpdateHotkeyRecorderButtons();

        // Force closed WPF ComboBoxes using {DynamicResource} ComboBoxItems to refresh SelectionBoxItem immediately
        bool prevInit = _isInitializingUi;
        _isInitializingUi = true;
        try
        {
            RefreshComboBoxSelectionBox(UiLanguageCombo);
            RefreshComboBoxSelectionBox(ThemeStyleCombo);
            RefreshComboBoxSelectionBox(MouseBatteryModeCombo);
            RefreshComboBoxSelectionBox(MouseLowBatterySoundCombo);
            RefreshComboBoxSelectionBox(NotificationSoundCombo);
            RefreshComboBoxSelectionBox(TranslationModeCombo);
            RefreshComboBoxSelectionBox(OcrModeCombo);
            RefreshComboBoxSelectionBox(OnlineProviderCombo);
            RefreshComboBoxSelectionBox(AiBackendCombo);
            RefreshComboBoxSelectionBox(AiFeedActionCombo);
            RefreshComboBoxSelectionBox(WaveStyleCombo);
            RefreshComboBoxSelectionBox(WaveColorModeCombo);

            var selectedAiModel = AiModelCombo.SelectedValue;
            AiModelCombo.ItemsSource = null;
            AiModelCombo.ItemsSource = LocalLlmCatalogService.BuiltInModels;
            if (selectedAiModel is not null)
                AiModelCombo.SelectedValue = selectedAiModel;
        }
        finally
        {
            _isInitializingUi = prevInit;
        }

        RefreshMonitorsList();
        RefreshAudioDevicesListAsync();
        RefreshLanguageDropdownLabels();
        RefreshOfflineModelsUi();
        UpdateSettingsMouseBatteryPreview(_mouseBatteryService?.LastSnapshot ?? default);
    }

    private void LoadFromSettings(AppSettings s)
    {
        PortableRootText.Text = LocalizationService.Pick(
            "Control panel for modules, translation, AI, and appearance",
            "Панель управления модулями, переводом и оформлением");

        // Tab 1: General & Appearance
        _isInitializingUi = true;
        SelectByTag(UiLanguageCombo, s.UiLanguage.ToString());
        SelectByTag(ThemeStyleCombo, s.ThemeStyle.ToString());
        EnableDwmBlurCheck.IsChecked = s.EnableDwmBackdropBlur;
        ShowTrackProgressBarCheck.IsChecked = s.ShowTrackProgressBar;
        EnableAlbumArtGlowCheck.IsChecked = s.EnableAlbumArtGlow;
        UseCustomAppearanceCheck.IsChecked = s.UseCustomIslandAppearance;
        CornerRadiusSlider.Value = Math.Clamp(s.IslandCornerRadius, 12, 32);
        OpacitySlider.Value = Math.Clamp(s.IslandBackgroundOpacity, 0.45, 1.0);
        UpdateSliderReadouts();

        EnableSmartClipboardCheck.IsChecked = s.EnableSmartClipboard;
        EnableVolumeAndMicCheck.IsChecked = s.EnableVolumeAndMicIndicator;
        EnableFileDropZoneCheck.IsChecked = s.EnableFileDropZone;
        EnableSystemLoadCheck.IsChecked = s.EnableSystemLoadMonitor;
        ShowCpuCheck.IsChecked = s.ShowCpuInIsland;
        ShowRamCheck.IsChecked = s.ShowRamInIsland;
        ShowTimeDateCheck.IsChecked = s.ShowTimeDateInIsland;
        ShowNetworkSpeedCheck.IsChecked = s.ShowNetworkSpeedInIsland;
        ShowBatteryCheck.IsChecked = s.ShowBatteryInIsland;
        ShowUptimeCheck.IsChecked = s.ShowUptimeInIsland;
        UpdateSystemIndicatorsEnabled();
        EnableMouseBatteryCheck.IsChecked = s.EnableMouseBatteryIndicator;
        SelectByTag(MouseBatteryModeCombo, s.MouseConnectionMode.ToString());
        ShowMouseBatteryInCompactCheck.IsChecked = s.ShowMouseBatteryInCompact;
        EnableMouseLowBatterySoundCheck.IsChecked = s.EnableMouseLowBatterySound;
        MouseLowBatteryThresholdBox.Text = s.MouseLowBatteryThresholdPercent.ToString(CultureInfo.InvariantCulture);
        SelectByTag(MouseLowBatterySoundCombo, s.MouseLowBatterySound.ToString());
        UpdateMouseBatteryOptionsEnabled();
        EnableIslandNotificationsCheck.IsChecked = s.EnableIslandNotifications;
        SuppressWindowsToastCheck.IsChecked = s.SuppressWindowsToastWhenIslandActive;
        NotifAutoDismissBox.Text = s.NotificationAutoDismissSeconds.ToString(CultureInfo.InvariantCulture);
        SelectByTag(NotificationSoundCombo, s.NotificationSound.ToString());
        UpdateIslandNotificationsEnabled();
        _isInitializingUi = false;

        AutoStartCheck.IsChecked = s.AutoStartWithWindows || AutoStartService.IsAutoStartEnabled();
        ShowTrayCheck.IsChecked = s.ShowTrayIcon;
        HideFullscreenCheck.IsChecked = s.HideInFullscreenApps;
        PinIslandCheck.IsChecked = !s.AutoHideIsland;
        UpdateAutoHideDelayEnabled();
        ExpandOnHoverCheck.IsChecked = s.ExpandOnHover;
        CollapseOnMouseLeaveCheck.IsChecked = s.ExpandOnHover && s.CollapseOnMouseLeave;
        UpdateCollapseOnMouseLeaveEnabled();
        AutoHideDelayBox.Text = s.AutoHideDelayMs.ToString(CultureInfo.InvariantCulture);
        TopHoverTriggerBox.Text = s.TopHoverTriggerHeightPx.ToString(CultureInfo.InvariantCulture);

        if (MonitorCombo.Items.Count > 0)
            MonitorCombo.SelectedIndex = Math.Clamp(s.MonitorIndex, 0, MonitorCombo.Items.Count - 1);

        // Tab 2: Translator
        SelectByTag(TranslationModeCombo, s.TranslationMode.ToString());
        SelectByTag(OcrModeCombo, s.OcrMode.ToString());
        SelectByTag(OnlineProviderCombo, s.OnlineProvider.ToString());

        TargetLangCombo.SelectedValue = LanguageCatalog.FindOrDefault(s.TargetLanguage, "en").Code;
        ManualSourceLangCombo.SelectedValue = LanguageCatalog.FindOrDefault(s.ManualSourceLanguage, "ru").Code;
        AutoDetectLangCheck.IsChecked = s.AutoDetectSourceLanguage;

        bool hasSavedKey = !string.IsNullOrWhiteSpace(s.EncryptedDeepLApiKey);
        DeepLKeyLabel.Text = hasSavedKey
            ? LocalizationService.Pick("DeepL API key (saved via DPAPI, enter new to replace):", "API-ключ DeepL (сохранён в DPAPI, введите новый для замены):")
            : LocalizationService.Pick("DeepL API key (DPAPI encrypted):", "API-ключ DeepL (DPAPI шифрование):");
        DeepLKeyBox.Password = string.Empty;
        _clearApiKeyRequested = false;

        HotkeyBox.Text = s.HotkeyGesture;
        EnableClipboardTranslateCheck.IsChecked = s.EnableClipboardTranslateHotkey;
        ClipboardHotkeyBox.Text = s.ClipboardTranslateHotkeyGesture;
        _selectedOfflineModelId = string.IsNullOrWhiteSpace(s.SelectedOfflineModelId) ? "opus-mt-en-ru" : s.SelectedOfflineModelId;

        TrailLengthBox.Text = s.CursorTrailLengthMs.ToString(CultureInfo.InvariantCulture);
        TrailThicknessBox.Text = s.CursorTrailThickness.ToString("0.#", CultureInfo.InvariantCulture);
        TrailOpacityBox.Text = s.CursorTrailOpacity.ToString("0.##", CultureInfo.InvariantCulture);
        PopupAutoHideBox.Text = s.TranslationPopupAutoHideSeconds.ToString(CultureInfo.InvariantCulture);

        EnableLocalAiCheck.IsChecked = s.EnableLocalAiAssistant;
        AiModelCombo.SelectedValue = LocalLlmCatalogService.FindModel(s.SelectedAiModelId).ModelId;
        SelectByTag(AiBackendCombo, s.AiBackend.ToString());
        AiWeatherCityBox.Text = s.AiWeatherCity;
        EnableAiFeedHotkeyCheck.IsChecked = s.EnableAiFeedHotkey;
        AiFeedHotkeyBox.Text = s.AiFeedHotkeyGesture;
        SelectByTag(AiFeedActionCombo, s.AiFeedAction.ToString());
        _customPrompts.Clear();
        var promptSource = s.CustomAiPrompts is { Count: > 0 } ? s.CustomAiPrompts : AppSettings.GetDefaultCustomAiPrompts(s.UiLanguage);
        foreach (var chip in promptSource.Take(8))
        {
            if (!string.IsNullOrWhiteSpace(chip.Title) && !string.IsNullOrWhiteSpace(chip.PromptTemplate))
                _customPrompts.Add(chip);
        }
        RefreshAiModelUi();

        // Tab 3: Music & Waves
        EnableMusicCheck.IsChecked = s.EnableMusicModule;
        ShowAlbumArtCheck.IsChecked = s.ShowAlbumArtInCompact;
        MarqueeCheck.IsChecked = s.EnableMarqueeInCompact;
        HideMusicOnPauseCheck.IsChecked = s.HideMusicOnPause;
        PauseHideDelayBox.Text = s.HideMusicOnPauseDelaySeconds.ToString(CultureInfo.InvariantCulture);
        AudioDeviceCombo.SelectedValue = s.AudioDeviceId;
        if (AudioDeviceCombo.SelectedIndex < 0)
            AudioDeviceCombo.SelectedIndex = 0;

        HideWavesSilenceCheck.IsChecked = s.HideWavesOnSilence;
        SelectByTag(WaveStyleCombo, s.WaveStyle.ToString());
        SelectByTag(WaveColorModeCombo, s.WaveColorMode.ToString());
        WaveBarCountBox.Text = s.WaveBarCount.ToString(CultureInfo.InvariantCulture);
        WaveSensitivityBox.Text = s.WaveSensitivity.ToString("0.##", CultureInfo.InvariantCulture);
        WaveSmoothingBox.Text = s.WaveSmoothing.ToString("0.##", CultureInfo.InvariantCulture);
        StaticColorHexBox.Text = s.StaticWaveColorHex;
        GradientColorHexBox.Text = s.GradientSecondaryColorHex;
        UpdateWaveColorPreviews();
    }

    private IReadOnlyList<OfflineModelStatusDto> _offlineModelStatuses = Array.Empty<OfflineModelStatusDto>();

    private void RefreshOfflineModelsUi()
    {
        string selectedOfflineId = _selectedOfflineModelId;
        string selectedAiId = GetSelectedAiModelId();
        _ = Task.Run(() =>
        {
            var disk = _modelCatalogService.GetDiskSpaceSummary();
            var statuses = _modelCatalogService.GetModelStatuses(selectedOfflineId);
            var aiStatus = _aiCatalogService.GetStatus(selectedAiId);
            var whisperReady = WhisperVoiceService.IsReady();
            var whisperStatus = WhisperVoiceService.GetStatusText();
            Dispatcher.InvokeAsync(() =>
            {
                ApplyOfflineModelsSnapshot(disk, statuses, aiStatus);
                ApplyWhisperStatusSnapshot(whisperReady, whisperStatus);
            });
        });
    }

    private void ApplyWhisperStatusSnapshot(bool isReady, string statusText)
    {
        if (WhisperStatusText is not null)
            WhisperStatusText.Text = statusText;
        if (WhisperReadyBadge is not null)
            WhisperReadyBadge.Visibility = isReady ? Visibility.Visible : Visibility.Collapsed;
        if (DownloadWhisperBtn is not null)
            DownloadWhisperBtn.Content = isReady
                ? LocalizationService.Pick("Verify", "Проверить")
                : LocalizationService.Get("Loc_Download");
    }

    private void ApplyOfflineModelsSnapshot(
        DiskSpaceSummaryDto disk,
        IReadOnlyList<OfflineModelStatusDto> statuses,
        AiPackageStatus aiStatus)
    {
        DiskSummaryText.Text = disk.SummaryText;
        _offlineModelStatuses = statuses;

        bool prevInit = _isInitializingUi;
        _isInitializingUi = true;
        try
        {
            OfflineModelCombo.ItemsSource = _offlineModelStatuses;
            OfflineModelCombo.SelectedValue = _selectedOfflineModelId;
            if (OfflineModelCombo.SelectedIndex < 0 && _offlineModelStatuses.Count > 0)
            {
                OfflineModelCombo.SelectedIndex = 0;
                _selectedOfflineModelId = _offlineModelStatuses[0].ModelId;
            }
        }
        finally
        {
            _isInitializingUi = prevInit;
        }

        UpdateSelectedOfflineModelCard();
        ApplyAiStatusSnapshot(aiStatus);
    }

    private void UpdateSelectedOfflineModelCard()
    {
        if (OfflineModelTitleText is null)
            return;

        var selected = _offlineModelStatuses.FirstOrDefault(m =>
            string.Equals(m.ModelId, _selectedOfflineModelId, StringComparison.OrdinalIgnoreCase))
            ?? _offlineModelStatuses.FirstOrDefault();

        if (selected is null)
            return;

        OfflineModelTitleText.Text = selected.DisplayName;
        OfflineModelDescText.Text = selected.Description;
        OfflineModelSizeText.Text = selected.SizeText;
        OfflineModelActiveBadge.Visibility = selected.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        DownloadOfflineModelBtn.Content = selected.IsInstalled
            ? LocalizationService.Pick("Verify", "Проверить")
            : LocalizationService.Get("Loc_Download");
    }

    private void OnOfflineModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingUi)
            return;

        if (OfflineModelCombo.SelectedValue is string id && !string.IsNullOrWhiteSpace(id))
        {
            _selectedOfflineModelId = id;
            UpdateSelectedOfflineModelCard();
        }
    }

    private string GetSelectedAiModelId()
    {
        return AiModelCombo?.SelectedValue is string id && !string.IsNullOrWhiteSpace(id)
            ? id
            : LocalLlmCatalogService.DefaultModelId;
    }

    private void OnAiModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingUi)
            return;

        RefreshAiModelUi();
    }

    private void RefreshAiModelUi()
    {
        if (AiModelStatusText is null)
            return;

        string modelId = GetSelectedAiModelId();
        var st = _aiCatalogService.GetStatus(modelId);
        ApplyAiStatusSnapshot(st);
    }

    private void ApplyAiStatusSnapshot(AiPackageStatus st)
    {
        if (AiModelStatusText is null)
            return;

        if (AiModelTitleText is not null)
            AiModelTitleText.Text = st.DisplayName;
        if (AiModelDescText is not null)
            AiModelDescText.Text = st.Description;
        AiModelStatusText.Text = st.StatusText;
        AiHardwareText.Text = LocalizationService.Pick($"Hardware: {st.HardwareSummary}", $"Оборудование: {st.HardwareSummary}");
        bool ready = st.IsModelInstalled && st.IsRuntimeInstalled;
        AiReadyBadge.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        DownloadAiBtn.Content = ready
            ? LocalizationService.Pick("Verify", "Проверить")
            : (st.PartialDownloadBytes > 0
                ? LocalizationService.Pick("Resume", "Докачать")
                : LocalizationService.Get("Loc_Download"));
    }

    private async void OnDownloadAiPackageClick(object sender, RoutedEventArgs e)
    {
        if (_downloadCts is not null)
        {
            FooterStatusText.Text = LocalizationService.Pick(
                "Please wait for the active download to finish or cancel it.",
                "Дождитесь завершения текущей загрузки или отмените её.");
            return;
        }

        string modelId = GetSelectedAiModelId();
        var entry = LocalLlmCatalogService.FindModel(modelId);

        _downloadCts = new CancellationTokenSource();
        DownloadProgressPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        DownloadStatusText.Text = LocalizationService.Pick(
            $"Preparing download for {entry.ModelId} + llama-server...",
            $"Подготовка загрузки {entry.ModelId} + llama-server...");

        var progress = new Progress<DownloadProgressInfo>(info =>
        {
            DownloadProgressBar.Value = info.Percentage;
            DownloadStatusText.Text = $"{info.FileName} — {info.Percentage:0.0}% ({DiskSpaceSummaryDto.FormatBytes(info.BytesDownloaded)})";
        });

        try
        {
            await _aiCatalogService.DownloadAiPackageAsync(entry.ModelId, progress, _downloadCts.Token);
            FooterStatusText.Text = LocalizationService.Pick(
                $"{entry.DisplayName} and GPU/CPU runtime downloaded successfully.",
                $"{entry.DisplayName} и движок GPU/CPU успешно скачаны.");
        }
        catch (OperationCanceledException)
        {
            FooterStatusText.Text = LocalizationService.Pick(
                $"Download of {entry.ModelId} paused (click \"Resume\" to continue where it left off).",
                $"Загрузка {entry.ModelId} приостановлена (при нажатии «Докачать» продолжится с места остановки).");
        }
        catch (Exception ex)
        {
            FooterStatusText.Text = LocalizationService.Pick($"AI model download failed: {ex.Message}", $"Ошибка загрузки ИИ-модели: {ex.Message}");
            LogService.Error($"AI package download failed for {entry.ModelId}.", ex);
            System.Windows.MessageBox.Show(
                this,
                LocalizationService.Pick(
                    $"Failed to download {entry.DisplayName} or llama-server runtime:\n\n{ex.Message}\n\n" +
                    $"Model folder: {LocalLlmCatalogService.GetModelDirectory(entry.ModelId)}\nRuntime folder: {LocalLlmCatalogService.LlamaRuntimeDirectory}",
                    $"Не удалось скачать {entry.DisplayName} или движок llama-server:\n\n{ex.Message}\n\n" +
                    $"Папка модели: {LocalLlmCatalogService.GetModelDirectory(entry.ModelId)}\nПапка движка: {LocalLlmCatalogService.LlamaRuntimeDirectory}"),
                LocalizationService.Pick("AI Model Download Error", "Ошибка загрузки ИИ-модели"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;
            DownloadProgressPanel.Visibility = Visibility.Collapsed;
            RefreshOfflineModelsUi();
        }
    }

    private void OnDeleteAiPackageClick(object sender, RoutedEventArgs e)
    {
        string modelId = GetSelectedAiModelId();
        if (_aiCatalogService.DeleteAiPackage(modelId))
        {
            FooterStatusText.Text = LocalizationService.Pick(
                $"Local AI model files ({modelId}) deleted from disk.",
                $"Файлы локальной модели {modelId} удалены с диска.");
            RefreshOfflineModelsUi();
        }
    }

    private async void OnDownloadWhisperClick(object sender, RoutedEventArgs e)
    {
        if (_downloadCts is not null)
        {
            FooterStatusText.Text = LocalizationService.Pick(
                "Please wait for the active download to finish or cancel it.",
                "Дождитесь завершения текущей загрузки или отмените её.");
            return;
        }

        _downloadCts = new CancellationTokenSource();
        DownloadProgressPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        DownloadStatusText.Text = LocalizationService.Pick(
            "Preparing Whisper voice dictation download (whisper.cpp + ggml-base)...",
            "Подготовка загрузки голосовой диктовки Whisper (whisper.cpp + ggml-base)...");

        var progress = new Progress<DownloadProgressInfo>(info =>
        {
            DownloadProgressBar.Value = info.Percentage;
            DownloadStatusText.Text = $"{info.FileName} — {info.Percentage:0.0}% ({DiskSpaceSummaryDto.FormatBytes(info.BytesDownloaded)})";
        });

        try
        {
            await _whisperService.DownloadPackageAsync(progress, _downloadCts.Token);
            FooterStatusText.Text = LocalizationService.Pick(
                "Whisper voice dictation downloaded and ready to use!",
                "Голосовая диктовка Whisper успешно скачана и готова к работе!");
        }
        catch (OperationCanceledException)
        {
            FooterStatusText.Text = LocalizationService.Pick(
                "Whisper download paused (will resume on next click).",
                "Загрузка Whisper приостановлена (при следующем нажатии продолжится с места остановки).");
        }
        catch (Exception ex)
        {
            FooterStatusText.Text = LocalizationService.Pick($"Whisper download error: {ex.Message}", $"Ошибка загрузки Whisper: {ex.Message}");
            LogService.Error("Whisper package download failed.", ex);
            System.Windows.MessageBox.Show(
                this,
                LocalizationService.Pick(
                    $"Failed to download Whisper voice dictation package:\n\n{ex.Message}\n\n" +
                    $"You can also download the files manually:\n" +
                    $"1) {WhisperVoiceService.WhisperZipUrl} (extract .exe and .dll)\n" +
                    $"2) {WhisperVoiceService.DefaultModelUrl} (ggml-base.bin)\n" +
                    $"into folder:\n{WhisperVoiceService.WhisperDirectory}",
                    $"Не удалось скачать пакет голосовой диктовки Whisper:\n\n{ex.Message}\n\n" +
                    $"Вы также можете скачать файлы вручную:\n" +
                    $"1) {WhisperVoiceService.WhisperZipUrl} (распаковать .exe и .dll)\n" +
                    $"2) {WhisperVoiceService.DefaultModelUrl} (файл ggml-base.bin)\n" +
                    $"в папку:\n{WhisperVoiceService.WhisperDirectory}"),
                LocalizationService.Pick("Whisper Download Error", "Ошибка загрузки Whisper"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;
            DownloadProgressPanel.Visibility = Visibility.Collapsed;
            RefreshOfflineModelsUi();
        }
    }

    private void OnDeleteWhisperClick(object sender, RoutedEventArgs e)
    {
        if (_whisperService.DeletePackage())
        {
            FooterStatusText.Text = LocalizationService.Pick(
                "Whisper voice dictation package deleted from disk.",
                "Пакет голосовой диктовки Whisper удалён с диска.");
            RefreshOfflineModelsUi();
        }
    }

    private void OnAddCustomPromptClick(object sender, RoutedEventArgs e)
    {
        string title = (NewPromptTitleBox.Text ?? string.Empty).Trim();
        string tpl = (NewPromptTemplateBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(tpl))
        {
            FooterStatusText.Text = LocalizationService.Pick(
                "Enter a short button label and AI prompt text.",
                "Укажите короткое название кнопки и текст запроса к ИИ.");
            return;
        }

        if (_customPrompts.Count >= 8)
        {
            FooterStatusText.Text = LocalizationService.Pick(
                "Maximum 8 custom quick buttons (delete one before adding).",
                "Максимум 8 пользовательских быстрых кнопок (удалите лишнюю перед добавлением).");
            return;
        }

        if (title.Length > 26)
            title = title[..26].TrimEnd();

        _customPrompts.Add(new CustomAiPromptChip(title, tpl));
        NewPromptTitleBox.Text = string.Empty;
        NewPromptTemplateBox.Text = string.Empty;
        FooterStatusText.Text = LocalizationService.Pick(
            $"Added quick button \"{title}\" (click \"Save settings\").",
            $"Добавлена быстрая кнопка «{title}» (нажмите «Сохранить настройки»).");
    }

    private void OnRemoveCustomPromptClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CustomAiPromptChip chip })
        {
            _customPrompts.Remove(chip);
        }
    }

    private void OnResetCustomPromptsClick(object sender, RoutedEventArgs e)
    {
        _customPrompts.Clear();
        foreach (var chip in AppSettings.GetDefaultCustomAiPrompts(GetSelectedUiLanguage()))
            _customPrompts.Add(chip);
        FooterStatusText.Text = LocalizationService.Pick(
            "Default AI quick buttons restored.",
            "Восстановлены стандартные быстрые кнопки ИИ.");
    }

    private void OnOpenOnboardingGuideClick(object sender, RoutedEventArgs e)
    {
        var win = new WelcomeOnboardingWindow(_settingsService)
        {
            Owner = this
        };
        win.Closed += (_, _) =>
        {
            if (win.AppliedChanges)
            {
                _initialLanguage = _settingsService.Current.UiLanguage;
                LoadFromSettings(_settingsService.Current);
                RefreshDynamicLocalizedTexts();
                DwmBackdropService.ApplyToCard(this, MainSettingsCard, _settingsService.Current.EnableDwmBackdropBlur, 26);
            }
            else
            {
                // Keep SettingsWindow controls synced if onboarding reverted or previewed language
                bool prevInit = _isInitializingUi;
                _isInitializingUi = true;
                try
                {
                    string langTag = LocalizationService.CurrentLanguage.ToString();
                    foreach (var item in UiLanguageCombo.Items.OfType<ComboBoxItem>())
                    {
                        if (string.Equals(item.Tag?.ToString(), langTag, StringComparison.OrdinalIgnoreCase))
                        {
                            UiLanguageCombo.SelectedItem = item;
                            break;
                        }
                    }
                }
                finally
                {
                    _isInitializingUi = prevInit;
                }
                RefreshDynamicLocalizedTexts();
            }
        };
        win.ShowDialog();
    }

    // --- Interactive Hotkey Recorder ---

    private void OnRecordHotkeyClick(object sender, RoutedEventArgs e)
    {
        bool start = _recordingHotkeyTarget != "Lasso";
        _recordingHotkeyTarget = start ? "Lasso" : "None";
        UpdateHotkeyRecorderButtons();
        if (start)
            FooterStatusText.Text = LocalizationService.Pick(
                "Press modifiers (Ctrl/Alt/Shift) and a key for screen lasso...",
                "Зажмите модификаторы (Ctrl/Alt/Shift) и клавишу для обводки...");
    }

    private void OnRecordClipboardHotkeyClick(object sender, RoutedEventArgs e)
    {
        bool start = _recordingHotkeyTarget != "Clipboard";
        _recordingHotkeyTarget = start ? "Clipboard" : "None";
        UpdateHotkeyRecorderButtons();
        if (start)
            FooterStatusText.Text = LocalizationService.Pick(
                "Press modifiers (Ctrl/Alt/Shift) and a key for clipboard translation...",
                "Зажмите модификаторы (Ctrl/Alt/Shift) и клавишу для перевода буфера...");
    }

    private void OnRecordAiHotkeyClick(object sender, RoutedEventArgs e)
    {
        bool start = _recordingHotkeyTarget != "AiFeed";
        _recordingHotkeyTarget = start ? "AiFeed" : "None";
        UpdateHotkeyRecorderButtons();
        if (start)
            FooterStatusText.Text = LocalizationService.Pick(
                "Press modifiers (Ctrl/Alt/Shift) and a key to send text to AI...",
                "Зажмите модификаторы (Ctrl/Alt/Shift) и клавишу для отправки текста в нейросеть...");
    }

    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_recordingHotkeyTarget == "None")
            return;

        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            _recordingHotkeyTarget = "None";
            UpdateHotkeyRecorderButtons();
            FooterStatusText.Text = string.Empty;
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        var mods = Keyboard.Modifiers;
        var sb = new StringBuilder();
        if (mods.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (mods.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
        if (mods.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
        if (mods.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");

        if (sb.Length == 0)
        {
            sb.Append("Alt+");
        }

        sb.Append(key.ToString());
        if (_recordingHotkeyTarget == "Clipboard")
        {
            ClipboardHotkeyBox.Text = sb.ToString();
            FooterStatusText.Text = LocalizationService.Pick(
                $"Recorded clipboard hotkey: {ClipboardHotkeyBox.Text}",
                $"Записана клавиша перевода буфера: {ClipboardHotkeyBox.Text}");
        }
        else if (_recordingHotkeyTarget == "AiFeed")
        {
            AiFeedHotkeyBox.Text = sb.ToString();
            FooterStatusText.Text = LocalizationService.Pick(
                $"Recorded AI hotkey: {AiFeedHotkeyBox.Text}",
                $"Записана клавиша для нейросети: {AiFeedHotkeyBox.Text}");
        }
        else
        {
            HotkeyBox.Text = sb.ToString();
            FooterStatusText.Text = LocalizationService.Pick(
                $"Recorded lasso hotkey: {HotkeyBox.Text}",
                $"Записана клавиша обводки: {HotkeyBox.Text}");
        }

        _recordingHotkeyTarget = "None";
        UpdateHotkeyRecorderButtons();
    }

    // --- Offline Models Actions ---

    private void OnRefreshModelsClick(object sender, RoutedEventArgs e) => RefreshOfflineModelsUi();

    private void OnOpenModelsFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            PortablePaths.EnsureDirectoriesExist();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{PortablePaths.ModelsDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            FooterStatusText.Text = LocalizationService.Pick(
                $"Failed to open models folder: {ex.Message}",
                $"Не удалось открыть папку моделей: {ex.Message}");
        }
    }

    private async void OnDownloadSelectedOfflineModelClick(object sender, RoutedEventArgs e)
    {
        string modelId = _selectedOfflineModelId;
        if (string.IsNullOrWhiteSpace(modelId))
            return;

        if (_downloadCts is not null)
        {
            FooterStatusText.Text = LocalizationService.Pick(
                "Please wait for the active download to finish or cancel it.",
                "Дождитесь завершения текущей загрузки или отмените её.");
            return;
        }

        _downloadCts = new CancellationTokenSource();
        DownloadProgressPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        DownloadStatusText.Text = LocalizationService.Pick(
            $"Preparing download for {modelId}...",
            $"Подготовка загрузки {modelId}...");

        var progress = new Progress<DownloadProgressInfo>(info =>
        {
            DownloadProgressBar.Value = info.Percentage;
            DownloadStatusText.Text = $"{info.FileName} — {info.Percentage:0.0}% ({DiskSpaceSummaryDto.FormatBytes(info.BytesDownloaded)})";
        });

        try
        {
            await _modelCatalogService.DownloadModelPackageAsync(modelId, progress, _downloadCts.Token);
            _selectedOfflineModelId = modelId;
            FooterStatusText.Text = LocalizationService.Pick(
                $"Model '{modelId}' downloaded successfully.",
                $"Модель '{modelId}' успешно скачана.");
        }
        catch (OperationCanceledException)
        {
            FooterStatusText.Text = LocalizationService.Pick(
                $"Download of '{modelId}' paused (will resume from where it left off next time).",
                $"Загрузка '{modelId}' приостановлена (при следующем запуске докачается с места остановки).");
        }
        catch (Exception ex)
        {
            FooterStatusText.Text = LocalizationService.Pick($"Download error: {ex.Message}", $"Ошибка загрузки: {ex.Message}");
            LogService.Error($"Model download failed for '{modelId}'.", ex);

            string targetFolder = System.IO.Path.Combine(PortablePaths.ModelsDirectory, modelId);
            System.Windows.MessageBox.Show(
                this,
                LocalizationService.Pick(
                    $"Failed to download model '{modelId}' due to a blocked network connection for PulseIsland.exe:\n\n" +
                    $"{ex.Message}\n\n" +
                    "What you can do:\n" +
                    "1) Allow PulseIsland.exe internet access (port 443) in your firewall, antivirus, or VPN and try again.\n" +
                    $"2) Or download the files manually in a browser from https://huggingface.co/Xenova/{modelId}/tree/main " +
                    $"(needed: encoder_model.onnx and decoder_model.onnx from onnx/, plus source.spm, vocab.json, and config.json), " +
                    $"place them into:\n{targetFolder}\nand click \"Refresh list\".",
                    $"Не удалось скачать модель '{modelId}' из-за блокировки сетевого подключения для PulseIsland.exe:\n\n" +
                    $"{ex.Message}\n\n" +
                    "Что можно сделать:\n" +
                    "1) Разрешите PulseIsland.exe доступ в сеть (порт 443) в брандмауэре, антивирусе или VPN/фильтре трафика и повторите.\n" +
                    $"2) Либо скачайте файлы вручную в браузере со страницы https://huggingface.co/Xenova/{modelId}/tree/main " +
                    $"(нужны: encoder_model.onnx и decoder_model.onnx из папки onnx/, а также source.spm, vocab.json и config.json), " +
                    $"положите их в папку:\n{targetFolder}\nи нажмите «Обновить список»."),
                LocalizationService.Pick("Model download blocked by network", "Загрузка модели заблокирована сетью"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;
            DownloadProgressPanel.Visibility = Visibility.Collapsed;
            RefreshOfflineModelsUi();
        }
    }

    private void OnCancelModelDownloadClick(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
    }

    private void OnDeleteSelectedOfflineModelClick(object sender, RoutedEventArgs e)
    {
        string modelId = _selectedOfflineModelId;
        if (!string.IsNullOrWhiteSpace(modelId) && _modelCatalogService.DeleteModelPackage(modelId))
        {
            FooterStatusText.Text = LocalizationService.Pick(
                $"Model '{modelId}' deleted from disk.",
                $"Модель '{modelId}' удалена с диска.");
            RefreshOfflineModelsUi();
        }
    }

    private void OnPinIslandChanged(object sender, RoutedEventArgs e) => UpdateAutoHideDelayEnabled();

    private void OnExpandOnHoverChanged(object sender, RoutedEventArgs e) => UpdateCollapseOnMouseLeaveEnabled();

    private void OnSystemLoadChanged(object sender, RoutedEventArgs e) => UpdateSystemIndicatorsEnabled();

    private void UpdateSystemIndicatorsEnabled()
    {
        if (SystemIndicatorsPanel is null || EnableSystemLoadCheck is null)
            return;

        bool enabled = EnableSystemLoadCheck.IsChecked == true;
        SystemIndicatorsPanel.IsEnabled = enabled;
        SystemIndicatorsPanel.Opacity = enabled ? 1.0 : 0.45;
    }

    private void OnToggleMouseBatteryDetailsClick(object sender, RoutedEventArgs e)
    {
        bool expand = MouseBatteryDetailsPanel.Visibility != Visibility.Visible;
        MouseBatteryDetailsPanel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        ToggleMouseBatteryDetailsBtn.Content = expand
            ? LocalizationService.Get("Loc_Btn_CollapseSettings")
            : LocalizationService.Get("Loc_Btn_MoreSettings");
        if (expand)
        {
            MouseBatteryDetailsPanel.Opacity = 0.0;
            MouseBatteryDetailsPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
    }

    private void OnMouseBatteryEnabledChanged(object sender, RoutedEventArgs e)
    {
        UpdateMouseBatteryOptionsEnabled();
        if (!_isInitializingUi && EnableMouseBatteryCheck.IsChecked == true)
            TriggerMouseBatteryProbe();
    }

    private void OnMouseLowBatterySoundChanged(object sender, RoutedEventArgs e) => UpdateMouseBatteryOptionsEnabled();

    private void OnMouseBatteryModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitializingUi && EnableMouseBatteryCheck.IsChecked == true)
            TriggerMouseBatteryProbe();
    }

    private void OnRefreshMouseBatteryClick(object sender, RoutedEventArgs e)
    {
        TriggerMouseBatteryProbe();
    }

    private void TriggerMouseBatteryProbe()
    {
        var selectedMode = MouseConnectionMode.Auto;
        if (MouseBatteryModeCombo?.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<MouseConnectionMode>(item.Tag?.ToString(), out var parsed))
        {
            selectedMode = parsed;
        }

        if (_mouseBatteryService is not null &&
            _settingsService.Current.EnableMouseBatteryIndicator &&
            _settingsService.Current.MouseConnectionMode == selectedMode)
        {
            _mouseBatteryService.PollNow();
            UpdateSettingsMouseBatteryPreview(_mouseBatteryService.LastSnapshot);
            return;
        }

        if (SettingsMouseBatteryDetailText is not null)
            SettingsMouseBatteryDetailText.Text = LocalizationService.Pick(
                "Checking wireless mouse connection...",
                "Проверка подключения мыши...");

        _ = Task.Run(() =>
        {
            var snap = MouseBatteryService.Sample(selectedMode);
            Dispatcher.InvokeAsync(() => UpdateSettingsMouseBatteryPreview(snap));
        });
    }

    private void OnServiceMouseBatteryUpdated(MouseBatterySnapshot snapshot)
    {
        Dispatcher.InvokeAsync(() => UpdateSettingsMouseBatteryPreview(snapshot));
    }

    private void UpdateSettingsMouseBatteryPreview(MouseBatterySnapshot snap)
    {
        if (SettingsMouseBatteryPill is null || SettingsMouseBatteryText is null ||
            SettingsMouseBatteryDetailText is null || SettingsMouseBolt is null)
            return;

        bool enabled = EnableMouseBatteryCheck?.IsChecked == true;
        SettingsMouseBatteryPill.Opacity = enabled ? 1.0 : 0.45;

        int threshold = int.TryParse(MouseLowBatteryThresholdBox?.Text, out int t)
            ? Math.Clamp(t, 5, 50)
            : _settingsService.Current.MouseLowBatteryThresholdPercent;

        if (!snap.HasDevice)
        {
            SettingsMouseBatteryText.Text = "--%";
            SettingsMouseBatteryDetailText.Text = enabled
                ? LocalizationService.Pick(
                    "No mouse detected (check Bluetooth / 2.4 GHz / USB)",
                    "Мышь не обнаружена (проверьте Bluetooth / 2.4 ГГц / USB)")
                : LocalizationService.Pick(
                    "Mouse battery monitoring is off",
                    "Мониторинг заряда мыши выключен");
            SettingsMouseBolt.SetResourceReference(System.Windows.Shapes.Path.FillProperty, "ThemeAccentBrush");
            UpdateSettingsMouseBoltBlink(false);
            return;
        }

        SettingsMouseBatteryText.Text = (snap.IsCharging && snap.Percent <= 0)
            ? "USB"
            : $"{snap.Percent}%";

        string name = string.IsNullOrWhiteSpace(snap.DeviceName)
            ? LocalizationService.Pick("Wireless mouse", "Беспроводная мышь")
            : snap.DeviceName;
        SettingsMouseBatteryDetailText.Text = (snap.IsCharging && snap.Percent <= 0)
            ? $"{name} • {snap.ConnectionLabel} {LocalizationService.Pick("(charging)", "(зарядка)")}"
            : $"{name} • {snap.ConnectionLabel}{(snap.IsCharging ? LocalizationService.Pick(" • Charging", " • Заряжается") : string.Empty)}";

        bool isLow = !snap.IsCharging && snap.Percent <= threshold;
        if (snap.IsCharging)
        {
            SettingsMouseBolt.Fill = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
        }
        else if (isLow)
        {
            SettingsMouseBolt.Fill = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        }
        else
        {
            SettingsMouseBolt.SetResourceReference(System.Windows.Shapes.Path.FillProperty, "ThemeAccentBrush");
        }

        UpdateSettingsMouseBoltBlink(enabled && snap.IsCharging);
    }

    private void UpdateSettingsMouseBoltBlink(bool shouldBlink)
    {
        if (_isSettingsMouseBoltBlinking == shouldBlink || SettingsMouseBolt is null)
            return;

        _isSettingsMouseBoltBlinking = shouldBlink;
        if (shouldBlink)
        {
            var blink = new DoubleAnimation(1.0, 0.18, TimeSpan.FromMilliseconds(550))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            SettingsMouseBolt.BeginAnimation(OpacityProperty, blink);
        }
        else
        {
            SettingsMouseBolt.BeginAnimation(OpacityProperty, null);
            SettingsMouseBolt.Opacity = 1.0;
        }
    }

    private void UpdateMouseBatteryOptionsEnabled()
    {
        if (MouseBatteryDetailsPanel is null || EnableMouseBatteryCheck is null)
            return;

        bool mouseEnabled = EnableMouseBatteryCheck.IsChecked == true;
        MouseBatteryDetailsPanel.IsEnabled = mouseEnabled;
        MouseBatteryDetailsPanel.Opacity = mouseEnabled ? 1.0 : 0.45;

        if (MouseLowBatterySoundSettingsPanel is not null && EnableMouseLowBatterySoundCheck is not null)
        {
            bool soundEnabled = mouseEnabled && EnableMouseLowBatterySoundCheck.IsChecked == true;
            MouseLowBatterySoundSettingsPanel.IsEnabled = soundEnabled;
            MouseLowBatterySoundSettingsPanel.Opacity = soundEnabled ? 1.0 : 0.45;
        }

        UpdateSettingsMouseBatteryPreview(_mouseBatteryService?.LastSnapshot ?? default);
    }

    private void OnTestMouseLowBatterySoundClick(object sender, RoutedEventArgs e)
    {
        if (MouseLowBatterySoundCombo.SelectedItem is ComboBoxItem soundItem &&
            Enum.TryParse<NotificationSoundPreset>(soundItem.Tag?.ToString(), out var soundPreset))
        {
            NotificationSoundService.Play(soundPreset);
        }
    }

    private void OnIslandNotificationsChanged(object sender, RoutedEventArgs e) => UpdateIslandNotificationsEnabled();

    private void OnTestNotificationSoundClick(object sender, RoutedEventArgs e)
    {
        if (NotificationSoundCombo.SelectedItem is ComboBoxItem soundItem &&
            Enum.TryParse<NotificationSoundPreset>(soundItem.Tag?.ToString(), out var soundPreset))
        {
            NotificationSoundService.Play(soundPreset);
        }
    }

    private void UpdateIslandNotificationsEnabled()
    {
        if (IslandNotificationsPanel is null || EnableIslandNotificationsCheck is null)
            return;

        bool enabled = EnableIslandNotificationsCheck.IsChecked == true;
        IslandNotificationsPanel.IsEnabled = enabled;
        IslandNotificationsPanel.Opacity = enabled ? 1.0 : 0.45;
    }

    private void UpdateCollapseOnMouseLeaveEnabled()
    {
        if (CollapseOnMouseLeaveCheck is null)
            return;

        bool canEnable = ExpandOnHoverCheck.IsChecked == true;
        CollapseOnMouseLeaveCheck.IsEnabled = canEnable;
        CollapseOnMouseLeaveCheck.Opacity = canEnable ? 1.0 : 0.45;
        if (!canEnable)
        {
            CollapseOnMouseLeaveCheck.IsChecked = false;
        }
    }

    private void UpdateAutoHideDelayEnabled()
    {
        if (AutoHideDelayBox is not null)
        {
            bool autoHideEnabled = PinIslandCheck.IsChecked != true;
            AutoHideDelayBox.IsEnabled = autoHideEnabled;
            AutoHideDelayBox.Opacity = autoHideEnabled ? 1.0 : 0.45;
        }
    }

    private void OnRefreshMonitorsClick(object sender, RoutedEventArgs e) => RefreshMonitorsList();

    private void OnClearApiKeyClick(object sender, RoutedEventArgs e)
    {
        _clearApiKeyRequested = true;
        DeepLKeyBox.Password = string.Empty;
        DeepLKeyLabel.Text = LocalizationService.Pick(
            "DeepL API key (will be removed on save):",
            "API-ключ DeepL (будет удалён при сохранении):");
    }

    private void OnUiLanguageComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingUi)
            return;

        if (UiLanguageCombo.SelectedItem is ComboBoxItem langItem &&
            Enum.TryParse<AppLanguage>(langItem.Tag?.ToString(), out var lang))
        {
            LocalizationService.ApplyLanguage(lang);

            if (AppSettings.IsBuiltInDefaultPromptSet(_customPrompts))
            {
                _customPrompts.Clear();
                foreach (var chip in AppSettings.GetDefaultCustomAiPrompts(lang))
                    _customPrompts.Add(chip);
            }

            RefreshDynamicLocalizedTexts();
            FooterStatusText.Text = LocalizationService.Pick(
                "Language preview: English (click \"Save settings\" to keep)",
                "Предпросмотр языка: Русский (нажмите «Сохранить настройки»)");
        }
    }

    private void OnThemeComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingUi)
            return;

        if (ThemeStyleCombo.SelectedItem is ComboBoxItem themeItem &&
            Enum.TryParse<AppThemeStyle>(themeItem.Tag?.ToString(), out var style))
        {
            ThemeService.ApplyTheme(style);
            var palette = ThemeService.GetPalette(style);

            // Auto-sync waves to the newly chosen theme style without manual setup
            SelectByTag(WaveColorModeCombo, nameof(WaveColorMode.ThemeAdaptive));
            _suppressHexSync = true;
            StaticColorHexBox.Text = $"#FF{palette.AccentPrimary.R:X2}{palette.AccentPrimary.G:X2}{palette.AccentPrimary.B:X2}";
            GradientColorHexBox.Text = $"#FF{palette.AccentSecondary.R:X2}{palette.AccentSecondary.G:X2}{palette.AccentSecondary.B:X2}";
            _suppressHexSync = false;
            UpdateWaveColorPreviews();

            string themeName = LocalizationService.Get($"Loc_Theme_{style}");
            FooterStatusText.Text = LocalizationService.Pick(
                $"Theme preview: {themeName}",
                $"Предпросмотр темы: {themeName}");
        }
    }

    private void OnLiveAppearanceOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializingUi || !IsLoaded)
            return;

        DwmBackdropService.ApplyToCard(this, MainSettingsCard, EnableDwmBlurCheck.IsChecked == true, 26);
    }

    private void OnAppearanceSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSliderReadouts();
    }

    private void UpdateSliderReadouts()
    {
        if (CornerRadiusValueText is not null && CornerRadiusSlider is not null)
            CornerRadiusValueText.Text = $"{(int)Math.Round(CornerRadiusSlider.Value)} px";
        if (OpacityValueText is not null && OpacitySlider is not null)
            OpacityValueText.Text = $"{(int)Math.Round(OpacitySlider.Value * 100)}%";
    }

    private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        LocalizationService.ApplyLanguage(defaults.UiLanguage);
        LoadFromSettings(defaults);
        ThemeService.ApplyTheme(defaults.ThemeStyle);
        DwmBackdropService.ApplyToCard(this, MainSettingsCard, defaults.EnableDwmBackdropBlur, 26);
        RefreshDynamicLocalizedTexts();
        FooterStatusText.Text = LocalizationService.Pick(
            "Settings reset to defaults (click \"Save settings\").",
            "Параметры сброшены к значениям по умолчанию (нажмите «Сохранить настройки»).");
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var s = _settingsService.Current;

        // Tab 1: General & Appearance
        if (UiLanguageCombo.SelectedItem is ComboBoxItem langItem &&
            Enum.TryParse<AppLanguage>(langItem.Tag?.ToString(), out var uiLang))
        {
            s.UiLanguage = uiLang;
        }
        LocalizationService.ApplyLanguage(s.UiLanguage);

        if (ThemeStyleCombo.SelectedItem is ComboBoxItem themeItem &&
            Enum.TryParse<AppThemeStyle>(themeItem.Tag?.ToString(), out var themeStyle))
        {
            s.ThemeStyle = themeStyle;
        }
        ThemeService.ApplyTheme(s.ThemeStyle);

        s.EnableDwmBackdropBlur = EnableDwmBlurCheck.IsChecked == true;
        s.ShowTrackProgressBar = ShowTrackProgressBarCheck.IsChecked == true;
        s.EnableAlbumArtGlow = EnableAlbumArtGlowCheck.IsChecked == true;
        s.UseCustomIslandAppearance = UseCustomAppearanceCheck.IsChecked == true;
        s.IslandCornerRadius = Math.Clamp(Math.Round(CornerRadiusSlider.Value), 12, 32);
        s.IslandBackgroundOpacity = Math.Clamp(OpacitySlider.Value, 0.45, 1.0);

        s.EnableSmartClipboard = EnableSmartClipboardCheck.IsChecked == true;
        s.EnableVolumeAndMicIndicator = EnableVolumeAndMicCheck.IsChecked == true;
        s.EnableFileDropZone = EnableFileDropZoneCheck.IsChecked == true;
        s.EnableSystemLoadMonitor = EnableSystemLoadCheck.IsChecked == true;
        s.ShowCpuInIsland = ShowCpuCheck.IsChecked == true;
        s.ShowRamInIsland = ShowRamCheck.IsChecked == true;
        s.ShowTimeDateInIsland = ShowTimeDateCheck.IsChecked == true;
        s.ShowNetworkSpeedInIsland = ShowNetworkSpeedCheck.IsChecked == true;
        s.ShowBatteryInIsland = ShowBatteryCheck.IsChecked == true;
        s.ShowUptimeInIsland = ShowUptimeCheck.IsChecked == true;
        s.EnableMouseBatteryIndicator = EnableMouseBatteryCheck.IsChecked == true;
        if (MouseBatteryModeCombo.SelectedItem is ComboBoxItem mouseModeItem &&
            Enum.TryParse<MouseConnectionMode>(mouseModeItem.Tag?.ToString(), out var mouseMode))
        {
            s.MouseConnectionMode = mouseMode;
        }
        s.ShowMouseBatteryInCompact = ShowMouseBatteryInCompactCheck.IsChecked == true;
        s.EnableMouseLowBatterySound = EnableMouseLowBatterySoundCheck.IsChecked == true;
        if (int.TryParse(MouseLowBatteryThresholdBox.Text, out int mouseLowPct))
            s.MouseLowBatteryThresholdPercent = Math.Clamp(mouseLowPct, 5, 50);
        if (MouseLowBatterySoundCombo.SelectedItem is ComboBoxItem mouseSoundItem &&
            Enum.TryParse<NotificationSoundPreset>(mouseSoundItem.Tag?.ToString(), out var mouseSoundPreset))
        {
            s.MouseLowBatterySound = mouseSoundPreset;
        }
        s.EnableIslandNotifications = EnableIslandNotificationsCheck.IsChecked == true;
        s.SuppressWindowsToastWhenIslandActive = SuppressWindowsToastCheck.IsChecked == true;
        if (TryParseDoubleFlexible(NotifAutoDismissBox.Text, out double dismissSec))
            s.NotificationAutoDismissSeconds = Math.Clamp(dismissSec, 2.0, 30.0);

        if (NotificationSoundCombo.SelectedItem is ComboBoxItem soundItem &&
            Enum.TryParse<NotificationSoundPreset>(soundItem.Tag?.ToString(), out var soundPreset))
        {
            s.NotificationSound = soundPreset;
        }

        s.AutoStartWithWindows = AutoStartCheck.IsChecked == true;
        AutoStartService.SetAutoStart(s.AutoStartWithWindows);

        s.ShowTrayIcon = ShowTrayCheck.IsChecked == true;
        s.HideInFullscreenApps = HideFullscreenCheck.IsChecked == true;
        s.AutoHideIsland = PinIslandCheck.IsChecked != true;
        s.ExpandOnHover = ExpandOnHoverCheck.IsChecked == true;
        s.CollapseOnMouseLeave = s.ExpandOnHover && CollapseOnMouseLeaveCheck.IsChecked == true;
        s.MonitorIndex = Math.Max(0, MonitorCombo.SelectedIndex);

        if (int.TryParse(AutoHideDelayBox.Text, out int delayMs))
            s.AutoHideDelayMs = Math.Clamp(delayMs, 1000, 30000);

        if (int.TryParse(TopHoverTriggerBox.Text, out int triggerPx))
            s.TopHoverTriggerHeightPx = Math.Clamp(triggerPx, 2, 24);

        // Tab 2: Translator
        if (TranslationModeCombo.SelectedItem is ComboBoxItem modeItem &&
            Enum.TryParse<TranslationEngineMode>(modeItem.Tag?.ToString(), out var mode))
        {
            s.TranslationMode = mode;
        }

        if (OcrModeCombo.SelectedItem is ComboBoxItem ocrModeItem &&
            Enum.TryParse<OcrEngineMode>(ocrModeItem.Tag?.ToString(), out var ocrMode))
        {
            s.OcrMode = ocrMode;
        }

        if (OnlineProviderCombo.SelectedItem is ComboBoxItem provItem &&
            Enum.TryParse<OnlineTranslationProvider>(provItem.Tag?.ToString(), out var provider))
        {
            s.OnlineProvider = provider;
        }

        if (TargetLangCombo.SelectedValue is string targetCode && !string.IsNullOrWhiteSpace(targetCode))
            s.TargetLanguage = targetCode;

        if (ManualSourceLangCombo.SelectedValue is string sourceCode && !string.IsNullOrWhiteSpace(sourceCode))
            s.ManualSourceLanguage = sourceCode;

        s.AutoDetectSourceLanguage = AutoDetectLangCheck.IsChecked == true;
        s.SelectedOfflineModelId = _selectedOfflineModelId;
        s.EnableLocalAiAssistant = EnableLocalAiCheck.IsChecked == true;
        s.SelectedAiModelId = GetSelectedAiModelId();
        if (AiBackendCombo.SelectedItem is ComboBoxItem backendItem &&
            Enum.TryParse<AiComputeBackend>(backendItem.Tag?.ToString(), out var backend))
        {
            s.AiBackend = backend;
        }
        s.AiWeatherCity = AiWeatherCityBox.Text.Trim();
        s.EnableAiFeedHotkey = EnableAiFeedHotkeyCheck.IsChecked == true;
        var trimmedAiHotkey = AiFeedHotkeyBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedAiHotkey))
        {
            if (!HotkeyService.TryParseGesture(trimmedAiHotkey, out _, out _))
            {
                FooterStatusText.Text = LocalizationService.Pick(
                    $"Invalid AI hotkey format '{trimmedAiHotkey}'. Example: Ctrl+Alt+A.",
                    $"Неверный формат клавиши для нейросети '{trimmedAiHotkey}'. Пример: Ctrl+Alt+A.");
                return;
            }
            s.AiFeedHotkeyGesture = trimmedAiHotkey;
        }
        if (AiFeedActionCombo.SelectedItem is ComboBoxItem aiActItem &&
            Enum.TryParse<AiFeedHotkeyAction>(aiActItem.Tag?.ToString(), out var aiAction))
        {
            s.AiFeedAction = aiAction;
        }
        s.CustomAiPrompts = AppSettings.IsBuiltInDefaultPromptSet(_customPrompts)
            ? AppSettings.GetDefaultCustomAiPrompts(s.UiLanguage)
            : _customPrompts.ToList();

        var trimmedHotkey = HotkeyBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedHotkey))
        {
            if (!HotkeyService.TryParseGesture(trimmedHotkey, out _, out _))
            {
                FooterStatusText.Text = LocalizationService.Pick(
                    $"Invalid hotkey format '{trimmedHotkey}'. Example: Alt+Q or Ctrl+Shift+T.",
                    $"Неверный формат горячей клавиши '{trimmedHotkey}'. Пример: Alt+Q или Ctrl+Shift+T.");
                return;
            }
            s.HotkeyGesture = trimmedHotkey;
        }

        s.EnableClipboardTranslateHotkey = EnableClipboardTranslateCheck.IsChecked == true;
        var trimmedClipHotkey = ClipboardHotkeyBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedClipHotkey))
        {
            if (!HotkeyService.TryParseGesture(trimmedClipHotkey, out _, out _))
            {
                FooterStatusText.Text = LocalizationService.Pick(
                    $"Invalid clipboard hotkey format '{trimmedClipHotkey}'. Example: Ctrl+Alt+C.",
                    $"Неверный формат клавиши буфера '{trimmedClipHotkey}'. Пример: Ctrl+Alt+C.");
                return;
            }
            s.ClipboardTranslateHotkeyGesture = trimmedClipHotkey;
        }

        if (_clearApiKeyRequested)
        {
            s.EncryptedDeepLApiKey = string.Empty;
        }
        if (!string.IsNullOrWhiteSpace(DeepLKeyBox.Password))
        {
            s.EncryptedDeepLApiKey = SecurityHelper.ProtectString(DeepLKeyBox.Password.Trim());
        }

        if (int.TryParse(TrailLengthBox.Text, out int trailMs))
            s.CursorTrailLengthMs = Math.Clamp(trailMs, 80, 1200);

        if (TryParseDoubleFlexible(TrailThicknessBox.Text, out double trailThick))
            s.CursorTrailThickness = Math.Clamp(trailThick, 1.0, 10.0);

        if (TryParseDoubleFlexible(TrailOpacityBox.Text, out double trailOp))
            s.CursorTrailOpacity = Math.Clamp(trailOp, 0.1, 1.0);

        if (int.TryParse(PopupAutoHideBox.Text, out int popupSec))
            s.TranslationPopupAutoHideSeconds = Math.Clamp(popupSec, 0, 120);

        // Tab 3: Music & Waves
        s.EnableMusicModule = EnableMusicCheck.IsChecked == true;
        s.ShowAlbumArtInCompact = ShowAlbumArtCheck.IsChecked == true;
        s.EnableMarqueeInCompact = MarqueeCheck.IsChecked == true;
        s.HideMusicOnPause = HideMusicOnPauseCheck.IsChecked == true;

        if (int.TryParse(PauseHideDelayBox.Text, out int pauseDelay))
            s.HideMusicOnPauseDelaySeconds = Math.Clamp(pauseDelay, 1, 120);

        s.AudioDeviceId = AudioDeviceCombo.SelectedIndex <= 0
            ? null
            : AudioDeviceCombo.SelectedValue as string;
        s.HideWavesOnSilence = HideWavesSilenceCheck.IsChecked == true;

        if (WaveStyleCombo.SelectedItem is ComboBoxItem styleItem &&
            Enum.TryParse<WaveVisualStyle>(styleItem.Tag?.ToString(), out var waveStyle))
        {
            s.WaveStyle = waveStyle;
        }

        if (WaveColorModeCombo.SelectedItem is ComboBoxItem colorItem &&
            Enum.TryParse<WaveColorMode>(colorItem.Tag?.ToString(), out var colorMode))
        {
            s.WaveColorMode = colorMode;
        }

        if (int.TryParse(WaveBarCountBox.Text, out int barCount))
            s.WaveBarCount = Math.Clamp(barCount, 6, 32);

        if (TryParseDoubleFlexible(WaveSensitivityBox.Text, out double sens))
            s.WaveSensitivity = Math.Clamp(sens, 0.3, 5.0);

        if (TryParseDoubleFlexible(WaveSmoothingBox.Text, out double smooth))
            s.WaveSmoothing = Math.Clamp(smooth, 0.05, 0.92);

        if (TryNormalizeHexColor(StaticColorHexBox.Text, out string staticHex))
            s.StaticWaveColorHex = staticHex;
        else
        {
            FooterStatusText.Text = LocalizationService.Pick(
                "Invalid HEX color in Primary wave color (example: #FF00F5D4).",
                "Некорректный HEX цвет в поле «Основной цвет волн» (пример: #FF00F5D4).");
            return;
        }

        if (TryNormalizeHexColor(GradientColorHexBox.Text, out string gradHex))
            s.GradientSecondaryColorHex = gradHex;
        else
        {
            FooterStatusText.Text = LocalizationService.Pick(
                "Invalid HEX color in Secondary gradient color (example: #FFF72585).",
                "Некорректный HEX цвет в поле «Второй цвет градиента» (пример: #FFF72585).");
            return;
        }

        _isSaved = true;
        _settingsService.Save(s);
        CloseWithAnimation();
    }

    private static bool TryParseDoubleFlexible(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        string normalized = text.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private void CloseWithAnimation()
    {
        if (_isClosing) return;
        _isClosing = true;
        var dur = TimeSpan.FromMilliseconds(170);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(1.0, 0.0, dur) { EasingFunction = ease };
        fade.Completed += (_, _) => Close();
        RootAnimatedGrid.BeginAnimation(OpacityProperty, fade);
        WindowScaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 0.94, dur) { EasingFunction = ease });
        WindowScaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 0.94, dur) { EasingFunction = ease });
        WindowBlurEffect.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, new DoubleAnimation(0.0, 6.0, dur) { EasingFunction = ease });
    }

    private static bool TryNormalizeHexColor(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        try
        {
            if (System.Windows.Media.ColorConverter.ConvertFromString(input.Trim()) is System.Windows.Media.Color)
            {
                normalized = input.Trim();
                return true;
            }
        }
        catch { }
        return false;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        LocalizationService.ApplyLanguage(_initialLanguage);
        ThemeService.ApplyTheme(_settingsService.Current.ThemeStyle);
        CloseWithAnimation();
    }

    // --- Interactive Frosted Glass Color Palette Picker ---

    private void OnOpenColorPaletteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn)
            return;

        _activeColorTarget = string.Equals(btn.Tag?.ToString(), "Secondary", StringComparison.OrdinalIgnoreCase)
            ? "Secondary"
            : "Primary";

        PaletteTitleText.Text = _activeColorTarget == "Primary"
            ? LocalizationService.Pick("Primary wave color", "Основной цвет волн")
            : LocalizationService.Pick("Secondary gradient color", "Второй цвет градиента");

        string currentHex = _activeColorTarget == "Primary" ? StaticColorHexBox.Text : GradientColorHexBox.Text;
        if (TryParseColor(currentHex, out Color c))
        {
            RgbToHsv(c, out _pickerHue, out _pickerSat, out _pickerVal);
        }

        UpdatePaletteVisuals(updateHexBox: false);
        ColorPalettePopup.PlacementTarget = btn;
        ColorPalettePopup.IsOpen = true;
    }

    private void OnCloseColorPaletteClick(object sender, RoutedEventArgs e) => ColorPalettePopup.IsOpen = false;

    private void OnPresetSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex } && TryParseColor(hex, out Color c))
        {
            RgbToHsv(c, out _pickerHue, out _pickerSat, out _pickerVal);
            UpdatePaletteVisuals(updateHexBox: true);
        }
    }

    private void OnWaveHexTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressHexSync)
            return;
        UpdateWaveColorPreviews();
    }

    private void UpdateWaveColorPreviews()
    {
        if (StaticColorPreviewBtn is null || GradientColorPreviewBtn is null)
            return;

        if (TryParseColor(StaticColorHexBox.Text, out Color primary))
            StaticColorPreviewBtn.Background = new SolidColorBrush(primary);
        if (TryParseColor(GradientColorHexBox.Text, out Color secondary))
            GradientColorPreviewBtn.Background = new SolidColorBrush(secondary);
    }

    private void OnSvCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSv = true;
        SvPickerCanvas.CaptureMouse();
        UpdateSvFromPoint(e.GetPosition(SvPickerCanvas));
    }

    private void OnSvCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingSv && e.LeftButton == MouseButtonState.Pressed)
            UpdateSvFromPoint(e.GetPosition(SvPickerCanvas));
    }

    private void OnSvCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingSv)
            return;
        _isDraggingSv = false;
        SvPickerCanvas.ReleaseMouseCapture();
    }

    private void UpdateSvFromPoint(Point pt)
    {
        double w = Math.Max(1.0, SvPickerCanvas.ActualWidth > 0 ? SvPickerCanvas.ActualWidth : 248.0);
        double h = Math.Max(1.0, SvPickerCanvas.ActualHeight > 0 ? SvPickerCanvas.ActualHeight : 130.0);
        _pickerSat = Math.Clamp(pt.X / w, 0.0, 1.0);
        _pickerVal = Math.Clamp(1.0 - (pt.Y / h), 0.0, 1.0);
        UpdatePaletteVisuals(updateHexBox: true);
    }

    private void OnHueCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingHue = true;
        HuePickerCanvas.CaptureMouse();
        UpdateHueFromPoint(e.GetPosition(HuePickerCanvas));
    }

    private void OnHueCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingHue && e.LeftButton == MouseButtonState.Pressed)
            UpdateHueFromPoint(e.GetPosition(HuePickerCanvas));
    }

    private void OnHueCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingHue)
            return;
        _isDraggingHue = false;
        HuePickerCanvas.ReleaseMouseCapture();
    }

    private void UpdateHueFromPoint(Point pt)
    {
        double w = Math.Max(1.0, HuePickerCanvas.ActualWidth > 0 ? HuePickerCanvas.ActualWidth : 248.0);
        _pickerHue = Math.Clamp(pt.X / w, 0.0, 1.0) * 360.0;
        UpdatePaletteVisuals(updateHexBox: true);
    }

    private void UpdatePaletteVisuals(bool updateHexBox)
    {
        Color pureHue = HsvToRgb(_pickerHue, 1.0, 1.0);
        SvHueBackground.Fill = new SolidColorBrush(pureHue);

        double svW = SvPickerCanvas.Width;
        double svH = SvPickerCanvas.Height;
        Canvas.SetLeft(SvThumb, Math.Clamp(_pickerSat * svW - 7, -3, svW - 11));
        Canvas.SetTop(SvThumb, Math.Clamp((1.0 - _pickerVal) * svH - 7, -3, svH - 11));

        double hueW = HuePickerCanvas.Width;
        Canvas.SetLeft(HueThumb, Math.Clamp((_pickerHue / 360.0) * hueW - 4, 0, hueW - 8));

        Color chosen = HsvToRgb(_pickerHue, _pickerSat, _pickerVal);
        string hex = $"#FF{chosen.R:X2}{chosen.G:X2}{chosen.B:X2}";
        PaletteLivePreview.Background = new SolidColorBrush(chosen);
        PaletteHexReadout.Text = hex;

        if (updateHexBox)
        {
            if (WaveColorModeCombo.SelectedItem is ComboBoxItem wc &&
                wc.Tag?.ToString() == nameof(WaveColorMode.ThemeAdaptive))
            {
                SelectByTag(WaveColorModeCombo, nameof(WaveColorMode.Gradient));
            }

            _suppressHexSync = true;
            if (_activeColorTarget == "Primary")
                StaticColorHexBox.Text = hex;
            else
                GradientColorHexBox.Text = hex;
            _suppressHexSync = false;
            UpdateWaveColorPreviews();
        }
    }

    private static bool TryParseColor(string? input, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(input))
            return false;
        try
        {
            if (ColorConverter.ConvertFromString(input.Trim()) is Color c)
            {
                color = c;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        double c = v * s;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = v - c;

        (double r1, double g1, double b1) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return Color.FromArgb(255,
            (byte)Math.Round((r1 + m) * 255.0),
            (byte)Math.Round((g1 + m) * 255.0),
            (byte)Math.Round((b1 + m) * 255.0));
    }

    private static void RgbToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0;
        double g = c.G / 255.0;
        double b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        h = 0.0;
        if (delta > 1e-6)
        {
            if (Math.Abs(max - r) < 1e-6)
                h = 60.0 * (((g - b) / delta) % 6.0);
            else if (Math.Abs(max - g) < 1e-6)
                h = 60.0 * (((b - r) / delta) + 2.0);
            else
                h = 60.0 * (((r - g) / delta) + 4.0);
            if (h < 0.0) h += 360.0;
        }

        s = max <= 1e-6 ? 0.0 : delta / max;
        v = max;
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }
}
