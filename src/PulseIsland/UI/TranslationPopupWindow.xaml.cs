using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using PulseIsland.Core;
using PulseIsland.Interop;
using PulseIsland.Services;

namespace PulseIsland.UI;

public partial class TranslationPopupWindow : Window
{
    private readonly DispatcherTimer _autoHideTimer;
    private string _translatedText = string.Empty;
    private string _sourceText = string.Empty;
    private string _originalOcrText = string.Empty;
    private string _originalSourceLang = "en";
    private string _currentSourceLang = "en";
    private string _currentTargetLang = "en";
    private bool _lastIsOffline;
    private bool _isErrorState;
    private bool _lastDwmBlur;
    private bool _isSwapping;
    private bool _allowClose;
    private CapturedScreenRegion? _capturedRegion;
    private Int32Rect _lastPhysicalRect;
    private Rect _lastDipRect;
    private bool _autoHideEnabled;

    public event Action<string, CapturedScreenRegion?, Int32Rect, Rect>? AskAiRequested;
    public event Func<string, string, string, Task<TranslationResult>>? SwapTranslateAsync;

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    public TranslationPopupWindow()
    {
        InitializeComponent();

        _autoHideTimer = new DispatcherTimer();
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer.Stop();
            AnimateBlurOutAndHide();
        };

        MouseEnter += (_, _) => _autoHideTimer.Stop();
        MouseLeave += (_, _) => RestartAutoHide();
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Closing += (_, e) =>
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                _autoHideTimer.Stop();
                Hide();
            }
        };
        Closed += (_, _) => LocalizationService.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(AppLanguage _)
    {
        Dispatcher.InvokeAsync(() =>
        {
            bool hasSource = !string.IsNullOrWhiteSpace(_sourceText);
            bool hasImage = _capturedRegion is not null;
            AskAiButton.Content = (!hasSource && hasImage)
                ? LocalizationService.Pick("AI Vision", "ИИ по фото")
                : LocalizationService.Get("Loc_TransPopup_AskAiBtn");
            CopyButton.Content = LocalizationService.Get("Loc_Copy");
            CopySourceButton.Content = LocalizationService.Get("Loc_CopyOriginal");
            string modeTag = _lastIsOffline ? LocalizationService.Pick(" • OFFLINE", " • ОФЛАЙН") : string.Empty;
            LangBadgeText.Text = $"{_currentSourceLang.ToUpperInvariant()} → {_currentTargetLang.ToUpperInvariant()}{modeTag}";
        });
    }

    public void ShowTranslation(
        TranslationRecord record,
        Int32Rect selectionPhysicalRect,
        Rect selectionDipRect,
        int autoHideSeconds,
        bool enableDwmBlur = false,
        CapturedScreenRegion? capturedRegion = null,
        bool isError = false,
        bool allowAiButton = true)
    {
        _autoHideTimer.Stop();
        _isErrorState = isError;
        _translatedText = isError ? string.Empty : record.TranslatedText;
        _sourceText = record.SourceText;
        _originalOcrText = record.SourceText;
        _currentSourceLang = string.IsNullOrWhiteSpace(record.DetectedSourceLanguage) ? "en" : record.DetectedSourceLanguage;
        _originalSourceLang = _currentSourceLang;
        _currentTargetLang = string.IsNullOrWhiteSpace(record.TargetLanguage) ? "en" : record.TargetLanguage;
        _lastIsOffline = record.IsOffline;
        _lastDwmBlur = enableDwmBlur;
        _capturedRegion = capturedRegion;
        _lastPhysicalRect = selectionPhysicalRect;
        _lastDipRect = selectionDipRect;

        TranslatedTextBlock.Text = record.TranslatedText;
        SourceTextBlock.Text = record.SourceText;
        bool hasSource = !string.IsNullOrWhiteSpace(record.SourceText);
        bool hasImage = capturedRegion is not null;
        SourceTextBorder.Visibility = hasSource
            ? Visibility.Visible
            : Visibility.Collapsed;
        SwapDirectionButton.Visibility = (hasSource && !isError)
            ? Visibility.Visible
            : Visibility.Collapsed;
        CopyButton.Visibility = isError ? Visibility.Collapsed : Visibility.Visible;
        AskAiButton.Visibility = (allowAiButton && (hasSource || hasImage))
            ? Visibility.Visible
            : Visibility.Collapsed;
        AskAiButton.Content = (!hasSource && hasImage)
            ? LocalizationService.Pick("AI Vision", "ИИ по фото")
            : LocalizationService.Get("Loc_TransPopup_AskAiBtn");

        string modeTag = record.IsOffline ? LocalizationService.Pick(" • OFFLINE", " • ОФЛАЙН") : string.Empty;
        LangBadgeText.Text = $"{record.DetectedSourceLanguage.ToUpperInvariant()} → {record.TargetLanguage.ToUpperInvariant()}{modeTag}";

        CopyButton.Content = LocalizationService.Get("Loc_Copy");
        CopySourceButton.Content = LocalizationService.Get("Loc_CopyOriginal");

        Show();
        UpdateLayout();

        int centerX = selectionPhysicalRect.X + selectionPhysicalRect.Width / 2;
        int centerY = selectionPhysicalRect.Y + selectionPhysicalRect.Height / 2;
        var targetMonitor = MonitorService.GetMonitorForPhysicalPoint(centerX, centerY, this);
        var monitorBounds = targetMonitor.DipBounds;

        double desiredLeft = selectionDipRect.Left;
        double desiredTop = selectionDipRect.Bottom + 8;

        if (desiredLeft + ActualWidth > monitorBounds.Right - 12)
            desiredLeft = monitorBounds.Right - ActualWidth - 12;
        if (desiredLeft < monitorBounds.Left + 12)
            desiredLeft = monitorBounds.Left + 12;

        if (desiredTop + ActualHeight > monitorBounds.Bottom - 12)
            desiredTop = Math.Max(monitorBounds.Top + 12, selectionDipRect.Top - ActualHeight - 8);

        Left = desiredLeft;
        Top = desiredTop;

        DwmBackdropService.ApplyToCard(this, MainPopupCard, enableDwmBlur, 20);

        // Materialize from air (Blur-in: Opacity 0->1, Scale 0.92->1.0, BlurRadius 8->0)
        AnimateBlurIn();

        _autoHideEnabled = autoHideSeconds > 0;
        if (_autoHideEnabled)
        {
            _autoHideTimer.Interval = TimeSpan.FromSeconds(autoHideSeconds);
            _autoHideTimer.Start();
        }
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
        var duration = TimeSpan.FromMilliseconds(200);
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

        var fadeAnim = new DoubleAnimation(1.0, 0.0, duration) { EasingFunction = easeIn };
        fadeAnim.Completed += (_, _) => Hide();

        RootPopupContainer.BeginAnimation(OpacityProperty, fadeAnim);
        PopupScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 0.92, duration) { EasingFunction = easeIn });
        PopupScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 0.92, duration) { EasingFunction = easeIn });
        PopupBlurEffect.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0.0, 8.0, duration) { EasingFunction = easeIn });
    }

    private void RestartAutoHide()
    {
        if (_autoHideEnabled && _autoHideTimer.Interval.TotalSeconds > 0 && IsVisible)
            _autoHideTimer.Start();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_translatedText))
        {
            try { System.Windows.Clipboard.SetText(_translatedText); } catch { }
            CopyButton.Content = LocalizationService.Get("Loc_Copied");
        }
    }

    private void OnCopySourceClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_sourceText))
        {
            try { System.Windows.Clipboard.SetText(_sourceText); } catch { }
            CopySourceButton.Content = LocalizationService.Get("Loc_Copied");
        }
    }

    private async void OnSwapDirectionClick(object sender, RoutedEventArgs e)
    {
        if (_isSwapping || SwapTranslateAsync is null)
            return;

        string baseOcr = _originalOcrText;
        if (string.IsNullOrWhiteSpace(baseOcr) && string.IsNullOrWhiteSpace(_translatedText))
            return;

        _autoHideTimer.Stop();
        _isSwapping = true;
        SwapDirectionButton.IsEnabled = false;

        try
        {
            string src = _currentSourceLang.ToLowerInvariant();
            string tgt = _currentTargetLang.ToLowerInvariant();
            string nextSource;
            string nextTarget;
            string textToTranslate;

            if (string.Equals(src, tgt, StringComparison.OrdinalIgnoreCase) || src == "und" || src == "auto")
            {
                nextSource = tgt;
                nextTarget = string.Equals(tgt, "ru", StringComparison.OrdinalIgnoreCase) ? "en" : "ru";
                textToTranslate = !string.IsNullOrWhiteSpace(baseOcr) ? baseOcr : _translatedText;
            }
            else
            {
                nextSource = tgt;
                nextTarget = src;
                // Deterministically reuse original OCR text when swapping back to its source language (all 16 languages)
                bool origMatchesNextSource = string.Equals(_originalSourceLang, nextSource, StringComparison.OrdinalIgnoreCase);
                textToTranslate = origMatchesNextSource && !string.IsNullOrWhiteSpace(baseOcr)
                    ? baseOcr
                    : (!string.IsNullOrWhiteSpace(_translatedText) ? _translatedText : baseOcr);
            }

            LangBadgeText.Text = $"{nextSource.ToUpperInvariant()} ⇄ {nextTarget.ToUpperInvariant()} ...";

            var res = await SwapTranslateAsync(textToTranslate, nextSource, nextTarget);
            if (res.Success && !string.IsNullOrWhiteSpace(res.TranslatedText))
            {
                _isErrorState = false;
                _currentSourceLang = string.IsNullOrWhiteSpace(res.DetectedSourceLanguage) ? nextSource : res.DetectedSourceLanguage;
                _currentTargetLang = string.IsNullOrWhiteSpace(res.TargetLanguage) ? nextTarget : res.TargetLanguage;
                _lastIsOffline = res.UsedOfflineModel;
                _sourceText = textToTranslate;
                _translatedText = res.TranslatedText;

                TranslatedTextBlock.Text = _translatedText;
                SourceTextBlock.Text = _sourceText;
                SourceTextBorder.Visibility = Visibility.Visible;
                CopyButton.Visibility = Visibility.Visible;
                CopyButton.Content = LocalizationService.Get("Loc_Copy");
                CopySourceButton.Content = LocalizationService.Get("Loc_CopyOriginal");

                string modeTag = res.UsedOfflineModel ? LocalizationService.Pick(" • OFFLINE", " • ОФЛАЙН") : string.Empty;
                LangBadgeText.Text = $"{_currentSourceLang.ToUpperInvariant()} → {_currentTargetLang.ToUpperInvariant()}{modeTag}";

                UpdateLayout();
                DwmBackdropService.ApplyToCard(this, MainPopupCard, _lastDwmBlur, 20);
            }
            else
            {
                string modeTag = _lastIsOffline ? LocalizationService.Pick(" • OFFLINE", " • ОФЛАЙН") : string.Empty;
                LangBadgeText.Text = $"{_currentSourceLang.ToUpperInvariant()} → {_currentTargetLang.ToUpperInvariant()}{modeTag}";
                if (!string.IsNullOrWhiteSpace(res.ErrorMessage))
                    TranslatedTextBlock.Text = res.ErrorMessage!;
            }
        }
        catch (Exception ex)
        {
            string modeTag = _lastIsOffline ? LocalizationService.Pick(" • OFFLINE", " • ОФЛАЙН") : string.Empty;
            LangBadgeText.Text = $"{_currentSourceLang.ToUpperInvariant()} → {_currentTargetLang.ToUpperInvariant()}{modeTag}";
            TranslatedTextBlock.Text = LocalizationService.Pick(
                $"Direction swap error: {ex.Message}",
                $"Ошибка смены направления: {ex.Message}");
        }
        finally
        {
            _isSwapping = false;
            SwapDirectionButton.IsEnabled = true;
            RestartAutoHide();
        }
    }

    private static bool ContainsCyrillic(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char c in text)
        {
            if (c is >= '\u0400' and <= '\u04FF')
                return true;
        }
        return false;
    }

    private static bool ContainsLatin(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char c in text)
        {
            if ((c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z'))
                return true;
        }
        return false;
    }

    private void OnAskAiClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_sourceText) && _capturedRegion is null)
            return;

        _autoHideTimer.Stop();
        Hide();
        AskAiRequested?.Invoke(_sourceText, _capturedRegion, _lastPhysicalRect, _lastDipRect);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _autoHideTimer.Stop();
        AnimateBlurOutAndHide();
    }

    private void OnPopupMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source))
            return;

        _autoHideTimer.Stop();
        try
        {
            DragMove();
        }
        catch
        {
            // Ignore if mouse button was released before DragMove started
        }
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.Primitives.Thumb
                or System.Windows.Controls.Primitives.ScrollBar)
                return true;
            source = DwmBackdropService.GetParentSafe(source);
        }
        return false;
    }
}
