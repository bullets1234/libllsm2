# -*- coding: utf-8 -*-
"""Stage D gold-dump: real pyworld decode_spectral_envelope / decode_aperiodicity
on the actual smoke-test CSVs (smoke_out/case0, smoke_out/case1), for
verifying the C# WorldCodec port bit-for-bit against the reference WORLD codec.

Run with the SimpleEnunu embedded Python (has pyworld 0.3.2, numpy):
  G:\SimpleEnunu-0.3.1_TunedWavOut\python-3.9.13-embed-amd64\python.exe dump_worldcodec_gold.py

For each of smoke_out/case0 and smoke_out/case1, writes (next to the CSVs):
  case{tag}_sp_gold.bin  : float32 LE, (T, nbin) row-major, linear power
  case{tag}_ap_gold.bin  : float32 LE, (T, nbin) row-major, linear 0..1
  case{tag}_worldcodec_manifest.json : {"T", "nbin", "fftlen", "fs"}
"""
import json
import os

import numpy as np
import pyworld

SMOKE_OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "smoke_out")
FS = 48000


def load_csv(path, ndim2=True):
    a = np.loadtxt(path, delimiter=",", dtype=np.float64)
    if ndim2 and a.ndim == 1:
        a = a.reshape(-1, 1)
    return a


def main():
    # Sanity-check the FFT size formula against the real pyworld implementation
    # (must equal 2048 for fs=48000, per the extracted C++ formula).
    fftlen = pyworld.get_cheaptrick_fft_size(FS)
    print("pyworld.get_cheaptrick_fft_size(%d) = %d" % (FS, fftlen))
    assert fftlen == 2048, "unexpected fft size, C# formula assumption may be wrong"
    nbin = fftlen // 2 + 1

    for tag in ("0", "1"):
        case_dir = os.path.join(SMOKE_OUT, "case%s" % tag)
        mgc = load_csv(os.path.join(case_dir, "case%s_mgc.csv" % tag))
        bap = load_csv(os.path.join(case_dir, "case%s_bap.csv" % tag))

        assert bap.shape[1] == 5, "expected 5 aperiodicity bands for fs=48000"

        sp_gold = pyworld.decode_spectral_envelope(np.ascontiguousarray(mgc), FS, fftlen)
        ap_gold = pyworld.decode_aperiodicity(np.ascontiguousarray(bap), FS, fftlen)

        t = mgc.shape[0]
        assert sp_gold.shape == (t, nbin)
        assert ap_gold.shape == (t, nbin)

        sp_gold.astype("<f4").tofile(os.path.join(case_dir, "case%s_sp_gold.bin" % tag))
        ap_gold.astype("<f4").tofile(os.path.join(case_dir, "case%s_ap_gold.bin" % tag))
        with open(os.path.join(case_dir, "case%s_worldcodec_manifest.json" % tag), "w") as f:
            json.dump({"T": t, "nbin": nbin, "fftlen": fftlen, "fs": FS}, f)

        print("case %s: T=%d nbin=%d sp[min,max]=(%.4g,%.4g) ap[min,max]=(%.4g,%.4g)"
              % (tag, t, nbin, sp_gold.min(), sp_gold.max(), ap_gold.min(), ap_gold.max()))


if __name__ == "__main__":
    main()
