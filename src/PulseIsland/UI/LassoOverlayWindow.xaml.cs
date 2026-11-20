using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PulseIsland.Core;
using PulseIsland.Interop;

namespace PulseIsland.UI;

public partial class LassoOverlayWindow : Window
{
    private readonly record struct TrailSample(Point Position, long TimestampMs);

    private static readonly SolidColorBrush[] WhiteAlphaBrushes = CreateAlphaBrushPalette();

    private readonly AppSettings _settings;
    private readonly List<TrailSample> _trailSamples = new(128);
    private readonly List<Point> _lassoPoints = new(256);
    private readonly List<Line> _linePool = new(128);
    private bool _isDragging;
    private bool _completed;

    private TaskCompletionSource<(Int32Rect PhysicalRect, Rect DipRect)?>? _tcs;

    public LassoOverlayWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        Loaded += OnLoaded;
        Closed += OnClosed;
        MouseMove += OnMouseMove;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseRightButtonDown += (_, _) => CancelCapture();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                CancelCapture();
        };
    }

    public Task<(Int32Rect PhysicalRect, Rect DipRect)?> CaptureSelectionAsync()
    {
        _tcs = new TaskCompletionSource<(Int32Rect, Rect)?>();
        Show();
        Activate();
        Focus();
        return _tcs.Task;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var vs = MonitorService.GetVirtualScreenPhysicalBounds();
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

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        Point pos = e.GetPosition(OverlayCanvas);

        CursorDot.Visibility = Visibility.Visible;
        Canvas.SetLeft(CursorDot, pos.X - CursorDot.Width * 0.5);
        Canvas.SetTop(CursorDot, pos.Y - CursorDot.Height * 0.5);

        _trailSamples.Add(new TrailSample(pos, Environment.TickCount64));

        if (_isDragging)
        {
            if (_lassoPoints.Count == 0 || (pos - _lassoPoints[^1]).Length >= 1.5)
            {
                _lassoPoints.Add(pos);
                LassoPolyline.Points.Add(pos);
                UpdateLiveSelectionBox();
            }
        }
    }

    private void UpdateLiveSelectionBox()
    {
        var boundsDip = new Rect(0, 0, Math.Max(100, ActualWidth), Math.Max(100, ActualHeight));
        var box = LassoGeometryHelper.ComputeBoundingBox(_lassoPoints, boundsDip, padding: 14.0);
        if (box is null)
        {
            SelectionPreviewBorder.Visibility = Visibility.Collapsed;
            return;
        }

        SelectionPreviewBorder.Visibility = Visibility.Visible;
        SelectionPreviewBorder.Width = box.Value.Width;
        SelectionPreviewBorder.Height = box.Value.Height;
        Canvas.SetLeft(SelectionPreviewBorder, box.Value.X);
        Canvas.SetTop(SelectionPreviewBorder, box.Value.Y);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _lassoPoints.Clear();
        LassoPolyline.Points.Clear();
        SelectionPreviewBorder.Visibility = Visibility.Collapsed;

        Point pos = e.GetPosition(OverlayCanvas);
        _lassoPoints.Add(pos);
        LassoPolyline.Points.Add(pos);
        CaptureMouse();
    }

    private async void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        ReleaseMouseCapture();

        Point pos = e.GetPosition(OverlayCanvas);
        _lassoPoints.Add(pos);

        var boundsDip = new Rect(0, 0, Math.Max(100, ActualWidth), Math.Max(100, ActualHeight));
        var localDipRect = LassoGeometryHelper.ComputeBoundingBox(_lassoPoints, boundsDip, padding: 14.0);

        if (localDipRect is null)
        {
            _lassoPoints.Clear();
            LassoPolyline.Points.Clear();
            SelectionPreviewBorder.Visibility = Visibility.Collapsed;
            return;
        }

        Point topLeftPhysical = PointToScreen(new Point(localDipRect.Value.X, localDipRect.Value.Y));
        Point bottomRightPhysical = PointToScreen(new Point(
            localDipRect.Value.X + localDipRect.Value.Width,
            localDipRect.Value.Y + localDipRect.Value.Height));

        var physicalRect = new Int32Rect(
            (int)Math.Round(topLeftPhysical.X),
            (int)Math.Round(topLeftPhysical.Y),
            Math.Max(16, (int)Math.Round(bottomRightPhysical.X - topLeftPhysical.X)),
            Math.Max(16, (int)Math.Round(bottomRightPhysical.Y - topLeftPhysical.Y)));

        var screenDipRect = new Rect(
            Left + localDipRect.Value.X,
            Top + localDipRect.Value.Y,
            localDipRect.Value.Width,
            localDipRect.Value.Height);

        _completed = true;
        Opacity = 0;
        Hide();
        // Wait for DWM to clear overlay completely before BitBlt screen capture
        await Task.Delay(75);
        Close();

        _tcs?.TrySetResult((physicalRect, screenDipRect));
    }

    private void CancelCapture()
    {
        if (_isDragging)
        {
            _isDragging = false;
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
}
