---
name: naudio-wasapi-loopback
description: >-
  Use this skill when implementing or debugging PulseIsland's zero-NuGet native COM
  WASAPI loopback audio capture (WasapiComInterop.cs, WasapiAudioCaptureService.cs)
  and Core Audio active-music session filtering (AudioSessionMusicFilter.cs).
---

# Native COM WASAPI Loopback & Music-Only Session Filtering

## 1. Zero-NuGet COM WASAPI Capture (`Audio/WasapiComInterop.cs`, `Audio/WasapiAudioCaptureService.cs`)
PulseIsland captures system audio without NAudio NuGet dependencies using direct COM vtable interfaces:
- `IMMDeviceEnumerator`, `IMMDevice`, `IAudioClient`, `IAudioCaptureClient`, `IMMNotificationClient`.
- **Audio Format:** WASAPI loopback outputs `32-bit IEEE Float` (`WAVEFORMATEX`), multi-channel (typically 2 ch @ 48 kHz). Mix channels down to mono float samples into a lock-protected ring buffer before feeding `FftSpectrumProcessor`.
- **Thread Safety:** Never touch WPF UI controls or allocate large buffers on the capture thread; handle `AUDCLNT_E_DEVICE_INVALIDATED` (`0x88890004`) and `IMMNotificationClient.OnDefaultDeviceChanged` cleanly.

## 2. Music-Only Filtering (`Audio/AudioSessionMusicFilter.cs`)
To prevent visualizer waves from reacting to Discord voice chat, browser calls, or system notifications when no music is playing:
- Enumerate active sessions via `IAudioSessionManager2` → `IAudioSessionEnumerator` → `IAudioSessionControl2` + `IAudioMeterInformation`.
- Ignore voice/communication apps (e.g., `discord`, `teams`, `zoom`, `skype`, `telegram`) and check `MediaTrackInfo.HasActiveSession && MediaTrackInfo.IsPlaying` from SMTC alongside active non-communication audio peak meters.
- When `HideWavesOnSilence` is enabled or only voice-chat audio is active, decay FFT bands smoothly to zero (`FftSpectrumProcessor.DecayToZero`).
