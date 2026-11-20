namespace PulseIsland.Audio;

/// <summary>
/// Real-time Radix-2 FFT processor with Hann windowing, Automatic Gain Control (AGC),
/// center-weighted logarithmic bass mapping (20-150 Hz in the center, mids in the middle,
/// high-frequency ripple at the edges), instant 1-frame bass attack, and gravity decay
/// (current = Math.Max(target, current * decay)).
/// </summary>
public sealed class FftSpectrumProcessor
{
    private readonly int _fftSize;
    private readonly double[] _hannWindow;
    private readonly double[] _real;
    private readonly double[] _imag;
    private double[] _rawFreqBands;
    private double[] _gravityBands;
    private double _breathingPhase;
    private double _rollingPeak = 0.08;

    public FftSpectrumProcessor(int fftSize = 1024, int initialBandCount = 18)
    {
        if ((fftSize & (fftSize - 1)) != 0 || fftSize < 128)
            throw new ArgumentException("FFT size must be a power of 2 >= 128.", nameof(fftSize));

        _fftSize = fftSize;
        _hannWindow = new double[fftSize];
        _real = new double[fftSize];
        _imag = new double[fftSize];

        int count = Math.Max(4, initialBandCount);
        _rawFreqBands = new double[count];
        _gravityBands = new double[count];

        for (int i = 0; i < fftSize; i++)
        {
            _hannWindow[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (fftSize - 1)));
        }
    }

    public int FftSize => _fftSize;

    public void ComputeBands(
        ReadOnlySpan<float> monoSamples,
        int sampleRate,
        Span<double> outputBands,
        double sensitivity = 1.65,
        double smoothing = 0.85)
    {
        int bandCount = outputBands.Length;
        if (bandCount <= 0)
            return;

        if (_gravityBands.Length != bandCount)
        {
            _gravityBands = new double[bandCount];
            _rawFreqBands = new double[bandCount];
        }

        int copyCount = Math.Min(monoSamples.Length, _fftSize);
        double rms = 0.0;
        double framePeak = 0.0;
        for (int i = 0; i < copyCount; i++)
        {
            double abs = Math.Abs(monoSamples[i]);
            if (abs > framePeak) framePeak = abs;
        }

        // Automatic Gain Control (AGC) so quiet playback (5%-20% volume) still produces full-height waves
        if (framePeak > 0.00004)
        {
            _rollingPeak = Math.Clamp(Math.Max(framePeak, _rollingPeak * 0.985), 0.003, 1.0);
        }
        double agcGain = framePeak > 0.00004 ? Math.Clamp(0.45 / _rollingPeak, 1.0, 50.0) : 1.0;

        for (int i = 0; i < _fftSize; i++)
        {
            double sample = i < copyCount ? monoSamples[i] * agcGain : 0.0;
            rms += sample * sample;
            _real[i] = sample * _hannWindow[i];
            _imag[i] = 0.0;
        }
        rms = copyCount > 0 ? Math.Sqrt(rms / copyCount) : 0.0;

        PerformRadix2Fft(_real, _imag);

        int usableBins = _fftSize / 2;
        double minFreq = 20.0;
        double maxFreq = Math.Min(16000.0, Math.Max(4000.0, sampleRate * 0.45));
        double binWidth = Math.Max(1.0, (double)sampleRate / _fftSize);

        // Compute half-spectrum logarithmic bands from 20 Hz (bass) to 16 kHz (treble)
        int halfCount = (bandCount + 1) / 2;
        for (int h = 0; h < halfCount; h++)
        {
            double t0 = (double)h / halfCount;
            double t1 = (double)(h + 1) / halfCount;

            double fStart = minFreq * Math.Pow(maxFreq / minFreq, Math.Pow(t0, 1.25));
            double fEnd = minFreq * Math.Pow(maxFreq / minFreq, Math.Pow(t1, 1.25));

            int binStart = Math.Clamp((int)Math.Floor(fStart / binWidth), 1, usableBins - 1);
            int binEnd = Math.Clamp((int)Math.Ceiling(fEnd / binWidth), binStart + 1, usableBins);

            double peakMag = 0.0;
            double sumMag = 0.0;
            int count = 0;
            for (int k = binStart; k < binEnd; k++)
            {
                double re = _real[k];
                double im = _imag[k];
                double mag = Math.Sqrt(re * re + im * im) / (_fftSize * 0.22);
                if (mag > peakMag) peakMag = mag;
                sumMag += mag;
                count++;
            }

            double avgMag = count > 0 ? (sumMag / count) * 0.55 + peakMag * 0.45 : 0.0;

            // Boost low-end bass (20-150 Hz) and mid punch
            double bassBoost = fStart < 160.0 ? 2.4 : (fStart < 600.0 ? 1.65 : 1.15);
            double scaled = Math.Log10(1.0 + avgMag * 52.0 * sensitivity * bassBoost);
            _rawFreqBands[h] = Math.Clamp(scaled, 0.0, 1.0);
        }

        // Map frequencies symmetrically: Bass (20-150 Hz) in the center, Treble ripple at the outer edges
        _breathingPhase = (_breathingPhase + 0.11) % (Math.PI * 2.0);
        double gravityDecay = Math.Clamp(smoothing, 0.72, 0.93);
        double centerIndex = (bandCount - 1) * 0.5;

        for (int i = 0; i < bandCount; i++)
        {
            double distFromCenter = Math.Abs(i - centerIndex) / Math.Max(1.0, centerIndex);
            int freqIdx = Math.Clamp((int)Math.Floor(distFromCenter * (halfCount - 1)), 0, halfCount - 1);

            double target = _rawFreqBands[freqIdx];

            // Add organic breathing motion modulated by real audio energy
            if (rms > 0.0005)
            {
                double ripple = (1.0 - distFromCenter * 0.40) *
                                (0.10 * Math.Sin(_breathingPhase + i * 0.75) + 0.12);
                target = Math.Clamp(target + ripple * Math.Min(1.0, rms * 16.0), 0.0, 1.0);
            }

            // Instant 1-frame attack on bass hit, smooth gravity decay on fall:
            // current = Math.Max(target, current * 0.85)
            double prev = _gravityBands[i];
            double current = Math.Max(target, prev * gravityDecay);
            _gravityBands[i] = current;
        }

        // Spatial 3-tap smoothing so adjacent control points form a liquid wave
        for (int i = 0; i < bandCount; i++)
        {
            double left = i > 0 ? _gravityBands[i - 1] : _gravityBands[i];
            double mid = _gravityBands[i];
            double right = i < bandCount - 1 ? _gravityBands[i + 1] : _gravityBands[i];

            double edgeEnvelope = Math.Sin(((i + 0.5) / bandCount) * Math.PI);
            double envelopeFactor = 0.42 + 0.58 * edgeEnvelope;

            outputBands[i] = Math.Clamp((0.22 * left + 0.56 * mid + 0.22 * right) * envelopeFactor, 0.0, 1.0);
        }
    }

    /// <summary>
    /// Synthesizes organic rhythmic bass/mid/treble movement when SMTC reports active playback
    /// on an external/Bluetooth/exclusive-mode device where loopback samples aren't exposed.
    /// </summary>
    public void ComputeRhythmicFallbackBands(Span<double> outputBands, double smoothing = 0.85)
    {
        int bandCount = outputBands.Length;
        if (bandCount <= 0) return;

        if (_gravityBands.Length != bandCount)
            _gravityBands = new double[bandCount];

        _breathingPhase = (_breathingPhase + 0.14) % (Math.PI * 20.0);
        double gravityDecay = Math.Clamp(smoothing, 0.75, 0.90);
        double centerIndex = (bandCount - 1) * 0.5;

        double bassPulse = Math.Pow(Math.Max(0.0, Math.Sin(_breathingPhase * 0.85)), 3.0) * 0.88;
        double subPulse = Math.Pow(Math.Max(0.0, Math.Cos(_breathingPhase * 0.42)), 2.0) * 0.55;

        for (int i = 0; i < bandCount; i++)
        {
            double distFromCenter = Math.Abs(i - centerIndex) / Math.Max(1.0, centerIndex);
            double midWave = 0.32 * Math.Abs(Math.Sin(_breathingPhase * 1.3 + i * 0.65));
            double hiRipple = 0.22 * Math.Abs(Math.Cos(_breathingPhase * 2.4 - i * 1.1));

            double target = (1.0 - distFromCenter) * Math.Max(bassPulse, subPulse)
                            + midWave * (0.65 - 0.25 * distFromCenter)
                            + hiRipple * distFromCenter;

            double edgeEnvelope = Math.Sin(((i + 0.5) / bandCount) * Math.PI);
            target = Math.Clamp(target * (0.45 + 0.55 * edgeEnvelope), 0.14, 0.95);

            double current = Math.Max(target, _gravityBands[i] * gravityDecay);
            _gravityBands[i] = current;
            outputBands[i] = current;
        }
    }

    public void DecayToZero(Span<double> outputBands, double decayFactor = 0.85)
    {
        if (_gravityBands.Length != outputBands.Length)
            _gravityBands = new double[outputBands.Length];

        _breathingPhase = (_breathingPhase + 0.04) % (Math.PI * 2.0);
        for (int i = 0; i < outputBands.Length; i++)
        {
            _gravityBands[i] *= decayFactor;
            if (_gravityBands[i] < 0.004)
                _gravityBands[i] = 0.0;
            outputBands[i] = _gravityBands[i];
        }
    }

    private static void PerformRadix2Fft(double[] real, double[] imag)
    {
        int n = real.Length;
        int j = 0;
        for (int i = 0; i < n; i++)
        {
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
            int m = n >> 1;
            while (m >= 1 && j >= m)
            {
                j -= m;
                m >>= 1;
            }
            j += m;
        }

        for (int step = 1; step < n; step <<= 1)
        {
            double theta = -Math.PI / step;
            double wStepRe = Math.Cos(theta);
            double wStepIm = Math.Sin(theta);

            for (int group = 0; group < n; group += step << 1)
            {
                double wRe = 1.0;
                double wIm = 0.0;

                for (int pair = 0; pair < step; pair++)
                {
                    int even = group + pair;
                    int odd = even + step;

                    double tRe = wRe * real[odd] - wIm * imag[odd];
                    double tIm = wRe * imag[odd] + wIm * real[odd];

                    real[odd] = real[even] - tRe;
                    imag[odd] = imag[even] - tIm;
                    real[even] += tRe;
                    imag[even] += tIm;

                    double nextWRe = wRe * wStepRe - wIm * wStepIm;
                    wIm = wRe * wStepIm + wIm * wStepRe;
                    wRe = nextWRe;
                }
            }
        }
    }
}
