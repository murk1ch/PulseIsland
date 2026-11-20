using System.Collections.ObjectModel;
using System.Windows.Threading;
using PulseIsland.Core;

namespace PulseIsland.Services;

/// <summary>
/// Manages incoming notifications and notification history inside PulseIsland.
/// Separates initial Action Center backfill (History only) from live incoming notifications (Banner + History).
/// </summary>
public sealed class IslandNotificationService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _autoDismissTimer;
    private bool _disposed;

    public IslandNotification? ActiveNotification { get; private set; }
    public bool HasActiveNotification => ActiveNotification is not null;
    public ObservableCollection<IslandNotification> History { get; } = new();

    public event Action<IslandNotification>? NotificationArrived;
    public event Action? NotificationDismissed;
    public event Action? HistorySeeded;
    public event Action? HistoryChanged;

    public IslandNotificationService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _autoDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoDismissTimer.Tick += (_, _) =>
        {
            _autoDismissTimer.Stop();
            DismissActive();
        };
    }

    /// <summary>
    /// Seeds existing Action Center notifications into History silently without showing a toast banner.
    /// </summary>
    public void SeedHistory(IEnumerable<IslandNotification> existingItems)
    {
        if (_disposed) return;
        var list = existingItems.Take(50).ToList();
        if (list.Count == 0) return;

        _dispatcher.InvokeAsync(() =>
        {
            History.Clear();
            foreach (var item in list)
                History.Add(item);

            HistorySeeded?.Invoke();
            HistoryChanged?.Invoke();
        });
    }

    /// <summary>
    /// Pushes a live incoming notification into the island banner and prepends it to History.
    /// </summary>
    public void Push(IslandNotification notification)
    {
        if (_disposed) return;
        _dispatcher.InvokeAsync(() =>
        {
            ActiveNotification = notification;

            History.Insert(0, notification);
            while (History.Count > 50)
                History.RemoveAt(History.Count - 1);

            NotificationArrived?.Invoke(notification);
            HistoryChanged?.Invoke();

            _autoDismissTimer.Stop();
            _autoDismissTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(notification.AutoDismissSeconds, 2.0, 30.0));
            _autoDismissTimer.Start();
        });
    }

    public void DismissActive()
    {
        if (_disposed) return;
        _dispatcher.InvokeAsync(() =>
        {
            if (ActiveNotification is null) return;
            ActiveNotification = null;
            _autoDismissTimer.Stop();
            NotificationDismissed?.Invoke();
        });
    }

    public void ClearHistory()
    {
        if (_disposed) return;
        _dispatcher.InvokeAsync(() =>
        {
            History.Clear();
            HistoryChanged?.Invoke();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _autoDismissTimer.Stop();
    }
}
