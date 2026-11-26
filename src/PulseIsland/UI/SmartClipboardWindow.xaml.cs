using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using PulseIsland.Core;
using PulseIsland.Interop;
using PulseIsland.Services;

namespace PulseIsland.UI;

public partial class SmartClipboardWindow : Window
{
    private sealed record ClipboardItemVm(string Id, string Text, string PreviewText, string MetaText, string PinLabel);
    private sealed record ClipboardImageVm(string Id, BitmapSource Thumbnail, string TitleText, string MetaText);

    private readonly SmartClipboardService _clipboardService;
    private readonly SettingsService _settingsService;
    private bool _pinnedOnly;
    private bool _isClosing;

    public event Action<CapturedScreenRegion>? SendImageToAiRequested;

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
            StatusFooterText.Text = LocalizationService.Get("Loc_Clip_FooterHint");
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
        string query = SearchBox.Text ?? string.Empty;
        var items = _clipboardService.Search(query, _pinnedOnly);
        string charsUnit = LocalizationService.Pick("chars", "симв.");
        string pinnedSuffix = LocalizationService.Pick("   •   Pinned", "   •   Закреплено");
        string pinText = LocalizationService.Pick("Pin", "Закрепить");
        string unpinText = LocalizationService.Pick("Unpin", "Открепить");
        EntriesItemsControl.ItemsSource = items.Select(e => new ClipboardItemVm(
            Id: e.Id,
            Text: e.Text,
            PreviewText: e.Text.Replace("\r\n", " ").Replace('\n', ' ').Trim(),
            MetaText: $"{e.Timestamp:HH:mm:ss  •  dd.MM}   •   {e.Text.Length} {charsUnit}{(e.IsPinned ? pinnedSuffix : string.Empty)}",
            PinLabel: e.IsPinned ? unpinText : pinText)).ToList();

        bool showImages = !_pinnedOnly && string.IsNullOrWhiteSpace(query);
        var images = showImages ? _clipboardService.GetImageSnapshot() : Array.Empty<ClipboardImageEntry>();
        var imageVms = new List<ClipboardImageVm>(images.Count);
        string screenshotLabel = LocalizationService.Pick("Screenshot", "Скриншот");
        string inRamLabel = LocalizationService.Pick("In RAM", "В ОЗУ");
        foreach (var img in images)
        {
            var thumb = CreateFrozenThumbnail(img);
            if (thumb is not null)
            {
                imageVms.Add(new ClipboardImageVm(
                    Id: img.Id,
                    Thumbnail: thumb,
                    TitleText: $"{screenshotLabel} • {img.Width} × {img.Height}",
                    MetaText: $"{img.Timestamp:HH:mm:ss  •  dd.MM}   •   {inRamLabel}"));
            }
        }

        ImageEntriesItemsControl.ItemsSource = imageVms;
        ImagesSectionPanel.Visibility = imageVms.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ImagesHeaderText.Text = LocalizationService.Pick(
            $"SCREENSHOTS & PHOTOS ({imageVms.Count})",
            $"СКРИНШОТЫ И ФОТО ({imageVms.Count})");
        CountBadgeText.Text = (items.Count + imageVms.Count).ToString();
    }

    private static BitmapSource? CreateFrozenThumbnail(ClipboardImageEntry entry)
    {
        try
        {
            var bmp = BitmapSource.Create(
                entry.Width,
                entry.Height,
                96.0,
                96.0,
                PixelFormats.Bgra32,
                null,
                entry.Bgra32Pixels,
                entry.Width * 4);

            if (entry.Width > 180 || entry.Height > 120)
            {
                double scale = Math.Min(180.0 / entry.Width, 120.0 / entry.Height);
                var scaled = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
                scaled.Freeze();
                return scaled;
            }

            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
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

    private void OnCopyImageClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } && _clipboardService.CopyImageToClipboard(id))
        {
            StatusFooterText.Text = LocalizationService.Pick("Image copied to clipboard!", "Изображение скопировано в буфер обмена!");
        }
    }

    private void OnAskAiImageClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } && _clipboardService.TryGetImageRegion(id, out var region) && region is not null)
        {
            SendImageToAiRequested?.Invoke(region);
            CloseWithAnimation();
        }
    }

    private void OnDeleteImageClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
            _clipboardService.RemoveImage(id);
    }

    private void OnClearUnpinnedClick(object sender, RoutedEventArgs e)
    {
        _clipboardService.ClearUnpinned();
        StatusFooterText.Text = LocalizationService.Pick(
            "Unpinned entries and snapshots cleared.",
            "Незакреплённые записи и снимки очищены.");
    }

    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && !IsInsideInteractive(e.OriginalSource as DependencyObject))
        {
            try { DragMove(); } catch { }
        }
    }

    private static bool IsInsideInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBoxBase or Thumb or ScrollBar)
                return true;
            source = DwmBackdropService.GetParentSafe(source);
        }
        return false;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            CloseWithAnimation();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseWithAnimation();

    private void CloseWithAnimation()
    {
        if (_isClosing) return;
        _isClosing = true;
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
