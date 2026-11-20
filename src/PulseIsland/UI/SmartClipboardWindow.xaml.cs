using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using PulseIsland.Core;
using PulseIsland.Interop;
using PulseIsland.Services;

namespace PulseIsland.UI;

public partial class SmartClipboardWindow : Window
{
    private sealed record ClipboardItemVm(string Id, string Text, string PreviewText, string MetaText, string PinLabel);

    private readonly SmartClipboardService _clipboardService;
    private readonly SettingsService _settingsService;
    private bool _pinnedOnly;

    public SmartClipboardWindow(SmartClipboardService clipboardService, SettingsService settingsService)
    {
        InitializeComponent();
        _clipboardService = clipboardService;
        _settingsService = settingsService;

        _clipboardService.HistoryChanged += OnHistoryChanged;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) =>
        {
            _clipboardService.HistoryChanged -= OnHistoryChanged;
            LocalizationService.LanguageChanged -= OnLanguageChanged;
        };
        RefreshList();
    }

    private void OnLanguageChanged(AppLanguage _)
    {
        Dispatcher.InvokeAsync(() =>
        {
            FilterPinnedBtn.Content = _pinnedOnly
                ? LocalizationService.Get("Loc_Clip_AllItems")
                : LocalizationService.Get("Loc_Clip_Pinned");
            RefreshList();
        });
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(260);

        RootClipGrid.Opacity = 0.0;
        ClipScaleTransform.ScaleX = 0.92;
        ClipScaleTransform.ScaleY = 0.92;
        ClipBlurEffect.Radius = 8.0;

        RootClipGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, dur) { EasingFunction = ease });
        ClipScaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1.0, dur) { EasingFunction = ease });
        ClipScaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1.0, dur) { EasingFunction = ease });
        ClipBlurEffect.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, new DoubleAnimation(8.0, 0.0, dur) { EasingFunction = ease });

        DwmBackdropService.ApplyToCard(this, MainSurfaceBorder, _settingsService.Current.EnableDwmBackdropBlur, 24);
        SearchBox.Focus();
    }

    private void OnHistoryChanged()
    {
        Dispatcher.InvokeAsync(RefreshList);
    }

    private void RefreshList()
    {
        var items = _clipboardService.Search(SearchBox.Text, _pinnedOnly);
        EntriesItemsControl.ItemsSource = items.Select(e => new ClipboardItemVm(
            Id: e.Id,
            Text: e.Text,
            PreviewText: e.Text.Replace("\r\n", " ").Replace('\n', ' ').Trim(),
            MetaText: $"{e.Timestamp:HH:mm:ss  •  dd.MM}   •   {e.Text.Length} {LocalizationService.Pick("chars", "симв.")}{(e.IsPinned ? LocalizationService.Pick("   •   Pinned", "   •   Закреплено") : string.Empty)}",
            PinLabel: e.IsPinned ? LocalizationService.Pick("Unpin", "Открепить") : LocalizationService.Pick("Pin", "Закрепить"))).ToList();

        CountBadgeText.Text = items.Count.ToString();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        RefreshList();
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        SearchBox.Focus();
    }

    private void OnTogglePinnedFilterClick(object sender, RoutedEventArgs e)
    {
        _pinnedOnly = !_pinnedOnly;
        FilterPinnedBtn.Content = _pinnedOnly
            ? LocalizationService.Get("Loc_Clip_AllItems")
            : LocalizationService.Get("Loc_Clip_Pinned");
        RefreshList();
    }

    private void OnPinEntryClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
            _clipboardService.TogglePin(id);
    }

    private void OnCopyEntryClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            var entry = _clipboardService.GetSnapshot().FirstOrDefault(x => x.Id == id);
            if (entry is not null && _clipboardService.CopyToClipboard(entry.Text))
            {
                StatusFooterText.Text = LocalizationService.Pick("Copied to clipboard!", "Скопировано в буфер обмена!");
            }
        }
    }

    private void OnDeleteEntryClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
            _clipboardService.Remove(id);
    }

    private void OnClearUnpinnedClick(object sender, RoutedEventArgs e)
    {
        _clipboardService.ClearUnpinned();
        StatusFooterText.Text = LocalizationService.Pick("Unpinned entries cleared.", "Незакреплённые записи очищены.");
    }

    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.TextBoxBase)
        {
            try { DragMove(); } catch { }
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            CloseWithAnimation();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseWithAnimation();

    private void CloseWithAnimation()
    {
        var dur = TimeSpan.FromMilliseconds(160);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(1.0, 0.0, dur) { EasingFunction = ease };
        fade.Completed += (_, _) => Close();
        RootClipGrid.BeginAnimation(OpacityProperty, fade);
        ClipScaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 0.94, dur) { EasingFunction = ease });
        ClipScaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 0.94, dur) { EasingFunction = ease });
        ClipBlurEffect.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, new DoubleAnimation(0.0, 6.0, dur) { EasingFunction = ease });
    }
}
