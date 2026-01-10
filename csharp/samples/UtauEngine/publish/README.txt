L2R - LLSM2 Resampler for UTAU
===============================

An UTAU resampler powered by LLSM2.

Built on the foundation of moresampler with enhanced synthesis quality.

Basic Usage
-----------
L2R.exe <input.wav> <output.wav> [flags]

Required Arguments:
  input.wav   - Source audio file (WAV format)
  output.wav  - Output synthesized audio file

Optional Flags
--------------
Voice Modulation:
  B<0-100>    - Breathiness/air noise amount (default: 0)
                Example: B30 adds 30% breathiness
  
  g<-100~100> - Gender shift in semitones (default: 0)
                Example: g12 raises pitch 1 octave, g-12 lowers 1 octave
  
  F<0-100>    - Formant preservation during pitch shift (default: 100)
                Example: F0 disables formant correction, F100 full correction

Advanced Features:
  H           - High-resolution mode (dynamic harmonic adjustment)
                Automatically adjusts harmonic count based on F0
                Improves quality for high-pitched voices
  
  R           - Enable RPS (Repeated Phase Synchronization)
                Reduces phase artifacts in interpolation
  
  M+          - Modulation boundary fade (reduces artifacts at boundaries)
  M1          - Alternative modulation mode
  
  N<0-100>    - Neural Vocoder blend ratio (default: 0, requires models/)
                Example: N50 uses 50% neural vocoder output
                Requires HiFi-GAN model files in models/ directory
  
  O           - Enable observation mode (noise analysis)
  
  E           - Extended processing mode
  
  P           - Phase-aware processing

Examples
--------
# Basic synthesis
L2R.exe input.wav output.wav

# Add breathiness and raise pitch
L2R.exe input.wav output.wav B40 g5

# High-quality synthesis with neural vocoder
L2R.exe input.wav output.wav H N30 R

# Gender conversion with formant preservation
L2R.exe input.wav output.wav g-12 F100

Performance Tips
----------------
- Use H flag for high-pitched voices (improves harmonic coverage)
- Use N flag with 20-50 blend ratio for smoother output (requires GPU for speed)
- Use R flag to reduce periodic artifacts during pitch/time changes
- Combine B, g, F flags for natural-sounding voice transformations

Neural Vocoder Requirements
----------------------------
To use the N flag (Neural Vocoder), place HiFi-GAN model files in:
  models/hifigan_generator.onnx
  models/hifigan_config.json

Without model files, the N flag will be ignored.

Technical Details
-----------------
- Vocoder: LLSM (Low-Level Speech Model)
- F0 Estimator: PYIN (Probabilistic YIN)
- Harmonic Count: 800 (standard) or dynamic with H flag (100-2000)
- Frame Rate: 200 Hz (5ms hop size)
- FFT Size: 8192
- PSD Bins: 128
- Interpolation: Monotonic Cubic (Fritsch-Carlson)
- Phase Sync: Frame-level RPS (with R flag)

License & Source Code
---------------------
This software is licensed under the GNU General Public License v3.0.
Complete source code is available at:

  [YOUR_GITHUB_REPOSITORY_URL]

You have the right to:
- Use this software for any purpose
- Study and modify the source code
- Share the software and your modifications

See LICENSE.txt for full license terms.
See NOTICE.txt for third-party library information.

Acknowledgments
---------------
- libllsm2 by Kanru Hua (GPL-3.0)
- libpyin by Kanru Hua (BSD 3-Clause)
- Microsoft.ML.OnnxRuntime (MIT License)

For bug reports, feature requests, and contributions:
[YOUR_GITHUB_REPOSITORY_URL]/issues

================================================================================
Copyright (c) 2024 [YOUR_NAME]
This program comes with ABSOLUTELY NO WARRANTY.
This is free software, and you are welcome to redistribute it under certain
conditions; see LICENSE.txt for details.
================================================================================
