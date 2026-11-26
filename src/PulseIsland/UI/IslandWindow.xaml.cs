using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using PulseIsland.Audio;
using PulseIsland.Core;
using PulseIsland.Interop;
using PulseIsland.Media;
using PulseIsland.Services;
using PulseIsland.ViewModels;

namespace PulseIsland.UI;

public partial class IslandWindow : Window
{
    /// <summary>
    /// Renders a symmetric center-aligned Dynamic Island audio waveform (cubic Bezier envelope + harmonic capsule bars)
    /// at 60 FPS with pooled WPF elements.
    /// </summary>
    private sealed class WaveCanvasRenderer
    {
        private readonly List<Rectangle> _bars = new();
        private Path? _fillPath;
        private Path? _lowerStrokePath;
        private Path? _strokePath;
        private WaveVisualStyle? _lastStyle;
        private int _lastCount;
        private double _lastWidth;
        private double _phaseShift;

        public void Render(
            Canvas canvas,
            double[] bands,
            AppSettings settings,
            Brush strokeBrush,
            Brush fillBrush,
            bool isSilent = false)
        {
            if (settings.HideWavesOnSilence && isSilent)
            {
                if (canvas.Opacity > 0.02)
                {
                    canvas.Opacity = Math.Max(0.0, canvas.Opacity - 0.08);
                }
                else
                {
                    canvas.Opacity = 0.0;
                    canvas.Visibility = Visibility.Collapsed;
                    return;
                }
            }
            else
            {
                if (canvas.Visibility != Visibility.Visible)
                    canvas.Visibility = Visibility.Visible;
                if (canvas.Opacity < 0.99)
                    canvas.Opacity = Math.Min(1.0, canvas.Opacity + 0.14);
            }

            double width = canvas.ActualWidth;
            double height = canvas.ActualHeight;
            if (width <= 4 || height <= 4 || bands.Length == 0)
                return;

            int count = bands.Length;
            bool needRebuild = _lastStyle != settings.WaveStyle ||
                               _lastCount != count ||
                               Math.Abs(_lastWidth - width) > 0.5;

            _phaseShift = (_phaseShift + 0.09) % (Math.PI * 2.0);
            double gap = Math.Max(1.6, width / (count * 3.1));
            double barWidth = Math.Max(2.0, (width - gap * (count - 1)) / count);
            double centerY = height * 0.5;

            if (settings.WaveStyle == WaveVisualStyle.SmoothCurve && count >= 3)
            {
                if (needRebuild || _strokePath is null || _fillPath is null || _lowerStrokePath is null || _bars.Count != count)
                {
                    canvas.Children.Clear();
                    _bars.Clear();

                    _fillPath = new Path
                    {
                        Fill = fillBrush,
                        Opacity = 0.48
                    };
                    canvas.Children.Add(_fillPath);

                    // Crisp symmetric harmonic capsule bars inside the Bezier wave
                    for (int i = 0; i < count; i++)
                    {
                        var rect = new Rectangle
                        {
                            Width = barWidth,
                            Height = 3.0,
                            RadiusX = barWidth * 0.5,
                            RadiusY = barWidth * 0.5,
                            Fill = strokeBrush,
                            Opacity = 0.72
                        };
                        Canvas.SetLeft(rect, i * (barWidth + gap));
                        Canvas.SetTop(rect, centerY - 1.5);
                        _bars.Add(rect);
                        canvas.Children.Add(rect);
                    }

                    _lowerStrokePath = new Path
                    {
                        Stroke = strokeBrush,
                        StrokeThickness = 1.4,
                        Opacity = 0.55,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    canvas.Children.Add(_lowerStrokePath);

                    _strokePath = new Path
                    {
                        Stroke = strokeBrush,
                        StrokeThickness = 2.0,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    canvas.Children.Add(_strokePath);

                    _lastStyle = settings.WaveStyle;
                    _lastCount = count;
                    _lastWidth = width;
                }
                else
                {
                    if (!ReferenceEquals(_strokePath.Stroke, strokeBrush))
                    {
                        _strokePath.Stroke = strokeBrush;
                        _lowerStrokePath.Stroke = strokeBrush;
                        foreach (var r in _bars) r.Fill = strokeBrush;
                    }
                    if (!ReferenceEquals(_fillPath.Fill, fillBrush))
                    {
                        _fillPath.Fill = fillBrush;
                    }
                }

                RenderSymmetricBezierAndBars(width, height, centerY, bands, settings.HideWavesOnSilence && isSilent);
                return;
            }

            // Capsule Bars mode
            if (needRebuild || _bars.Count != count)
            {
                canvas.Children.Clear();
                _bars.Clear();
                _strokePath = null;
                _fillPath = null;
                _lowerStrokePath = null;

                for (int i = 0; i < count; i++)
                {
                    var rect = new Rectangle
                    {
                        Width = barWidth,
                        Height = 3.0,
                        RadiusX = barWidth * 0.5,
                        RadiusY = barWidth * 0.5,
                        Fill = strokeBrush
                    };
                    Canvas.SetLeft(rect, i * (barWidth + gap));
                    Canvas.SetTop(rect, centerY - 1.5);
                    _bars.Add(rect);
                    canvas.Children.Add(rect);
                }

                _lastStyle = settings.WaveStyle;
                _lastCount = count;
                _lastWidth = width;
            }

            for (int i = 0; i < count; i++)
            {
                var rect = _bars[i];
                if (!ReferenceEquals(rect.Fill, strokeBrush))
                    rect.Fill = strokeBrush;

                double idle = (settings.HideWavesOnSilence && isSilent)
                    ? 0.0
                    : 0.12 + 0.04 * Math.Sin(_phaseShift + i * 0.6);
                double norm = Math.Max(idle, bands[i]);
                double minH = (settings.HideWavesOnSilence && isSilent) ? 1.5 : 3.0;
                double barHeight = Math.Clamp(norm * (height - 2.0), minH, height - 1.0);
                rect.Height = barHeight;
                Canvas.SetTop(rect, centerY - barHeight * 0.5);
            }
        }

        private void RenderSymmetricBezierAndBars(double width, double height, double centerY, double[] bands, bool suppressIdle)
        {
            int count = bands.Length;
            double maxHalfAmp = Math.Max(3.0, centerY - 1.5);

            var upperNodes = new Point[count + 2];
            var lowerNodes = new Point[count + 2];

            upperNodes[0] = new Point(0, centerY);
            lowerNodes[0] = new Point(0, centerY);

            for (int i = 0; i < count; i++)
            {
                double t = (i + 1.0) / (count + 1.0);
                double x = t * width;

                double idleWave = suppressIdle ? 0.0 : 0.12 + 0.04 * Math.Sin(_phaseShift + i * 0.55);
                double minNorm = suppressIdle ? 0.0 : 0.08;
                double norm = Math.Clamp(Math.Max(idleWave, bands[i]), minNorm, 1.0);
                double offset = norm * maxHalfAmp;

                upperNodes[i + 1] = new Point(x, centerY - offset);
                lowerNodes[i + 1] = new Point(x, centerY + offset * 0.85);

                if (i < _bars.Count)
                {
                    var rect = _bars[i];
                    double barH = Math.Clamp(offset * 1.85, 3.0, height - 2.0);
                    rect.Height = barH;
                    Canvas.SetTop(rect, centerY - barH * 0.5);
                }
            }

            upperNodes[count + 1] = new Point(width, centerY);
            lowerNodes[count + 1] = new Point(width, centerY);

            _strokePath!.Data = BuildBezierPathGeometry(upperNodes, closeToBaseline: false, centerY);
            _lowerStrokePath!.Data = BuildBezierPathGeometry(lowerNodes, closeToBaseline: false, centerY);
            _fillPath!.Data = BuildBezierPathGeometry(upperNodes, closeToBaseline: true, centerY);
        }

        private static PathGeometry BuildBezierPathGeometry(Point[] points, bool closeToBaseline, double baselineY)
        {
            int n = points.Length;
            var bezierPoints = new PointCollection((n - 1) * 3);

            for (int i = 0; i < n - 1; i++)
            {
                Point p0 = i > 0 ? points[i - 1] : points[i];
                Point p1 = points[i];
                Point p2 = points[i + 1];
                Point p3 = i + 2 < n ? points[i + 2] : p2;

                const double tension = 0.20;
                Point cp1 = new(
                    p1.X + (p2.X - p0.X) * tension,
                    p1.Y + (p2.Y - p0.Y) * tension);
                Point cp2 = new(
                    p2.X - (p3.X - p1.X) * tension,
                    p2.Y - (p3.Y - p1.Y) * tension);

                bezierPoints.Add(cp1);
                bezierPoints.Add(cp2);
                bezierPoints.Add(p2);
            }

            var figure = new PathFigure
            {
                StartPoint = points[0],
                IsClosed = closeToBaseline,
                IsFilled = closeToBaseline
            };
            figure.Segments.Add(new PolyBezierSegment(bezierPoints, isStroked: true));

            if (closeToBaseline)
            {
                figure.Segments.Add(new LineSegment(new Point(points[^1].X, baselineY), isStroked: false));
                figure.Segments.Add(new LineSegment(new Point(points[0].X, baselineY), isStroked: false));
            }

            var geom = new PathGeometry();
            geom.Figures.Add(figure);
            return geom;
        }
    }

    private readonly IslandViewModel _vm;
    private readonly SettingsService _settingsService;
    private readonly IMediaSessionService _mediaService;
    private readonly WasapiAudioCaptureService _audioCapture;
    private readonly FftSpectrumProcessor _fftProcessor;
    private readonly AudioEndpointMonitorService? _audioEndpointMonitor;
    private readonly SmartClipboardService? _smartClipboard;
    private readonly IslandNotificationService _notificationService;
    private readonly WindowsNotificationListener _notificationListener;
    private readonly WindowsToastSuppressor _toastSuppressor = new();
    private readonly CornerPopupInterceptor _cornerPopupInterceptor;
    private readonly DispatcherTimer _notifTimeAgoTimer;
    private readonly DispatcherTimer _dragLeaveTimer;

    private readonly DispatcherTimer _autoHideTimer;
    private readonly DispatcherTimer _pauseMusicHideTimer;
    private readonly DispatcherTimer _topmostAndFullscreenTimer;
    private readonly DispatcherTimer _statusClearTimer;
    private readonly DispatcherTimer _volumeHudTimer;
    private readonly DispatcherTimer _timelineTickTimer;

    private readonly WaveCanvasRenderer _compactWaveRenderer = new();
    private readonly WaveCanvasRenderer _expandedWaveRenderer = new();
    private readonly AudioSessionMusicFilter _musicSessionFilter = new();
    private readonly SystemMonitorService _systemMonitor = new();
    private readonly MouseBatteryService _mouseBatteryService;
    private readonly DispatcherTimer _mouseHotplugTimer;
    private bool _mouseLowBatteryAlertSent;
    private bool _isMouseBoltBlinking;
    private string _lastAnimatedTrackKey = string.Empty;
    private Brush? _cachedStrokeBrush;
    private Brush? _cachedFillBrush;
    private string _cachedBrushKey = string.Empty;

    private readonly float[] _sampleBuffer = new float[1024];
    private double[] _bandValues = new double[18];

    private double _marqueeOffset;
    private bool _isPointerInside;
    private bool _isHiddenByFullscreen;
    private bool _isTemporarilyHiddenForCapture;
    private long _manualHideCooldownUntilMs;
    private IntPtr _ownHwnd;
    private HwndSource? _hwndSource;
    private Point? _dragStartPoint;
    private string? _dragFilePath;
    private int _accordionAnimGen;
    private bool _allowClose;

    public event Action? TranslationRequested;
    public event Action? SettingsRequested;
    public event Action? SmartClipboardRequested;
    public event Action<string>? DropImageAskAiRequested;

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    internal MouseBatteryService MouseBatteryService => _mouseBatteryService;

    public IslandWindow(
        IslandViewModel viewModel,
        SettingsService settingsService,
        IMediaSessionService mediaService,
        WasapiAudioCaptureService audioCapture,
        FftSpectrumProcessor fftProcessor,
        AudioEndpointMonitorService? audioEndpointMonitor = null,
        SmartClipboardService? smartClipboard = null)
    {
        InitializeComponent();

        _vm = viewModel;
        _settingsService = settingsService;
        _mediaService = mediaService;
        _audioCapture = audioCapture;
        _fftProcessor = fftProcessor;
        _audioEndpointMonitor = audioEndpointMonitor;
        _smartClipboard = smartClipboard;
        _mouseBatteryService = new MouseBatteryService(
            () => _settingsService.Current.EnableMouseBatteryIndicator,
            () => _settingsService.Current.MouseConnectionMode);
        _mouseHotplugTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        _mouseHotplugTimer.Tick += (_, _) =>
        {
            _mouseHotplugTimer.Stop();
            if (_settingsService.Current.EnableMouseBatteryIndicator)
                _mouseBatteryService.PollNow();
        };
        DataContext = _vm;

        // Notification service
        _notificationService = new IslandNotificationService(Dispatcher);
        _notificationListener = new WindowsNotificationListener(
            _notificationService,
            () => _settingsService.Current.NotificationAutoDismissSeconds,
            () => _settingsService.Current.EnableIslandNotifications && _settingsService.Current.SuppressWindowsToastWhenIslandActive);
        _cornerPopupInterceptor = new CornerPopupInterceptor(
            _notificationService,
            () => _settingsService.Current.EnableIslandNotifications && _settingsService.Current.SuppressWindowsToastWhenIslandActive,
            () => _settingsService.Current.NotificationAutoDismissSeconds);
        _notificationService.NotificationArrived += OnIslandNotificationArrived;
        _notificationService.NotificationDismissed += OnIslandNotificationDismissed;
        _notificationService.HistorySeeded += OnIslandNotificationHistorySeeded;

        _dragLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _dragLeaveTimer.Tick += (_, _) =>
        {
            _dragLeaveTimer.Stop();
            if (_vm.IsDragOverFileDropZone)
            {
                _vm.IsDragOverFileDropZone = false;
                if (_vm.State == IslandState.Expanded)
                    ApplyStateVisuals(animate: true, stateChanged: false);
            }
        };

        // Refresh "X мин назад" labels every 30 s when history list is open
        _notifTimeAgoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _notifTimeAgoTimer.Tick += (_, _) =>
        {
            if (_vm.IsNotificationHistoryVisible && _vm.HasNotificationHistory && _vm.State == IslandState.Expanded)
            {
                OnPropertyChanged_NotifHistory();
            }
        };
        _notifTimeAgoTimer.Start();

        _autoHideTimer = new DispatcherTimer();
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer.Stop();
            if (!_isPointerInside && _settingsService.Current.AutoHideIsland && _vm.State == IslandState.Compact)
            {
                SetIslandState(IslandState.Hidden);
            }
        };

        _pauseMusicHideTimer = new DispatcherTimer();
        _pauseMusicHideTimer.Tick += (_, _) =>
        {
            _pauseMusicHideTimer.Stop();
            if (!_vm.TrackInfo.IsPlaying)
            {
                _vm.ShowMusicInCompact = false;
                ApplyStateVisuals(animate: true);
            }
        };

        // Guards Topmost Z-Order (including against Win+D Show Desktop) & checks true fullscreen games
        _topmostAndFullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _topmostAndFullscreenTimer.Tick += (_, _) => MaintainTopmostAndFullscreenState();

        _statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _statusClearTimer.Tick += (_, _) =>
        {
            _statusClearTimer.Stop();
            if (!_vm.IsTranslating && !string.IsNullOrEmpty(_vm.StatusMessage))
            {
                _vm.StatusMessage = string.Empty;
            }
        };

        _volumeHudTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        _volumeHudTimer.Tick += (_, _) =>
        {
            _volumeHudTimer.Stop();
            _vm.IsVolumeHudActive = false;
        };

        _timelineTickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timelineTickTimer.Tick += (_, _) =>
        {
            if (_vm.TrackInfo.IsPlaying && _vm.TrackInfo.DurationSeconds > 1.0 &&
                _vm.TrackInfo.PositionSeconds < _vm.TrackInfo.DurationSeconds)
            {
                _vm.TrackInfo = _vm.TrackInfo with
                {
                    PositionSeconds = Math.Min(_vm.TrackInfo.DurationSeconds, _vm.TrackInfo.PositionSeconds + 1.0)
                };
                UpdateTrackProgressBarWidth();
            }
        };
        _timelineTickTimer.Start();

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.FilteredTranslationHistory.CollectionChanged += OnFilteredTranslationsChanged;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnWindowClosing;
        Closed += OnClosed;
        StateChanged += OnWindowStateChanged;
        DpiChanged += (_, _) => PositionOnTargetMonitor();

        _mediaService.TrackChanged += OnTrackChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
        ThemeService.ThemeChanged += OnThemeChanged;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        if (_audioEndpointMonitor is not null)
        {
            _vm.AudioState = _audioEndpointMonitor.CurrentState;
            _audioEndpointMonitor.StateChanged += OnAudioEndpointStateChanged;
        }
    }

    public void SetTemporaryCaptureHide(bool hideForCapture)
    {
        _isTemporarilyHiddenForCapture = hideForCapture;
        if (hideForCapture)
        {
            Opacity = 0;
            Hide();
        }
        else
        {
            Opacity = 1;
            Show();
            EnsureTopmostZOrder();
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
            EnsureTopmostZOrder();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _ownHwnd = new WindowInteropHelper(this).Handle;
        int exStyle = Win32Api.GetWindowLong(_ownHwnd, Win32Api.GWL_EXSTYLE);
        Win32Api.SetWindowLong(_ownHwnd, Win32Api.GWL_EXSTYLE,
            exStyle | Win32Api.WS_EX_TOOLWINDOW | Win32Api.WS_EX_TOPMOST | Win32Api.WS_EX_NOACTIVATE);

        _hwndSource = HwndSource.FromHwnd(_ownHwnd);
        _hwndSource?.AddHook(WndProc);
        _smartClipboard?.Attach(_ownHwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DEVICECHANGE = 0x0219;
        if (msg == SmartClipboardService.WM_CLIPBOARDUPDATE)
        {
            _smartClipboard?.HandleWndProc(msg, _settingsService.Current.EnableSmartClipboard);
        }
        else if (msg == WM_DEVICECHANGE && _settingsService.Current.EnableMouseBatteryIndicator)
        {
            _mouseHotplugTimer.Stop();
            _mouseHotplugTimer.Start();
        }
        else if (msg == Win32Api.WM_WINDOWPOSCHANGING && lParam != IntPtr.Zero)
        {
            if (!_isHiddenByFullscreen && !_isTemporarilyHiddenForCapture)
            {
                var wp = Marshal.PtrToStructure<Win32Api.WINDOWPOS>(lParam);
                // Block Windows Show Desktop (Win+D) from hiding or dropping Z-order of the island
                wp.flags &= ~Win32Api.SWP_HIDEWINDOW;
                wp.hwndInsertAfter = Win32Api.HWND_TOPMOST;
                Marshal.StructureToPtr(wp, lParam, fDeleteOld: false);
            }
        }
        else if (msg == Win32Api.WM_SHOWWINDOW && wParam == IntPtr.Zero)
        {
            if (!_isHiddenByFullscreen && !_isTemporarilyHiddenForCapture)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
                {
                    if (!_isHiddenByFullscreen && !_isTemporarilyHiddenForCapture)
                    {
                        if (!IsVisible) Show();
                        EnsureTopmostZOrder();
                    }
                });
            }
        }

        return IntPtr.Zero;
    }

    private void EnsureTopmostZOrder()
    {
        if (_ownHwnd == IntPtr.Zero || _isHiddenByFullscreen || _isTemporarilyHiddenForCapture)
            return;

        Win32Api.SetWindowPos(
            _ownHwnd,
            Win32Api.HWND_TOPMOST,
            0, 0, 0, 0,
            Win32Api.SWP_NOMOVE | Win32Api.SWP_NOSIZE | Win32Api.SWP_NOACTIVATE | Win32Api.SWP_SHOWWINDOW);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IslandViewModel.StatusMessage))
        {
            _statusClearTimer.Stop();
            if (!string.IsNullOrWhiteSpace(_vm.StatusMessage) && !_vm.IsTranslating)
            {
                _statusClearTimer.Start();
            }

            if (_vm.State == IslandState.Expanded)
            {
                ApplyStateVisuals(animate: true, stateChanged: false);
            }
        }
        else if (e.PropertyName is nameof(IslandViewModel.HasDroppedFiles)
                 or nameof(IslandViewModel.IsFileDropZoneVisible)
                 or nameof(IslandViewModel.IsProgressBarVisible)
                 or nameof(IslandViewModel.HasActiveNotification)
                 or nameof(IslandViewModel.HasTranslationHistory)
                 or nameof(IslandViewModel.HasNotificationHistory)
                 or nameof(IslandViewModel.TranslationSearchQuery)
                 or nameof(IslandViewModel.ShowEmptyTranslationSearchHint))
        {
            DropShelfEmptyHint.Visibility = _vm.HasDroppedFiles ? Visibility.Collapsed : Visibility.Visible;
            if (_vm.State == IslandState.Expanded)
            {
                ApplyStateVisuals(animate: true, stateChanged: false);
            }
        }
        else if (e.PropertyName == nameof(IslandViewModel.IsAiGenerating))
        {
            if (_vm.IsAiGenerating)
                StartAiThinkingAnimation();
            else
                StopAiThinkingAnimation();
        }
    }

    private void OnThemeChanged(AppThemeStyle style)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _cachedBrushKey = string.Empty;
        });
    }

    private void OnLanguageChanged(AppLanguage _)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _vm.RefreshLocalizedProperties();
            if (_vm.HasNotificationHistory)
                OnPropertyChanged_NotifHistory();
            if (_vm.State == IslandState.Expanded)
                ApplyStateVisuals(animate: false, stateChanged: false);
        });
    }

    private void OnAudioEndpointStateChanged(AudioEndpointState state, bool showHudToast)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var prev = _vm.AudioState;
            _vm.AudioState = state;

            if (!showHudToast || !_settingsService.Current.EnableVolumeAndMicIndicator)
                return;

            bool micToggled = state.IsMicMuted != prev.IsMicMuted;
            _vm.VolumeHudTitle = micToggled
                ? (state.IsMicMuted
                    ? LocalizationService.Pick("Mic muted", "Мик. выключен")
                    : LocalizationService.Pick("Mic active", "Мик. включён"))
                : (state.IsSpeakerMuted
                    ? LocalizationService.Pick("Audio muted", "Звук выключен")
                    : LocalizationService.Pick($"Volume {state.MasterVolumePercent}%", $"Громкость {state.MasterVolumePercent}%"));
            _vm.VolumeHudFraction = micToggled
                ? (state.IsMicMuted ? 0.0 : 1.0)
                : (state.IsSpeakerMuted ? 0.0 : state.MasterVolumePercent / 100.0);

            _vm.IsVolumeHudActive = true;
            CompactVolumeFill.Width = Math.Clamp(_vm.VolumeHudFraction * 180.0, 0.0, 180.0);

            _volumeHudTimer.Stop();
            _volumeHudTimer.Start();
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionOnTargetMonitor();
        ApplySettings(_settingsService.Current);
        SetIslandState(IslandState.Compact, animate: false);

        _musicSessionFilter.Start();
        _systemMonitor.StatsUpdated += OnSystemStatsUpdated;
        _vm.UpdateSystemLoad(_systemMonitor.SampleNow());
        _mouseBatteryService.BatteryUpdated += OnMouseBatteryUpdated;
        if (_settingsService.Current.EnableMouseBatteryIndicator)
            _ = Task.Run(() => _mouseBatteryService.PollNow());
        CompositionTarget.Rendering += OnRenderFrame;
        _topmostAndFullscreenTimer.Start();
        RestartAutoHideTimer();

        // Start notification listener after window is ready
        _notificationListener.Start(Dispatcher);
        _cornerPopupInterceptor.Start(Dispatcher);
        _vm.IsNotificationsEnabled = _settingsService.Current.EnableIslandNotifications;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            RestoreNoActivateStyle();
            SetIslandState(IslandState.Compact, animate: true);
        }
    }

    private void OnFilteredTranslationsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_vm.State == IslandState.Expanded && _vm.IsTranslationSectionVisible)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_vm.State == IslandState.Expanded)
                    ApplyStateVisuals(animate: true, stateChanged: false);
            });
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRenderFrame;
        _vm.FilteredTranslationHistory.CollectionChanged -= OnFilteredTranslationsChanged;
        _systemMonitor.StatsUpdated -= OnSystemStatsUpdated;
        _systemMonitor.Dispose();
        _mouseBatteryService.BatteryUpdated -= OnMouseBatteryUpdated;
        _mouseBatteryService.Dispose();
        _mouseHotplugTimer.Stop();
        _musicSessionFilter.Dispose();
        _hwndSource?.RemoveHook(WndProc);
        _autoHideTimer.Stop();
        _pauseMusicHideTimer.Stop();
        _topmostAndFullscreenTimer.Stop();
        _statusClearTimer.Stop();
        _volumeHudTimer.Stop();
        _timelineTickTimer.Stop();
        _notifTimeAgoTimer.Stop();
        _dragLeaveTimer.Stop();
        _notificationService.NotificationArrived -= OnIslandNotificationArrived;
        _notificationService.NotificationDismissed -= OnIslandNotificationDismissed;
        _notificationService.HistorySeeded -= OnIslandNotificationHistorySeeded;
        _notificationListener.Dispose();
        _cornerPopupInterceptor.Dispose();
        _notificationService.Dispose();
        _toastSuppressor.Dispose();
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _mediaService.TrackChanged -= OnTrackChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        ThemeService.ThemeChanged -= OnThemeChanged;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        if (_audioEndpointMonitor is not null)
            _audioEndpointMonitor.StateChanged -= OnAudioEndpointStateChanged;
    }

    public void PositionOnTargetMonitor()
    {
        var monitor = MonitorService.GetTargetMonitor(_settingsService.Current.MonitorIndex, this);

        if (_ownHwnd != IntPtr.Zero)
        {
            int physWidth = (int)Math.Round(Width * monitor.DpiScaleX);
            int physHeight = (int)Math.Round(Height * monitor.DpiScaleY);
            int physLeft = (int)Math.Round(monitor.PhysicalBounds.Left + (monitor.PhysicalBounds.Width - physWidth) * 0.5);
            int physTop = (int)Math.Round(monitor.PhysicalBounds.Top);

            Win32Api.SetWindowPos(
                _ownHwnd,
                Win32Api.HWND_TOPMOST,
                physLeft,
                physTop,
                physWidth,
                physHeight,
                Win32Api.SWP_NOACTIVATE | Win32Api.SWP_SHOWWINDOW);
        }
        else
        {
            var dip = monitor.DipBounds;
            Left = dip.Left + (dip.Width - Width) * 0.5;
            Top = dip.Top;
        }
    }

    public void SetIslandState(IslandState nextState, bool animate = true)
    {
        if (nextState != IslandState.Expanded)
            RestoreNoActivateStyle();

        bool stateChanged = _vm.State != nextState;
        _vm.State = nextState;
        ApplyStateVisuals(animate, stateChanged);

        if (nextState == IslandState.Compact)
            RestartAutoHideTimer();
        else
            _autoHideTimer.Stop();
    }

    private void EnableInteractiveKeyboardFocus()
    {
        if (_ownHwnd == IntPtr.Zero) return;
        int exStyle = Win32Api.GetWindowLong(_ownHwnd, Win32Api.GWL_EXSTYLE);
        if ((exStyle & Win32Api.WS_EX_NOACTIVATE) != 0)
        {
            Win32Api.SetWindowLong(_ownHwnd, Win32Api.GWL_EXSTYLE, exStyle & ~Win32Api.WS_EX_NOACTIVATE);
        }
        try { Activate(); } catch { }
    }

    private void RestoreNoActivateStyle()
    {
        if (_ownHwnd == IntPtr.Zero) return;
        int exStyle = Win32Api.GetWindowLong(_ownHwnd, Win32Api.GWL_EXSTYLE);
        if ((exStyle & Win32Api.WS_EX_NOACTIVATE) == 0)
        {
            Win32Api.SetWindowLong(_ownHwnd, Win32Api.GWL_EXSTYLE, exStyle | Win32Api.WS_EX_NOACTIVATE);
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        PositionOnTargetMonitor();
        TopHoverTrigger.Height = Math.Clamp(settings.TopHoverTriggerHeightPx, 2, 24);
        CompactCoverBorder.Visibility = settings.ShowAlbumArtInCompact ? Visibility.Visible : Visibility.Collapsed;
        _cachedBrushKey = string.Empty;

        _vm.ShowTrackProgressBar = settings.ShowTrackProgressBar;
        _vm.IsSmartClipboardEnabled = settings.EnableSmartClipboard;
        _vm.IsVolumeAndMicEnabled = settings.EnableVolumeAndMicIndicator;
        _vm.IsFileDropZoneEnabled = settings.EnableFileDropZone;
        _vm.IsAiAssistantEnabled = settings.EnableLocalAiAssistant;
        _vm.ConfigureSystemIndicators(settings);
        if (!settings.EnableMouseBatteryIndicator)
        {
            _mouseLowBatteryAlertSent = false;
            UpdateMouseChargingBlink(false);
        }
        else
        {
            _mouseLowBatteryAlertSent = false;
            _mouseBatteryService.PollNow();
        }
        _vm.IsNotificationsEnabled = settings.EnableIslandNotifications;
        _toastSuppressor.SetSuppression(settings.EnableIslandNotifications && settings.SuppressWindowsToastWhenIslandActive);
        DropShelfEmptyHint.Visibility = _vm.HasDroppedFiles ? Visibility.Collapsed : Visibility.Visible;

        IslandBackgroundLayer.Opacity = settings.UseCustomIslandAppearance
            ? Math.Clamp(settings.IslandBackgroundOpacity, 0.45, 1.0)
            : (settings.EnableDwmBackdropBlur ? 0.56 : 1.0);

        int bands = Math.Clamp(settings.WaveBarCount, 6, 32);
        if (_bandValues.Length != bands)
            _bandValues = new double[bands];

        AnimateAdaptiveGlow(_vm.TrackInfo.AccentColor, settings);
        UpdateTrackProgressBarWidth();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        Dispatcher.InvokeAsync(() =>
        {
            ApplySettings(settings);
            if (!settings.AutoHideIsland && _vm.State == IslandState.Hidden)
            {
                SetIslandState(IslandState.Compact, animate: true);
                return;
            }

            RestartAutoHideTimer();
            ApplyStateVisuals(animate: true, stateChanged: false);
        });
    }

    private void OnSystemStatsUpdated(SystemLoadSnapshot snapshot)
    {
        Dispatcher.InvokeAsync(() => _vm.UpdateSystemLoad(snapshot));
    }

    private void OnMouseBatteryUpdated(MouseBatterySnapshot snapshot)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _vm.UpdateMouseBattery(snapshot);

            var s = _settingsService.Current;
            bool shouldBlink = s.EnableMouseBatteryIndicator && snapshot.HasDevice && snapshot.IsCharging;
            UpdateMouseChargingBlink(shouldBlink);

            if (s.EnableMouseBatteryIndicator && s.EnableMouseLowBatterySound && snapshot.HasDevice)
            {
                int threshold = Math.Clamp(s.MouseLowBatteryThresholdPercent, 5, 50);
                if (!snapshot.IsCharging && snapshot.Percent <= threshold)
                {
                    if (!_mouseLowBatteryAlertSent)
                    {
                        _mouseLowBatteryAlertSent = true;
                        NotificationSoundService.Play(s.MouseLowBatterySound);
                        _vm.StatusMessage = LocalizationService.Pick(
                            $"Low mouse battery: {snapshot.Percent}%",
                            $"Низкий заряд мыши: {snapshot.Percent}%");
                    }
                }
                else if (snapshot.IsCharging || snapshot.Percent >= threshold + 5)
                {
                    _mouseLowBatteryAlertSent = false;
                }
            }
            else
            {
                _mouseLowBatteryAlertSent = false;
            }
        });
    }

    private void UpdateMouseChargingBlink(bool shouldBlink)
    {
        if (_isMouseBoltBlinking == shouldBlink)
            return;

        _isMouseBoltBlinking = shouldBlink;
        if (shouldBlink)
        {
            var blink = new DoubleAnimation(1.0, 0.18, TimeSpan.FromMilliseconds(550))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            CompactMouseBolt?.BeginAnimation(OpacityProperty, blink);
            ExpandedMouseBolt?.BeginAnimation(OpacityProperty, blink);
        }
        else
        {
            CompactMouseBolt?.BeginAnimation(OpacityProperty, null);
            if (CompactMouseBolt is not null) CompactMouseBolt.Opacity = 1.0;
            ExpandedMouseBolt?.BeginAnimation(OpacityProperty, null);
            if (ExpandedMouseBolt is not null) ExpandedMouseBolt.Opacity = 1.0;
        }
    }

    private void OnTrackChanged(MediaTrackInfo track)
    {
        Dispatcher.InvokeAsync(() =>
        {
            string trackKey = $"{track.Artist}|{track.Title}";
            bool isNewTrack = track.HasActiveSession &&
                              !string.IsNullOrWhiteSpace(track.Title) &&
                              !string.Equals(trackKey, _lastAnimatedTrackKey, StringComparison.Ordinal);

            _vm.TrackInfo = track;
            _musicSessionFilter.ActiveSourceAppId = track.SourceAppId;
            CompactCoverImage.Source = track.Thumbnail;
            ExpandedCoverImage.Source = track.Thumbnail;
            _cachedBrushKey = string.Empty;

            if (isNewTrack)
            {
                _marqueeOffset = 0;
                _lastAnimatedTrackKey = trackKey;
                AnimateTrackCrossfade();
            }

            var s = _settingsService.Current;
            AnimateAdaptiveGlow(track.AccentColor, s);
            UpdateTrackProgressBarWidth();

            if (!s.EnableMusicModule || !track.HasActiveSession)
            {
                _pauseMusicHideTimer.Stop();
                _vm.ShowMusicInCompact = false;
            }
            else if (track.IsPlaying)
            {
                _pauseMusicHideTimer.Stop();
                _vm.ShowMusicInCompact = true;
            }
            else if (s.HideMusicOnPause)
            {
                _pauseMusicHideTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(s.HideMusicOnPauseDelaySeconds, 1, 120));
                _pauseMusicHideTimer.Start();
            }
            else
            {
                _vm.ShowMusicInCompact = true;
            }

            ApplyStateVisuals(animate: true, stateChanged: false);
        });
    }

    private void AnimateAdaptiveGlow(Color trackAccent, AppSettings settings)
    {
        // Subtle ambient tint from album cover (controlled by EnableAlbumArtGlow toggle in settings)
        if (settings.EnableAlbumArtGlow && _vm.HasTrackTitle)
        {
            Color subtleTint = Color.FromArgb(0x40, trackAccent.R, trackAccent.G, trackAccent.B);
            var dur = TimeSpan.FromMilliseconds(550);
            InnerGlowStop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(subtleTint, dur));
            AlbumArtGlowEffect.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(trackAccent, dur));
            AlbumArtGlowEffect.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0.72, dur));
        }
        else
        {
            var dur = TimeSpan.FromMilliseconds(350);
            InnerGlowStop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF), dur));
            AlbumArtGlowEffect.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0.0, dur));
        }
    }

    private void UpdateTrackProgressBarWidth()
    {
        if (TrackSeekBarContainer is null || TrackProgressFill is null)
            return;

        double totalW = TrackSeekBarContainer.ActualWidth;
        if (totalW <= 4) totalW = 240;
        TrackProgressFill.Width = Math.Clamp(_vm.TrackProgressFraction * totalW, 0.0, totalW);
    }

    private void ApplyStateVisuals(bool animate, bool stateChanged = true)
    {
        var settings = _settingsService.Current;
        double targetWidth;
        double targetHeight;
        double targetRadius;
        double targetY;

        switch (_vm.State)
        {
            case IslandState.Hidden:
                targetWidth = 156;
                targetHeight = 34;
                targetRadius = 17;
                targetY = -29;
                CompactContent.Visibility = Visibility.Visible;
                ExpandedContent.Visibility = Visibility.Collapsed;
                break;

            case IslandState.Compact:
                targetWidth = 376;
                targetHeight = 42;
                targetRadius = settings.UseCustomIslandAppearance
                    ? Math.Clamp(settings.IslandCornerRadius, 10, 21)
                    : 21;
                targetY = 4;
                CompactContent.Visibility = Visibility.Visible;
                ExpandedContent.Visibility = Visibility.Collapsed;
                break;

            case IslandState.Expanded:
                targetWidth = 516;
                CompactContent.Visibility = Visibility.Collapsed;
                ExpandedContent.Visibility = Visibility.Visible;
                targetHeight = CalculateExpandedHeight();
                targetRadius = settings.UseCustomIslandAppearance
                    ? Math.Clamp(settings.IslandCornerRadius, 12, 32)
                    : 28;
                targetY = 6;
                break;

            default:
                return;
        }

        void RefreshBackdrop()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                DwmBackdropService.ApplyToElement(
                    this,
                    IslandPill,
                    settings.EnableDwmBackdropBlur && _vm.State != IslandState.Hidden,
                    targetRadius);
                UpdateTrackProgressBarWidth();
            });
        }

        if (!animate)
        {
            IslandPill.Width = targetWidth;
            IslandPill.Height = targetHeight;
            IslandPill.CornerRadius = new CornerRadius(targetRadius);
            OuterNeonAuraBorder.Width = targetWidth + 4;
            OuterNeonAuraBorder.Height = targetHeight + 4;
            OuterNeonAuraBorder.CornerRadius = new CornerRadius(targetRadius + 2);
            IslandTranslate.Y = targetY;

            CompactContent.Opacity = 1;
            CompactScaleTransform.ScaleX = 1;
            CompactScaleTransform.ScaleY = 1;
            CompactBlurEffect.Radius = 0;

            ExpandedContent.Opacity = _vm.State == IslandState.Expanded ? 1 : 0;
            ExpandedScaleTransform.ScaleX = 1;
            ExpandedScaleTransform.ScaleY = 1;
            ExpandedBlurEffect.Radius = 0;
            RefreshBackdrop();
            return;
        }

        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.20 };
        var duration = TimeSpan.FromMilliseconds(360);

        var widthAnim = new DoubleAnimation(targetWidth, duration) { EasingFunction = spring };
        widthAnim.Completed += (_, _) => RefreshBackdrop();
        IslandPill.BeginAnimation(WidthProperty, widthAnim);
        IslandPill.BeginAnimation(HeightProperty, new DoubleAnimation(targetHeight, duration) { EasingFunction = spring });
        OuterNeonAuraBorder.BeginAnimation(WidthProperty, new DoubleAnimation(targetWidth + 4, duration) { EasingFunction = spring });
        OuterNeonAuraBorder.BeginAnimation(HeightProperty, new DoubleAnimation(targetHeight + 4, duration) { EasingFunction = spring });
        IslandTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(targetY, duration) { EasingFunction = easeOut });

        IslandPill.CornerRadius = new CornerRadius(targetRadius);
        OuterNeonAuraBorder.CornerRadius = new CornerRadius(targetRadius + 2);

        if (!stateChanged)
            return;

        // Content Materialization Morphing (Blur-in: Opacity 0->1, Scale 0.92->1.0, BlurRadius 8->0)
        if (_vm.State == IslandState.Expanded)
        {
            var matDur = TimeSpan.FromMilliseconds(340);
            ExpandedContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, matDur) { EasingFunction = easeOut });
            ExpandedScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1.0, matDur) { EasingFunction = spring });
            ExpandedScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1.0, matDur) { EasingFunction = spring });
            ExpandedBlurEffect.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(8.0, 0.0, matDur) { EasingFunction = easeOut });
        }
        else if (_vm.State == IslandState.Compact)
        {
            var matDur = TimeSpan.FromMilliseconds(300);
            CompactContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, matDur) { EasingFunction = easeOut });
            CompactScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1.0, matDur) { EasingFunction = spring });
            CompactScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1.0, matDur) { EasingFunction = spring });
            CompactBlurEffect.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(8.0, 0.0, matDur) { EasingFunction = easeOut });
        }
    }

    private static void AnimateSectionBlurIn(UIElement element, ScaleTransform scale, BlurEffect blur)
    {
        var dur = TimeSpan.FromMilliseconds(280);
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 };

        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, dur) { EasingFunction = easeOut });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1.0, dur) { EasingFunction = spring });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1.0, dur) { EasingFunction = spring });
        blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(8.0, 0.0, dur) { EasingFunction = easeOut });
    }

    private double CalculateExpandedHeight()
    {
        var monitor = MonitorService.GetTargetMonitor(_settingsService.Current.MonitorIndex, this);
        double maxAllowed = Math.Clamp(monitor.DipBounds.Height - 40, 580, 780);

        ExpandedContent.InvalidateMeasure();
        ExpandedContent.Measure(new Size(516, double.PositiveInfinity));
        double measured = Math.Ceiling(ExpandedContent.DesiredSize.Height) + 2;
        if (measured > 40)
            return Math.Clamp(measured, 150, maxAllowed);

        double height = 78;
        if (_vm.HasStatusMessage) height += 34;
        height += _vm.IsMusicSectionVisible ? (_vm.IsProgressBarVisible ? 146 : 122) : 52;
        height += _vm.IsTranslationSectionVisible ? 168 : 54;
        if (_vm.IsFileDropZoneVisible)
        {
            height += _vm.HasDroppedFiles ? 94 : 88;
        }
        return Math.Clamp(height, 165, maxAllowed);
    }

    private void MaintainTopmostAndFullscreenState()
    {
        if (_isTemporarilyHiddenForCapture)
            return;

        if (MonitorService.IsDesktopActive())
        {
            if (_isHiddenByFullscreen)
                _isHiddenByFullscreen = false;
            if (!IsVisible)
                Show();
            EnsureTopmostZOrder();
            return;
        }

        if (!_settingsService.Current.HideInFullscreenApps)
        {
            if (_isHiddenByFullscreen)
            {
                _isHiddenByFullscreen = false;
                Show();
                EnsureTopmostZOrder();
            }
            return;
        }

        var monitor = MonitorService.GetTargetMonitor(_settingsService.Current.MonitorIndex, this);
        bool isFs = MonitorService.IsForegroundWindowFullscreen(monitor, _ownHwnd);

        if (isFs && !_isHiddenByFullscreen)
        {
            _isHiddenByFullscreen = true;
            Hide();
        }
        else if (!isFs && _isHiddenByFullscreen)
        {
            _isHiddenByFullscreen = false;
            Show();
            EnsureTopmostZOrder();
        }
    }

    private void RestartAutoHideTimer()
    {
        _autoHideTimer.Stop();
        if (_settingsService.Current.AutoHideIsland && _vm.State == IslandState.Compact)
        {
            _autoHideTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settingsService.Current.AutoHideDelayMs, 1500, 30000));
            _autoHideTimer.Start();
        }
    }

    private void OnRenderFrame(object? sender, EventArgs e)
    {
        if (!IsVisible || _vm.State == IslandState.Hidden)
            return;

        var settings = _settingsService.Current;

        // 1. Update Marquee if text exceeds clip width
        if (_vm.State == IslandState.Compact && settings.EnableMarqueeInCompact)
        {
            double textWidth = CompactTrackText.ActualWidth;
            double boxWidth = MarqueeClipCanvas.ActualWidth;
            if (textWidth > boxWidth && boxWidth > 10)
            {
                _marqueeOffset -= 0.55;
                if (_marqueeOffset < -(textWidth + 24))
                    _marqueeOffset = boxWidth;
                MarqueeTransform.X = _marqueeOffset;
            }
            else
            {
                _marqueeOffset = 0;
                MarqueeTransform.X = 0;
            }
        }

        // 2. Update 60 FPS FFT Waves (Strictly music-only; never react to Discord or voice chat)
        bool musicPlaying = settings.EnableMusicModule && _vm.TrackInfo.HasActiveSession && _vm.TrackInfo.IsPlaying;
        float musicGain = _musicSessionFilter.MusicGain;

        if (musicPlaying && !_audioCapture.IsSilent && musicGain > 0.08f)
        {
            _audioCapture.CopyLatestSamples(_sampleBuffer);
            _fftProcessor.ComputeBands(
                _sampleBuffer,
                _audioCapture.SampleRate,
                _bandValues,
                settings.WaveSensitivity * musicGain,
                settings.WaveSmoothing);
        }
        else if (musicPlaying && (_musicSessionFilter.MusicPeak > 0.002f || (!_musicSessionFilter.HasLiveSessionData && _audioCapture.IsSilent)))
        {
            // Guaranteed wave animation while music track is playing without Discord/voice false-triggers
            _fftProcessor.ComputeRhythmicFallbackBands(_bandValues, settings.WaveSmoothing);
        }
        else
        {
            _fftProcessor.DecayToZero(_bandValues, settings.WaveSmoothing);
        }

        var (strokeBrush, fillBrush) = ResolveFrozenWaveBrushes(settings);
        bool isWaveSilent = !musicPlaying && _bandValues.Max() < 0.015;

        if (_vm.State == IslandState.Compact)
        {
            bool hideForLongPause = settings.HideMusicOnPause && !_vm.ShowMusicInCompact && !musicPlaying;
            if (hideForLongPause || !settings.EnableMusicModule)
            {
                CompactWaveCanvas.Visibility = Visibility.Collapsed;
            }
            else
            {
                _compactWaveRenderer.Render(CompactWaveCanvas, _bandValues, settings, strokeBrush, fillBrush, isWaveSilent);
            }
        }
        else if (_vm.State == IslandState.Expanded && _vm.IsMusicSectionVisible)
        {
            _expandedWaveRenderer.Render(ExpandedWaveCanvas, _bandValues, settings, strokeBrush, fillBrush, isWaveSilent);
        }
    }

    private void AnimateTrackCrossfade()
    {
        var dur = TimeSpan.FromMilliseconds(240);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fadeIn = new DoubleAnimation(0.15, 1.0, dur) { EasingFunction = ease };
        CompactCoverBorder.BeginAnimation(OpacityProperty, fadeIn);
        CompactTrackText.BeginAnimation(OpacityProperty, fadeIn);
        ExpandedCoverBorder.BeginAnimation(OpacityProperty, fadeIn);
        ExpandedTrackInfoPanel.BeginAnimation(OpacityProperty, fadeIn);

        var slideUp = new DoubleAnimation(6.0, 0.0, dur) { EasingFunction = ease };
        ExpandedTrackSlideTransform.BeginAnimation(TranslateTransform.YProperty, slideUp);
    }

    private (Brush Stroke, Brush Fill) ResolveFrozenWaveBrushes(AppSettings settings)
    {
        var palette = ThemeService.GetPalette(ThemeService.CurrentStyle);
        Color primary;
        Color secondary;

        if (settings.WaveColorMode == WaveColorMode.ThemeAdaptive)
        {
            primary = palette.AccentPrimary;
            secondary = palette.AccentSecondary;
        }
        else if (settings.WaveColorMode == WaveColorMode.AlbumAdaptive && _vm.TrackInfo.HasActiveSession)
        {
            primary = _vm.TrackInfo.AccentColor;
            secondary = palette.AccentSecondary;
        }
        else
        {
            primary = ParseColorOrDefault(settings.StaticWaveColorHex, palette.AccentPrimary);
            secondary = ParseColorOrDefault(settings.GradientSecondaryColorHex, palette.AccentSecondary);
        }

        string key = $"{ThemeService.CurrentStyle}|{settings.WaveColorMode}|{primary}|{secondary}";
        if (_cachedStrokeBrush is not null && _cachedFillBrush is not null && _cachedBrushKey == key)
            return (_cachedStrokeBrush, _cachedFillBrush);

        Brush stroke;
        if (settings.WaveColorMode == WaveColorMode.Static)
        {
            stroke = new SolidColorBrush(primary);
        }
        else
        {
            stroke = new LinearGradientBrush(primary, secondary, 0.0);
        }
        stroke.Freeze();

        var fillGrad = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0.0),
            EndPoint = new Point(0.5, 1.0)
        };
        fillGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x95, primary.R, primary.G, primary.B), 0.0));
        fillGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x50, secondary.R, secondary.G, secondary.B), 0.5));
        fillGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x95, primary.R, primary.G, primary.B), 1.0));
        fillGrad.Freeze();

        _cachedStrokeBrush = stroke;
        _cachedFillBrush = fillGrad;
        _cachedBrushKey = key;

        return (stroke, fillGrad);
    }

    private static Color ParseColorOrDefault(string hex, Color fallback)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color c)
                return c;
        }
        catch { }
        return fallback;
    }

    private static bool IsClickInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.Primitives.Thumb
                or System.Windows.Controls.Primitives.ScrollBar)
            {
                return true;
            }
            source = DwmBackdropService.GetParentSafe(source);
        }
        return false;
    }

    // --- Event Handlers ---

    private void OnTopTriggerMouseEnter(object sender, MouseEventArgs e)
    {
        if (Environment.TickCount64 < _manualHideCooldownUntilMs)
            return;

        if (_vm.State == IslandState.Hidden)
        {
            SetIslandState(IslandState.Compact);
        }
    }

    private void OnIslandMouseEnter(object sender, MouseEventArgs e)
    {
        if (Environment.TickCount64 < _manualHideCooldownUntilMs)
            return;

        _isPointerInside = true;
        _autoHideTimer.Stop();

        if (_vm.State == IslandState.Hidden)
        {
            SetIslandState(IslandState.Compact);
        }
        else if (_vm.State == IslandState.Compact && _settingsService.Current.ExpandOnHover)
        {
            SetIslandState(IslandState.Expanded);
        }
    }

    private void OnIslandMouseLeave(object sender, MouseEventArgs e)
    {
        _isPointerInside = false;
        if (TranslationSearchBox?.IsKeyboardFocused == true)
            return;

        if (_vm.State == IslandState.Expanded &&
            _settingsService.Current.ExpandOnHover &&
            _settingsService.Current.CollapseOnMouseLeave)
        {
            SetIslandState(IslandState.Compact);
            RestartAutoHideTimer();
            return;
        }

        if (_vm.State == IslandState.Compact)
        {
            RestartAutoHideTimer();
        }
    }

    private void OnIslandMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsClickInsideButton(e.OriginalSource as DependencyObject))
            return;

        if (_vm.State == IslandState.Compact)
        {
            SetIslandState(IslandState.Expanded);
        }
    }

    private void OnCollapseToCompactClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SetIslandState(IslandState.Compact);
    }

    private void OnHideIslandClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _manualHideCooldownUntilMs = Environment.TickCount64 + 1200;
        _isPointerInside = false;
        SetIslandState(IslandState.Hidden);
    }

    private void OnTriggerTranslateClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        TranslationRequested?.Invoke();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SettingsRequested?.Invoke();
    }

    private void OnOpenSmartClipboardClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SmartClipboardRequested?.Invoke();
    }

    private void OnCycleAudioOutputClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_audioEndpointMonitor is null)
            return;

        var (switched, fullName, shortName) = _audioEndpointMonitor.CycleDefaultRenderDevice();
        if (switched)
        {
            string label = string.IsNullOrWhiteSpace(fullName) ? shortName : fullName;
            string shortLabel = string.IsNullOrWhiteSpace(shortName) ? label : shortName;
            _vm.StatusMessage = LocalizationService.Pick($"Audio output: {label}", $"Аудиовыход: {label}");
            _vm.VolumeHudTitle = LocalizationService.Pick($"Output: {shortLabel}", $"Выход: {shortLabel}");
            _vm.VolumeHudFraction = 1.0;
            _vm.IsVolumeHudActive = true;
            CompactVolumeFill.Width = 180.0;
            _volumeHudTimer.Stop();
            _volumeHudTimer.Start();
        }
        else
        {
            _vm.StatusMessage = LocalizationService.Pick(
                "Only one active audio output is available",
                "Доступен только один активный аудиовыход");
        }
    }

    private void OnToggleSpeakerMuteClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _audioEndpointMonitor?.ToggleSpeakerMute();
    }

    private void OnToggleMicMuteClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _audioEndpointMonitor?.ToggleMicMute();
    }

    private void OnTranslationSearchMouseDown(object sender, MouseButtonEventArgs e)
    {
        EnableInteractiveKeyboardFocus();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            TranslationSearchBox?.Focus();
        });
    }

    private void OnTranslationSearchLostFocus(object sender, RoutedEventArgs e)
    {
        RestoreNoActivateStyle();
        if (!_isPointerInside &&
            _vm.State == IslandState.Expanded &&
            _settingsService.Current.ExpandOnHover &&
            _settingsService.Current.CollapseOnMouseLeave)
        {
            SetIslandState(IslandState.Compact);
            RestartAutoHideTimer();
        }
    }

    private void OnTranslationSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (!string.IsNullOrEmpty(_vm.TranslationSearchQuery))
            {
                _vm.TranslationSearchQuery = string.Empty;
            }
            Keyboard.ClearFocus();
            RestoreNoActivateStyle();
        }
    }

    private void OnClearTranslationSearchClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _vm.TranslationSearchQuery = string.Empty;
        Keyboard.ClearFocus();
        RestoreNoActivateStyle();
    }

    private async void OnTrackProgressSeekClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_vm.TrackInfo.DurationSeconds <= 1.0 || TrackSeekBarContainer.ActualWidth <= 4)
            return;

        double x = e.GetPosition(TrackSeekBarContainer).X;
        double ratio = Math.Clamp(x / TrackSeekBarContainer.ActualWidth, 0.0, 1.0);
        double targetSec = ratio * _vm.TrackInfo.DurationSeconds;
        await _mediaService.SeekToAsync(targetSec);
        UpdateTrackProgressBarWidth();
    }

    private void OnIslandDragEnter(object sender, DragEventArgs e)
    {
        if (!_settingsService.Current.EnableFileDropZone)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            _dragLeaveTimer.Stop();
            e.Effects = DragDropEffects.Copy;
            bool wasVisible = _vm.IsFileDropZoneVisible;
            _vm.IsDragOverFileDropZone = true;

            if (_vm.State != IslandState.Expanded)
                SetIslandState(IslandState.Expanded, animate: true);
            else if (!wasVisible)
                ApplyStateVisuals(animate: true, stateChanged: false);
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnIslandDragLeave(object sender, DragEventArgs e)
    {
        _dragLeaveTimer.Stop();
        _dragLeaveTimer.Start();
        e.Handled = true;
    }

    private void OnIslandDrop(object sender, DragEventArgs e)
    {
        _dragLeaveTimer.Stop();
        _vm.IsDragOverFileDropZone = false;

        if (!_settingsService.Current.EnableFileDropZone)
            return;

        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] paths &&
            paths.Length > 0)
        {
            var items = new List<DropShelfItem>();
            foreach (string path in paths)
            {
                try
                {
                    bool isDir = System.IO.Directory.Exists(path);
                    string name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
                    if (string.IsNullOrWhiteSpace(name)) name = path;
                    string sizeText = isDir
                        ? LocalizationService.Pick("Folder", "Папка")
                        : FormatFileSize(new System.IO.FileInfo(path).Length);
                    items.Add(new DropShelfItem(path, name, sizeText, isDir));
                }
                catch { }
            }

            if (items.Count > 0)
            {
                _vm.AddDroppedFiles(items);
                DropShelfEmptyHint.Visibility = Visibility.Collapsed;
                SetIslandState(IslandState.Expanded, animate: true);
            }
        }
        e.Handled = true;
    }

    private void OnClearDroppedFilesClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _vm.IsDragOverFileDropZone = false;
        _vm.ClearDroppedFiles();
        DropShelfEmptyHint.Visibility = Visibility.Visible;
        ApplyStateVisuals(animate: true, stateChanged: false);
    }

    private void OnDropItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsClickInsideButton(e.OriginalSource as DependencyObject))
        {
            _dragStartPoint = null;
            _dragFilePath = null;
            return;
        }

        if (sender is FrameworkElement { Tag: string path })
        {
            if (e.ClickCount == 2)
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                }
                catch { }
                e.Handled = true;
                return;
            }
            _dragStartPoint = e.GetPosition(null);
            _dragFilePath = path;
        }
    }

    private void OnDropItemMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = null;
        _dragFilePath = null;
    }

    private void OnDropItemAskAiClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: string path } && System.IO.File.Exists(path))
        {
            DropImageAskAiRequested?.Invoke(path);
        }
    }

    private async void OnDropItemConvertPngClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.Tag is string path && System.IO.File.Exists(path))
        {
            btn.IsEnabled = false;
            try { await ConvertDroppedImageAsync(path, toPng: true); }
            finally { btn.IsEnabled = true; }
        }
    }

    private async void OnDropItemCompressJpgClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.Tag is string path && System.IO.File.Exists(path))
        {
            btn.IsEnabled = false;
            try { await ConvertDroppedImageAsync(path, toPng: false); }
            finally { btn.IsEnabled = true; }
        }
    }

    private async Task ConvertDroppedImageAsync(string sourcePath, bool toPng)
    {
        try
        {
            string? outPath = await Task.Run(() =>
            {
                var region = ScreenCaptureService.LoadFromImageFile(sourcePath);
                if (region is null)
                    return null;

                string dir = System.IO.Path.GetDirectoryName(sourcePath) ?? string.Empty;
                string stem = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
                string ext = toPng ? ".png" : ".jpg";
                string suffix = toPng ? "_png" : "_compressed";
                string destPath = BuildUniqueSiblingPath(dir, stem + suffix, ext);

                byte[] pixels = toPng
                    ? region.Bgra32Pixels
                    : ScreenCaptureService.CompositeBgraOverWhiteIfNeeded(region.Bgra32Pixels);

                var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
                    region.Width,
                    region.Height,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    pixels,
                    region.Width * 4);
                bmp.Freeze();

                System.Windows.Media.Imaging.BitmapEncoder encoder = toPng
                    ? new System.Windows.Media.Imaging.PngBitmapEncoder()
                    : new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 82 };
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));

                using var outFs = new System.IO.FileStream(destPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read);
                encoder.Save(outFs);
                return destPath;
            });

            if (!string.IsNullOrWhiteSpace(outPath) && System.IO.File.Exists(outPath))
            {
                string fileName = System.IO.Path.GetFileName(outPath);
                string sizeText = FormatFileSize(new System.IO.FileInfo(outPath).Length);
                _vm.AddDroppedFiles([new DropShelfItem(outPath, fileName, sizeText, false)]);
                _vm.StatusMessage = toPng
                    ? LocalizationService.Pick($"Saved as PNG: {fileName}", $"Сохранено в PNG: {fileName}")
                    : LocalizationService.Pick($"Compressed to JPG: {fileName} ({sizeText})", $"Сжато в JPG: {fileName} ({sizeText})");
            }
            else
            {
                _vm.StatusMessage = LocalizationService.Pick("Failed to process image", "Не удалось обработать изображение");
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"ConvertDroppedImageAsync error: {ex.Message}");
            _vm.StatusMessage = LocalizationService.Pick("Image conversion error", "Ошибка конвертации изображения");
        }
    }

    private static string BuildUniqueSiblingPath(string dir, string baseName, string ext)
    {
        string candidate = System.IO.Path.Combine(dir, baseName + ext);
        int idx = 2;
        while (System.IO.File.Exists(candidate))
        {
            candidate = System.IO.Path.Combine(dir, $"{baseName}_{idx}{ext}");
            idx++;
        }
        return candidate;
    }

    private void OnDropItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStartPoint is null || string.IsNullOrWhiteSpace(_dragFilePath))
            return;

        Point pos = e.GetPosition(null);
        Vector diff = _dragStartPoint.Value - pos;
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            string path = _dragFilePath;
            _dragStartPoint = null;
            _dragFilePath = null;
            if (System.IO.File.Exists(path) || System.IO.Directory.Exists(path))
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { path });
                DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
            }
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} {LocalizationService.Pick("B", "Б")}";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} {LocalizationService.Pick("KB", "КБ")}";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.#} {LocalizationService.Pick("MB", "МБ")}";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} {LocalizationService.Pick("GB", "ГБ")}";
    }

    private void OnToggleMusicSectionClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _vm.IsMusicSectionVisible = !_vm.IsMusicSectionVisible;
        ApplyStateVisuals(animate: true, stateChanged: false);
        if (_vm.IsMusicSectionVisible)
        {
            AnimateSectionBlurIn(ExpandedMusicBody, MusicSectionScale, MusicSectionBlur);
        }
    }

    private void OnToggleTranslationSectionClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        bool nextTrans = !_vm.IsTranslationSectionVisible;
        bool nextNotif = nextTrans ? false : _vm.IsNotificationHistoryVisible;
        AnimateAccordionSections(nextTrans, nextNotif);
    }

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _vm.ClearTranslations();
    }

    private void OnCopyHistoryItemClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.Tag is string text && !string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.SetText(text);
            _vm.StatusMessage = LocalizationService.Pick("Copied to clipboard", "Скопировано в буфер обмена");
        }
    }

    private void OnHistoryItemTextLoaded(object sender, RoutedEventArgs e)
        => ApplyHistoryItemTextState(sender as TextBlock, isTranslated: true, resetCollapsed: true);

    private void OnHistoryItemTextDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => ApplyHistoryItemTextState(sender as TextBlock, isTranslated: true, resetCollapsed: true);

    private void OnHistorySourceTextLoaded(object sender, RoutedEventArgs e)
        => ApplyHistoryItemTextState(sender as TextBlock, isTranslated: false, resetCollapsed: true);

    private void OnHistorySourceTextDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => ApplyHistoryItemTextState(sender as TextBlock, isTranslated: false, resetCollapsed: true);

    private void OnHistoryItemToggleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsClickInsideButton(e.OriginalSource as DependencyObject))
            return;

        e.Handled = true;
        if (sender is not Border border)
            return;

        var translatedTb = FindChildByName<TextBlock>(border, "HistoryTranslatedText");
        var sourceTb = FindChildByName<TextBlock>(border, "HistorySourceText");
        if (translatedTb is null)
            return;

        bool nextExpanded = translatedTb.Tag is not true;
        translatedTb.Tag = nextExpanded;
        if (sourceTb is not null)
            sourceTb.Tag = nextExpanded;

        ApplyHistoryItemTextState(translatedTb, isTranslated: true, resetCollapsed: false);
        ApplyHistoryItemTextState(sourceTb, isTranslated: false, resetCollapsed: false);

        if (_vm.State == IslandState.Expanded)
        {
            ApplyStateVisuals(animate: true, stateChanged: false);
        }
    }

    private static void ApplyHistoryItemTextState(TextBlock? tb, bool isTranslated, bool resetCollapsed)
    {
        if (tb is null || tb.DataContext is not TranslationRecord record)
            return;

        if (resetCollapsed)
            tb.Tag = false;

        bool isExpanded = tb.Tag is true;
        string full = isTranslated ? record.TranslatedText : record.SourceText;
        tb.TextWrapping = isExpanded ? TextWrapping.Wrap : TextWrapping.NoWrap;
        tb.Text = isExpanded ? full : FormatFirstWords(full);
    }

    private static string FormatFirstWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string normalized = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 4 && normalized.Length <= 34)
            return normalized;

        int take = Math.Min(4, words.Length);
        string preview = string.Join(" ", words, 0, take);
        if (preview.Length > 34)
            preview = preview.Substring(0, 34).TrimEnd();

        return preview + "...";
    }

    private static T? FindChildByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name)
                return match;

            var nested = FindChildByName<T>(child, name);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    private async void OnPrevTrackClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await _mediaService.PreviousTrackAsync();
    }

    private async void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await _mediaService.TogglePlayPauseAsync();
    }

    private async void OnNextTrackClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await _mediaService.NextTrackAsync();
    }

    // ══════════════════════════════════════════════
    //  ISLAND NOTIFICATION HANDLERS
    // ══════════════════════════════════════════════

    private void OnIslandNotificationHistorySeeded()
    {
        _vm.SeedNotificationHistory(_notificationService.History);
        UpdateNotifCountBadge();
        if (_vm.State == IslandState.Expanded)
            ApplyStateVisuals(animate: true, stateChanged: false);
    }

    private void OnIslandNotificationArrived(IslandNotification notification)
    {
        if (!_settingsService.Current.EnableIslandNotifications)
            return;

        // Mirror to ViewModel for binding
        _vm.PushNotification(notification);
        UpdateNotifCountBadge();

        // Flash-expand to compact if hidden, so user notices the banner
        if (_vm.State == IslandState.Hidden)
        {
            SetIslandState(IslandState.Compact, animate: true);
        }
        else if (_vm.State == IslandState.Expanded)
        {
            ApplyStateVisuals(animate: true, stateChanged: false);
        }

        // Animate the active-notification banner in and play configured notification sound
        AnimateNotificationBannerIn();
        NotificationSoundService.Play(_settingsService.Current.NotificationSound);
    }

    private void OnIslandNotificationDismissed()
    {
        _vm.DismissNotification();
        UpdateNotifCountBadge();
    }

    private void AnimateNotificationBannerIn()
    {
        if (ActiveNotifBanner is not null)
        {
            ActiveNotifBanner.Opacity = 0;
            var anim = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var slideAnim = new DoubleAnimation(-8, 0, new Duration(TimeSpan.FromMilliseconds(220)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var translateTransform = new TranslateTransform();
            ActiveNotifBanner.RenderTransform = translateTransform;
            ActiveNotifBanner.BeginAnimation(OpacityProperty, anim);
            translateTransform.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }

        if (CompactNotificationHud is not null)
        {
            CompactNotificationHud.Opacity = 0;
            var cAnim = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var cSlide = new DoubleAnimation(-6, 0, new Duration(TimeSpan.FromMilliseconds(200)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var cTransform = new TranslateTransform();
            CompactNotificationHud.RenderTransform = cTransform;
            CompactNotificationHud.BeginAnimation(OpacityProperty, cAnim);
            cTransform.BeginAnimation(TranslateTransform.YProperty, cSlide);
        }
    }

    private void OnCompactNotificationClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_vm.ActiveNotification is { } notif)
        {
            NotificationActivator.Activate(notif);
            _notificationService.DismissActive();
        }
    }

    private void OnActiveNotificationClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_vm.ActiveNotification is { } notif)
        {
            NotificationActivator.Activate(notif);
            _notificationService.DismissActive();
        }
    }

    private void OnHistoryNotificationClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: IslandNotification notif })
        {
            NotificationActivator.Activate(notif);
        }
    }

    private void OnDismissNotificationClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _notificationService.DismissActive();
    }

    private void OnClearNotificationsClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _notificationService.ClearHistory();
        _vm.ClearNotificationHistory();
        _vm.DismissNotification();
        UpdateNotifCountBadge();
    }

    private void OnToggleNotificationHistoryClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        bool nextNotif = !_vm.IsNotificationHistoryVisible;
        bool nextTrans = nextNotif ? false : _vm.IsTranslationSectionVisible;
        if (_vm.State == IslandState.Expanded)
        {
            AnimateAccordionSections(nextTrans, nextNotif);
        }
        else
        {
            _vm.IsNotificationHistoryVisible = nextNotif;
            _vm.IsTranslationSectionVisible = nextTrans;
        }
    }

    private void AnimateAccordionSections(bool nextTranslation, bool nextNotifications)
    {
        int gen = ++_accordionAnimGen;
        bool prevTranslation = _vm.IsTranslationSectionVisible;
        bool prevNotifications = _vm.IsNotificationHistoryVisible;

        if (prevTranslation == nextTranslation && prevNotifications == nextNotifications)
            return;

        if (nextTranslation || nextNotifications)
            _vm.IsAiSectionVisible = false;

        // Clear any prior MaxHeight/Opacity animations before measuring
        ExpandedTranslationBody.BeginAnimation(MaxHeightProperty, null);
        ExpandedTranslationBody.BeginAnimation(OpacityProperty, null);
        ExpandedNotificationHistoryBody.BeginAnimation(MaxHeightProperty, null);
        ExpandedNotificationHistoryBody.BeginAnimation(OpacityProperty, null);

        FrameworkElement? outBody = null;
        ScaleTransform? outScale = null;
        BlurEffect? outBlur = null;
        Action? hideOutgoingVm = null;

        FrameworkElement? inBody = null;
        ScaleTransform? inScale = null;
        BlurEffect? inBlur = null;

        if (prevTranslation && !nextTranslation)
        {
            outBody = ExpandedTranslationBody;
            outScale = TranslationSectionScale;
            outBlur = TranslationSectionBlur;
            hideOutgoingVm = () => _vm.IsTranslationSectionVisible = false;
        }
        else if (prevNotifications && !nextNotifications)
        {
            outBody = ExpandedNotificationHistoryBody;
            outScale = NotificationHistoryScale;
            outBlur = NotificationHistoryBlur;
            hideOutgoingVm = () => _vm.IsNotificationHistoryVisible = false;
        }

        if (!prevTranslation && nextTranslation)
        {
            _vm.IsTranslationSectionVisible = true;
            inBody = ExpandedTranslationBody;
            inScale = TranslationSectionScale;
            inBlur = TranslationSectionBlur;
        }
        else if (!prevNotifications && nextNotifications)
        {
            _vm.IsNotificationHistoryVisible = true;
            inBody = ExpandedNotificationHistoryBody;
            inScale = NotificationHistoryScale;
            inBlur = NotificationHistoryBlur;
        }

        double outStart = outBody is not null ? Math.Max(1.0, outBody.ActualHeight) : 0.0;
        double inTarget = 0.0;
        if (inBody is not null)
        {
            inBody.MaxHeight = double.PositiveInfinity;
            inBody.UpdateLayout();
            inBody.Measure(new Size(480, double.PositiveInfinity));
            inTarget = Math.Clamp(Math.Ceiling(inBody.DesiredSize.Height), 24.0, 210.0);
        }

        // Measure final island height with outgoing collapsed to 0 and incoming at full target
        if (outBody is not null) outBody.MaxHeight = 0;
        if (inBody is not null) inBody.MaxHeight = inTarget;
        double targetIslandHeight = CalculateExpandedHeight();

        // Restore starting MaxHeights for smooth simultaneous curtain morphing
        if (outBody is not null) outBody.MaxHeight = outStart;
        if (inBody is not null)
        {
            inBody.MaxHeight = 0;
            inBody.Opacity = 0;
        }

        var easeInOut = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 };
        var pillDur = TimeSpan.FromMilliseconds(320);

        var pillHeightAnim = new DoubleAnimation(targetIslandHeight, pillDur) { EasingFunction = easeInOut };
        pillHeightAnim.Completed += (_, _) =>
        {
            if (gen != _accordionAnimGen) return;
            DwmBackdropService.ApplyToElement(
                this,
                IslandPill,
                _settingsService.Current.EnableDwmBackdropBlur && _vm.State != IslandState.Hidden,
                IslandPill.CornerRadius.TopLeft);
        };
        IslandPill.BeginAnimation(HeightProperty, pillHeightAnim);
        OuterNeonAuraBorder.BeginAnimation(HeightProperty, new DoubleAnimation(targetIslandHeight + 4, pillDur) { EasingFunction = easeInOut });

        if (outBody is not null && outScale is not null && outBlur is not null)
        {
            var outDur = TimeSpan.FromMilliseconds(240);
            var outMaxAnim = new DoubleAnimation(outStart, 0.0, outDur) { EasingFunction = easeInOut };
            outMaxAnim.Completed += (_, _) =>
            {
                if (gen != _accordionAnimGen) return;
                hideOutgoingVm?.Invoke();
                outBody.BeginAnimation(MaxHeightProperty, null);
                outBody.MaxHeight = double.PositiveInfinity;
                outBody.BeginAnimation(OpacityProperty, null);
                outBody.Opacity = 1.0;
                outScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                outScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                outScale.ScaleX = 1.0;
                outScale.ScaleY = 1.0;
                outBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
                outBlur.Radius = 0.0;
            };

            outBody.BeginAnimation(MaxHeightProperty, outMaxAnim);
            outBody.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = easeOut });
            outScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 0.94, outDur) { EasingFunction = easeInOut });
            outScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 0.94, outDur) { EasingFunction = easeInOut });
            outBlur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0.0, 6.0, outDur) { EasingFunction = easeOut });
        }

        if (inBody is not null && inScale is not null && inBlur is not null)
        {
            var inDur = TimeSpan.FromMilliseconds(290);
            var inMaxAnim = new DoubleAnimation(0.0, inTarget, inDur) { EasingFunction = easeOut };
            inMaxAnim.Completed += (_, _) =>
            {
                if (gen != _accordionAnimGen) return;
                inBody.BeginAnimation(MaxHeightProperty, null);
                inBody.MaxHeight = double.PositiveInfinity;
            };

            inBody.BeginAnimation(MaxHeightProperty, inMaxAnim);
            inBody.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, inDur) { EasingFunction = easeOut });
            inScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.93, 1.0, inDur) { EasingFunction = spring });
            inScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.93, 1.0, inDur) { EasingFunction = spring });
            inBlur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(8.0, 0.0, inDur) { EasingFunction = easeOut });
        }
    }

    private void UpdateNotifCountBadge()
    {
        // Bound via {Binding NotificationCountText} in XAML; avoid direct .Text overwrite which breaks the binding.
    }

    private void OnPropertyChanged_NotifHistory()
    {
        // Force WPF to re-evaluate TimeAgoText on history items (computed property)
        var items = _vm.NotificationHistory.ToList();
        _vm.NotificationHistory.Clear();
        foreach (var item in items)
            _vm.NotificationHistory.Add(item);
    }

    /// <summary>Allows App.xaml.cs to push a custom island notification directly.</summary>
    public void PushNotification(string appName, string title, string body,
        NotificationKind kind = NotificationKind.Default, double autoDismissSeconds = 5.0)
    {
        var notification = new IslandNotification(
            Guid.NewGuid().ToString("N")[..8],
            appName, title, body, kind, DateTime.Now, autoDismissSeconds);
        _notificationService.Push(notification);
    }

    private void StartAiThinkingAnimation()
    {
        var pulseEase = new SineEase { EasingMode = EasingMode.EaseInOut };
        var auraPulse = new DoubleAnimation(0.45, 1.0, TimeSpan.FromMilliseconds(720))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = pulseEase
        };
        OuterNeonAuraBorder.BeginAnimation(OpacityProperty, auraPulse);

        AnimateDot(AiDot1Scale, TimeSpan.Zero);
        AnimateDot(AiDot2Scale, TimeSpan.FromMilliseconds(140));
        AnimateDot(AiDot3Scale, TimeSpan.FromMilliseconds(280));
    }

    private static void AnimateDot(ScaleTransform target, TimeSpan beginTime)
    {
        var anim = new DoubleAnimation(0.45, 1.35, TimeSpan.FromMilliseconds(420))
        {
            BeginTime = beginTime,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        target.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void StopAiThinkingAnimation()
    {
        OuterNeonAuraBorder.BeginAnimation(OpacityProperty, null);
        OuterNeonAuraBorder.Opacity = 0.42;
        AiDot1Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        AiDot2Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        AiDot3Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }
}
