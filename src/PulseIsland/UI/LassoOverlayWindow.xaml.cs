using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using PulseIsland.Core;
using PulseIsland.Interop;
using PulseIsland.Services;

namespace PulseIsland.UI;

public enum LassoCaptureIntent
{
    Translate,
    AskAiAboutImage
}

public sealed record LassoCaptureResult(
    Int32Rect PhysicalRect,
    Rect DipRect,
    LassoCaptureIntent Intent);

public partial class LassoOverlayWindow : Window
{
    private readonly record struct TrailSample(Point Position, long TimestampMs);

    private static readonly SolidColorBrush[] WhiteAlphaBrushes = CreateAlphaBrushPalette();
    private static readonly SolidColorBrush AiModeAccentBrush = CreateFrozenBrush(Color.FromRgb(120, 210, 255));

    private readonly AppSettings _settings;
    private readonly List<TrailSample> _trailSamples = new(128);
    private readonly List<Line> _linePool = new(128);
    private Rect _virtualScreenPhysical;

    private bool _isDragging;
    private bool _isRightButtonDrag;
    private bool _completed;
    private Point _dragStartDip;
    private Point _dragCurrentDip;
    private Point _dragStartPhysical;
    private Point _dragCurrentPhysical;
    private bool _hasStartPhysical;
    private bool _hasCurrentPhysical;

    private TaskCompletionSource<LassoCaptureResult?>? _tcs;

    public LassoOverlayWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        Loaded += OnLoaded;
        Closed += OnClosed;
        MouseMove += OnMouseMove;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseRightButtonDown += OnMouseRightButtonDown;
        MouseRightButtonUp += OnMouseRightButtonUp;
        KeyDown += OnKeyDownHandler;
        KeyUp += (_, _) =>
        {
            if (_isDragging)
                UpdateLiveSelectionBox();
        };
    }

    public void CancelFromExternal() => CancelCapture();

    public Task<LassoCaptureResult?> CaptureSelectionAsync()
    {
        _tcs = new TaskCompletionSource<LassoCaptureResult?>();
        Show();
        Activate();
        Focus();
        return _tcs.Task;
    }

    private void OnKeyDownHandler(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelCapture();
            return;
        }

        if (_isDragging)
            UpdateLiveSelectionBox();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var vs = MonitorService.GetVirtualScreenPhysicalBounds();
        _virtualScreenPhysical = vs;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;

        if (hwnd != IntPtr.Zero)
        {
            Win32Api.SetWindowPos(
                hwnd,
                Win32Api.HWND_TOPMOST,
                (int)Math.Round(vs.Left),
                (int)Math.Round(vs.Top),
                (int)Math.Round(vs.Width),
                (int)Math.Round(vs.Height),
                0);
        }
        else
        {
            var (scaleX, scaleY) = MonitorService.GetVisualDpiScale(this);
            Left = vs.Left / scaleX;
            Top = vs.Top / scaleY;
            Width = vs.Width / scaleX;
            Height = vs.Height / scaleY;
        }

        // Show cursor reticle immediately at current mouse position even before first MouseMove
        if (Win32Api.GetCursorPos(out var pt))
        {
            try
            {
                var dip = PointFromScreen(new Point(pt.X, pt.Y));
                PositionReticle(dip);
            }
            catch { }
        }

        CompositionTarget.Rendering += OnRenderFrame;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRenderFrame;
        if (!_completed)
        {
            _tcs?.TrySetResult(null);
        }
    }

    private void PositionReticle(Point pos)
    {
        CursorReticle.Visibility = Visibility.Visible;
        Canvas.SetLeft(CursorReticle, pos.X - CursorReticle.Width * 0.5);
        Canvas.SetTop(CursorReticle, pos.Y - CursorReticle.Height * 0.5);
    }

    private LassoCaptureIntent GetCurrentIntent()
    {
        if (_isRightButtonDrag || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            return LassoCaptureIntent.AskAiAboutImage;

        return LassoCaptureIntent.Translate;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        Point pos = e.GetPosition(OverlayCanvas);
        PositionReticle(pos);
        _trailSamples.Add(new TrailSample(pos, Environment.TickCount64));

        if (_isDragging)
        {
            _dragCurrentDip = pos;
            if (Win32Api.GetCursorPos(out var physPt))
            {
                _dragCurrentPhysical = new Point(physPt.X, physPt.Y);
                _hasCurrentPhysical = true;
            }

            UpdateLiveSelectionBox();
        }
    }

    private void UpdateLiveSelectionBox()
    {
        double canvasW = Math.Max(100, ActualWidth);
        double canvasH = Math.Max(100, ActualHeight);
        var boundsDip = new Rect(0, 0, canvasW, canvasH);
        var box = LassoGeometryHelper.ComputeRectangleBox(_dragStartDip, _dragCurrentDip, boundsDip, padding: 2.0);
        if (box is null)
        {
            SelectionPreviewOuter.Visibility = Visibility.Collapsed;
            SelectionSizeBadge.Visibility = Visibility.Collapsed;
            DimBackdropPath.Visibility = Visibility.Collapsed;
            return;
        }

        var rect = new Rect(box.Value.X, box.Value.Y, box.Value.Width, box.Value.Height);
        var intent = GetCurrentIntent();
        bool isAi = intent == LassoCaptureIntent.AskAiAboutImage;

        // Cut-out dim mask around the exact selection rectangle
        DimBackdropPath.Data = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(boundsDip),
            new RectangleGeometry(rect, 4, 4));
        DimBackdropPath.Visibility = Visibility.Visible;

        // Update selection rectangle border (shrinks smoothly when cursor moves back!)
        SelectionPreviewOuter.Visibility = Visibility.Visible;
        SelectionPreviewOuter.Width = rect.Width + 4;
        SelectionPreviewOuter.Height = rect.Height + 4;
        Canvas.SetLeft(SelectionPreviewOuter, rect.X - 2);
        Canvas.SetTop(SelectionPreviewOuter, rect.Y - 2);

        Brush modeAccent = isAi
            ? AiModeAccentBrush
            : (TryFindResource("ThemeAccentBrush") as Brush ?? Brushes.White);

        SelectionPreviewInner.BorderBrush = modeAccent;
        SelectionSizeBadge.BorderBrush = modeAccent;
        SelectionModeDot.Fill = modeAccent;
        CursorCoreDot.Fill = modeAccent;
        CursorInnerRing.Stroke = isAi ? AiModeAccentBrush : Brushes.White;

        string modeLabel = isAi
            ? LocalizationService.Pick("AI Vision", "ИИ по фото")
            : LocalizationService.Pick("Translate", "Перевод");
        SelectionSizeText.Text = $"{box.Value.Width} × {box.Value.Height} • {modeLabel}";
        SelectionSizeBadge.Visibility = Visibility.Visible;
        SelectionSizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double badgeW = SelectionSizeBadge.DesiredSize.Width;
        double badgeH = SelectionSizeBadge.DesiredSize.Height;

        double badgeLeft = Math.Clamp(rect.X, 8, Math.Max(8, canvasW - badgeW - 8));
        double badgeTop = rect.Y >= badgeH + 10
            ? rect.Y - badgeH - 8
            : Math.Min(canvasH - badgeH - 8, rect.Bottom + 8);
        Canvas.SetLeft(SelectionSizeBadge, badgeLeft);
        Canvas.SetTop(SelectionSizeBadge, badgeTop);
    }

    private void BeginDrag(Point pos, bool rightButton)
    {
        _isDragging = true;
        _isRightButtonDrag = rightButton;
        _dragStartDip = pos;
        _dragCurrentDip = pos;

        if (Win32Api.GetCursorPos(out var physPt))
        {
            _dragStartPhysical = new Point(physPt.X, physPt.Y);
            _dragCurrentPhysical = _dragStartPhysical;
            _hasStartPhysical = true;
            _hasCurrentPhysical = true;
        }
        else
        {
            _hasStartPhysical = false;
            _hasCurrentPhysical = false;
        }

        SelectionPreviewOuter.Visibility = Visibility.Collapsed;
        SelectionSizeBadge.Visibility = Visibility.Collapsed;
        DimBackdropPath.Visibility = Visibility.Collapsed;
        AnimateOrbScale(0.76);
        CaptureMouse();
    }

    private void AnimateOrbScale(double target)
    {
        var dur = TimeSpan.FromMilliseconds(120);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        CursorOrbScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(target, dur) { EasingFunction = ease });
        CursorOrbScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(target, dur) { EasingFunction = ease });
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
            return;

        BeginDrag(e.GetPosition(OverlayCanvas), rightButton: false);
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            // Right-click while dragging with left button cancels immediately
            CancelCapture();
            return;
        }

        BeginDrag(e.GetPosition(OverlayCanvas), rightButton: true);
    }

    private async void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging || _isRightButtonDrag)
            return;

        await FinishDragAsync(e.GetPosition(OverlayCanvas), cancelIfTooSmall: false);
    }

    private async void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging || !_isRightButtonDrag)
            return;

        // Short right-click without dragging acts as cancel (classic Escape/RMB cancel UX)
        await FinishDragAsync(e.GetPosition(OverlayCanvas), cancelIfTooSmall: true);
    }

    private async Task FinishDragAsync(Point endPos, bool cancelIfTooSmall)
    {
        var intent = GetCurrentIntent();
        _isDragging = false;
        _isRightButtonDrag = false;
        AnimateOrbScale(1.0);
        ReleaseMouseCapture();

        _dragCurrentDip = endPos;
        if (Win32Api.GetCursorPos(out var physPt))
        {
            _dragCurrentPhysical = new Point(physPt.X, physPt.Y);
            _hasCurrentPhysical = true;
        }

        var boundsDip = new Rect(0, 0, Math.Max(100, ActualWidth), Math.Max(100, ActualHeight));
        var localDipRect = LassoGeometryHelper.ComputeRectangleBox(_dragStartDip, _dragCurrentDip, boundsDip, padding: 2.0);

        if (localDipRect is null)
        {
            SelectionPreviewOuter.Visibility = Visibility.Collapsed;
            SelectionSizeBadge.Visibility = Visibility.Collapsed;
            DimBackdropPath.Visibility = Visibility.Collapsed;
            if (cancelIfTooSmall)
                CancelCapture();
            return;
        }

        // Compute exact physical screen capture rectangle directly from Win32 GetCursorPos coordinates
        // (avoids mixed-DPI multi-monitor WPF PointToScreen drift), falling back to PointToScreen if unavailable.
        Int32Rect physicalRect;
        var vsBounds = _virtualScreenPhysical.Width > 0 && _virtualScreenPhysical.Height > 0
            ? _virtualScreenPhysical
            : MonitorService.GetVirtualScreenPhysicalBounds();
        var physBox = _hasStartPhysical && _hasCurrentPhysical
            ? LassoGeometryHelper.ComputeRectangleBox(_dragStartPhysical, _dragCurrentPhysical, vsBounds, padding: 2.0)
            : null;

        if (physBox is not null)
        {
            physicalRect = new Int32Rect(
                physBox.Value.X,
                physBox.Value.Y,
                Math.Max(16, physBox.Value.Width),
                Math.Max(16, physBox.Value.Height));
        }
        else
        {
            Point topLeftPhysical = PointToScreen(new Point(localDipRect.Value.X, localDipRect.Value.Y));
            Point bottomRightPhysical = PointToScreen(new Point(
                localDipRect.Value.X + localDipRect.Value.Width,
                localDipRect.Value.Y + localDipRect.Value.Height));

            physicalRect = new Int32Rect(
                (int)Math.Round(topLeftPhysical.X),
                (int)Math.Round(topLeftPhysical.Y),
                Math.Max(16, (int)Math.Round(bottomRightPhysical.X - topLeftPhysical.X)),
                Math.Max(16, (int)Math.Round(bottomRightPhysical.Y - topLeftPhysical.Y)));
        }

        var screenDipRect = new Rect(
            Left + localDipRect.Value.X,
            Top + localDipRect.Value.Y,
            localDipRect.Value.Width,
            localDipRect.Value.Height);

        _completed = true;
        Opacity = 0;
        Hide();
        // Wait for DWM compositor to clear overlay completely before BitBlt screen capture
        await Task.Delay(65);
        Close();

        _tcs?.TrySetResult(new LassoCaptureResult(physicalRect, screenDipRect, intent));
    }

    private void CancelCapture()
    {
        if (_isDragging)
        {
            _isDragging = false;
            _isRightButtonDrag = false;
            ReleaseMouseCapture();
        }
        _completed = true;
        Close();
        _tcs?.TrySetResult(null);
    }

    private void OnRenderFrame(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        int lifetimeMs = Math.Clamp(_settings.CursorTrailLengthMs, 80, 1200);
        double maxThickness = Math.Clamp(_settings.CursorTrailThickness, 1.0, 10.0);
        double maxOpacity = Math.Clamp(_settings.CursorTrailOpacity, 0.1, 1.0);

        while (_trailSamples.Count > 0 && (now - _trailSamples[0].TimestampMs) > lifetimeMs)
        {
            _trailSamples.RemoveAt(0);
        }

        int segmentCount = Math.Max(0, _trailSamples.Count - 1);
        EnsureLinePoolSize(segmentCount);

        int activeIndex = 0;
        for (int i = 1; i < _trailSamples.Count; i++)
        {
            var prev = _trailSamples[i - 1];
            var curr = _trailSamples[i];

            double age = now - curr.TimestampMs;
            double lifeRatio = Math.Clamp(1.0 - (age / lifetimeMs), 0.0, 1.0);
            if (lifeRatio <= 0.01) continue;

            var line = _linePool[activeIndex++];
            line.X1 = prev.Position.X;
            line.Y1 = prev.Position.Y;
            line.X2 = curr.Position.X;
            line.Y2 = curr.Position.Y;
            line.StrokeThickness = Math.Max(0.9, maxThickness * lifeRatio);

            int paletteIdx = Math.Clamp((int)Math.Round(lifeRatio * maxOpacity * (WhiteAlphaBrushes.Length - 1)), 0, WhiteAlphaBrushes.Length - 1);
            line.Stroke = WhiteAlphaBrushes[paletteIdx];
            line.Visibility = Visibility.Visible;
        }

        for (int i = activeIndex; i < _linePool.Count; i++)
        {
            _linePool[i].Visibility = Visibility.Collapsed;
        }
    }

    private void EnsureLinePoolSize(int requiredCount)
    {
        while (_linePool.Count < requiredCount)
        {
            var line = new Line
            {
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Visibility = Visibility.Collapsed
            };
            _linePool.Add(line);
            TrailCanvas.Children.Add(line);
        }
    }

    private static SolidColorBrush[] CreateAlphaBrushPalette()
    {
        const int steps = 32;
        var palette = new SolidColorBrush[steps];
        for (int i = 0; i < steps; i++)
        {
            byte alpha = (byte)Math.Round(255.0 * i / (steps - 1));
            var brush = new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));
            brush.Freeze();
            palette[i] = brush;
        }
        return palette;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }
}
