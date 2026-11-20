using System.Collections.Concurrent;
using System.IO;
using System.Media;
using PulseIsland.Core;

namespace PulseIsland.Services;

/// <summary>
/// Plays pleasant in-memory synthesized Dynamic Island notification chimes (zero disk writes, zero NuGet)
/// or native Windows system sounds based on the user's chosen preset.
/// </summary>
public static class NotificationSoundService
{
    private static readonly ConcurrentDictionary<NotificationSoundPreset, byte[]> CachedWavs = new();

    public static void Play(NotificationSoundPreset preset)
    {
        if (preset == NotificationSoundPreset.None)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                if (preset == NotificationSoundPreset.SystemAsterisk)
                {
                    SystemSounds.Asterisk.Play();
                    return;
                }

                byte[] wav = GetOrBuildWav(preset);
                using var ms = new MemoryStream(wav, writable: false);
                using var player = new SoundPlayer(ms);
                player.PlaySync();
            }
            catch (Exception ex)
            {
                LogService.Error($"Failed to play notification sound {preset}.", ex);
            }
        });
    }

    public static byte[] GetOrBuildWav(NotificationSoundPreset preset)
    {
        return CachedWavs.GetOrAdd(preset, BuildWavForPreset);
    }

    private static byte[] BuildWavForPreset(NotificationSoundPreset preset)
    {
        const int sampleRate = 44100;
        double durationSec = preset switch
        {
            NotificationSoundPreset.PulseChime => 0.48,
            NotificationSoundPreset.CrystalDrop => 0.42,
            NotificationSoundPreset.SoftBubble => 0.30,
            NotificationSoundPreset.CosmicPing => 0.52,
            _ => 0.35
        };

        int sampleCount = (int)(sampleRate * durationSec);
        short[] samples = new short[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            double value = preset switch
            {
                NotificationSoundPreset.PulseChime => SynthesizePulseChime(t),
                NotificationSoundPreset.CrystalDrop => SynthesizeCrystalDrop(t),
                NotificationSoundPreset.SoftBubble => SynthesizeSoftBubble(t),
                NotificationSoundPreset.CosmicPing => SynthesizeCosmicPing(t),
                _ => 0.0
            };

            samples[i] = (short)Math.Clamp(value * short.MaxValue * 0.42, short.MinValue, short.MaxValue);
        }

        return EncodePcm16MonoWav(samples, sampleRate);
    }

    private static double SynthesizePulseChime(double t)
    {
        // Two-note glass interval: F5 (698.46 Hz) -> C6 (1046.50 Hz)
        double n1 = ToneEnvelope(t, 0.0, 0.008, 0.28) * (Math.Sin(2 * Math.PI * 698.46 * t) * 0.72 + Math.Sin(2 * Math.PI * 1396.91 * t) * 0.18);
        double n2 = t >= 0.085
            ? ToneEnvelope(t - 0.085, 0.007, 0.38) * (Math.Sin(2 * Math.PI * 1046.50 * t) * 0.75 + Math.Sin(2 * Math.PI * 2093.0 * t) * 0.16)
            : 0.0;
        return n1 + n2;
    }

    private static double SynthesizeCrystalDrop(double t)
    {
        // Smooth ascending droplet triad: E5 -> B5
        double n1 = ToneEnvelope(t, 0.0, 0.005, 0.18) * Math.Sin(2 * Math.PI * 659.25 * t);
        double n2 = t >= 0.065
            ? ToneEnvelope(t - 0.065, 0.005, 0.34) * (Math.Sin(2 * Math.PI * 987.77 * t) * 0.8 + Math.Sin(2 * Math.PI * 1975.53 * t) * 0.15)
            : 0.0;
        return n1 * 0.7 + n2;
    }

    private static double SynthesizeSoftBubble(double t)
    {
        // Warm rising frequency sweep marimba pop (420 Hz -> 880 Hz in first 55ms)
        double freq = 420.0 + 460.0 * Math.Min(1.0, t / 0.055);
        double env = ToneEnvelope(t, 0.0, 0.006, 0.24);
        return env * (Math.Sin(2 * Math.PI * freq * t) * 0.85 + Math.Sin(2 * Math.PI * freq * 2.0 * t) * 0.12);
    }

    private static double SynthesizeCosmicPing(double t)
    {
        // Luminous high bell ping A5 (880 Hz) + shimmer harmonic E6 (1318.5 Hz)
        double env = ToneEnvelope(t, 0.0, 0.010, 0.50);
        return env * (Math.Sin(2 * Math.PI * 880.0 * t) * 0.65
                    + Math.Sin(2 * Math.PI * 1318.51 * t) * 0.30
                    + Math.Sin(2 * Math.PI * 2637.02 * t) * 0.10);
    }

    private static double ToneEnvelope(double t, double delay, double attack, double decay)
    {
        double local = t - delay;
        if (local < 0) return 0.0;
        double att = Math.Min(1.0, local / attack);
        double dec = Math.Exp(-local / (decay * 0.32));
        return att * dec;
    }

    private static double ToneEnvelope(double local, double attack, double decay)
        => ToneEnvelope(local, 0.0, attack, decay);

    private static byte[] EncodePcm16MonoWav(short[] samples, int sampleRate)
    {
        int dataBytes = samples.Length * 2;
        using var ms = new MemoryStream(44 + dataBytes);
        using var bw = new BinaryWriter(ms);

        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);               // PCM chunk size
        bw.Write((short)1);         // PCM format
        bw.Write((short)1);         // Mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);   // Byte rate
        bw.Write((short)2);         // Block align
        bw.Write((short)16);        // Bits per sample
        bw.Write("data"u8);
        bw.Write(dataBytes);
        foreach (short s in samples)
            bw.Write(s);

        return ms.ToArray();
    }
}
