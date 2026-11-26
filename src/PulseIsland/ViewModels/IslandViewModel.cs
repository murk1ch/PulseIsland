using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using PulseIsland.Audio;
using PulseIsland.Core;
using PulseIsland.Services;

namespace PulseIsland.ViewModels;

public sealed class IslandViewModel : INotifyPropertyChanged
{
    private IslandState _state = IslandState.Compact;
    private MediaTrackInfo _trackInfo = new(string.Empty, string.Empty, false, false, null, Color.FromRgb(0x4C, 0xC9, 0xF0));
    private bool _showMusicInCompact;
    private bool _isMusicSectionVisible = true;
    private bool _isTranslationSectionVisible = true;
    private string _statusMessage = string.Empty;
    private bool _isTranslating;

    private bool _showTrackProgressBar = true;
    private bool _isSmartClipboardEnabled = true;
    private bool _isVolumeAndMicEnabled = true;
    private bool _isFileDropZoneEnabled = true;
    private bool _isSystemLoadMonitorEnabled = true;
    private bool _showCpuInIsland = true;
    private bool _showRamInIsland = true;
    private bool _showTimeDateInIsland = true;
    private bool _showNetworkSpeedInIsland;
    private bool _showBatteryInIsland;
    private bool _showUptimeInIsland;
    private SystemLoadSnapshot _lastSystemSnapshot;
    private AudioEndpointState _audioState = new(50, false, false, true, true);
    private bool _isVolumeHudActive;
    private string _volumeHudTitle = string.Empty;
    private double _volumeHudFraction;

    // ── Island Notifications ──
    private bool _isNotificationsEnabled = true;
    private IslandNotification? _activeNotification;
    private bool _isNotificationHistoryVisible;

    // ── Wireless Mouse Battery ──
    private bool _isMouseBatteryEnabled;
    private bool _showMouseBatteryInCompact = true;
    private int _mouseLowBatteryThreshold = 15;
    private MouseBatterySnapshot _lastMouseBattery = new(false, 0, false, string.Empty, LocalizationService.Get("Loc_Auto"));

    public event PropertyChangedEventHandler? PropertyChanged;

    public IslandViewModel()
    {
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(AppLanguage _)
    {
        RefreshLocalizedProperties();
    }

    public void RefreshLocalizedProperties()
    {
        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp is not null && !disp.CheckAccess())
        {
            disp.BeginInvoke(RefreshLocalizedProperties);
            return;
        }

        if (_aiBackendBadge is "Не скачана" or "Not downloaded")
            AiBackendBadge = LocalizationService.Pick("Not downloaded", "Не скачана");
        if (_aiStatusLine is "Готов (Qwen 2.5 3B)" or "Ready (Qwen 2.5 3B)")
            AiStatusLine = LocalizationService.Pick("Ready (Qwen 2.5 3B)", "Готов (Qwen 2.5 3B)");

        OnPropertyChanged(nameof(AudioOutputShortLabel));
        OnPropertyChanged(nameof(AudioOutputTooltip));
        OnPropertyChanged(nameof(CompactMicTooltip));
        OnPropertyChanged(nameof(SpeakerLabel));
        OnPropertyChanged(nameof(MicLabel));
        OnPropertyChanged(nameof(MouseBatteryTooltip));
        OnPropertyChanged(nameof(ExpandedTrackTitle));
        OnPropertyChanged(nameof(ExpandedTrackArtist));
        OnPropertyChanged(nameof(CompactHeaderText));
        RebuildSystemIndicatorBadges();
    }

    public IslandState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSettingsButtonVisible));
            OnPropertyChanged(nameof(IsCompact));
            OnPropertyChanged(nameof(IsExpanded));
            OnPropertyChanged(nameof(IsHidden));
        }
    }

    public bool IsHidden => State == IslandState.Hidden;
    public bool IsCompact => State == IslandState.Compact;
    public bool IsExpanded => State == IslandState.Expanded;

    // Strict requirement: Settings button is visible ONLY when the island is expanded.
    public bool IsSettingsButtonVisible => State == IslandState.Expanded;

    public MediaTrackInfo TrackInfo
    {
        get => _trackInfo;
        set
        {
            if (Equals(_trackInfo, value)) return;
            _trackInfo = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTrackTitle));
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(DisplayTrackText));
            OnPropertyChanged(nameof(CompactHeaderText));
            OnPropertyChanged(nameof(ExpandedTrackTitle));
            OnPropertyChanged(nameof(ExpandedTrackArtist));
            OnPropertyChanged(nameof(PlayPauseGlyph));
            OnPropertyChanged(nameof(TrackPositionText));
            OnPropertyChanged(nameof(TrackDurationText));
            OnPropertyChanged(nameof(TrackProgressFraction));
            OnPropertyChanged(nameof(IsProgressBarVisible));
            OnPropertyChanged(nameof(ShowCompactMouseBattery));
        }
    }

    public bool ShowTrackProgressBar
    {
        get => _showTrackProgressBar;
        set
        {
            if (_showTrackProgressBar == value) return;
            _showTrackProgressBar = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsProgressBarVisible));
        }
    }

    public bool IsProgressBarVisible => _showTrackProgressBar && _trackInfo.DurationSeconds > 1.0;

    public string TrackPositionText => FormatDuration(_trackInfo.PositionSeconds);
    public string TrackDurationText => FormatDuration(_trackInfo.DurationSeconds);
    public double TrackProgressFraction => _trackInfo.DurationSeconds > 0.1
        ? Math.Clamp(_trackInfo.PositionSeconds / _trackInfo.DurationSeconds, 0.0, 1.0)
        : 0.0;

    public bool IsSmartClipboardEnabled
    {
        get => _isSmartClipboardEnabled;
        set
        {
            if (_isSmartClipboardEnabled == value) return;
            _isSmartClipboardEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool IsVolumeAndMicEnabled
    {
        get => _isVolumeAndMicEnabled;
        set
        {
            if (_isVolumeAndMicEnabled == value) return;
            _isVolumeAndMicEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowCompactActiveMicPill));
            OnPropertyChanged(nameof(CanSwitchAudioOutput));
        }
    }

    private bool _isDragOverFileDropZone;

    public bool IsFileDropZoneEnabled
    {
        get => _isFileDropZoneEnabled;
        set
        {
            if (_isFileDropZoneEnabled == value) return;
            _isFileDropZoneEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFileDropZoneVisible));
        }
    }

    public bool IsDragOverFileDropZone
    {
        get => _isDragOverFileDropZone;
        set
        {
            if (_isDragOverFileDropZone == value) return;
            _isDragOverFileDropZone = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFileDropZoneVisible));
        }
    }

    public bool IsFileDropZoneVisible => _isFileDropZoneEnabled && (HasDroppedFiles || _isDragOverFileDropZone);

    public AudioEndpointState AudioState
    {
        get => _audioState;
        set
        {
            if (_audioState == value) return;
            _audioState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeakerLabel));
            OnPropertyChanged(nameof(MicLabel));
            OnPropertyChanged(nameof(IsMicMuted));
            OnPropertyChanged(nameof(IsSpeakerMuted));
            OnPropertyChanged(nameof(IsMicActivelyCapturing));
            OnPropertyChanged(nameof(ShowCompactActiveMicPill));
            OnPropertyChanged(nameof(CompactMicTooltip));
            OnPropertyChanged(nameof(CanSwitchAudioOutput));
            OnPropertyChanged(nameof(AudioOutputShortLabel));
            OnPropertyChanged(nameof(AudioOutputTooltip));
        }
    }

    public bool IsSpeakerMuted => _audioState.IsSpeakerMuted;
    public bool IsMicMuted => _audioState.IsMicMuted;
    public bool IsMicActivelyCapturing => _audioState.IsMicActivelyCapturing;
    public bool ShowCompactActiveMicPill => _isVolumeAndMicEnabled && _audioState.HasMicDevice && _audioState.IsMicActivelyCapturing;
    public bool CanSwitchAudioOutput => _isVolumeAndMicEnabled && _audioState.RenderDeviceCount >= 2;

    public string AudioOutputShortLabel => string.IsNullOrWhiteSpace(_audioState.CurrentRenderShortName)
        ? LocalizationService.Pick("Output", "Выход")
        : _audioState.CurrentRenderShortName;

    public string AudioOutputTooltip => string.IsNullOrWhiteSpace(_audioState.CurrentRenderFullName)
        ? LocalizationService.Pick("Switch default audio output", "Переключить аудиовыход по умолчанию")
        : LocalizationService.Pick(
            $"Audio output: {_audioState.CurrentRenderFullName} (click to switch)",
            $"Аудиовыход: {_audioState.CurrentRenderFullName} (нажмите, чтобы переключить)");

    public string CompactMicTooltip
    {
        get
        {
            string app = string.IsNullOrWhiteSpace(_audioState.ActiveMicAppName)
                ? LocalizationService.Pick("App", "Приложение")
                : _audioState.ActiveMicAppName;
            return _audioState.IsMicMuted
                ? LocalizationService.Pick($"Microphone muted ({app}) — click to unmute", $"Микрофон выключен ({app}) — нажмите, чтобы включить")
                : LocalizationService.Pick($"Microphone in use ({app}) — click to mute", $"Микрофон используется ({app}) — нажмите, чтобы выключить");
        }
    }

    public string SpeakerLabel => _audioState.IsSpeakerMuted
        ? LocalizationService.Pick("Muted", "Без звука")
        : $"{_audioState.MasterVolumePercent}%";

    public string MicLabel
    {
        get
        {
            if (!_audioState.HasMicDevice)
                return LocalizationService.Pick("Mic N/A", "Мик. н/д");

            if (_audioState.IsMicActivelyCapturing && !string.IsNullOrWhiteSpace(_audioState.ActiveMicAppName))
            {
                return _audioState.IsMicMuted
                    ? LocalizationService.Pick($"Mic off • {_audioState.ActiveMicAppName}", $"Мик. выкл • {_audioState.ActiveMicAppName}")
                    : LocalizationService.Pick($"Mic • {_audioState.ActiveMicAppName}", $"Мик. • {_audioState.ActiveMicAppName}");
            }

            return _audioState.IsMicMuted
                ? LocalizationService.Pick("Mic off", "Мик. выкл")
                : LocalizationService.Pick("Mic on", "Мик. вкл");
        }
    }

    public bool IsVolumeHudActive
    {
        get => _isVolumeHudActive;
        set
        {
            if (_isVolumeHudActive == value) return;
            _isVolumeHudActive = value;
            OnPropertyChanged();
        }
    }

    public string VolumeHudTitle
    {
        get => _volumeHudTitle;
        set
        {
            if (_volumeHudTitle == value) return;
            _volumeHudTitle = value;
            OnPropertyChanged();
        }
    }

    public double VolumeHudFraction
    {
        get => _volumeHudFraction;
        set
        {
            if (Math.Abs(_volumeHudFraction - value) < 0.001) return;
            _volumeHudFraction = value;
            OnPropertyChanged();
        }
    }

    public bool IsPlaying => _trackInfo.IsPlaying;

    // ── Island Notifications Properties ──
    public bool IsNotificationsEnabled
    {
        get => _isNotificationsEnabled;
        set
        {
            if (_isNotificationsEnabled == value) return;
            _isNotificationsEnabled = value;
            OnPropertyChanged();
        }
    }

    public IslandNotification? ActiveNotification
    {
        get => _activeNotification;
        set
        {
            if (_activeNotification == value) return;
            _activeNotification = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasActiveNotification));
            OnPropertyChanged(nameof(HasActiveNotificationTag));
            OnPropertyChanged(nameof(NotificationKindColor));
            OnPropertyChanged(nameof(NotificationKindLabel));
        }
    }

    public bool HasActiveNotification => _activeNotification is not null;
    public bool HasActiveNotificationTag => _activeNotification?.HasContentTag == true;

    public bool IsNotificationHistoryVisible
    {
        get => _isNotificationHistoryVisible;
        set
        {
            if (_isNotificationHistoryVisible == value) return;
            _isNotificationHistoryVisible = value;
            OnPropertyChanged();
        }
    }

    public string NotificationKindColor => _activeNotification?.Kind switch
    {
        NotificationKind.Success => "#22C55E",
        NotificationKind.Warning => "#F59E0B",
        NotificationKind.Error   => "#EF4444",
        NotificationKind.Music   => "#38BDF8",
        NotificationKind.System  => "#A78BFA",
        NotificationKind.Info    => "#60A5FA",
        _                        => "#94A3B8"
    };

    public string NotificationKindLabel => _activeNotification?.Kind switch
    {
        NotificationKind.Success => "✓",
        NotificationKind.Warning => "!",
        NotificationKind.Error   => "✕",
        NotificationKind.Music   => "♪",
        NotificationKind.System  => "⚙",
        NotificationKind.Info    => "i",
        _                        => "•"
    };

    public ObservableCollection<IslandNotification> NotificationHistory { get; } = new();

    public bool HasNotificationHistory => NotificationHistory.Count > 0;
    public string NotificationCountText => NotificationHistory.Count > 99 ? "99+" : NotificationHistory.Count.ToString();

    private bool _isCompactNotificationActive;
    public bool IsCompactNotificationActive
    {
        get => _isCompactNotificationActive;
        set
        {
            if (_isCompactNotificationActive == value) return;
            _isCompactNotificationActive = value;
            OnPropertyChanged();
        }
    }

    public void SeedNotificationHistory(IEnumerable<IslandNotification> items)
    {
        NotificationHistory.Clear();
        foreach (var item in items.Take(50))
            NotificationHistory.Add(item);
        OnPropertyChanged(nameof(HasNotificationHistory));
        OnPropertyChanged(nameof(NotificationCountText));
    }

    public void PushNotification(IslandNotification notification)
    {
        ActiveNotification = notification;
        IsCompactNotificationActive = true;
        NotificationHistory.Insert(0, notification);
        while (NotificationHistory.Count > 50)
            NotificationHistory.RemoveAt(NotificationHistory.Count - 1);
        OnPropertyChanged(nameof(HasNotificationHistory));
        OnPropertyChanged(nameof(NotificationCountText));
    }

    public void DismissNotification()
    {
        ActiveNotification = null;
        IsCompactNotificationActive = false;
    }

    public void ClearNotificationHistory()
    {
        NotificationHistory.Clear();
        OnPropertyChanged(nameof(HasNotificationHistory));
        OnPropertyChanged(nameof(NotificationCountText));
    }

    public bool ShowMusicInCompact
    {
        get => _showMusicInCompact;
        set
        {
            if (_showMusicInCompact == value) return;
            _showMusicInCompact = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CompactHeaderText));
            OnPropertyChanged(nameof(ShowCompactMouseBattery));
        }
    }

    public bool IsMouseBatteryEnabled
    {
        get => _isMouseBatteryEnabled;
        set
        {
            if (_isMouseBatteryEnabled == value) return;
            _isMouseBatteryEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowCompactMouseBattery));
        }
    }

    public bool ShowMouseBatteryInCompact
    {
        get => _showMouseBatteryInCompact;
        set
        {
            if (_showMouseBatteryInCompact == value) return;
            _showMouseBatteryInCompact = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowCompactMouseBattery));
        }
    }

    public bool HasMouseBattery => _lastMouseBattery.HasDevice;
    public bool IsMouseCharging => _lastMouseBattery.IsCharging;
    public bool IsMouseBatteryLow => _lastMouseBattery.HasDevice && !_lastMouseBattery.IsCharging && _lastMouseBattery.Percent <= _mouseLowBatteryThreshold;
    public string MouseBatteryText
    {
        get
        {
            if (!_lastMouseBattery.HasDevice)
                return "--%";
            if (_lastMouseBattery.IsCharging && _lastMouseBattery.Percent <= 0)
                return "USB";
            return $"{_lastMouseBattery.Percent}%";
        }
    }

    public string MouseBatteryTooltip
    {
        get
        {
            if (!_lastMouseBattery.HasDevice)
                return LocalizationService.Pick(
                    "Wireless mouse not detected or does not report battery via selected protocol",
                    "Беспроводная мышь не обнаружена или не передаёт заряд по выбранному протоколу");

            string name = string.IsNullOrWhiteSpace(_lastMouseBattery.DeviceName)
                ? LocalizationService.Pick("Wireless mouse", "Беспроводная мышь")
                : _lastMouseBattery.DeviceName;

            if (_lastMouseBattery.IsCharging && _lastMouseBattery.Percent <= 0)
                return LocalizationService.Pick(
                    $"{name} ({_lastMouseBattery.ConnectionLabel}): Charging via cable (exact % updates over 2.4 GHz when unplugged)",
                    $"{name} ({_lastMouseBattery.ConnectionLabel}): Заряжается по кабелю (точный % обновится по 2.4 ГГц после отключения провода)");

            string chargingSuffix = _lastMouseBattery.IsCharging
                ? LocalizationService.Pick(" • Charging", " • Заряжается")
                : string.Empty;
            return $"{name} ({_lastMouseBattery.ConnectionLabel}): {_lastMouseBattery.Percent}%{chargingSuffix}";
        }
    }

    public bool ShowCompactMouseBattery =>
        _isMouseBatteryEnabled &&
        _showMouseBatteryInCompact &&
        _lastMouseBattery.HasDevice &&
        !(HasTrackTitle && ShowMusicInCompact);

    public void UpdateMouseBattery(MouseBatterySnapshot snapshot)
    {
        _lastMouseBattery = snapshot;
        OnPropertyChanged(nameof(HasMouseBattery));
        OnPropertyChanged(nameof(IsMouseCharging));
        OnPropertyChanged(nameof(IsMouseBatteryLow));
        OnPropertyChanged(nameof(MouseBatteryText));
        OnPropertyChanged(nameof(MouseBatteryTooltip));
        OnPropertyChanged(nameof(ShowCompactMouseBattery));
    }

    public ObservableCollection<string> SystemIndicatorBadges { get; } = new();

    public bool HasSystemIndicators => SystemIndicatorBadges.Count > 0;

    public void ConfigureSystemIndicators(AppSettings s)
    {
        _isSystemLoadMonitorEnabled = s.EnableSystemLoadMonitor;
        _showCpuInIsland = s.ShowCpuInIsland;
        _showRamInIsland = s.ShowRamInIsland;
        _showTimeDateInIsland = s.ShowTimeDateInIsland;
        _showNetworkSpeedInIsland = s.ShowNetworkSpeedInIsland;
        _showBatteryInIsland = s.ShowBatteryInIsland;
        _showUptimeInIsland = s.ShowUptimeInIsland;
        _isMouseBatteryEnabled = s.EnableMouseBatteryIndicator;
        _showMouseBatteryInCompact = s.ShowMouseBatteryInCompact;
        _mouseLowBatteryThreshold = Math.Clamp(s.MouseLowBatteryThresholdPercent, 5, 50);
        RebuildSystemIndicatorBadges();
        OnPropertyChanged(nameof(IsMouseBatteryEnabled));
        OnPropertyChanged(nameof(ShowMouseBatteryInCompact));
        OnPropertyChanged(nameof(IsMouseBatteryLow));
        OnPropertyChanged(nameof(ShowCompactMouseBattery));
    }

    public void UpdateSystemLoad(SystemLoadSnapshot snapshot)
    {
        _lastSystemSnapshot = snapshot;
        RebuildSystemIndicatorBadges();
    }

    private void RebuildSystemIndicatorBadges()
    {
        var items = new List<string>();
        if (_isSystemLoadMonitorEnabled)
        {
            if (_showCpuInIsland)
                items.Add($"CPU {_lastSystemSnapshot.CpuPercent}%");
            if (_showRamInIsland)
            {
                string gbUnit = LocalizationService.Pick("GB", "ГБ");
                string ram = _lastSystemSnapshot.TotalRamGb > 0.1
                    ? $"RAM {_lastSystemSnapshot.RamPercent}% ({_lastSystemSnapshot.UsedRamGb:0.#}/{_lastSystemSnapshot.TotalRamGb:0.#} {gbUnit})"
                    : $"RAM {_lastSystemSnapshot.RamPercent}%";
                items.Add(ram);
            }
            if (_showTimeDateInIsland)
                items.Add($"{DateTime.Now:HH:mm • dd.MM}");
            if (_showNetworkSpeedInIsland)
                items.Add($"↓ {FormatSpeed(_lastSystemSnapshot.DownloadKBps)}  ↑ {FormatSpeed(_lastSystemSnapshot.UploadKBps)}");
            if (_showBatteryInIsland)
            {
                items.Add(_lastSystemSnapshot.HasBattery
                    ? LocalizationService.Pick(
                        $"BAT {_lastSystemSnapshot.BatteryPercent}%{(_lastSystemSnapshot.IsCharging ? " (charging)" : string.Empty)}",
                        $"АКБ {_lastSystemSnapshot.BatteryPercent}%{(_lastSystemSnapshot.IsCharging ? " (зарядка)" : string.Empty)}")
                    : LocalizationService.Pick("Power: AC", "Питание: Сеть"));
            }
            if (_showUptimeInIsland && !string.IsNullOrWhiteSpace(_lastSystemSnapshot.UptimeText))
                items.Add(LocalizationService.Pick($"Uptime: {_lastSystemSnapshot.UptimeText}", $"Работа: {_lastSystemSnapshot.UptimeText}"));
        }

        SystemIndicatorBadges.Clear();
        foreach (var item in items)
            SystemIndicatorBadges.Add(item);

        OnPropertyChanged(nameof(HasSystemIndicators));
        OnPropertyChanged(nameof(CompactHeaderText));
    }

    private static string FormatSpeed(double kbps)
    {
        if (kbps >= 1024.0)
            return $"{kbps / 1024.0:0.#} {LocalizationService.Pick("MB/s", "МБ/с")}";
        return $"{kbps:0} {LocalizationService.Pick("KB/s", "КБ/с")}";
    }

    public bool IsMusicSectionVisible
    {
        get => _isMusicSectionVisible;
        set
        {
            if (_isMusicSectionVisible == value) return;
            _isMusicSectionVisible = value;
            OnPropertyChanged();
        }
    }

    public bool IsTranslationSectionVisible
    {
        get => _isTranslationSectionVisible;
        set
        {
            if (_isTranslationSectionVisible == value) return;
            _isTranslationSectionVisible = value;
            OnPropertyChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (_statusMessage == value) return;
            _statusMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStatusMessage));
        }
    }

    public bool IsTranslating
    {
        get => _isTranslating;
        set
        {
            if (_isTranslating == value) return;
            _isTranslating = value;
            OnPropertyChanged();
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(_statusMessage);

    // ── Local AI Assistant (Qwen 2.5 3B) ──
    private bool _isAiAssistantEnabled = true;
    private bool _isAiSectionVisible;
    private bool _isAiGenerating;
    private string _aiBackendBadge = LocalizationService.Pick("Not downloaded", "Не скачана");
    private string _aiStatusLine = LocalizationService.Pick("Ready (Qwen 2.5 3B)", "Готов (Qwen 2.5 3B)");
    private string _aiResponseText = string.Empty;

    public bool IsAiAssistantEnabled
    {
        get => _isAiAssistantEnabled;
        set
        {
            if (_isAiAssistantEnabled == value) return;
            _isAiAssistantEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool IsAiSectionVisible
    {
        get => _isAiSectionVisible;
        set
        {
            if (_isAiSectionVisible == value) return;
            _isAiSectionVisible = value;
            OnPropertyChanged();
        }
    }

    public bool IsAiGenerating
    {
        get => _isAiGenerating;
        set
        {
            if (_isAiGenerating == value) return;
            _isAiGenerating = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotAiGenerating));
        }
    }

    public bool IsNotAiGenerating => !_isAiGenerating;

    public string AiBackendBadge
    {
        get => _aiBackendBadge;
        set
        {
            if (_aiBackendBadge == value) return;
            _aiBackendBadge = value;
            OnPropertyChanged();
        }
    }

    public string AiStatusLine
    {
        get => _aiStatusLine;
        set
        {
            if (_aiStatusLine == value) return;
            _aiStatusLine = value;
            OnPropertyChanged();
        }
    }

    public string AiResponseText
    {
        get => _aiResponseText;
        set
        {
            if (_aiResponseText == value) return;
            _aiResponseText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAiResponse));
        }
    }

    public bool HasAiResponse => !string.IsNullOrWhiteSpace(_aiResponseText);
    public bool HasTrackTitle => _trackInfo.HasActiveSession && !string.IsNullOrWhiteSpace(_trackInfo.Title);

    public string DisplayTrackText => string.IsNullOrWhiteSpace(_trackInfo.Artist)
        ? _trackInfo.Title
        : $"{_trackInfo.Artist} — {_trackInfo.Title}";

    public string CompactHeaderText
    {
        get
        {
            if (HasTrackTitle && ShowMusicInCompact)
                return DisplayTrackText;

            if (!_isSystemLoadMonitorEnabled)
                return "PulseIsland  •  Alt+Q";

            var compactParts = new List<string>();
            if (_showCpuInIsland) compactParts.Add($"CPU {_lastSystemSnapshot.CpuPercent}%");
            if (_showRamInIsland) compactParts.Add($"RAM {_lastSystemSnapshot.RamPercent}%");
            if (_showTimeDateInIsland) compactParts.Add($"{DateTime.Now:HH:mm dd.MM}");
            if (_showNetworkSpeedInIsland) compactParts.Add($"↓{FormatSpeed(_lastSystemSnapshot.DownloadKBps)}");
            if (_showBatteryInIsland && _lastSystemSnapshot.HasBattery) compactParts.Add(LocalizationService.Pick($"BAT {_lastSystemSnapshot.BatteryPercent}%", $"АКБ {_lastSystemSnapshot.BatteryPercent}%"));
            if (_showUptimeInIsland && !string.IsNullOrWhiteSpace(_lastSystemSnapshot.UptimeText)) compactParts.Add(_lastSystemSnapshot.UptimeText);

            return compactParts.Count > 0
                ? string.Join(" • ", compactParts)
                : "PulseIsland  •  Alt+Q";
        }
    }

    public string ExpandedTrackTitle => HasTrackTitle
        ? _trackInfo.Title
        : LocalizationService.Pick("Waiting for music playback", "Ожидание воспроизведения музыки");

    public string ExpandedTrackArtist => HasTrackTitle
        ? (string.IsNullOrWhiteSpace(_trackInfo.Artist)
            ? LocalizationService.Pick("Active media session", "Активная медиа-сессия")
            : _trackInfo.Artist)
        : LocalizationService.Pick("Play a track in your browser, Spotify, or media player", "Включите трек в браузере, Spotify или плеере");

    public string PlayPauseGlyph => _trackInfo.IsPlaying ? "⏸" : "▶";

    public ObservableCollection<TranslationRecord> TranslationHistory { get; } = new();
    public ObservableCollection<TranslationRecord> FilteredTranslationHistory { get; } = new();
    public ObservableCollection<DropShelfItem> DroppedFiles { get; } = new();

    private string _translationSearchQuery = string.Empty;

    public string TranslationSearchQuery
    {
        get => _translationSearchQuery;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(_translationSearchQuery, next, StringComparison.Ordinal))
                return;
            _translationSearchQuery = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTranslationSearchQuery));
            RefreshFilteredTranslations();
        }
    }

    public bool HasTranslationSearchQuery => !string.IsNullOrWhiteSpace(_translationSearchQuery);
    public bool HasTranslationHistory => TranslationHistory.Count > 0;
    public bool ShowEmptyTranslationSearchHint => TranslationHistory.Count > 0 && FilteredTranslationHistory.Count == 0;

    public bool HasDroppedFiles => DroppedFiles.Count > 0;

    public void LoadPersistedTranslations()
    {
        var items = TranslationHistoryService.Load();
        TranslationHistory.Clear();
        foreach (var item in items)
            TranslationHistory.Add(item);
        RefreshFilteredTranslations();
    }

    public void AddTranslation(TranslationRecord record)
    {
        TranslationHistory.Insert(0, record);
        while (TranslationHistory.Count > 30)
        {
            TranslationHistory.RemoveAt(TranslationHistory.Count - 1);
        }
        TranslationHistoryService.Save(TranslationHistory);
        RefreshFilteredTranslations();
    }

    public void ClearTranslations()
    {
        TranslationHistory.Clear();
        TranslationHistoryService.Save(TranslationHistory);
        RefreshFilteredTranslations();
    }

    private void RefreshFilteredTranslations()
    {
        string q = _translationSearchQuery.Trim();
        FilteredTranslationHistory.Clear();
        foreach (var item in TranslationHistory)
        {
            if (string.IsNullOrEmpty(q) ||
                (item.SourceText?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) ||
                (item.TranslatedText?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) ||
                (item.DetectedSourceLanguage?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) ||
                (item.TargetLanguage?.Contains(q, StringComparison.OrdinalIgnoreCase) == true))
            {
                FilteredTranslationHistory.Add(item);
            }
        }
        OnPropertyChanged(nameof(HasTranslationHistory));
        OnPropertyChanged(nameof(ShowEmptyTranslationSearchHint));
    }

    public void AddDroppedFiles(IEnumerable<DropShelfItem> items)
    {
        foreach (var item in items)
        {
            if (!DroppedFiles.Any(x => string.Equals(x.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)))
            {
                DroppedFiles.Insert(0, item);
            }
        }
        while (DroppedFiles.Count > 12)
            DroppedFiles.RemoveAt(DroppedFiles.Count - 1);
        OnPropertyChanged(nameof(HasDroppedFiles));
        OnPropertyChanged(nameof(IsFileDropZoneVisible));
    }

    public void ClearDroppedFiles()
    {
        DroppedFiles.Clear();
        OnPropertyChanged(nameof(HasDroppedFiles));
        OnPropertyChanged(nameof(IsFileDropZoneVisible));
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
            return "0:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
