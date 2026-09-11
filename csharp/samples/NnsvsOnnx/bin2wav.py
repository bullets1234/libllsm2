"""bin (W2L1 feature dump) -> WAV via pyworld.

Usage: python bin2wav.py <input.bin> [output.wav]
"""
import struct
import sys

import numpy as np
import pyworld
import wave


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    src = sys.argv[1]
    dst = sys.argv[2] if len(sys.argv) > 2 else src.rsplit(".", 1)[0] + ".wav"

    with open(src, "rb") as f:
        f.read(4)  # magic
        fs, t, nbin = struct.unpack("<iii", f.read(12))
        period = struct.unpack("<f", f.read(4))[0]
        f0 = np.frombuffer(f.read(4 * t), dtype="<f4").astype(np.float64)
        sp = np.frombuffer(f.read(4 * t * nbin), dtype="<f4").astype(np.float64).reshape(t, nbin)
        ap = np.frombuffer(f.read(4 * t * nbin), dtype="<f4").astype(np.float64).reshape(t, nbin)

    wav = pyworld.synthesize(
        np.ascontiguousarray(f0),
        np.ascontiguousarray(sp),
        np.ascontiguousarray(np.clip(ap, 0, 1)),
        fs, period)
    peak = np.max(np.abs(wav))
    if peak > 0:
        wav = wav / peak * 0.9

    with wave.open(dst, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(fs)
        w.writeframes((wav * 32767).astype(np.int16).tobytes())
    print(f"fs={fs} T={t} peak={peak:.3f} -> {dst}")


if __name__ == "__main__":
    main()
