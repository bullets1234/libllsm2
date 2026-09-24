"""
倍音コムのフィットで F0 を高精度に求め、推定器の判定基準にする。
探索範囲を全走査するので、どちらの推定器にも寄らない中立な基準になる。

  python harmonic_ref.py <wav> --at 1.39 2.75
  python harmonic_ref.py <wav> --grid 1.3 5.0 0.05 --csv ref.csv
"""

import argparse

import numpy as np
import soundfile as sf


def comb_f0(x, sr, t, fmin, fmax, win_sec=0.25, band_hz=5000.0):
    """長窓 FFT のスペクトルに倍音コムを当て、スコア最大の f0 を返す。"""
    n = int(win_sec * sr)
    s = int(t * sr) - n // 2
    if s < 0 or s + n > len(x):
        return None
    seg = x[s:s + n] * np.hanning(n)
    rms = float(np.sqrt((seg ** 2).mean()))
    if rms < 1e-5:
        return None

    nfft = 1 << ((n - 1).bit_length() + 2)
    mag = np.abs(np.fft.rfft(seg, nfft))
    logmag = np.log(mag + mag.max() * 1e-6)
    df = sr / nfft

    # スペクトル傾斜を除いた「山らしさ」で評価する。単純な対数振幅和だと
    # 低域ほど強い音声では f0/2, f0/3 のコムが常に勝ってしまう。
    span = max(3, int(60.0 / df))
    kernel = np.ones(2 * span + 1) / (2 * span + 1)
    local = np.convolve(logmag, kernel, mode="same")
    peakiness = logmag - local

    k_max = min(len(logmag) - 3, int(band_hz / df))

    def score(f0):
        # 帯域は f0 に依らず固定。倍音本数で割るので候補間で公平になる。
        h = np.arange(1, int(band_hz / f0) + 1)
        if len(h) < 4:
            return -1e9
        k = (h * f0 / df).astype(int)
        k = k[(k >= 1) & (k < k_max)]
        if len(k) < 4:
            return -1e9
        v = np.maximum.reduce([peakiness[k + d] for d in (-1, 0, 1, 2)])
        return float(v.mean())

    # 粗探索 2 セント刻み -> 細探索 0.05 セント刻み
    n_coarse = int(1200 * np.log2(fmax / fmin) / 2) + 1
    coarse = fmin * 2.0 ** (np.arange(n_coarse) * 2.0 / 1200)
    best = float(coarse[int(np.argmax([score(f) for f in coarse]))])
    fine = best * 2.0 ** (np.arange(-40, 41) * 0.05 / 1200)
    best = float(fine[int(np.argmax([score(f) for f in fine]))])
    return best, rms


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("wav")
    ap.add_argument("--at", nargs="*", type=float, default=[])
    ap.add_argument("--grid", nargs=3, type=float, metavar=("START", "END", "STEP"))
    ap.add_argument("--fmin", type=float, default=70.0)
    ap.add_argument("--fmax", type=float, default=900.0)
    ap.add_argument("--csv", help="結果を CSV に書き出す")
    ap.add_argument("--quiet", action="store_true")
    args = ap.parse_args()

    x, sr = sf.read(args.wav, dtype="float32", always_2d=False)
    if x.ndim > 1:
        x = x.mean(axis=1)

    times = list(args.at)
    if args.grid:
        times += list(np.arange(*args.grid))

    rows = []
    for t in times:
        r = comb_f0(x, sr, float(t), args.fmin, args.fmax)
        if r is None:
            if not args.quiet:
                print(f"{t:6.3f}s  --- (silent)")
            continue
        if not args.quiet:
            print(f"{t:6.3f}s  ref {r[0]:7.2f} Hz  rms {r[1]:.4f}")
        rows.append((float(t), r[0]))

    if args.csv:
        with open(args.csv, "w", encoding="utf-8") as f:
            f.write("time_s,f0_hz\n")
            for t, v in rows:
                f.write(f"{t:.4f},{v:.3f}\n")
        print(f"wrote {args.csv} ({len(rows)} points)")


if __name__ == "__main__":
    main()
