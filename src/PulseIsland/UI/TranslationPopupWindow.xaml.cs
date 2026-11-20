using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using PulseIsland.Core;
using PulseIsland.Interop;

namespace PulseIsland.UI;

public partial class TranslationPopupWindow : Window
{
    private readonly DispatcherTimer _autoHideTimer;
    private string _translatedText = string.Empty;
    private bool _autoHideEnabled;

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
    }

    public void ShowTranslation(
        TranslationRecord record,
        Int32Rect selectionPhysicalRect,
        Rect selectionDipRect,
        int autoHideSeconds,
        bool enableDwmBlur = false)
    {
        _autoHideTimer.Stop();
        _translatedText = record.TranslatedText;
        TranslatedTextBlock.Text = record.TranslatedText;
        SourceTextBlock.Text = record.SourceText;
        SourceTextBorder.Visibility = string.IsNullOrWhiteSpace(record.SourceText)
            ? Visibility.Collapsed
            : Visibility.Visible;

        string modeTag = record.IsOffline ? " • OFFLINE" : string.Empty;
        LangBadgeText.Text = $"{record.DetectedSourceLanguage.ToUpperInvariant()} → {record.TargetLanguage.ToUpperInvariant()}{modeTag}";

        CopyButton.Content = Services.LocalizationService.Get("Loc_Copy");

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
            System.Windows.Clipboard.SetText(_translatedText);
            CopyButton.Content = Services.LocalizationService.Get("Loc_Copied");
        }
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
            if (source is Button)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }
}
