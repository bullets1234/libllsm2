"""
高精度リファレンス（harmonic_ref.py の出力）に対して複数の F0 トラックを採点する。

  python score_vs_ref.py ref.csv rmvpe=dump_l2r/f0_l2r.f0.csv pyin=dump_pyin/f0_pyin.f0.csv
"""

import sys

import numpy as np


def load_dump(path):
    lines = [ln for ln in open(path, encoding="utf-8").read().splitlines() if ln.strip()]
    if lines and not lines[0][0].isdigit():
        lines = lines[1:]
    a = np.array([[float(c) for c in ln.split(",")] for ln in lines])
    return a[:, -2], a[:, -1]  # time_s, f0_hz


def load_ref(path):
    lines = open(path, encoding="utf-8").read().splitlines()[1:]
    a = np.array([[float(c) for c in ln.split(",")] for ln in lines if ln.strip()])
    return a[:, 0], a[:, 1]


def main():
    ref_t, ref_f = load_ref(sys.argv[1])

    # リファレンス自体が外れている点を除く（局所中央値から 100 cent 以上）
    med = np.array([np.median(ref_f[max(0, i - 5):i + 6]) for i in range(len(ref_f))])
    keep = np.abs(1200 * np.log2(ref_f / med)) < 100
    ref_t, ref_f = ref_t[keep], ref_f[keep]
    print(f"reference: {len(ref_f)} points, {ref_f.min():.1f}-{ref_f.max():.1f} Hz "
          f"(dropped {(~keep).sum()} outliers)")

    for spec in sys.argv[2:]:
        name, path = spec.split("=", 1)
        t, f = load_dump(path)
        v = f > 0
        est = np.interp(ref_t, t[v], f[v], left=np.nan, right=np.nan)
        ok = ~np.isnan(est)
        cent = 1200 * np.log2(est[ok] / ref_f[ok])
        print(f"{name:8s} n={ok.sum():4d}  bias {np.median(cent):+7.1f}c  "
              f"MAE {np.abs(cent - np.median(cent)).mean():5.1f}c  "
              f"RMSE {np.sqrt((cent ** 2).mean()):6.1f}c  "
              f">20c {(np.abs(cent) > 20).mean():5.1%}  "
              f">50c {(np.abs(cent) > 50).mean():5.1%}")


if __name__ == "__main__":
    main()
