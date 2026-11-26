---
name: naudio-wasapi-loopback
description: >-
  Use this skill when implementing or debugging PulseIsland's zero-NuGet native COM
  WASAPI loopback capture, IPropertyStore endpoint naming, IPolicyConfig output
  switching, active mic capture session tracking, and music-only session filtering.
---

# Native COM WASAPI Loopback, Endpoint Monitor & Music Filtering

PulseIsland integrates with Windows Core Audio directly via COM vtables with **zero external NuGet dependencies** (no NAudio package required).

## 1. COM Interop Layer (`Audio/WasapiComInterop.cs`)
- Declare `[PreserveSig]` on COM interface methods (`IMMDeviceEnumerator`, `IMMDeviceCollection`, `IMMDevice`, `IPropertyStore`, `IPolicyConfig`, `IAudioClient`, `IAudioCaptureClient`, `IAudioEndpointVolume`, `IAudioMeterInformation`, `IAudioSessionManager2`, `IAudioSessionControl`/`2`) so `HRESULT` codes are checked cleanly without throwing exceptions across COM boundaries.
- Use `[StructLayout(LayoutKind.Sequential, Pack = 1)]` on `WAVEFORMATEX` (18 bytes) and `WAVEFORMATEXTENSIBLE` so `SubFormat` aligns at exact native offset `24`.
- **Real Endpoint Names:** Query `IMMDevice.OpenPropertyStore` -> `IPropertyStore.GetValue` with `PKEY_Device_FriendlyName` / `PKEY_Device_DeviceDesc` into `PropVariant`, and always free via `ole32.dll` `PropVariantClear(ref pv)`.
- **1-Click Output Switching:** Instantiate `PolicyConfigClientComObject` (`IPolicyConfig`) and call `SetDefaultEndpoint(deviceId, role)` for `eConsole`, `eMultimedia`, and `eCommunications`.

## 2. Loopback Capture (`Audio/WasapiAudioCaptureService.cs`)
- Guard `Start(deviceId)` and `Stop()` with `_lifecycleLock` (`StopLocked`) so endpoint hot-swaps and shutdowns are completely thread-safe.
- When no device ID is pinned, capture from all active render endpoints simultaneously so music animates regardless of whether the player routes to Speakers, Headphones, or HDMI.
- Convert `IEEE Float`, `PCM32`, `PCM24`, and `PCM16` frames to mono float samples in a `_bufferLock`-protected ring buffer for `FftSpectrumProcessor`.
- Never touch WPF UI controls on capture threads; handle `AUDCLNT_E_DEVICE_INVALIDATED` and `IMMNotificationClient.OnDefaultDeviceChanged` by scheduling a clean restart.

## 3. Endpoint Volume, Output Switcher & Active Mic Tracking (`Audio/AudioEndpointMonitorService.cs`)
- Initialize Core Audio COM on an MTA background thread (`Task.Run`) to avoid STA marshaling stalls.
- Always swap and unregister `IAudioEndpointVolumeCallback` **outside** `_syncLock` (`DetachEndpointsSafe`) to prevent AB-BA deadlocks with `AudioSes.dll` RPC callbacks.
- Poll active capture sessions via `IAudioSessionManager2` -> `IAudioSessionControl2` (`PollActiveCaptureSessionsSafe`) to detect when an app is actively recording the microphone and display a friendly app label (`FormatFriendlyProcessLabel`), while exposing `CycleDefaultRenderDevice()` for 1-click output switching.

## 4. Music-Only Session Filtering (`Audio/AudioSessionMusicFilter.cs`)
- Ignore voice/communication apps (e.g., Discord, Teams, Zoom, Skype, Telegram) and combine non-communication peak meters with SMTC `MediaTrackInfo.HasActiveSession && IsPlaying` so voice chats do not animate music waves.
- Decay spectrum bars smoothly (`FftSpectrumProcessor.DecayToZero`) when silent or only voice-chat audio is active.
