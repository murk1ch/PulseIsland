using System.Runtime.InteropServices;
using PulseIsland.Services;
using static PulseIsland.Audio.WasapiComInterop;

namespace PulseIsland.Audio;

/// <summary>
/// Captures system loopback audio via Windows Core Audio (WASAPI) on background threads,
/// converts IEEE Float / PCM32 / PCM24 / PCM16 to mono float samples into a ring buffer,
/// and supports automatic multi-endpoint capture when no specific device ID is pinned.
/// </summary>
public sealed class WasapiAudioCaptureService : IDisposable
{
    private readonly object _bufferLock = new();
    private readonly object _lifecycleLock = new();
    private readonly float[] _ringBuffer;
    private int _writeIndex;
    private volatile int _sampleRate = 48000;
    private long _lastAudioTicksUtc;

    private readonly List<Thread> _captureThreads = new();
    private CancellationTokenSource? _cts;
    private string? _targetDeviceId;
    private volatile bool _isRunning;
    private volatile bool _disposed;
    private int _restartScheduled;

    private IMMDeviceEnumerator? _enumerator;
    private DeviceNotificationClient? _notificationClient;

    public WasapiAudioCaptureService(int ringBufferSize = 2048)
    {
        _ringBuffer = new float[ringBufferSize];
    }

    public int SampleRate => _sampleRate;
    public bool IsSilent => (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastAudioTicksUtc)) > TimeSpan.FromMilliseconds(450).Ticks;

    public void Start(string? deviceId = null)
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;

            StopLocked();
            _targetDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;

            var endpointsToCapture = new List<string?>();
            try
            {
                _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                _notificationClient = new DeviceNotificationClient(this);
                _enumerator.RegisterEndpointNotificationCallback(_notificationClient);

                if (_targetDeviceId is not null)
                {
                    endpointsToCapture.Add(_targetDeviceId);
                }
                else
                {
                    // Capture from default endpoint + any other active render endpoints so music is captured
                    // regardless of whether the player outputs to Speakers, Headphones, or Monitor HDMI.
                    var activeDevices = GetRenderDevices();
                    foreach (var dev in activeDevices)
                    {
                        if (!endpointsToCapture.Contains(dev.Id))
                            endpointsToCapture.Add(dev.Id);
                    }

                    if (endpointsToCapture.Count == 0)
                        endpointsToCapture.Add(null);
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"Could not initialize audio endpoint enumerator: {ex.Message}");
                if (endpointsToCapture.Count == 0)
                    endpointsToCapture.Add(_targetDeviceId);
            }

            _cts = new CancellationTokenSource();
            _isRunning = true;

            var token = _cts.Token;
            int idx = 0;
            foreach (var epId in endpointsToCapture)
            {
                string? capturedEndpointId = epId;
                var thread = new Thread(() => CaptureLoop(capturedEndpointId, token))
                {
                    IsBackground = true,
                    Name = $"PulseIsland.WasapiLoopback.{idx++}",
                    Priority = ThreadPriority.AboveNormal
                };
                _captureThreads.Add(thread);
                thread.Start();
            }
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        _isRunning = false;
        _cts?.Cancel();
        var threadsSnapshot = _captureThreads.ToList();
        _captureThreads.Clear();

        foreach (var t in threadsSnapshot)
        {
            try
            {
                t.Join(250);
            }
            catch
            {
                // Ignore join timeout on shutdown
            }
        }

        _cts?.Dispose();
        _cts = null;

        if (_enumerator is not null)
        {
            if (_notificationClient is not null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_notificationClient); } catch { }
            }
            try { Marshal.ReleaseComObject(_enumerator); } catch { }
            _enumerator = null;
            _notificationClient = null;
        }
    }

    public void CopyLatestSamples(Span<float> destination)
    {
        lock (_bufferLock)
        {
            int len = _ringBuffer.Length;
            int count = Math.Min(destination.Length, len);
            int start = (_writeIndex - count + len) % len;

            for (int i = 0; i < count; i++)
            {
                destination[i] = _ringBuffer[(start + i) % len];
            }
        }
    }

    public static IReadOnlyList<(string Id, string Name)> GetRenderDevices()
    {
        var detailed = GetRenderDevicesDetailed();
        var list = new List<(string Id, string Name)>(detailed.Count);
        foreach (var d in detailed)
        {
            string label = d.IsDefault ? $"{d.FullName} [{LocalizationService.Pick("Default", "По умолчанию")}]" : d.FullName;
            list.Add((d.Id, label));
        }
        return list;
    }

    public static IReadOnlyList<(string Id, string FullName, string ShortName, bool IsDefault)> GetRenderDevicesDetailed()
    {
        var list = new List<(string Id, string FullName, string ShortName, bool IsDefault)>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                string? defaultId = null;
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var defDev) == 0 && defDev is not null)
                {
                    try { defDev.GetId(out defaultId); }
                    finally { Marshal.ReleaseComObject(defDev); }
                }

                if (enumerator.EnumAudioEndpoints(EDataFlow.eRender, 0x00000001 /* ACTIVE */, out var collection) == 0 && collection is not null)
                {
                    try
                    {
                        collection.GetCount(out uint count);
                        for (uint i = 0; i < count; i++)
                        {
                            if (collection.Item(i, out var device) == 0 && device is not null)
                            {
                                try
                                {
                                    device.GetId(out string id);
                                    var (fullName, shortName) = ReadDeviceNames(device, i);
                                    bool isDef = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);
                                    list.Add((id, fullName, shortName, isDef));
                                }
                                finally
                                {
                                    Marshal.ReleaseComObject(device);
                                }
                            }
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(collection);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"Failed to enumerate render devices: {ex.Message}");
        }
        return list;
    }

    internal static (string FullName, string ShortName) ReadDeviceNames(IMMDevice device, uint index)
    {
        string fallback = LocalizationService.Pick($"Audio Output #{index + 1}", $"Аудиовыход #{index + 1}");
        string shortFallback = LocalizationService.Pick($"Output #{index + 1}", $"Выход #{index + 1}");
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(0 /* STGM_READ */, out store) != 0 || store is null)
                return (fallback, shortFallback);

            string? friendly = ReadPropString(store, PKEY_Device_FriendlyName);
            string? desc = ReadPropString(store, PKEY_Device_DeviceDesc);

            string fullName = !string.IsNullOrWhiteSpace(friendly)
                ? friendly.Trim()
                : (!string.IsNullOrWhiteSpace(desc) ? desc.Trim() : fallback);

            string shortName = !string.IsNullOrWhiteSpace(desc)
                ? desc.Trim()
                : ExtractShortEndpointLabel(fullName, index);

            if (shortName.Length > 18)
                shortName = shortName[..17].TrimEnd() + "…";

            return (fullName, shortName);
        }
        catch
        {
            return (fallback, shortFallback);
        }
        finally
        {
            if (store is not null)
            {
                try { Marshal.ReleaseComObject(store); } catch { }
            }
        }
    }

    private static string ExtractShortEndpointLabel(string fullName, uint index)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return LocalizationService.Pick($"Output #{index + 1}", $"Выход #{index + 1}");

        int paren = fullName.IndexOf('(');
        if (paren > 1)
        {
            string prefix = fullName[..paren].Trim();
            if (!string.IsNullOrWhiteSpace(prefix))
                return prefix;
        }
        return fullName.Trim();
    }

    private static string? ReadPropString(IPropertyStore store, PropertyKey key)
    {
        PropVariant pv = default;
        try
        {
            if (store.GetValue(ref key, out pv) == 0 && pv.vt == 31 /* VT_LPWSTR */ && pv.pointerValue != IntPtr.Zero)
            {
                return Marshal.PtrToStringUni(pv.pointerValue);
            }
            return null;
        }
        finally
        {
            try { PropVariantClear(ref pv); } catch { }
        }
    }

    private void OnDefaultDeviceChanged()
    {
        if (_targetDeviceId is null && _isRunning && !_disposed)
        {
            if (Interlocked.Exchange(ref _restartScheduled, 1) != 0)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(180).ConfigureAwait(false);
                    Interlocked.Exchange(ref _restartScheduled, 0);
                    if (_isRunning && !_disposed)
                        Start(null);
                }
                catch
                {
                    Interlocked.Exchange(ref _restartScheduled, 0);
                }
            });
        }
    }

    private void CaptureLoop(string? endpointId, CancellationToken token)
    {
        IMMDeviceEnumerator? localEnum = null;
        IMMDevice? device = null;
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        IntPtr formatPtr = IntPtr.Zero;

        try
        {
            localEnum = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = endpointId is null
                ? localEnum.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device)
                : localEnum.GetDevice(endpointId, out device);

            if (hr != 0 || device is null)
            {
                LogService.Warn($"WASAPI failed to open audio endpoint (hr=0x{hr:X8}).");
                return;
            }

            Guid iidAudioClient = IID_IAudioClient;
            hr = device.Activate(ref iidAudioClient, CLSCTX_ALL, IntPtr.Zero, out object clientObj);
            if (hr != 0) return;
            audioClient = (IAudioClient)clientObj;

            hr = audioClient.GetMixFormat(out formatPtr);
            if (hr != 0 || formatPtr == IntPtr.Zero) return;

            var wf = Marshal.PtrToStructure<WAVEFORMATEX>(formatPtr);
            int endpointSampleRate = wf.nSamplesPerSec >= 8000 ? (int)wf.nSamplesPerSec : 48000;

            bool isFloat = IsIeeeFloatFormat(formatPtr, wf);
            int channels = Math.Max(1, (int)wf.nChannels);
            int bytesPerSample = Math.Max(1, wf.wBitsPerSample / 8);
            int blockAlign = Math.Max(channels * bytesPerSample, (int)wf.nBlockAlign);

            // Use 0 buffer duration for AUDCLNT_SHAREMODE_SHARED loopback so the driver uses its native period cleanly
            hr = audioClient.Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK,
                0,
                0,
                formatPtr,
                IntPtr.Zero);

            if (hr != 0)
            {
                LogService.Warn($"WASAPI IAudioClient.Initialize skipped endpoint '{endpointId ?? "default"}' (hr=0x{hr:X8}).");
                return;
            }

            Guid iidCapture = IID_IAudioCaptureClient;
            hr = audioClient.GetService(ref iidCapture, out object captureObj);
            if (hr != 0) return;
            captureClient = (IAudioCaptureClient)captureObj;

            hr = audioClient.Start();
            if (hr != 0) return;

            while (!token.IsCancellationRequested)
            {
                Thread.Sleep(8);

                int nextHr = captureClient.GetNextPacketSize(out uint packetFrames);
                if (nextHr != 0)
                    break; // Endpoint invalidated / format changed / disconnected -> exit cleanly

                while (!token.IsCancellationRequested && packetFrames > 0)
                {
                    hr = captureClient.GetBuffer(out IntPtr dataPtr, out uint numFrames, out uint flags, out _, out _);
                    if (hr != 0) break;

                    try
                    {
                        bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                        if (!silent && numFrames > 0 && dataPtr != IntPtr.Zero)
                        {
                            PushFrames(dataPtr, (int)numFrames, channels, bytesPerSample, blockAlign, isFloat, endpointSampleRate);
                        }
                    }
                    finally
                    {
                        captureClient.ReleaseBuffer(numFrames);
                    }

                    nextHr = captureClient.GetNextPacketSize(out packetFrames);
                    if (nextHr != 0) break;
                }
                if (nextHr != 0) break;
            }

            audioClient.Stop();
        }
        catch (Exception ex)
        {
            LogService.Warn($"WASAPI loopback endpoint '{endpointId ?? "default"}' stopped: {ex.Message}");
        }
        finally
        {
            if (formatPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(formatPtr);
            if (captureClient is not null) Marshal.ReleaseComObject(captureClient);
            if (audioClient is not null) Marshal.ReleaseComObject(audioClient);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (localEnum is not null) Marshal.ReleaseComObject(localEnum);
        }
    }

    private void PushFrames(IntPtr dataPtr, int numFrames, int channels, int bytesPerSample, int blockAlign, bool isFloat, int endpointSampleRate)
    {
        float peakInPacket = 0f;
        Span<float> tempMono = numFrames <= 1024 ? stackalloc float[numFrames] : new float[numFrames];

        for (int f = 0; f < numFrames; f++)
        {
            IntPtr framePtr = IntPtr.Add(dataPtr, f * blockAlign);
            float mono = 0f;

            if (bytesPerSample == 4)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    int raw = Marshal.ReadInt32(framePtr, ch * 4);
                    if (isFloat)
                    {
                        float val = BitConverter.Int32BitsToSingle(raw);
                        if (float.IsFinite(val))
                            mono += Math.Clamp(val, -1.5f, 1.5f);
                    }
                    else
                    {
                        mono += raw / 2147483648f;
                    }
                }
            }
            else if (bytesPerSample == 2)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    short sample = Marshal.ReadInt16(framePtr, ch * 2);
                    mono += sample / 32768f;
                }
            }
            else if (bytesPerSample == 3)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    int offset = ch * 3;
                    int b0 = Marshal.ReadByte(framePtr, offset);
                    int b1 = Marshal.ReadByte(framePtr, offset + 1);
                    int b2 = (sbyte)Marshal.ReadByte(framePtr, offset + 2);
                    int sample24 = b0 | (b1 << 8) | (b2 << 16);
                    mono += sample24 / 8388608f;
                }
            }

            mono /= channels;
            tempMono[f] = mono;
            float abs = Math.Abs(mono);
            if (abs > peakInPacket)
                peakInPacket = abs;
        }

        // Only write to shared ring buffer if this endpoint is actually outputting signal,
        // preventing silent secondary endpoints from overwriting an active endpoint's audio!
        if (peakInPacket > 0.00004f)
        {
            _sampleRate = endpointSampleRate;
            lock (_bufferLock)
            {
                int ringLen = _ringBuffer.Length;
                for (int f = 0; f < numFrames; f++)
                {
                    _ringBuffer[_writeIndex] = tempMono[f];
                    _writeIndex = (_writeIndex + 1) % ringLen;
                }
            }

            Interlocked.Exchange(ref _lastAudioTicksUtc, DateTime.UtcNow.Ticks);
        }
    }

    private static bool IsIeeeFloatFormat(IntPtr formatPtr, WAVEFORMATEX wf)
    {
        if (wf.wFormatTag == WAVE_FORMAT_IEEE_FLOAT)
            return true;

        if (wf.wFormatTag == WAVE_FORMAT_EXTENSIBLE && wf.cbSize >= 22)
        {
            var ext = Marshal.PtrToStructure<WAVEFORMATEXTENSIBLE>(formatPtr);
            if (ext.SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
                return true;
            if (ext.SubFormat == KSDATAFORMAT_SUBTYPE_PCM)
                return false;

            // Extra safety check at raw byte offset 24 (18-byte WAVEFORMATEX + 2-byte validBits + 4-byte channelMask)
            byte[] guidBytes = new byte[16];
            Marshal.Copy(IntPtr.Add(formatPtr, 24), guidBytes, 0, 16);
            var rawSub = new Guid(guidBytes);
            if (rawSub == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
                return true;
            if (rawSub == KSDATAFORMAT_SUBTYPE_PCM)
                return false;
        }

        // In WASAPI shared mode, 32-bit mix format is virtually always IEEE float
        return wf.wBitsPerSample == 32 && wf.wFormatTag != WAVE_FORMAT_PCM;
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            StopLocked();
        }
    }

    [ComVisible(true)]
    private sealed class DeviceNotificationClient : IMMNotificationClient
    {
        private readonly WasapiAudioCaptureService _owner;
        public DeviceNotificationClient(WasapiAudioCaptureService owner) => _owner = owner;
        public void OnDeviceStateChanged(string pwstrDeviceId, uint dwNewState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string pwstrDeviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string pwstrDefaultDeviceId)
        {
            if (flow == EDataFlow.eRender && role == ERole.eMultimedia)
                _owner.OnDefaultDeviceChanged();
        }
    }
}
