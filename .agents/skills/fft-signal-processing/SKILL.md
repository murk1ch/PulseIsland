---
name: fft-signal-processing
description: >-
  Use this skill when converting captured mono audio samples into smooth 60 FPS
  frequency-band heights for the Dynamic Island music visualizer using FFT,
  windowing, logarithmic frequency mapping, and temporal smoothing.
---

# FFT Signal Processing Skill

## Pipeline (Samples -> Visual Bars in `Audio/FftSpectrumProcessor.cs`)
1. **AGC & Windowing:** Track a rolling RMS peak (AGC) and multiply the `N` (default `1024`, power of 2) mono samples by a **Hann window** (`0.5 * (1 - cos(2*pi*i/(N-1)))`) to prevent spectral leakage.
2. **Zero-NuGet Radix-2 FFT:** Compute an in-place pure C# Cooley-Tukey Radix-2 FFT (`PerformRadix2Fft`) to obtain `N/2` frequency bins with zero external packages.
3. **Magnitude & Compression:**
   - Compute magnitude `sqrt(re^2 + im^2)` normalized by AGC peak.
   - Apply logarithmic compression so quiet and loud passages both look expressive.
4. **Logarithmic & Symmetric Center-Weighted Mapping:**
   - Map FFT bins across `20 Hz – 16 kHz` into `B` visual bars (`6–32` configurable in settings) using logarithmic bin boundaries with gentle bass emphasis and symmetric center-weighted layout.
5. **Smoothing, Spatial Blending & Gravity Decay (60 FPS):**
   - Apply fast 1-frame attack when amplitude rises, gravity-eased decay when dropping (`DecayToZero` on silence), and 3-tap spatial neighbor smoothing.
   - Multiply by the user-configured sensitivity multiplier and clamp to `[0.0, 1.0]`.
