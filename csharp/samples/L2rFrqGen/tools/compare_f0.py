"""RMVPE(f0_l2r) と pYIN(f0_pyin) のダンプを突き合わせる。"""

import sys

import numpy as np


def load(path):
    lines = [ln for ln in open(path, encoding="utf-8").read().splitlines() if ln.strip()]
    if lines and not lines[0][0].isdigit():
        lines = lines[1:]
    a = np.array([[float(c) for c in ln.split(",")] for ln in lines])
    return a[:, 1], a[:, 2]  # time_s, f0_hz


def main():
    t_a, a = load(sys.argv[1])
    t_b, b = load(sys.argv[2])
    n = min(len(a), len(b))
    a, b, t = a[:n], b[:n], t_a[:n]

    va, vb = a > 0, b > 0
    both = va & vb
    print(f"frames {n}  voiced: RMVPE {va.mean():.0%}  pYIN {vb.mean():.0%}  both {both.mean():.0%}")
    print(f"V/UV disagree: {(va ^ vb).mean():.1%}")

    cent = 1200 * np.log2(b[both] / a[both])
    print(f"cent diff (pYIN - RMVPE): med {np.median(cent):+.1f}  "
          f"p95|d| {np.percentile(np.abs(cent), 95):.1f}  max|d| {np.abs(cent).max():.1f}")

    for name, lo, hi in [("octave-down", -1250, -1150), ("octave-up", 1150, 1250),
                         ("fifth-down", -730, -670), ("fifth-up", 670, 730)]:
        k = ((cent > lo) & (cent < hi)).sum()
        if k:
            print(f"  pYIN {name}: {k} frames ({k / both.sum():.1%})")

    gross = np.abs(cent) > 50
    print(f"  gross error (>50 cent): {gross.sum()} frames ({gross.mean():.1%})")

    # 局所的な不連続（隣接フレーム間の跳び）は聴感上のノイズに直結する
    for name, f in [("RMVPE", a), ("pYIN", b)]:
        v = f > 0
        pair = v[:-1] & v[1:]
        d = np.abs(1200 * np.log2(f[1:][pair] / f[:-1][pair]))
        print(f"{name:6s} frame-to-frame jump: med {np.median(d):.1f}c  "
              f"p99 {np.percentile(d, 99):.1f}c  >100c {(d > 100).sum()} frames")

    if gross.any():
        idx = np.where(both)[0][gross]
        print("worst frames (time, RMVPE, pYIN, cent):")
        order = idx[np.argsort(-np.abs(1200 * np.log2(b[idx] / a[idx])))][:12]
        for i in sorted(order):
            print(f"  {t[i]:6.3f}s  {a[i]:7.1f}  {b[i]:7.1f}  {1200 * np.log2(b[i] / a[i]):+8.1f}")


if __name__ == "__main__":
    main()
