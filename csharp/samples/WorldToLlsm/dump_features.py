# -*- coding: utf-8 -*-
"""NNSVS/ENUNU acoustic features (mgc/bap/f0/vuv CSV) -> WORLD sp/ap binary dump + reference WAV.

NNSVS の gen.py と同じ経路 (use_world_codec=True) でデコードする:
  sp = pyworld.decode_spectral_envelope(mgc, fs, fftlen)   # WORLD codec (mc2sp ではない!)
  ap = pyworld.decode_aperiodicity(bap, fs, fftlen)
参照 WAV は pyworld.synthesize による (検証用ゴールドスタンダード。パイプラインには不使用)。

実行例 (SimpleEnunu の埋め込み Python を使用):
  G:\SimpleEnunu-0.3.1_TunedWavOut\python-3.9.13-embed-amd64\python.exe dump_features.py ^
    G:\libllsm2\csharp\samples\TestCoder\enunu_format 0_acoustic ^
    G:\libllsm2\csharp\samples\WorldToLlsm\features.bin

バイナリ形式 (little endian):
  magic   : 4 bytes b'W2L1'
  fs      : int32
  nfrm    : int32
  nbin    : int32 (= fftlen/2+1)
  period  : float32 (frame period, ms)
  f0      : float32 x nfrm  (unvoiced = 0)
  sp      : float32 x nfrm*nbin (linear power, row-major)
  ap      : float32 x nfrm*nbin (linear 0..1, row-major)
"""
import sys
import os
import struct
import wave

import numpy as np
import pyworld


def load_csv(path, ndim2=True):
    a = np.loadtxt(path, delimiter=",", dtype=np.float64)
    if ndim2 and a.ndim == 1:
        a = a.reshape(-1, 1)
    return a


def main():
    if len(sys.argv) < 4:
        print(__doc__)
        sys.exit(1)
    in_dir, prefix, out_bin = sys.argv[1], sys.argv[2], sys.argv[3]
    fs = int(sys.argv[4]) if len(sys.argv) > 4 else 48000
    period = float(sys.argv[5]) if len(sys.argv) > 5 else 5.0

    mgc = load_csv(os.path.join(in_dir, prefix + "_mgc.csv"))
    bap = load_csv(os.path.join(in_dir, prefix + "_bap.csv"))
    f0 = load_csv(os.path.join(in_dir, prefix + "_f0.csv"), ndim2=False).ravel()
    vuv = load_csv(os.path.join(in_dir, prefix + "_vuv.csv"), ndim2=False).ravel()

    nfrm = min(len(mgc), len(bap), len(f0), len(vuv))
    mgc, bap, f0, vuv = mgc[:nfrm], bap[:nfrm], f0[:nfrm], vuv[:nfrm]

    fftlen = pyworld.get_cheaptrick_fft_size(fs)
    nbin = fftlen // 2 + 1
    print("nfrm=%d fs=%d fftlen=%d nbin=%d mgc_dim=%d bap_dim=%d"
          % (nfrm, fs, fftlen, nbin, mgc.shape[1], bap.shape[1]))

    # NNSVS gen.py 相当のデコード
    sp = pyworld.decode_spectral_envelope(np.ascontiguousarray(mgc), fs, fftlen)
    ap = pyworld.decode_aperiodicity(np.ascontiguousarray(bap), fs, fftlen)
    ap = np.clip(ap, 0.0, 1.0)

    f0_gated = f0.copy()
    f0_gated[vuv < 0.5] = 0.0
    # NNSVS: 無声フレームは ap[:, 0] = 1.0 (decode_aperiodicity が処理済みだが念のため)
    ap[f0_gated <= 0, :] = 1.0

    print("voiced frames: %d/%d, f0 range: %.1f - %.1f Hz"
          % (int((f0_gated > 0).sum()), nfrm,
             f0_gated[f0_gated > 0].min() if (f0_gated > 0).any() else 0,
             f0_gated.max()))

    with open(out_bin, "wb") as f:
        f.write(b"W2L1")
        f.write(struct.pack("<iii f", fs, nfrm, nbin, period))
        f.write(f0_gated.astype("<f4").tobytes())
        f.write(sp.astype("<f4").tobytes())
        f.write(ap.astype("<f4").tobytes())
    print("wrote %s (%.1f MB)" % (out_bin, os.path.getsize(out_bin) / 1e6))

    # 参照 WAV (pyworld 合成; 検証専用)
    y = pyworld.synthesize(np.ascontiguousarray(f0_gated),
                           np.ascontiguousarray(sp),
                           np.ascontiguousarray(ap), fs, period)
    ref_path = os.path.splitext(out_bin)[0] + "_ref.wav"
    peak = np.abs(y).max()
    print("reference: %.2f s, peak=%.3f, rms=%.5f"
          % (len(y) / fs, peak, np.sqrt((y ** 2).mean())))
    if peak > 0.99:
        y = y / peak * 0.99
    pcm = (np.clip(y, -1, 1) * 32767).astype("<i2")
    with wave.open(ref_path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(fs)
        w.writeframes(pcm.tobytes())
    print("wrote %s" % ref_path)


if __name__ == "__main__":
    main()
