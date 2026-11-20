---
name: fft-signal-processing
description: >-
  Use this skill when converting captured mono audio samples into smooth 60 FPS
  frequency-band heights for the Dynamic Island music visualizer using FFT,
  windowing, logarithmic frequency mapping, and temporal smoothing.
---

# FFT Signal Processing Skill

## Pipeline (Samples -> Visual Bars)
1. **Windowing:** Multiply the `N` (e.g., `1024` or `2048`, power of 2) mono samples by a **Hann window** (`0.5 * (1 - cos(2*pi*i/(N-1)))`) to prevent spectral leakage.
2. **FFT Computation:** Use `NAudio.Dsp.FastFourierTransform.FFT(true, log2N, complexBuffer)` to obtain `N/2` frequency bins.
3. **Magnitude & Decibels:**
   - Compute magnitude: `sqrt(re^2 + im^2)`.
   - Convert to dB or apply `log10(1 + k * mag)` compression so quiet and loud passages both look expressive.
4. **Logarithmic Band Mapping:**
   - Human hearing is logarithmic; map FFT bins across `20 Hz – 16 kHz` into `B` visual bars (e.g., 8–24 bars configurable in settings) using logarithmic bin boundaries rather than linear slicing.
5. **Smoothing & Decay (60 FPS):**
   - Apply exponential moving average / attack-release smoothing:
     - Fast attack (`alpha = 0.5–0.7`) when amplitude rises.
     - Smooth release/decay (`alpha = 0.15–0.25`) when amplitude drops.
   - Multiply by the user-configured sensitivity multiplier and clamp to `[0.0, 1.0]`.
