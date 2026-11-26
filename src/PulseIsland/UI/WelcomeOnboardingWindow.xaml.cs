using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PulseIsland.Core;
using PulseIsland.Interop;
using PulseIsland.Services;

namespace PulseIsland.UI;

public partial class WelcomeOnboardingWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly AppLanguage _initialLanguage;
    private AppLanguage _selectedLanguage;
    private AppThemeStyle _selectedTheme;
    private int _stepIndex;
    private bool _isClosing;
    private bool _handledExit;

    public bool AppliedChanges { get; private set; }

    private static (AppThemeStyle Style, string ShortName)[] GetThemeChoices() =>
    [
        (AppThemeStyle.Classic, LocalizationService.Pick("Classic", "Классический")),
        (AppThemeStyle.CrimsonBlack, LocalizationService.Pick("Crimson / Black", "Алый / Чёрный")),
        (AppThemeStyle.IndigoAmber, LocalizationService.Pick("Indigo / Amber", "Индиго / Янтарь")),
        (AppThemeStyle.NavyMagenta, LocalizationService.Pick("Navy / Magenta", "Синий / Малина")),
        (AppThemeStyle.GraphiteSand, LocalizationService.Pick("Graphite / Sand", "Графит / Песок")),
        (AppThemeStyle.TealOrange, LocalizationService.Pick("Teal / Orange", "Бирюза / Оранж")),
        (AppThemeStyle.LightWhite, LocalizationService.Pick("Light Glass", "Светлое стекло"))
    ];

    public WelcomeOnboardingWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;

        var s = _settingsService.Current;
        _initialLanguage = LocalizationService.CurrentLanguage;
        _selectedLanguage = LocalizationService.CurrentLanguage;
        _selectedTheme = s.ThemeStyle;
        DwmBlurCheck.IsChecked = s.EnableDwmBackdropBlur;
        ModMusicCheck.IsChecked = s.EnableMusicModule;
        ModAudioLiveCheck.IsChecked = s.EnableVolumeAndMicIndicator;
        ModNotifCheck.IsChecked = s.EnableIslandNotifications;
        ModClipboardCheck.IsChecked = s.EnableSmartClipboard;
        ModMouseCheck.IsChecked = s.EnableMouseBatteryIndicator;
        ModAutoStartCheck.IsChecked = s.AutoStartWithWindows || AutoStartService.IsAutoStartEnabled();

        UpdateLanguageButtons();
        BuildThemeButtons();
        UpdateStepVisuals(animate: false);

        Closed += (_, _) =>
        {
            if (!_handledExit)
            {
                var cur = _settingsService.Current;
                ThemeService.ApplyTheme(cur.ThemeStyle);
                LocalizationService.ApplyLanguage(_initialLanguage);
                if (!cur.HasCompletedOnboarding)
                {
                    cur.HasCompletedOnboarding = true;
                    _settingsService.Save(cur);
                }
            }
        };
    }

    private void UpdateLanguageButtons()
    {
        ApplyLanguageButtonState(LangEnBtn, _selectedLanguage == AppLanguage.English);
        ApplyLanguageButtonState(LangRuBtn, _selectedLanguage == AppLanguage.Russian);
    }

    private static void ApplyLanguageButtonState(Button btn, bool active)
    {
        btn.FontWeight = active ? FontWeights.Bold : FontWeights.SemiBold;
        if (active)
        {
            btn.SetResourceReference(BorderBrushProperty, "ThemeAccentBrush");
            btn.SetResourceReference(BackgroundProperty, "ThemeHighlightBrush");
        }
        else
        {
            btn.SetResourceReference(BorderBrushProperty, "ThemeCardBorderBrush");
            btn.SetResourceReference(BackgroundProperty, "ThemeButtonBgBrush");
        }
    }

    private void OnLanguageChoiceClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string code })
        {
            var chosen = string.Equals(code, "ru", StringComparison.OrdinalIgnoreCase)
                ? AppLanguage.Russian
                : AppLanguage.English;
            if (_selectedLanguage == chosen && LocalizationService.CurrentLanguage == chosen)
                return;

            _selectedLanguage = chosen;
            LocalizationService.ApplyLanguage(chosen);
            UpdateLanguageButtons();
            BuildThemeButtons();
            UpdateStepVisuals(animate: false);
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(260);
        RootGrid.Opacity = 0.0;
        RootGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, dur) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.93, 1.0, dur) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.93, 1.0, dur) { EasingFunction = ease });
        CardBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, new DoubleAnimation(8.0, 0.0, dur) { EasingFunction = ease });

        DwmBackdropService.ApplyToCard(this, MainCard, DwmBlurCheck.IsChecked == true, 26);
    }

    private void BuildThemeButtons()
    {
        ThemesWrapPanel.Children.Clear();
        foreach (var (style, shortName) in GetThemeChoices())
        {
            var palette = ThemeService.GetPalette(style);
            bool active = style == _selectedTheme;

            var btn = new Button
            {
                Tag = style,
                Style = (Style)FindResource("GlassBtnStyle"),
                Padding = new Thickness(11, 7, 11, 7),
                Margin = new Thickness(0, 0, 8, 8)
            };
            if (active)
            {
                btn.SetResourceReference(BorderBrushProperty, "ThemeAccentBrush");
                btn.SetResourceReference(BackgroundProperty, "ThemeHighlightBrush");
            }

            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(6),
                Background = new LinearGradientBrush(palette.AccentPrimary, palette.AccentSecondary, 45.0),
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(new TextBlock
            {
                Text = shortName,
                FontSize = 11.5,
                FontWeight = active ? FontWeights.Bold : FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });

            btn.Content = sp;
            btn.Click += OnThemeChoiceClick;
            ThemesWrapPanel.Children.Add(btn);
        }
    }

    private void OnThemeChoiceClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AppThemeStyle chosen })
        {
            _selectedTheme = chosen;
            ThemeService.ApplyTheme(chosen);
            BuildThemeButtons();
        }
    }

    private void OnDwmBlurToggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        DwmBackdropService.ApplyToCard(this, MainCard, DwmBlurCheck.IsChecked == true, 26);
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_stepIndex > 0)
        {
            _stepIndex--;
            UpdateStepVisuals(animate: true);
        }
    }

    private void OnNextOrFinishClick(object sender, RoutedEventArgs e)
    {
        if (_stepIndex < 2)
        {
            _stepIndex++;
            UpdateStepVisuals(animate: true);
            return;
        }

        SaveOnboardingChoicesAndClose(applyModuleToggles: true);
    }

    private void OnSkipOrCloseClick(object sender, RoutedEventArgs e)
    {
        SaveOnboardingChoicesAndClose(applyModuleToggles: false);
    }

    private void SaveOnboardingChoicesAndClose(bool applyModuleToggles)
    {
        if (_handledExit) return;
        _handledExit = true;
        AppliedChanges = applyModuleToggles;

        var s = _settingsService.Current;
        s.HasCompletedOnboarding = true;

        if (applyModuleToggles)
        {
            s.UiLanguage = _selectedLanguage;
            if (AppSettings.IsBuiltInDefaultPromptSet(s.CustomAiPrompts))
            {
                s.CustomAiPrompts = AppSettings.GetDefaultCustomAiPrompts(_selectedLanguage);
            }
            LocalizationService.ApplyLanguage(_selectedLanguage);
            s.ThemeStyle = _selectedTheme;
            s.EnableDwmBackdropBlur = DwmBlurCheck.IsChecked == true;
            s.EnableMusicModule = ModMusicCheck.IsChecked == true;
            s.EnableVolumeAndMicIndicator = ModAudioLiveCheck.IsChecked == true;
            s.EnableIslandNotifications = ModNotifCheck.IsChecked == true;
            s.EnableSmartClipboard = ModClipboardCheck.IsChecked == true;
            s.EnableMouseBatteryIndicator = ModMouseCheck.IsChecked == true;
            s.AutoStartWithWindows = ModAutoStartCheck.IsChecked == true;
            AutoStartService.SetAutoStart(s.AutoStartWithWindows);
        }
        else
        {
            ThemeService.ApplyTheme(s.ThemeStyle);
            LocalizationService.ApplyLanguage(_initialLanguage);
        }

        _settingsService.Save(s);
        CloseWithAnimation();
    }

    private void UpdateStepVisuals(bool animate)
    {
        Step1Panel.Visibility = _stepIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _stepIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _stepIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

        StepSubtitleText.Text = _stepIndex switch
        {
            0 => LocalizationService.Pick("Step 1 of 3 • Language & Appearance", "Шаг 1 из 3 • Язык и оформление"),
            1 => LocalizationService.Pick("Step 2 of 3 • Alt+Q Translate, Vision & Voice", "Шаг 2 из 3 • Перевод Alt+Q, Vision и голос"),
            _ => LocalizationService.Pick("Step 3 of 3 • Island Modules", "Шаг 3 из 3 • Модули островка")
        };

        BackBtn.Visibility = _stepIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextOrFinishBtn.Content = _stepIndex < 2
            ? LocalizationService.Get("Loc_Onboard_Next")
            : LocalizationService.Get("Loc_Onboard_Finish");

        UpdateDot(Dot1, _stepIndex == 0);
        UpdateDot(Dot2, _stepIndex == 1);
        UpdateDot(Dot3, _stepIndex == 2);

        if (animate)
        {
            StepBodyContainer.Opacity = 0.0;
            StepBodyContainer.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(190))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
    }

    private static void UpdateDot(Border dot, bool active)
    {
        dot.Width = active ? 18 : 7;
        dot.SetResourceReference(Border.BackgroundProperty, active ? "ThemeAccentBrush" : "ThemeCardBorderBrush");
    }

    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && !IsInsideButton(e.OriginalSource as DependencyObject))
        {
            try { DragMove(); } catch { }
        }
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase)
                return true;
            source = DwmBackdropService.GetParentSafe(source);
        }
        return false;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            SaveOnboardingChoicesAndClose(applyModuleToggles: false);
    }

    private void CloseWithAnimation()
    {
        if (_isClosing) return;
        _isClosing = true;
        var dur = TimeSpan.FromMilliseconds(170);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(1.0, 0.0, dur) { EasingFunction = ease };
        fade.Completed += (_, _) => Close();
        RootGrid.BeginAnimation(OpacityProperty, fade);
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 0.94, dur) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 0.94, dur) { EasingFunction = ease });
    }
}
