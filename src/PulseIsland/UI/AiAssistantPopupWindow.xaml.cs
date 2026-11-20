using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using PulseIsland.Core;
using PulseIsland.Interop;
using PulseIsland.Services;
using PulseIsland.ViewModels;

namespace PulseIsland.UI;

/// <summary>
/// Standalone floating frosted-glass widget for Qwen 2.5 3B (summary, translation, weather, Q&A).
/// Matches TranslationPopupWindow design & animations, keeping the expanded island clean.
/// </summary>
public partial class AiAssistantPopupWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly LocalLlmService _llmService;
    private readonly WeatherService _weatherService = new();
    private readonly IslandViewModel? _islandVm;

    private CancellationTokenSource? _aiCts;
    private string _answerText = string.Empty;

    public AiAssistantPopupWindow(
        SettingsService settingsService,
        LocalLlmService llmService,
        IslandViewModel? islandVm = null)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _llmService = llmService;
        _islandVm = islandVm;
    }

    public async Task ShowAndFeedAsync(
        string text,
        AiFeedHotkeyAction action,
        Int32Rect anchorPhysicalRect,
        Rect anchorDipRect)
    {
        PrepareWindowAtAnchor(anchorPhysicalRect, anchorDipRect);

        if (string.IsNullOrWhiteSpace(text))
        {
            StatusLineText.Text = LocalizationService.Pick(
                "Waiting for prompt — type a question or pick an action above",
                "Ожидание запроса — введите вопрос или выберите действие выше");
            AnswerTextBox.Text = LocalizationService.Pick(
                "Select text in any window before pressing the hotkey — or ask anything here (e.g., \"what's the weather today\").",
                "Выделите текст в любом окне перед нажатием горячей клавиши — или спросите здесь (например, «какая сегодня погода»).");
            SourceTextBorder.Visibility = Visibility.Collapsed;
            PromptInputBox.Focus();
            return;
        }

        SourceTextBlock.Text = text;
        SourceTextBorder.Visibility = Visibility.Visible;

        if (action == AiFeedHotkeyAction.FillPrompt)
        {
            PromptInputBox.Text = text.Length > 1500 ? text[..1500] : text;
            PromptInputBox.CaretIndex = PromptInputBox.Text.Length;
            PromptInputBox.Focus();
            StatusLineText.Text = LocalizationService.Pick(
                $"Inserted {text.Length} chars — add your question and press Enter",
                $"Вставлено {text.Length} симв. — допишите вопрос и нажмите Enter");
            AnswerTextBox.Text = string.Empty;
            return;
        }

        if (action == AiFeedHotkeyAction.Translate)
        {
            string langName = LanguageCatalog.FindOrDefault(_settingsService.Current.TargetLanguage, "en").DisplayName;
            var (tSys, tUsr) = LocalLlmService.BuildTranslatePrompts(text, langName);
            await RunStreamAsync(LocalizationService.Pick($"Translating ({langName})...", $"Перевод ({langName})..."), tSys, tUsr);
            return;
        }

        var (sys, usr) = LocalLlmService.BuildSummaryPrompts(text);
        await RunStreamAsync(LocalizationService.Pick("Summarizing text...", "Пересказ текста..."), sys, usr);
    }

    public void ShowInteractiveNearCursor()
    {
        Win32Api.GetCursorPos(out var pt);
        var dpiScale = VisualTreeHelper.GetDpi(this);
        var physRect = new Int32Rect(pt.X - 10, pt.Y - 10, 20, 20);
        var dipRect = new Rect(pt.X / dpiScale.DpiScaleX, pt.Y / dpiScale.DpiScaleY, 12, 12);
        PrepareWindowAtAnchor(physRect, dipRect);
        PromptInputBox.Focus();
    }

    private void PrepareWindowAtAnchor(Int32Rect anchorPhysicalRect, Rect anchorDipRect)
    {
        RefreshBackendBadge();
        CopyButton.Content = LocalizationService.Get("Loc_Copy");

        bool alreadyVisible = IsVisible;
        if (!alreadyVisible)
        {
            Show();
            UpdateLayout();

            int centerX = anchorPhysicalRect.X + anchorPhysicalRect.Width / 2;
            int centerY = anchorPhysicalRect.Y + anchorPhysicalRect.Height / 2;
            var targetMonitor = MonitorService.GetMonitorForPhysicalPoint(centerX, centerY, this);
            var monitorBounds = targetMonitor.DipBounds;

            double desiredLeft = anchorDipRect.Left;
            double desiredTop = anchorDipRect.Bottom + 10;

            if (desiredLeft + ActualWidth > monitorBounds.Right - 14)
                desiredLeft = monitorBounds.Right - ActualWidth - 14;
            if (desiredLeft < monitorBounds.Left + 14)
                desiredLeft = monitorBounds.Left + 14;

            if (desiredTop + ActualHeight > monitorBounds.Bottom - 14)
                desiredTop = Math.Max(monitorBounds.Top + 14, anchorDipRect.Top - ActualHeight - 10);

            Left = desiredLeft;
            Top = desiredTop;

            DwmBackdropService.ApplyToCard(this, MainPopupCard, _settingsService.Current.EnableDwmBackdropBlur, 20);
            AnimateBlurIn();
        }
        else
        {
            Activate();
        }
    }

    private void RefreshBackendBadge()
    {
        if (!LocalLlmCatalogService.IsReady())
        {
            BackendBadgeText.Text = LocalizationService.Pick("Not downloaded", "Не скачана");
            return;
        }

        if (_llmService.ActiveBackendLabel is not ("Idle" or "Ожидание"))
        {
            BackendBadgeText.Text = _llmService.ActiveBackendLabel;
            return;
        }

        var (_, hasGpu) = LocalLlmCatalogService.DetectHardware();
        BackendBadgeText.Text = _settingsService.Current.AiBackend switch
        {
            AiComputeBackend.CpuOnly => "CPU",
            AiComputeBackend.GpuVulkan => "GPU • Vulkan",
            _ => hasGpu ? "Auto (GPU)" : "Auto (CPU)"
        };
    }

    private async Task RunWeatherAsync(string? customQuestion)
    {
        SetGeneratingState(true, LocalizationService.Pick("Fetching weather...", "Получение погоды..."));
        var weather = await _weatherService.GetCurrentWeatherAsync(_settingsService.Current.AiWeatherCity);
        if (weather is null)
        {
            SetGeneratingState(false, LocalizationService.Pick("Weather unavailable (offline & no cache)", "Погода недоступна (нет сети и кэша)"));
            AnswerTextBox.Text = LocalizationService.Pick(
                "Could not fetch weather online and no cached snapshot is available yet.",
                "Не удалось загрузить погоду из сети, и сохранённого кэша ещё нет.");
            return;
        }

        if (!LocalLlmCatalogService.IsReady())
        {
            SetGeneratingState(false, weather.IsFromCache
                ? LocalizationService.Pick("Cached weather (AI not downloaded)", "Погода из кэша (ИИ не скачан)")
                : LocalizationService.Pick("Weather summary (AI not downloaded)", "Сводка погоды (ИИ не скачан)"));
            AnswerTextBox.Text = $"{WeatherService.FormatWeatherBrief(weather)}\n\n" + LocalizationService.Pick(
                "(Download Qwen 2.5 3B in Settings → \"Translator & AI\" for richer AI summaries.)",
                "(Скачайте Qwen 2.5 3B в Настройках → «Переводчик и ИИ», чтобы получать развёрнутые ответы ИИ.)");
            return;
        }

        var (sys, usr) = LocalLlmService.BuildWeatherPrompts(weather, customQuestion);
        await RunStreamAsync(LocalizationService.Pick($"Weather • {weather.City}...", $"Погода • {weather.City}..."), sys, usr);
    }

    private async Task RunStreamAsync(string thinkingLabel, string systemPrompt, string userPrompt)
    {
        if (!LocalLlmCatalogService.IsReady())
        {
            SetGeneratingState(false, LocalizationService.Pick("Qwen 2.5 3B model is not installed", "Модель Qwen 2.5 3B не установлена"));
            AnswerTextBox.Text = LocalizationService.Pick(
                "Open Settings → \"Translator & AI\" → \"Local AI Assistant\" and click \"Download\" (~1.8 GB). Resumable download and Vulkan GPU / CPU acceleration supported.",
                "Откройте Настройки → «Переводчик и ИИ» → «Локальный ИИ-ассистент» и нажмите «Скачать» (~1.8 ГБ). Поддерживается докачка и ускорение GPU Vulkan / CPU.");
            return;
        }

        _aiCts?.Cancel();
        _aiCts?.Dispose();
        _aiCts = new CancellationTokenSource();
        var ct = _aiCts.Token;

        _answerText = string.Empty;
        AnswerTextBox.Text = string.Empty;
        SetGeneratingState(true, thinkingLabel);

        var (ok, full, tokSec, err) = await _llmService.StreamChatAsync(
            _settingsService.Current.AiBackend,
            systemPrompt,
            userPrompt,
            token =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    _answerText += token;
                    AnswerTextBox.Text = _answerText;
                    AnswerScrollViewer.ScrollToEnd();
                });
            },
            ct);

        RefreshBackendBadge();

        if (!ok && !string.IsNullOrWhiteSpace(err))
        {
            SetGeneratingState(false, LocalizationService.Pick("Local AI error", "Ошибка локального ИИ"));
            if (string.IsNullOrWhiteSpace(_answerText))
                AnswerTextBox.Text = err;
        }
        else
        {
            string finalText = !string.IsNullOrWhiteSpace(full) ? full : _answerText;
            _answerText = finalText;
            AnswerTextBox.Text = finalText;
            string doneWord = LocalizationService.Pick("Done", "Готово");
            string doneStatus = tokSec > 0.1
                ? $"{doneWord} • {tokSec:0.0} tok/s ({_llmService.ActiveBackendLabel})"
                : $"{doneWord} ({_llmService.ActiveBackendLabel})";
            SetGeneratingState(false, doneStatus);
        }
    }

    private void SetGeneratingState(bool generating, string statusLine)
    {
        StatusLineText.Text = statusLine;
        AskButton.Visibility = generating ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;
        ThinkingBarTrack.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;

        if (_islandVm is not null)
        {
            _islandVm.AiBackendBadge = BackendBadgeText.Text;
            _islandVm.AiStatusLine = statusLine;
            _islandVm.IsAiGenerating = generating;
        }

        if (generating)
        {
            var sweep = new DoubleAnimation(-140, 480, TimeSpan.FromMilliseconds(1150))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            ThinkingShimmerTransform.BeginAnimation(TranslateTransform.XProperty, sweep);

            var pulse = new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(720))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            AmbientShadowBorder.BeginAnimation(OpacityProperty, pulse);
        }
        else
        {
            ThinkingShimmerTransform.BeginAnimation(TranslateTransform.XProperty, null);
            AmbientShadowBorder.BeginAnimation(OpacityProperty, null);
            AmbientShadowBorder.Opacity = 1.0;
        }
    }

    private void OnWeatherClick(object sender, RoutedEventArgs e)
    {
        SourceTextBorder.Visibility = Visibility.Collapsed;
        _ = RunWeatherAsync(null);
    }

    private void OnSummarizeClipboardClick(object sender, RoutedEventArgs e)
    {
        string clip = GetClipboardTextSafe();
        if (string.IsNullOrWhiteSpace(clip))
        {
            StatusLineText.Text = LocalizationService.Pick("Clipboard is empty — copy some text first", "Буфер обмена пуст — скопируйте текст");
            return;
        }
        SourceTextBlock.Text = clip;
        SourceTextBorder.Visibility = Visibility.Visible;
        var (sys, usr) = LocalLlmService.BuildSummaryPrompts(clip);
        _ = RunStreamAsync(LocalizationService.Pick("Summarizing text...", "Пересказ текста..."), sys, usr);
    }

    private void OnTranslateClipboardClick(object sender, RoutedEventArgs e)
    {
        string clip = GetClipboardTextSafe();
        if (string.IsNullOrWhiteSpace(clip))
        {
            StatusLineText.Text = LocalizationService.Pick("Clipboard is empty — copy some text first", "Буфер обмена пуст — скопируйте текст");
            return;
        }
        SourceTextBlock.Text = clip;
        SourceTextBorder.Visibility = Visibility.Visible;
        string langName = LanguageCatalog.FindOrDefault(_settingsService.Current.TargetLanguage, "en").DisplayName;
        var (sys, usr) = LocalLlmService.BuildTranslatePrompts(clip, langName);
        _ = RunStreamAsync(LocalizationService.Pick($"Translating ({langName})...", $"Перевод ({langName})..."), sys, usr);
    }

    private void OnAskClick(object sender, RoutedEventArgs e) => SubmitPrompt();

    private void OnPromptKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SubmitPrompt();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnCloseClick(sender, e);
        }
    }

    private void SubmitPrompt()
    {
        string prompt = PromptInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        PromptInputBox.Text = string.Empty;
        SourceTextBorder.Visibility = Visibility.Collapsed;

        if (prompt.Contains("погод", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("weather", StringComparison.OrdinalIgnoreCase))
        {
            _ = RunWeatherAsync(prompt);
            return;
        }

        string sys = LocalizationService.Pick(
            "You are a concise, helpful desktop AI assistant. Answer clearly and briefly.",
            "Ты умный и лаконичный ИИ-помощник на рабочем столе. Отвечай по-русски, кратко и понятно.");
        _ = RunStreamAsync(LocalizationService.Pick("Qwen 2.5 3B is thinking...", "Qwen 2.5 3B думает..."), sys, prompt);
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => _aiCts?.Cancel();

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _aiCts?.Cancel();
        _answerText = string.Empty;
        AnswerTextBox.Text = string.Empty;
        PromptInputBox.Text = string.Empty;
        SourceTextBorder.Visibility = Visibility.Collapsed;
        SetGeneratingState(false, LocalizationService.Get("Loc_Ai_Ready"));
        PromptInputBox.Focus();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        string textToCopy = !string.IsNullOrWhiteSpace(_answerText) ? _answerText : AnswerTextBox.Text;
        if (!string.IsNullOrWhiteSpace(textToCopy))
        {
            try { System.Windows.Clipboard.SetText(textToCopy); } catch { }
            CopyButton.Content = LocalizationService.Get("Loc_Copied");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _aiCts?.Cancel();
        SetGeneratingState(false, LocalizationService.Get("Loc_Ai_Ready"));
        AnimateBlurOutAndHide();
    }

    private void OnPopupMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
            return;

        if (e.OriginalSource is DependencyObject dep && IsInsideInteractiveControl(dep))
            return;

        DragMove();
    }

    private static bool IsInsideInteractiveControl(DependencyObject? dep)
    {
        while (dep is not null)
        {
            if (dep is System.Windows.Controls.Primitives.TextBoxBase or
                System.Windows.Controls.Primitives.ScrollBar or
                System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }
            dep = VisualTreeHelper.GetParent(dep);
        }
        return false;
    }

    private void AnimateBlurIn()
    {
        var duration = TimeSpan.FromMilliseconds(320);
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 };

        RootPopupContainer.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = easeOut });
        PopupScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1.0, duration) { EasingFunction = spring });
        PopupScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1.0, duration) { EasingFunction = spring });
        PopupBlurEffect.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(8.0, 0.0, duration) { EasingFunction = easeOut });
    }

    private void AnimateBlurOutAndHide()
    {
        var duration = TimeSpan.FromMilliseconds(220);
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

        var fade = new DoubleAnimation(1.0, 0.0, duration) { EasingFunction = easeIn };
        fade.Completed += (_, _) => Hide();

        RootPopupContainer.BeginAnimation(OpacityProperty, fade);
        PopupScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 0.92, duration) { EasingFunction = easeIn });
        PopupScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 0.92, duration) { EasingFunction = easeIn });
        PopupBlurEffect.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0.0, 8.0, duration) { EasingFunction = easeIn });
    }

    private static string GetClipboardTextSafe()
    {
        try
        {
            return System.Windows.Clipboard.ContainsText()
                ? System.Windows.Clipboard.GetText().Trim()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
