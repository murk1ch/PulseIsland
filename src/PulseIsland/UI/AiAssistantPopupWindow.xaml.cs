using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using PulseIsland.Core;
using PulseIsland.Interop;
using PulseIsland.Services;
using PulseIsland.ViewModels;

namespace PulseIsland.UI;

/// <summary>
/// Standalone floating frosted-glass widget for local LLMs & Vision VLMs:
/// supports multi-turn conversation memory, user-defined prompt chips, table/code extraction,
/// offline Whisper voice dictation, weather, and formatted LaTeX/Markdown/Table rendering.
/// </summary>
public partial class AiAssistantPopupWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly LocalLlmService _llmService;
    private readonly WeatherService _weatherService = new();
    private readonly WhisperVoiceService _voiceService = new();
    private readonly IslandViewModel? _islandVm;

    private readonly List<(string DisplayLabel, AiChatTurn Turn)> _chatHistory = new();
    private CancellationTokenSource? _aiCts;
    private string _answerText = string.Empty;
    private string _contextText = string.Empty;
    private CapturedScreenRegion? _attachedImage;
    private byte[]? _attachedImageJpeg;
    private long _lastRenderTickMs;
    private int _requestGen;
    private int _hideAnimGen;
    private bool _isTranscribing;
    private bool _allowClose;

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    public AiAssistantPopupWindow(
        SettingsService settingsService,
        LocalLlmService llmService,
        IslandViewModel? islandVm = null)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _llmService = llmService;
        _islandVm = islandVm;

        // Rounded clip for thumbnail image (WPF Border.ClipToBounds does not clip child Image to CornerRadius)
        AttachedImagePreview.Clip = new RectangleGeometry(new Rect(0, 0, 72, 54), 7, 7);
        DataObject.AddPastingHandler(PromptInputBox, OnPromptBoxPasting);

        _settingsService.SettingsChanged += OnAppSettingsChanged;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        _voiceService.LevelChanged += OnVoiceLevelChanged;
        Closing += (_, e) =>
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                CancelPendingAndHide();
            }
        };
        Closed += (_, _) =>
        {
            _settingsService.SettingsChanged -= OnAppSettingsChanged;
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            _voiceService.LevelChanged -= OnVoiceLevelChanged;
            _aiCts?.Cancel();
            _voiceService.Dispose();
        };

        RefreshCustomPromptChips();
    }

    private void OnAppSettingsChanged(AppSettings _) => Dispatcher.InvokeAsync(RefreshCustomPromptChips);
    private void OnVoiceLevelChanged(double level) => Dispatcher.InvokeAsync(() => UpdateVoiceLevelVisual(level));

    private void OnLanguageChanged(AppLanguage _)
    {
        Dispatcher.InvokeAsync(() =>
        {
            RefreshCustomPromptChips();
            RefreshBackendBadge();
            UpdateChatMemoryBadge();
            if (_attachedImage is not null)
                UpdateAttachedImageTexts();
            else
                PromptPlaceholderText.Text = LocalizationService.Get("Loc_Ai_PromptPlaceholder");

            if (CopyButton is not null)
                CopyButton.Content = LocalizationService.Get("Loc_Copy");
            if (CopySourceButton is not null)
                CopySourceButton.Content = LocalizationService.Get("Loc_CopyOriginal");

            if (_chatHistory.Count > 1)
                RenderAnswerText(BuildConversationDisplayMarkdown());
            else if (_islandVm?.IsAiGenerating != true && !_voiceService.IsRecording && !_isTranscribing && _chatHistory.Count == 0 && _attachedImage is null && string.IsNullOrWhiteSpace(_contextText))
                StatusLineText.Text = LocalizationService.Get("Loc_Ai_Ready");
        });
    }

    private void RefreshCustomPromptChips()
    {
        var chips = _settingsService.Current.CustomAiPrompts;
        if (AppSettings.IsBuiltInDefaultPromptSet(chips))
            chips = AppSettings.GetDefaultCustomAiPrompts(LocalizationService.CurrentLanguage);

        CustomPromptsItemsControl.ItemsSource = chips
            .Where(c => !string.IsNullOrWhiteSpace(c.Title) && !string.IsNullOrWhiteSpace(c.PromptTemplate))
            .ToList();
    }

    public void ShowWithContextForQuestion(
        string sourceText,
        Int32Rect anchorPhysicalRect,
        Rect anchorDipRect,
        CapturedScreenRegion? capturedImage = null)
    {
        PrepareWindowAtAnchor(anchorPhysicalRect, anchorDipRect);
        _aiCts?.Cancel();
        ResetConversationHistory();
        PromptInputBox.Text = string.Empty;

        SetContextText(sourceText);
        SetAttachedImage(capturedImage);

        string? visionModelId = _attachedImage is not null
            ? LocalLlmCatalogService.FindBestInstalledVisionModelId(_settingsService.Current.SelectedAiModelId)
            : null;
        RefreshBackendBadge(visionModelId);

        if (_attachedImage is not null && !string.IsNullOrWhiteSpace(visionModelId))
        {
            SetGeneratingState(false, LocalizationService.Pick(
                "Photo attached — ask a question and press Enter",
                "Фото прикреплено — задайте вопрос и нажмите Enter"));
            RenderAnswerText(LocalizationService.Pick(
                "Image is ready for analysis! Ask a question (and follow up in chat) or pick an action on the photo card below.",
                "Изображение готово для анализа! Напишите вопрос (и уточняйте ответы в диалоге) или выберите действие на карточке фото ниже."));
        }
        else if (_attachedImage is not null && string.IsNullOrWhiteSpace(_contextText))
        {
            SetGeneratingState(false, LocalizationService.Pick(
                "A Vision model (Qwen 2.5 VL) is required to analyze photos",
                "Для распознавания фото нужна Vision-модель (Qwen 2.5 VL)"));
            RenderAnswerText(LocalizationService.Pick(
                "The currently installed model only supports text. To let AI see photos, open **Settings → Translator & AI**, select **Qwen 2.5 VL 3B** (~2.6 GB) or **Qwen 2.5 VL 7B** (~5.2 GB), and click **Download**.",
                "Текущая скачанная модель работает только с текстом. Чтобы нейросеть «видела» фотографии, откройте **Настройки → Переводчик и ИИ**, выберите **Qwen 2.5 VL 3B** (~2.6 ГБ) или **Qwen 2.5 VL 7B** (~5.2 ГБ) и нажмите **«Скачать»**."));
        }
        else if (!string.IsNullOrWhiteSpace(_contextText))
        {
            SetGeneratingState(false, LocalizationService.Pick(
                "Text loaded into AI — type a question and press Enter",
                "Текст передан в нейросеть — напишите вопрос и нажмите Enter"));
            RenderAnswerText(LocalizationService.Pick(
                "Ask any question about the selected text below or click a quick button above.",
                "Задайте любой вопрос по выделенному тексту ниже или нажмите быструю кнопку вверху."));
        }
        else
        {
            SetGeneratingState(false, LocalizationService.Pick(
                "Waiting for prompt — type a question below",
                "Ожидание запроса — введите вопрос ниже"));
        }

        Activate();
        PromptInputBox.Focus();
    }

    public void ShowWithImageForQuestion(
        CapturedScreenRegion capturedImage,
        string? sourceText,
        Int32Rect anchorPhysicalRect,
        Rect anchorDipRect)
    {
        ShowWithContextForQuestion(sourceText ?? string.Empty, anchorPhysicalRect, anchorDipRect, capturedImage);
    }

    private void ResetConversationHistory()
    {
        _chatHistory.Clear();
        _answerText = string.Empty;
        UpdateChatMemoryBadge();
        RenderAnswerText(string.Empty);
    }

    private void UpdateChatMemoryBadge()
    {
        int assistantReplies = _chatHistory.Count(h => h.Turn.Role == "assistant");
        if (assistantReplies > 0)
        {
            ChatMemoryBadge.Visibility = Visibility.Visible;
            ChatMemoryBadgeText.Text = LocalizationService.Pick($"Chat: {assistantReplies}", $"Чат: {assistantReplies}");
        }
        else
        {
            ChatMemoryBadge.Visibility = Visibility.Collapsed;
        }
    }

    private string BuildConversationDisplayMarkdown(string? streamingAssistantReply = null, string? pendingUserLabel = null)
    {
        if (_chatHistory.Count == 0 && string.IsNullOrEmpty(streamingAssistantReply) && string.IsNullOrEmpty(pendingUserLabel))
            return string.Empty;

        // Single-turn first answer: keep ultra-clean without chat prefixes unless user asks follow-ups
        int userCount = _chatHistory.Count(x => x.Turn.Role == "user") + (string.IsNullOrEmpty(pendingUserLabel) ? 0 : 1);
        if (userCount <= 1 && _chatHistory.Count <= 2)
        {
            if (!string.IsNullOrEmpty(streamingAssistantReply))
                return streamingAssistantReply;
            var lastAssistant = _chatHistory.LastOrDefault(x => x.Turn.Role == "assistant");
            return lastAssistant.Turn?.Content ?? string.Empty;
        }

        string userPrefix = LocalizationService.Pick("**You:** ", "**Вы:** ");
        var sb = new StringBuilder();
        foreach (var (displayLabel, turn) in _chatHistory)
        {
            if (sb.Length > 0)
                sb.AppendLine().AppendLine("---");

            if (turn.Role == "user")
            {
                sb.Append(userPrefix).AppendLine(displayLabel);
            }
            else
            {
                sb.AppendLine(turn.Content);
            }
        }

        if (!string.IsNullOrEmpty(pendingUserLabel))
        {
            if (sb.Length > 0)
                sb.AppendLine().AppendLine("---");
            sb.Append(userPrefix).AppendLine(pendingUserLabel);
        }

        if (!string.IsNullOrEmpty(streamingAssistantReply))
        {
            sb.AppendLine().AppendLine("---");
            sb.Append(streamingAssistantReply);
        }

        return sb.ToString();
    }

    private void SetAttachedImage(CapturedScreenRegion? image)
    {
        _attachedImage = image;
        _attachedImageJpeg = null;

        if (image is null)
        {
            AttachedImagePreview.Source = null;
            AttachedImageBorder.Visibility = Visibility.Collapsed;
            PromptPlaceholderText.Text = LocalizationService.Get("Loc_Ai_PromptPlaceholder");
            return;
        }

        try
        {
            AttachedImagePreview.Source = ScreenCaptureService.CreateBitmapSource(image);
            UpdateAttachedImageTexts();
            AttachedImageBorder.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            LogService.Warn($"Failed to render attached image preview: {ex.Message}");
            _attachedImage = null;
            AttachedImageBorder.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateAttachedImageTexts()
    {
        if (_attachedImage is null)
            return;

        AttachedImageTitleText.Text = LocalizationService.Pick(
            $"Screen photo • {_attachedImage.Width} × {_attachedImage.Height}",
            $"Фото с экрана • {_attachedImage.Width} × {_attachedImage.Height}");

        string? visionId = LocalLlmCatalogService.FindBestInstalledVisionModelId(_settingsService.Current.SelectedAiModelId);
        if (!string.IsNullOrWhiteSpace(visionId))
        {
            AttachedImageHintText.Text = LocalizationService.Pick(
                $"Vision active ({GetShortModelLabel(visionId)}) — ask about this photo",
                $"Vision активен ({GetShortModelLabel(visionId)}) — задайте вопрос по фото");
            PromptPlaceholderText.Text = LocalizationService.Pick(
                "Ask a question about this photo (Enter to send)...",
                "Задайте вопрос по этому фото (Enter — отправить)...");
        }
        else
        {
            AttachedImageHintText.Text = LocalizationService.Pick(
                "Download Qwen 2.5 VL 3B / 7B in Settings to analyze photos",
                "Для анализа фото скачайте Qwen 2.5 VL 3B / 7B в Настройках");
            PromptPlaceholderText.Text = !string.IsNullOrWhiteSpace(_contextText)
                ? LocalizationService.Pick(
                    "Ask about recognized text (Qwen 2.5 VL required for photos)...",
                    "Спросить по распознанному тексту (для фото нужна Qwen 2.5 VL)...")
                : LocalizationService.Pick(
                    "Download Qwen 2.5 VL in Settings to ask about photos...",
                    "Скачайте Qwen 2.5 VL в Настройках для вопросов по фото...");
        }
    }

    private void SetContextText(string? text)
    {
        _contextText = text?.Trim() ?? string.Empty;
        SourceTextBlock.Text = _contextText;
        SourceTextBorder.Visibility = string.IsNullOrWhiteSpace(_contextText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (CopySourceButton is not null)
            CopySourceButton.Content = LocalizationService.Get("Loc_CopyOriginal");
    }

    public async Task ShowAndFeedAsync(
        string text,
        AiFeedHotkeyAction action,
        Int32Rect anchorPhysicalRect,
        Rect anchorDipRect)
    {
        PrepareWindowAtAnchor(anchorPhysicalRect, anchorDipRect);
        ResetConversationHistory();
        SetAttachedImage(null);

        if (string.IsNullOrWhiteSpace(text))
        {
            SetContextText(string.Empty);
            StatusLineText.Text = LocalizationService.Pick(
                "Waiting for prompt — type a question or pick an action above",
                "Ожидание запроса — введите вопрос или выберите действие выше");
            RenderAnswerText(LocalizationService.Pick(
                "Select text in any window before pressing the hotkey — or ask anything here (e.g., \"what's the weather today\").",
                "Выделите текст в любом окне перед нажатием горячей клавиши — или спросите здесь (например, «какая сегодня погода»)."));
            PromptInputBox.Focus();
            return;
        }

        SetContextText(text);

        if (action == AiFeedHotkeyAction.FillPrompt)
        {
            PromptInputBox.Text = string.Empty;
            PromptInputBox.Focus();
            StatusLineText.Text = LocalizationService.Pick(
                $"Loaded {text.Length} chars — type a question about the text and press Enter",
                $"Загружено {text.Length} симв. — напишите вопрос по тексту и нажмите Enter");
            RenderAnswerText(string.Empty);
            return;
        }

        if (action == AiFeedHotkeyAction.Translate)
        {
            string fallbackLang = LocalizationService.IsRussian ? "ru" : "en";
            string langName = LanguageCatalog.FindOrDefault(_settingsService.Current.TargetLanguage, fallbackLang).DisplayName;
            var (tSys, tUsr) = LocalLlmService.BuildTranslatePrompts(text, langName);
            await RunStreamTurnAsync(
                LocalizationService.Pick($"Translating ({langName})...", $"Перевод ({langName})..."),
                tSys,
                tUsr,
                LocalizationService.Pick($"Translate to {langName}", $"Перевести на {langName}"));
            return;
        }

        var (sys, usr) = LocalLlmService.BuildSummaryPrompts(text);
        await RunStreamTurnAsync(
            LocalizationService.Pick("Summarizing text...", "Пересказ текста..."),
            sys,
            usr,
            LocalizationService.Pick("Summarize text", "Пересказать текст"));
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
        _hideAnimGen++;
        RefreshCustomPromptChips();
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
            RootPopupContainer.BeginAnimation(OpacityProperty, null);
            RootPopupContainer.Opacity = 1.0;
            PopupScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PopupScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PopupScaleTransform.ScaleX = 1.0;
            PopupScaleTransform.ScaleY = 1.0;
            PopupBlurEffect.BeginAnimation(BlurEffect.RadiusProperty, null);
            PopupBlurEffect.Radius = 0.0;
            Activate();
        }
    }

    private void RenderAnswerText(string raw, bool scrollToEnd = false)
    {
        Brush primaryBrush = TryFindResource("ThemeTextPrimaryBrush") as Brush ?? Brushes.White;
        Brush accentBrush = TryFindResource("ThemeAccentBrush") as Brush ?? Brushes.SkyBlue;
        Brush codeBgBrush = TryFindResource("ThemeControlBgBrush") as Brush ?? new SolidColorBrush(Color.FromArgb(80, 20, 24, 36));
        Brush borderBrush = TryFindResource("ThemeCardBorderBrush") as Brush ?? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));

        AnswerRichTextBox.Document = AiResponseFormatter.BuildFlowDocument(
            raw,
            primaryBrush,
            accentBrush,
            codeBgBrush,
            borderBrush,
            baseFontSize: 13.5);

        if (scrollToEnd)
            AnswerRichTextBox.ScrollToEnd();
    }

    private static string GetShortModelLabel(string? modelId)
    {
        var entry = LocalLlmCatalogService.FindModel(modelId);
        return entry.ModelId switch
        {
            "qwen2.5-7b-instruct" => "Qwen 2.5 7B",
            "qwen3-4b-instruct" => "Qwen 3 4B",
            "gemma-2-9b-it" => "Gemma 2 9B",
            "qwen2.5-vl-3b-instruct" => "Qwen 2.5 VL 3B",
            "qwen2.5-vl-7b-instruct" => "Qwen 2.5 VL 7B",
            _ => "Qwen 2.5 3B"
        };
    }

    private void RefreshBackendBadge(string? overrideModelId = null)
    {
        string selectedModelId = !string.IsNullOrWhiteSpace(overrideModelId)
            ? overrideModelId
            : _settingsService.Current.SelectedAiModelId;

        if (ModelNameBadgeText is not null)
            ModelNameBadgeText.Text = GetShortModelLabel(selectedModelId);

        if (!LocalLlmCatalogService.IsReady(selectedModelId))
        {
            BackendBadgeText.Text = LocalizationService.Pick("Not downloaded", "Не скачана");
            return;
        }

        if (_llmService.ActiveBackendLabel is not ("Ожидание" or "Idle") &&
            !string.IsNullOrWhiteSpace(_llmService.ActiveBackendLabel))
        {
            BackendBadgeText.Text = _llmService.ActiveBackendLabel;
            return;
        }

        var (_, hasGpu) = LocalLlmCatalogService.DetectHardware();
        var entry = LocalLlmCatalogService.FindModel(selectedModelId);
        string vTag = entry.SupportsVision ? " • Vision" : string.Empty;
        BackendBadgeText.Text = _settingsService.Current.AiBackend switch
        {
            AiComputeBackend.CpuOnly => $"CPU{vTag}",
            AiComputeBackend.GpuVulkan => $"GPU • Vulkan{vTag}",
            _ => (hasGpu
                ? LocalizationService.Pick("Auto (GPU)", "Авто (GPU)")
                : LocalizationService.Pick("Auto (CPU)", "Авто (CPU)")) + vTag
        };
    }

    private async Task RunWeatherAsync(string? customQuestion)
    {
        SetGeneratingState(true, LocalizationService.Pick("Fetching weather...", "Получение погоды..."));
        var weather = await _weatherService.GetCurrentWeatherAsync(_settingsService.Current.AiWeatherCity);
        if (weather is null)
        {
            SetGeneratingState(false, LocalizationService.Pick(
                "Weather unavailable (offline & no cache)",
                "Погода недоступна (нет сети и кэша)"));
            RenderAnswerText(LocalizationService.Pick(
                "Could not fetch weather online and no cached forecast is available yet.",
                "Не удалось загрузить погоду из сети, и сохранённого кэша ещё нет."));
            return;
        }

        string selectedModelId = _settingsService.Current.SelectedAiModelId;
        var modelEntry = LocalLlmCatalogService.FindModel(selectedModelId);

        if (!LocalLlmCatalogService.IsReady(modelEntry.ModelId))
        {
            SetGeneratingState(false, weather.IsFromCache
                ? LocalizationService.Pick("Cached weather (AI model not downloaded)", "Погода из кэша (ИИ не скачан)")
                : LocalizationService.Pick("Weather brief (AI model not downloaded)", "Сводка погоды (ИИ не скачан)"));
            RenderAnswerText(LocalizationService.Pick(
                $"{WeatherService.FormatWeatherBrief(weather)}\n\n(Download **{modelEntry.DisplayName}** in Settings → Translator & AI for detailed AI answers.)",
                $"{WeatherService.FormatWeatherBrief(weather)}\n\n(Скачайте **{modelEntry.DisplayName}** в Настройках → «Переводчик и ИИ», чтобы получать развёрнутые ответы ИИ.)"));
            return;
        }

        var (sys, usr) = LocalLlmService.BuildWeatherPrompts(weather, customQuestion);
        await RunStreamTurnAsync(
            LocalizationService.Pick($"Weather • {weather.City}...", $"Погода • {weather.City}..."),
            sys,
            usr,
            customQuestion ?? LocalizationService.Pick($"Weather ({weather.City})", $"Погода ({weather.City})"));
    }

    private async Task RunStreamTurnAsync(
        string thinkingLabel,
        string systemPrompt,
        string userPrompt,
        string displayUserLabel,
        byte[]? imageJpegBytes = null,
        string? overrideModelId = null)
    {
        string targetModelId = !string.IsNullOrWhiteSpace(overrideModelId)
            ? overrideModelId
            : _settingsService.Current.SelectedAiModelId;

        // If conversation already contains an image in history, keep using the Vision model for follow-ups
        if (string.IsNullOrWhiteSpace(overrideModelId) &&
            (imageJpegBytes is { Length: > 0 } || _chatHistory.Any(h => h.Turn.ImageJpegBytes is { Length: > 0 })))
        {
            string? visId = LocalLlmCatalogService.FindBestInstalledVisionModelId(targetModelId);
            if (!string.IsNullOrWhiteSpace(visId))
                targetModelId = visId;
        }

        var modelEntry = LocalLlmCatalogService.FindModel(targetModelId);

        if (!LocalLlmCatalogService.IsReady(modelEntry.ModelId))
        {
            SetGeneratingState(false, LocalizationService.Pick(
                $"Model {modelEntry.ModelId} is not installed",
                $"Модель {modelEntry.ModelId} не установлена"));
            RenderAnswerText(LocalizationService.Pick(
                $"Open **Settings → Translator & AI → Local AI Assistant**, choose a model (**{modelEntry.DisplayName}**), and click **Download**.",
                $"Откройте **Настройки → «Переводчик и ИИ» → «Локальный ИИ-ассистент»**, выберите нужную модель (**{modelEntry.DisplayName}**) и нажмите **«Скачать»**."));
            return;
        }

        _requestGen++;
        _aiCts?.Cancel();
        _aiCts?.Dispose();
        _aiCts = new CancellationTokenSource();
        var ct = _aiCts.Token;
        int myGen = _requestGen;

        var userTurn = new AiChatTurn("user", userPrompt, imageJpegBytes);
        var requestTurns = _chatHistory.Select(h => h.Turn).Append(userTurn).ToList();

        string streamingReply = string.Empty;
        _lastRenderTickMs = 0;
        RenderAnswerText(BuildConversationDisplayMarkdown(streamingReply, displayUserLabel), scrollToEnd: true);
        RefreshBackendBadge(modelEntry.ModelId);
        SetGeneratingState(true, thinkingLabel);

        var (ok, full, tokSec, err) = await _llmService.StreamChatHistoryAsync(
            _settingsService.Current.AiBackend,
            systemPrompt,
            requestTurns,
            token =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (myGen != _requestGen || ct.IsCancellationRequested)
                        return;
                    streamingReply += token;
                    long now = Environment.TickCount64;
                    if (now - _lastRenderTickMs >= 65)
                    {
                        _lastRenderTickMs = now;
                        RenderAnswerText(BuildConversationDisplayMarkdown(streamingReply, displayUserLabel), scrollToEnd: true);
                    }
                });
            },
            ct,
            modelEntry.ModelId,
            temperature: 0.45);

        // Ignore stale continuation if user cleared chat, closed popup, or started a newer turn
        if (myGen != _requestGen)
            return;

        RefreshBackendBadge(modelEntry.ModelId);

        if (ct.IsCancellationRequested)
        {
            string partial = AiResponseFormatter.StripThinkingTags(!string.IsNullOrWhiteSpace(full) ? full : streamingReply).Trim();
            if (!string.IsNullOrWhiteSpace(partial))
            {
                _answerText = partial;
                _chatHistory.Add((displayUserLabel, userTurn));
                _chatHistory.Add((LocalizationService.Pick("AI", "ИИ"), new AiChatTurn("assistant", partial)));
                UpdateChatMemoryBadge();
                RenderAnswerText(BuildConversationDisplayMarkdown(), scrollToEnd: true);
            }
            SetGeneratingState(false, LocalizationService.Pick("Stopped", "Остановлено"));
            return;
        }

        if (!ok && !string.IsNullOrWhiteSpace(err))
        {
            SetGeneratingState(false, LocalizationService.Pick("Local AI error", "Ошибка локального ИИ"));
            if (string.IsNullOrWhiteSpace(streamingReply))
                RenderAnswerText(BuildConversationDisplayMarkdown(err, displayUserLabel), scrollToEnd: true);
            else
                RenderAnswerText(BuildConversationDisplayMarkdown(streamingReply, displayUserLabel), scrollToEnd: true);
        }
        else
        {
            string rawFinal = !string.IsNullOrWhiteSpace(full) ? full : streamingReply;
            string cleanFinal = AiResponseFormatter.StripThinkingTags(rawFinal).Trim();
            if (!string.IsNullOrWhiteSpace(cleanFinal))
            {
                _answerText = cleanFinal;
                _chatHistory.Add((displayUserLabel, userTurn));
                _chatHistory.Add((LocalizationService.Pick("AI", "ИИ"), new AiChatTurn("assistant", cleanFinal)));
                UpdateChatMemoryBadge();
            }

            RenderAnswerText(BuildConversationDisplayMarkdown(), scrollToEnd: true);
            string doneStatus = tokSec > 0.1
                ? LocalizationService.Pick(
                    $"Done • {tokSec:0.0} tok/s ({_llmService.ActiveBackendLabel})",
                    $"Готово • {tokSec:0.0} ток/с ({_llmService.ActiveBackendLabel})")
                : LocalizationService.Pick(
                    $"Done ({_llmService.ActiveBackendLabel})",
                    $"Готово ({_llmService.ActiveBackendLabel})");
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
        _requestGen++;
        _aiCts?.Cancel();
        ResetConversationHistory();
        SetContextText(string.Empty);
        SetAttachedImage(null);
        _ = RunWeatherAsync(null);
    }

    private void OnSummarizeClipboardClick(object sender, RoutedEventArgs e)
    {
        string text = !string.IsNullOrWhiteSpace(_contextText) ? _contextText : GetClipboardTextSafe();
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusLineText.Text = LocalizationService.Pick(
                "Clipboard is empty — copy some text first",
                "Буфер обмена пуст — скопируйте текст");
            return;
        }
        _requestGen++;
        _aiCts?.Cancel();
        ResetConversationHistory();
        SetContextText(text);
        var (sys, usr) = LocalLlmService.BuildSummaryPrompts(text);
        _ = RunStreamTurnAsync(
            LocalizationService.Pick("Summarizing text...", "Пересказ текста..."),
            sys,
            usr,
            LocalizationService.Get("Loc_Ai_SummarizeClipBtn"));
    }

    private void OnTranslateClipboardClick(object sender, RoutedEventArgs e)
    {
        string text = !string.IsNullOrWhiteSpace(_contextText) ? _contextText : GetClipboardTextSafe();
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusLineText.Text = LocalizationService.Pick(
                "Clipboard is empty — copy some text first",
                "Буфер обмена пуст — скопируйте текст");
            return;
        }
        _requestGen++;
        _aiCts?.Cancel();
        ResetConversationHistory();
        SetContextText(text);
        string fallbackLang = LocalizationService.IsRussian ? "ru" : "en";
        string langName = LanguageCatalog.FindOrDefault(_settingsService.Current.TargetLanguage, fallbackLang).DisplayName;
        var (sys, usr) = LocalLlmService.BuildTranslatePrompts(text, langName);
        _ = RunStreamTurnAsync(
            LocalizationService.Pick($"Translating ({langName})...", $"Перевод ({langName})..."),
            sys,
            usr,
            LocalizationService.Pick($"Translate to {langName}", $"Перевести на {langName}"));
    }

    private void OnCustomPromptChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CustomAiPromptChip chip })
            return;

        if (_attachedImage is not null)
        {
            AskAboutAttachedImage(chip.PromptTemplate, chip.Title);
            return;
        }

        string targetText = !string.IsNullOrWhiteSpace(_contextText)
            ? _contextText
            : (_chatHistory.Count > 0 ? string.Empty : GetClipboardTextSafe());

        string sys = LocalizationService.Pick(
            "You are a smart, concise desktop AI assistant. Reply clearly, accurately, and concisely.",
            "Ты умный и лаконичный ИИ-помощник на рабочем столе. Отвечай по-русски, точно и по делу.");
        string usr;
        if (!string.IsNullOrWhiteSpace(targetText))
        {
            SetContextText(targetText);
            string trimmed = targetText.Length > 3500 ? targetText[..3500] : targetText;
            usr = $"{chip.PromptTemplate}\n\n\"\"\"\n{trimmed}\n\"\"\"";
        }
        else if (_chatHistory.Count > 0)
        {
            usr = chip.PromptTemplate;
        }
        else
        {
            PromptInputBox.Text = chip.PromptTemplate + " ";
            PromptInputBox.CaretIndex = PromptInputBox.Text.Length;
            PromptInputBox.Focus();
            StatusLineText.Text = LocalizationService.Pick(
                $"Template \"{chip.Title}\" inserted — finish your text and press Enter",
                $"Шаблон «{chip.Title}» вставлен — допишите текст и нажмите Enter");
            return;
        }

        string modelLabel = GetShortModelLabel(_settingsService.Current.SelectedAiModelId);
        _ = RunStreamTurnAsync($"{modelLabel} • {chip.Title}...", sys, usr, chip.Title);
    }

    private void OnDescribeImageClick(object sender, RoutedEventArgs e)
    {
        AskAboutAttachedImage(
            LocalizationService.Pick(
                "Clearly and thoroughly describe what is shown in this photo/screenshot.",
                "Подробно и понятно опиши, что изображено на этом фото/скриншоте."),
            LocalizationService.Get("Loc_Ai_WhatInPhotoBtn"));
    }

    private void OnExplainImageClick(object sender, RoutedEventArgs e)
    {
        AskAboutAttachedImage(
            LocalizationService.Pick(
                "Analyze and explain the contents of this image. If there is a problem, formula, code, or error — solve and explain it step by step.",
                "Разбери и объясни содержимое этого изображения. Если там задача, формула, код или ошибка — реши и объясни шаг за шагом."),
            LocalizationService.Get("Loc_Ai_ExplainSolveBtn"));
    }

    private void OnOcrImageClick(object sender, RoutedEventArgs e)
    {
        AskAboutAttachedImage(
            LocalizationService.Pick(
                "Extract and accurately transcribe all text and mathematical formulas from this image (including handwriting, fractions, degree symbols °C, and Greek letters). Output only the recognized text and formulas.",
                "Извлеки и точно перепиши весь текст и математические формулы с этого изображения (включая рукописный текст, дроби, знаки градусов °C и греческие буквы). Выведи только распознанный текст и формулы."),
            LocalizationService.Get("Loc_Ai_ExtractTextBtn"));
    }

    private void OnExtractTableClick(object sender, RoutedEventArgs e)
    {
        if (_attachedImage is null)
            return;

        string? visionModelId = LocalLlmCatalogService.FindBestInstalledVisionModelId(_settingsService.Current.SelectedAiModelId);
        if (!string.IsNullOrWhiteSpace(visionModelId))
        {
            _attachedImageJpeg ??= ScreenCaptureService.EncodeToJpegBytes(_attachedImage, maxSide: 1200, quality: 90);
            var (sys, usr) = LocalLlmService.BuildTableExtractPrompts(_contextText);
            bool alreadySentImage = _chatHistory.Any(h => h.Turn.ImageJpegBytes is { Length: > 0 });
            _ = RunStreamTurnAsync(
                LocalizationService.Pick(
                    $"{GetShortModelLabel(visionModelId)} extracting table...",
                    $"{GetShortModelLabel(visionModelId)} извлекает таблицу..."),
                sys,
                usr,
                LocalizationService.Pick("Table → Markdown/CSV", "Таблица → Markdown/CSV"),
                alreadySentImage ? null : _attachedImageJpeg,
                visionModelId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_contextText))
        {
            var (sys, usr) = LocalLlmService.BuildTableExtractPrompts(_contextText);
            _ = RunStreamTurnAsync(
                LocalizationService.Pick("Formatting table...", "Форматирование таблицы..."),
                sys,
                usr,
                LocalizationService.Pick("Table → Markdown/CSV", "Таблица → Markdown/CSV"));
            return;
        }

        SetGeneratingState(false, LocalizationService.Pick("Vision model not downloaded", "Vision-модель не скачана"));
        RenderAnswerText(LocalizationService.Pick(
            "To extract tables from screenshots, download **Qwen 2.5 VL 3B** or **Qwen 2.5 VL 7B** in Settings → Translator & AI.",
            "Для распознавания таблиц со скриншотов скачайте **Qwen 2.5 VL 3B** или **Qwen 2.5 VL 7B** в Настройках → «Переводчик и ИИ»."));
    }

    private void CancelVoiceRecordingIfActive()
    {
        if (_voiceService.IsRecording)
            _voiceService.CancelRecording();

        VoicePulseRing.Visibility = Visibility.Collapsed;
        VoiceMicIcon.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "ThemeAccentBrush");
        VoicePulseScale.ScaleX = 1.0;
        VoicePulseScale.ScaleY = 1.0;
    }

    private async void OnVoiceDictateClick(object sender, RoutedEventArgs e)
    {
        if (_isTranscribing)
            return;

        if (_voiceService.IsRecording)
        {
            _isTranscribing = true;
            VoiceDictateButton.IsEnabled = false;
            VoicePulseRing.Visibility = Visibility.Collapsed;
            VoiceMicIcon.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "ThemeAccentBrush");
            StatusLineText.Text = LocalizationService.Pick("Transcribing speech (Whisper)...", "Расшифровка голоса (Whisper)...");
            if (_islandVm is not null)
            {
                _islandVm.AiBackendBadge = "Whisper";
                _islandVm.AiStatusLine = LocalizationService.Pick("Transcribing speech...", "Расшифровка речи...");
                _islandVm.IsAiGenerating = true;
            }

            try
            {
                var (ok, text, err) = await _voiceService.StopAndTranscribeAsync();
                if (_islandVm is not null)
                    _islandVm.IsAiGenerating = false;

                if (!ok || string.IsNullOrWhiteSpace(text))
                {
                    StatusLineText.Text = err ?? LocalizationService.Pick("No speech recognized.", "Речь не распознана.");
                    return;
                }

                PromptInputBox.Text = string.IsNullOrWhiteSpace(PromptInputBox.Text)
                    ? text
                    : $"{PromptInputBox.Text.TrimEnd()} {text}";
                PromptInputBox.CaretIndex = PromptInputBox.Text.Length;
                PromptInputBox.Focus();
                StatusLineText.Text = LocalizationService.Pick(
                    "Voice transcribed — press Enter to send or edit your prompt",
                    "Голос распознан — нажмите Enter для отправки или допишите вопрос");
            }
            finally
            {
                _isTranscribing = false;
                VoiceDictateButton.IsEnabled = true;
            }
            return;
        }

        if (!WhisperVoiceService.IsReady())
        {
            StatusLineText.Text = LocalizationService.Pick(
                "Whisper voice module is not downloaded",
                "Модуль голоса Whisper не скачан");
            RenderAnswerText(LocalizationService.Pick(
                "For 100% offline voice dictation, open **Settings → Translator & AI** and click **Download** in the Whisper section (~150 MB, English and Russian offline).",
                "Для 100% оффлайн голосовой диктовки откройте **Настройки → «Переводчик и ИИ»** и нажмите **«Скачать»** в блоке Whisper (~150 МБ, русский и английский без интернета)."));
            return;
        }

        var (started, startErr) = _voiceService.StartRecording();
        if (!started)
        {
            StatusLineText.Text = startErr ?? LocalizationService.Pick(
                "Failed to start microphone recording.",
                "Не удалось включить запись микрофона.");
            return;
        }

        VoicePulseRing.Visibility = Visibility.Visible;
        VoiceMicIcon.Stroke = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        StatusLineText.Text = LocalizationService.Pick(
            "Recording from microphone... Click the mic again to transcribe",
            "Идёт запись с микрофона... Нажмите на микрофон ещё раз для расшифровки");
        if (_islandVm is not null)
        {
            _islandVm.AiBackendBadge = LocalizationService.Pick("REC", "Запись");
            _islandVm.AiStatusLine = LocalizationService.Pick("Listening...", "Слушаю микрофон...");
            _islandVm.IsAiGenerating = true;
        }
    }

    private void UpdateVoiceLevelVisual(double level)
    {
        if (!_voiceService.IsRecording)
        {
            VoicePulseScale.ScaleX = 1.0;
            VoicePulseScale.ScaleY = 1.0;
            return;
        }

        double s = 0.85 + Math.Clamp(level, 0.0, 1.0) * 0.55;
        VoicePulseScale.ScaleX = s;
        VoicePulseScale.ScaleY = s;
    }

    private void OnClearImageClick(object sender, RoutedEventArgs e)
    {
        SetAttachedImage(null);
        // Strip old image bytes from chat history so subsequent text-only turns don't re-send the removed image
        for (int i = 0; i < _chatHistory.Count; i++)
        {
            if (_chatHistory[i].Turn.ImageJpegBytes is not null)
            {
                _chatHistory[i] = (_chatHistory[i].DisplayLabel, _chatHistory[i].Turn with { ImageJpegBytes = null });
            }
        }
        RefreshBackendBadge();
        PromptInputBox.Focus();
    }

    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (TryPasteImageFromClipboard())
            {
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnCloseClick(sender, e);
        }
    }

    private void OnPromptBoxPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (TryPasteImageFromClipboard())
        {
            e.CancelCommand();
        }
    }

    private bool TryPasteImageFromClipboard()
    {
        try
        {
            CapturedScreenRegion? pasted = null;

            if (System.Windows.Clipboard.ContainsImage())
            {
                var bmp = System.Windows.Clipboard.GetImage();
                pasted = ScreenCaptureService.FromBitmapSource(bmp);
            }
            else if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var files = System.Windows.Clipboard.GetFileDropList();
                foreach (string? file in files)
                {
                    if (string.IsNullOrWhiteSpace(file)) continue;
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".gif")
                    {
                        pasted = ScreenCaptureService.LoadFromImageFile(file);
                        if (pasted is not null)
                            break;
                    }
                }
            }

            if (pasted is null)
                return false;

            // Attaching a new photo resets image payload for the next turn
            ResetConversationHistory();
            SetAttachedImage(pasted);
            string? visionId = LocalLlmCatalogService.FindBestInstalledVisionModelId(_settingsService.Current.SelectedAiModelId);
            RefreshBackendBadge(visionId);

            if (!string.IsNullOrWhiteSpace(visionId))
            {
                StatusLineText.Text = LocalizationService.Pick(
                    $"Photo pasted from clipboard ({pasted.Width} × {pasted.Height}) — type a question and press Enter",
                    $"Фото вставлено из буфера ({pasted.Width} × {pasted.Height}) — напишите вопрос и нажмите Enter");
            }
            else
            {
                StatusLineText.Text = LocalizationService.Pick(
                    $"Photo pasted ({pasted.Width} × {pasted.Height}) — download Qwen 2.5 VL in Settings to analyze",
                    $"Фото вставлено ({pasted.Width} × {pasted.Height}) — для анализа скачайте Qwen 2.5 VL в Настройках");
            }

            PromptInputBox.Focus();
            return true;
        }
        catch (Exception ex)
        {
            LogService.Warn($"Clipboard image paste error: {ex.Message}");
            return false;
        }
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

        // If an image is attached, route to Vision LLM (multi-turn aware!)
        if (_attachedImage is not null)
        {
            AskAboutAttachedImage(prompt, prompt);
            return;
        }

        if (string.IsNullOrWhiteSpace(_contextText) &&
            _chatHistory.Count == 0 &&
            (prompt.Contains("погод", StringComparison.OrdinalIgnoreCase) ||
             prompt.Contains("weather", StringComparison.OrdinalIgnoreCase)))
        {
            _ = RunWeatherAsync(prompt);
            return;
        }

        string modelLabel = GetShortModelLabel(_settingsService.Current.SelectedAiModelId);
        string sys = LocalizationService.Pick(
            "You are a smart, concise desktop AI assistant. Answer briefly, accurately, and clearly. Consider previous conversation turns when the user asks follow-up questions.",
            "Ты умный и лаконичный ИИ-помощник на рабочем столе. Отвечай по-русски, кратко, точно и понятно. Учитывай предыдущие сообщения диалога, если пользователь уточняет вопрос.");

        // Include source text context only on the first turn of the conversation so follow-up turns stay compact
        string clippedCtx = _contextText.Length > 3500 ? _contextText[..3500] : _contextText;
        string finalUserPrompt = (!string.IsNullOrWhiteSpace(_contextText) && _chatHistory.Count == 0)
            ? LocalizationService.Pick(
                $"Selected text (context):\n\"\"\"\n{clippedCtx}\n\"\"\"\n\nUser question about this text: {prompt}",
                $"Выделенный текст (контекст):\n\"\"\"\n{clippedCtx}\n\"\"\"\n\nВопрос пользователя по этому тексту: {prompt}")
            : prompt;

        _ = RunStreamTurnAsync(
            LocalizationService.Pick($"{modelLabel} thinking...", $"{modelLabel} думает..."),
            sys,
            finalUserPrompt,
            prompt);
    }

    private void AskAboutAttachedImage(string question, string? displayLabel = null)
    {
        if (_attachedImage is null)
            return;

        string shownLabel = string.IsNullOrWhiteSpace(displayLabel) ? question : displayLabel;
        string? visionModelId = LocalLlmCatalogService.FindBestInstalledVisionModelId(_settingsService.Current.SelectedAiModelId);
        if (!string.IsNullOrWhiteSpace(visionModelId))
        {
            _attachedImageJpeg ??= ScreenCaptureService.EncodeToJpegBytes(_attachedImage, maxSide: 1024, quality: 86);
            bool alreadySentImageInHistory = _chatHistory.Any(h => h.Turn.ImageJpegBytes is { Length: > 0 });

            var (sys, usr) = alreadySentImageInHistory
                ? (LocalizationService.Pick(
                    "You are a smart, attentive Vision AI assistant. Continue the conversation about the previously sent image accurately and clearly.",
                    "Ты умный и внимательный ИИ-ассистент с компьютерным зрением. Продолжай диалог по ранее отправленному изображению точно, понятно и по-русски."), question)
                : LocalLlmService.BuildImageQuestionPrompts(question, _contextText);

            string label = GetShortModelLabel(visionModelId);
            _ = RunStreamTurnAsync(
                LocalizationService.Pick($"{label} analyzing photo...", $"{label} анализирует фото..."),
                sys,
                usr,
                shownLabel,
                alreadySentImageInHistory ? null : _attachedImageJpeg,
                visionModelId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_contextText))
        {
            string modelLabel = GetShortModelLabel(_settingsService.Current.SelectedAiModelId);
            string sys = LocalizationService.Pick(
                "You are a smart, concise desktop AI assistant. Answer briefly, accurately, and clearly.",
                "Ты умный и лаконичный ИИ-помощник на рабочем столе. Отвечай по-русски, кратко, точно и понятно.");
            string clippedCtx = _contextText.Length > 3500 ? _contextText[..3500] : _contextText;
            string user = _chatHistory.Count == 0
                ? LocalizationService.Pick(
                    $"Recognized text from image (context):\n\"\"\"\n{clippedCtx}\n\"\"\"\n\nUser question: {question}",
                    $"Распознанный текст с изображения (контекст):\n\"\"\"\n{clippedCtx}\n\"\"\"\n\nВопрос пользователя: {question}")
                : question;
            _ = RunStreamTurnAsync(
                LocalizationService.Pick($"{modelLabel} (via OCR text)...", $"{modelLabel} (по OCR-тексту)..."),
                sys,
                user,
                shownLabel);
            return;
        }

        SetGeneratingState(false, LocalizationService.Pick("Vision model not downloaded", "Vision-модель не скачана"));
        RenderAnswerText(LocalizationService.Pick(
            "To analyze photos and screenshots, download a multimodal Vision model in **Settings → Translator & AI → Local AI Assistant**:\n- **Qwen 2.5 VL 3B • Vision** (~2.6 GB, for 4+ GB VRAM)\n- **Qwen 2.5 VL 7B • Vision** (~5.2 GB, high accuracy, for 6–8+ GB VRAM)",
            "Чтобы нейросеть распознавала фотографии и картинки, скачайте мультимодальную модель в **Настройках → «Переводчик и ИИ» → «Локальный ИИ-ассистент»**:\n- **Qwen 2.5 VL 3B • Vision** (~2.6 ГБ, для 4+ ГБ VRAM)\n- **Qwen 2.5 VL 7B • Vision** (~5.2 ГБ, высокая точность, для 6–8+ ГБ VRAM)"));
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => _aiCts?.Cancel();

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _requestGen++;
        _aiCts?.Cancel();
        CancelVoiceRecordingIfActive();
        ResetConversationHistory();
        PromptInputBox.Text = string.Empty;
        SetContextText(string.Empty);
        SetAttachedImage(null);
        RefreshBackendBadge();
        SetGeneratingState(false, LocalizationService.Pick("New chat ready", "Новый диалог готов"));
        PromptInputBox.Focus();
    }

    private void OnCopySourceClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_contextText))
        {
            try { System.Windows.Clipboard.SetText(_contextText); } catch { }
            CopySourceButton.Content = LocalizationService.Get("Loc_Copied");
        }
    }

    private void OnClearContextClick(object sender, RoutedEventArgs e)
    {
        SetContextText(string.Empty);
        PromptInputBox.Focus();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        string textToCopy = AiResponseFormatter.FormatToReadablePlainText(_answerText);
        if (!string.IsNullOrWhiteSpace(textToCopy))
        {
            try { System.Windows.Clipboard.SetText(textToCopy); } catch { }
            CopyButton.Content = LocalizationService.Get("Loc_Copied");
        }
    }

    private void CancelPendingAndHide()
    {
        _requestGen++;
        _aiCts?.Cancel();
        CancelVoiceRecordingIfActive();
        SetGeneratingState(false, LocalizationService.Get("Loc_Ai_Ready"));
        AnimateBlurOutAndHide();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CancelPendingAndHide();

    private void OnPopupMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
            return;

        if (e.OriginalSource is DependencyObject dep && IsInsideInteractiveControl(dep))
            return;

        try { DragMove(); } catch { }
    }

    private static bool IsInsideInteractiveControl(DependencyObject? dep)
    {
        while (dep is not null)
        {
            if (dep is System.Windows.Controls.Primitives.TextBoxBase or
                System.Windows.Controls.Primitives.ScrollBar or
                System.Windows.Controls.Primitives.Thumb or
                System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }
            dep = DwmBackdropService.GetParentSafe(dep);
        }
        return false;
    }

    private void AnimateBlurIn()
    {
        _hideAnimGen++;
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
        int gen = ++_hideAnimGen;
        var duration = TimeSpan.FromMilliseconds(220);
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

        var fade = new DoubleAnimation(1.0, 0.0, duration) { EasingFunction = easeIn };
        fade.Completed += (_, _) =>
        {
            if (gen == _hideAnimGen)
                Hide();
        };

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
