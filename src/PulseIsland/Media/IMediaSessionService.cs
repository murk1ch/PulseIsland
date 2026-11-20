using PulseIsland.Core;

namespace PulseIsland.Media;

public interface IMediaSessionService : IDisposable
{
    MediaTrackInfo CurrentTrack { get; }
    event Action<MediaTrackInfo>? TrackChanged;

    Task StartAsync();
    Task TogglePlayPauseAsync();
    Task NextTrackAsync();
    Task PreviousTrackAsync();
    Task SeekToAsync(double positionSeconds);
}
