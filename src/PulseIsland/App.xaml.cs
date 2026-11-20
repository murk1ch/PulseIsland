using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PulseIsland.Audio;
using PulseIsland.Core;
using PulseIsland.Interop;
using PulseIsland.Media;
using PulseIsland.Services;
using PulseIsland.Translation;
using PulseIsland.UI;
using PulseIsland.ViewModels;
using WinForms = System.Windows.Forms;

namespace PulseIsland;

public partial class App : System.Windows.Application
{
    private const string MutexName = "PulseIsland_SingleInstance_Mutex";
    private const string WakeEventName = "PulseIsland_WakeUp_Event";
    private const string WakeAckEventName = "PulseIsland_WakeUp_Ack";

    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _wakeEvent;
    private CancellationTokenSource? _wakeListenerCts;

    private SettingsService? _settingsService;
    private WasapiAudioCaptureService? _audioCapture;
    private AudioEndpointMonitorService? _audioEndpointMonitor;
    private SmartClipboardService? _smartClipboard;
    private FftSpectrumProcessor? _fftProcessor;
    private IMediaSessionService? _mediaService;
    private HotkeyService? _hotkeyService;
    private TranslationCoordinator? _translationCoordinator;

    private IslandViewModel? _islandViewModel;
    private IslandWindow? _islandWindow;
    private TranslationPopupWindow? _translationPopup;
    private AiAssistantPopupWindow? _aiPopup;
    private LocalLlmService? _localLlmService;
    private SettingsWindow? _settingsWindow;
    private SmartClipboardWindow? _clipboardWindow;
    private WinForms.NotifyIcon? _trayIcon;
    private bool _isCapturingSelection;
    private bool _isTranslatingClipboard;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        PortablePaths.EnsureDirectoriesExist();
        Environment.SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", PortablePaths.CacheDirectory);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LogService.Error("Unhandled AppDomain exception.", ex);
        };
        DispatcherUnhandledException += (_, args) =>
        {
            LogService.Error("Unhandled Dispatcher exception.", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogService.Error("Unobserved Task exception.", args.Exception);
            args.SetObserved();
        };

        try
        {
            _singleInstanceMutex = new Mutex(true, MutexName, out bool isNewInstance);
            _ownsMutex = isNewInstance;

            if (!isNewInstance)
            {
                if (TryWakeExistingInstance())
                {
                    Shutdown();
                    return;
                }

                // Existing instance is deadlocked/unresponsive: terminate stale processes and take over cleanly
                TerminateStaleInstances();
                try
                {
                    _ownsMutex = _singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(1.5));
                }
                catch (AbandonedMutexException)
                {
                    _ownsMutex = true;
                }
            }

            LogService.Info($"PulseIsland starting. Portable root: {PortablePaths.RootDirectory}");
            StartWakeUpListener();

            _settingsService = new SettingsService();
            var settings = _settingsService.Load();
            LocalizationService.ApplyLanguage(settings.UiLanguage);
            ThemeService.ApplyTheme(settings.ThemeStyle);

            _audioCapture = new WasapiAudioCaptureService();
            _audioEndpointMonitor = new AudioEndpointMonitorService();
            _audioEndpointMonitor.Start();
            _smartClipboard = new SmartClipboardService();
            _fftProcessor = new FftSpectrumProcessor(1024, settings.WaveBarCount);
            if (settings.EnableMusicModule)
            {
                _audioCapture.Start(settings.AudioDeviceId);
            }

            _mediaService = new SmtcMediaService(() => _audioCapture.IsSilent);

            var ocrService = new WindowsOcrService();
            var onlineService = new OnlineTranslationService();
            var offlineService = new OnnxOfflineTranslationService();
            _translationCoordinator = new TranslationCoordinator(ocrService, onlineService, offlineService, _settingsService);

            _islandViewModel = new IslandViewModel();
            _islandViewModel.LoadPersistedTranslations();
            _islandWindow = new IslandWindow(
                _islandViewModel,
                _settingsService,
                _mediaService,
                _audioCapture,
                _fftProcessor,
                _audioEndpointMonitor,
                _smartClipboard);
            _islandWindow.TranslationRequested += () => _ = StartLassoTranslationAsync();
            _islandWindow.SettingsRequested += OpenSettingsWindow;
            _islandWindow.SmartClipboardRequested += OpenSmartClipboardWindow;

            MainWindow = _islandWindow;
            _translationPopup = new TranslationPopupWindow();
            _localLlmService = new LocalLlmService();
            _aiPopup = new AiAssistantPopupWindow(_settingsService, _localLlmService, _islandViewModel);

            _hotkeyService = new HotkeyService();
            _hotkeyService.Attach(_islandWindow);
            if (!_hotkeyService.Register(settings.HotkeyGesture))
            {
                _islandViewModel.StatusMessage = LocalizationService.Pick(
                    $"Failed to register hotkey {settings.HotkeyGesture}",
                    $"Не удалось зарегистрировать клавишу {settings.HotkeyGesture}");
            }
            _hotkeyService.RegisterSecondary(settings.ClipboardTranslateHotkeyGesture, settings.EnableClipboardTranslateHotkey);
            _hotkeyService.RegisterAiFeed(settings.AiFeedHotkeyGesture, settings.EnableLocalAiAssistant && settings.EnableAiFeedHotkey);
            _hotkeyService.HotkeyPressed += () => _ = StartLassoTranslationAsync();
            _hotkeyService.ClipboardHotkeyPressed += () => _ = StartClipboardTranslationAsync();
            _hotkeyService.AiFeedHotkeyPressed += () => _ = StartAiFeedFromSelectionAsync();

            _settingsService.SettingsChanged += OnSettingsChanged;
            LocalizationService.LanguageChanged += _ => Dispatcher.InvokeAsync(RebuildTrayMenu);

            SetupTrayIcon(settings.ShowTrayIcon);
            _islandWindow.Show();
            LogService.Info($"PulseIsland UI initialized. IslandVisible={_islandWindow.IsVisible}, TrayVisible={_trayIcon?.Visible}");

            // Start media session polling after window is visible so nothing can delay startup
            _ = _mediaService.StartAsync();
        }
        catch (Exception ex)
        {
            LogService.Error("Fatal startup error in OnStartup.", ex);
            System.Windows.MessageBox.Show(
                LocalizationService.Pick(
                    $"PulseIsland startup error: {ex.Message}\nDetails saved to {PortablePaths.LogsDirectory}",
                    $"Ошибка запуска PulseIsland: {ex.Message}\nПодробности сохранены в {PortablePaths.LogsDirectory}"),
                "PulseIsland",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static bool TryWakeExistingInstance()
    {
        try
        {
            using var wake = EventWaitHandle.OpenExisting(WakeEventName);
            using var ack = new EventWaitHandle(false, EventResetMode.AutoReset, WakeAckEventName);
            wake.Set();
            return ack.WaitOne(TimeSpan.FromMilliseconds(700));
        }
        catch
        {
            return false;
        }
    }

    private static void TerminateStaleInstances()
    {
        int currentPid = Environment.ProcessId;
        foreach (var proc in Process.GetProcessesByName("PulseIsland"))
        {
            try
            {
                if (proc.Id != currentPid)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(1000);
                }
            }
            catch
            {
                // Ignore race conditions on exiting process
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    private void StartWakeUpListener()
    {
        _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);
        _wakeListenerCts = new CancellationTokenSource();
        var token = _wakeListenerCts.Token;

        Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                if (_wakeEvent.WaitOne(500))
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_islandWindow is not null)
                        {
                            _islandWindow.Show();
                            _islandWindow.PositionOnTargetMonitor();
                            _islandWindow.SetIslandState(IslandState.Expanded, animate: true);
                        }

                        try
                        {
                            if (EventWaitHandle.TryOpenExisting(WakeAckEventName, out var ack))
                            {
                                ack.Set();
                                ack.Dispose();
                            }
                        }
                        catch { }
                    });
                }
            }
        }, token);
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        Dispatcher.InvokeAsync(() =>
        {
            LocalizationService.ApplyLanguage(settings.UiLanguage);
            ThemeService.ApplyTheme(settings.ThemeStyle);

            if (_hotkeyService is not null)
            {
                if (!_hotkeyService.Register(settings.HotkeyGesture) && _islandViewModel is not null)
                {
                    _islandViewModel.StatusMessage = LocalizationService.Pick(
                        $"Hotkey {settings.HotkeyGesture} is used by another application",
                        $"Клавиша {settings.HotkeyGesture} занята другим приложением");
                }
                _hotkeyService.RegisterSecondary(settings.ClipboardTranslateHotkeyGesture, settings.EnableClipboardTranslateHotkey);
                _hotkeyService.RegisterAiFeed(settings.AiFeedHotkeyGesture, settings.EnableLocalAiAssistant && settings.EnableAiFeedHotkey);
            }

            if (settings.EnableMusicModule)
                _audioCapture?.Start(settings.AudioDeviceId);
            else
                _audioCapture?.Stop();

            if (_trayIcon is not null)
                _trayIcon.Visible = settings.ShowTrayIcon;

            if (!settings.EnableLocalAiAssistant)
                _localLlmService?.StopServer();
        });
    }

    private async Task StartLassoTranslationAsync()
    {
        if (_isCapturingSelection || _settingsService is null || _translationCoordinator is null || _islandViewModel is null)
            return;

        _isCapturingSelection = true;
        try
        {
            _islandWindow?.SetTemporaryCaptureHide(true);

            var overlay = new LassoOverlayWindow(_settingsService.Current);
            var selection = await overlay.CaptureSelectionAsync();

            _islandWindow?.SetTemporaryCaptureHide(false);

            if (selection is null)
                return;

            _islandViewModel.IsTranslating = true;
            _islandViewModel.StatusMessage = LocalizationService.Pick(
                "Recognizing and translating text...",
                "Распознавание и перевод текста...");

            var (ocr, translation) = await _translationCoordinator.CaptureOcrAndTranslateAsync(selection.Value.PhysicalRect);

            _islandViewModel.IsTranslating = false;

            if (!translation.Success)
            {
                string errorMsg = translation.ErrorMessage ?? LocalizationService.Pick(
                    "Failed to recognize or translate text.",
                    "Не удалось распознать или перевести текст.");
                _islandViewModel.StatusMessage = errorMsg;

                var failedRecord = new TranslationRecord(
                    SourceText: string.IsNullOrWhiteSpace(ocr.Text)
                        ? LocalizationService.Pick(
                            "No text detected in selected area (try circling the entire line)",
                            "Текст в выделенной области не обнаружен (попробуйте обвести строку целиком)")
                        : ocr.Text,
                    TranslatedText: errorMsg,
                    DetectedSourceLanguage: string.IsNullOrWhiteSpace(ocr.DetectedLanguageTag) ? "ocr" : ocr.DetectedLanguageTag,
                    TargetLanguage: _settingsService.Current.TargetLanguage,
                    Timestamp: DateTime.Now,
                    IsOffline: _settingsService.Current.TranslationMode == TranslationEngineMode.OfflineOnnx);

                _translationPopup?.ShowTranslation(
                    failedRecord,
                    selection.Value.PhysicalRect,
                    selection.Value.DipRect,
                    Math.Max(6, _settingsService.Current.TranslationPopupAutoHideSeconds),
                    _settingsService.Current.EnableDwmBackdropBlur);
                return;
            }

            _islandViewModel.StatusMessage = string.Empty;
            var record = new TranslationRecord(
                SourceText: ocr.Text,
                TranslatedText: translation.TranslatedText,
                DetectedSourceLanguage: translation.DetectedSourceLanguage,
                TargetLanguage: translation.TargetLanguage,
                Timestamp: DateTime.Now,
                IsOffline: translation.UsedOfflineModel);

            _islandViewModel.AddTranslation(record);
            _translationPopup?.ShowTranslation(
                record,
                selection.Value.PhysicalRect,
                selection.Value.DipRect,
                _settingsService.Current.TranslationPopupAutoHideSeconds,
                _settingsService.Current.EnableDwmBackdropBlur);
        }
        catch (Exception ex)
        {
            LogService.Error("Lasso translation flow failed.", ex);
            _islandWindow?.SetTemporaryCaptureHide(false);

            if (_islandViewModel is not null)
            {
                _islandViewModel.IsTranslating = false;
                _islandViewModel.StatusMessage = LocalizationService.Pick($"Error: {ex.Message}", $"Ошибка: {ex.Message}");
            }
        }
        finally
        {
            _islandWindow?.SetTemporaryCaptureHide(false);
            if (_islandViewModel is not null)
                _islandViewModel.IsTranslating = false;
            _isCapturingSelection = false;
        }
    }

    private async Task StartClipboardTranslationAsync()
    {
        if (_isTranslatingClipboard || _settingsService is null || _translationCoordinator is null || _islandViewModel is null)
            return;

        _isTranslatingClipboard = true;
        try
        {
            uint seqBefore = GetClipboardSequenceNumber();
            TryTriggerCopySelection();
            await Task.Delay(95);
            uint seqAfter = GetClipboardSequenceNumber();

            string text = string.Empty;
            if (System.Windows.Clipboard.ContainsText())
            {
                text = System.Windows.Clipboard.GetText().Trim();
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                _islandViewModel.StatusMessage = LocalizationService.Pick(
                    "Clipboard is empty — select or copy some text.",
                    "Буфер обмена пуст — выделите или скопируйте текст.");
                return;
            }

            _islandViewModel.IsTranslating = true;
            _islandViewModel.StatusMessage = seqAfter != seqBefore
                ? LocalizationService.Pick("Translating selected text...", "Перевод выделенного текста...")
                : LocalizationService.Pick("Translating clipboard text...", "Перевод текста из буфера обмена...");

            var translation = await _translationCoordinator.TranslateTextAsync(text);
            _islandViewModel.IsTranslating = false;

            Win32Api.GetCursorPos(out var pt);
            var dpiScale = _islandWindow is not null ? VisualTreeHelper.GetDpi(_islandWindow) : new DpiScale(1, 1);
            var physRect = new Int32Rect(pt.X - 10, pt.Y - 10, 20, 20);
            var dipRect = new Rect(pt.X / dpiScale.DpiScaleX, pt.Y / dpiScale.DpiScaleY, 12, 12);

            if (!translation.Success)
            {
                _islandViewModel.StatusMessage = translation.ErrorMessage ?? LocalizationService.Pick("Translation error.", "Ошибка перевода.");
                var failRec = new TranslationRecord(
                    text,
                    _islandViewModel.StatusMessage,
                    translation.DetectedSourceLanguage,
                    translation.TargetLanguage,
                    DateTime.Now,
                    translation.UsedOfflineModel);
                _translationPopup?.ShowTranslation(failRec, physRect, dipRect, 8, _settingsService.Current.EnableDwmBackdropBlur);
                return;
            }

            _islandViewModel.StatusMessage = string.Empty;
            var rec = new TranslationRecord(
                text,
                translation.TranslatedText,
                translation.DetectedSourceLanguage,
                translation.TargetLanguage,
                DateTime.Now,
                translation.UsedOfflineModel);
            _islandViewModel.AddTranslation(rec);
            _translationPopup?.ShowTranslation(
                rec,
                physRect,
                dipRect,
                _settingsService.Current.TranslationPopupAutoHideSeconds,
                _settingsService.Current.EnableDwmBackdropBlur);
        }
        catch (Exception ex)
        {
            LogService.Warn($"Clipboard quick translation error: {ex.Message}");
        }
        finally
        {
            _islandViewModel.IsTranslating = false;
            _isTranslatingClipboard = false;
        }
    }

    private async Task StartAiFeedFromSelectionAsync()
    {
        if (_isTranslatingClipboard || _settingsService is null || _aiPopup is null)
            return;

        _isTranslatingClipboard = true;
        try
        {
            TryTriggerCopySelection();
            await Task.Delay(95);

            string text = string.Empty;
            if (System.Windows.Clipboard.ContainsText())
                text = System.Windows.Clipboard.GetText().Trim();

            Win32Api.GetCursorPos(out var pt);
            var dpiScale = _islandWindow is not null ? VisualTreeHelper.GetDpi(_islandWindow) : new DpiScale(1, 1);
            var physRect = new Int32Rect(pt.X - 10, pt.Y - 10, 20, 20);
            var dipRect = new Rect(pt.X / dpiScale.DpiScaleX, pt.Y / dpiScale.DpiScaleY, 12, 12);

            await _aiPopup.ShowAndFeedAsync(text, _settingsService.Current.AiFeedAction, physRect, dipRect);
        }
        catch (Exception ex)
        {
            LogService.Warn($"AI feed hotkey error: {ex.Message}");
        }
        finally
        {
            _isTranslatingClipboard = false;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private static void TryTriggerCopySelection()
    {
        try
        {
            const uint KEYUP = 0x0002;
            const byte VK_MENU = 0x12;
            const byte VK_SHIFT = 0x10;
            const byte VK_CONTROL = 0x11;
            const byte VK_C = 0x43;

            // Release Alt/Shift in case user still holds modifier from hotkey like Ctrl+Alt+C
            keybd_event(VK_MENU, 0, KEYUP, UIntPtr.Zero);
            keybd_event(VK_SHIFT, 0, KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_C, 0, 0, UIntPtr.Zero);
            keybd_event(VK_C, 0, KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
        }
        catch { }
    }

    private void OpenSmartClipboardWindow()
    {
        if (_smartClipboard is null || _settingsService is null)
            return;

        if (_clipboardWindow is not null && _clipboardWindow.IsLoaded)
        {
            _clipboardWindow.Activate();
            return;
        }

        _clipboardWindow = new SmartClipboardWindow(_smartClipboard, _settingsService);
        _clipboardWindow.Closed += (_, _) => _clipboardWindow = null;
        _clipboardWindow.Show();
        _clipboardWindow.Activate();
    }

    private void OpenSettingsWindow()
    {
        if (_settingsService is null) return;

        // Collapse expanded island cleanly out of the way when Settings opens
        if (_islandWindow is not null && _islandViewModel is not null && _islandViewModel.State == IslandState.Expanded)
        {
            var collapsedState = _settingsService.Current.AutoHideIsland ? IslandState.Hidden : IslandState.Compact;
            _islandWindow.SetIslandState(collapsedState, animate: true);
        }

        if (_settingsWindow is not null && _settingsWindow.IsLoaded)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settingsService, _islandWindow?.MouseBatteryService);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void SetupTrayIcon(bool visible)
    {
        Icon trayAppIcon = SystemIcons.Application;
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && System.IO.File.Exists(exePath))
            {
                trayAppIcon = Icon.ExtractAssociatedIcon(exePath) ?? SystemIcons.Application;
            }
        }
        catch
        {
            trayAppIcon = SystemIcons.Application;
        }

        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "PulseIsland",
            Icon = trayAppIcon,
            Visible = visible
        };

        RebuildTrayMenu();
        _trayIcon.DoubleClick += (_, _) => _islandWindow?.SetIslandState(IslandState.Expanded);
    }

    private void RebuildTrayMenu()
    {
        if (_trayIcon is null)
            return;

        var oldMenu = _trayIcon.ContextMenuStrip;
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(LocalizationService.Pick("Show island", "Показать островок"), null, (_, _) => _islandWindow?.SetIslandState(IslandState.Compact));
        menu.Items.Add(LocalizationService.Pick("Expand island", "Развернуть островок"), null, (_, _) => _islandWindow?.SetIslandState(IslandState.Expanded));
        menu.Items.Add(LocalizationService.Pick("Hide island", "Спрятать островок"), null, (_, _) => _islandWindow?.SetIslandState(IslandState.Hidden));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(LocalizationService.Pick("Translate screen region", "Перевести область экрана"), null, (_, _) => _ = StartLassoTranslationAsync());
        menu.Items.Add(LocalizationService.Pick("Translate clipboard", "Перевести буфер обмена"), null, (_, _) => _ = StartClipboardTranslationAsync());
        menu.Items.Add(LocalizationService.Pick("AI Assistant (Qwen 3B)", "ИИ-ассистент (Qwen 3B)"), null, (_, _) => _aiPopup?.ShowInteractiveNearCursor());
        menu.Items.Add(LocalizationService.Pick("Smart Clipboard", "Умный буфер обмена"), null, (_, _) => OpenSmartClipboardWindow());
        menu.Items.Add(LocalizationService.Pick("Settings", "Настройки"), null, (_, _) => OpenSettingsWindow());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(LocalizationService.Pick("Exit", "Выход"), null, (_, _) => Shutdown());

        _trayIcon.ContextMenuStrip = menu;
        oldMenu?.Dispose();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _wakeListenerCts?.Cancel();
        _wakeListenerCts?.Dispose();
        _wakeEvent?.Dispose();

        _hotkeyService?.Dispose();
        _smartClipboard?.Dispose();
        _audioEndpointMonitor?.Dispose();
        _localLlmService?.Dispose();
        _mediaService?.Dispose();
        _audioCapture?.Dispose();
        _translationCoordinator?.Dispose();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        if (_ownsMutex && _singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch
            {
                // Ignore if already released
            }
        }
        _singleInstanceMutex?.Dispose();
        LogService.Info("PulseIsland exited cleanly.");
        base.OnExit(e);
    }
}
