# -*- coding: utf-8 -*-
"""ノイズ成分の「凍結度」計測。

L2R_DUMP で得た noise.wav（雑音成分のみ）について、
  1. 帯域包絡（10ms RMS, dB）の標準偏差と 5〜30Hz 変調エネルギー比
  2. log スペクトル微細構造（平滑包絡との差）のフレーム間相関（lag 1..4）
を測る。冷凍ノイズは (1) の変調エネルギー低下と (2) の相関上昇として現れる。
等倍レンダリング（伸長なし）を基準に、伸長レンダリングがどれだけ近いかを見る。

使い方:
  python diag/noise_texture.py <label=path/to/noise.wav> ...
  例: python diag/noise_texture.py ref=dump_on_1x/noise.wav off=dump_off_3x/noise.wav on=dump_on_3x/noise.wav
"""
import sys
import numpy as np
from scipy.io import wavfile
from scipy.signal import stft
from scipy.ndimage import uniform_filter1d

BANDS = [(1000, 2000), (2000, 4000), (4000, 8000), (8000, 16000)]
FRAME_MS = 10.0
SKIP_HEAD_MS = 150.0   # 子音部 + 遷移を除外
SKIP_TAIL_MS = 50.0


def load(path):
    fs, x = wavfile.read(path)
    if x.dtype != np.float32:
        x = x.astype(np.float64) / np.iinfo(x.dtype).max
    if x.ndim > 1:
        x = x[:, 0]
    return fs, x.astype(np.float64)


def band_envelope_stats(fs, x):
    nfft = 1024
    hop = int(fs * FRAME_MS / 1000)
    f, t, Z = stft(x, fs=fs, nperseg=nfft, noverlap=nfft - hop, padded=False, boundary=None)
    P = np.abs(Z) ** 2
    lo = int(SKIP_HEAD_MS / FRAME_MS)
    hi = P.shape[1] - int(SKIP_TAIL_MS / FRAME_MS)
    P = P[:, lo:hi]
    out = {}
    for (a, b) in BANDS:
        m = (f >= a) & (f < b)
        env = 10 * np.log10(P[m].mean(axis=0) + 1e-14)
        env = env - env.mean()
        # 変調スペクトル: 包絡 (100Hz サンプリング) のパワースペクトル
        n = len(env)
        w = np.hanning(n)
        spec = np.abs(np.fft.rfft(env * w)) ** 2
        fm = np.fft.rfftfreq(n, d=FRAME_MS / 1000)
        total = spec[1:].sum() + 1e-14
        mod_5_30 = spec[(fm >= 5) & (fm <= 30)].sum() / total
        mod_0_5 = spec[(fm > 0) & (fm < 5)].sum() / total
        out[(a, b)] = (env.std(), mod_0_5, mod_5_30)
    return out


def fine_structure_corr(fs, x, lags=(1, 2, 3, 4)):
    """log スペクトルから周波数方向の平滑包絡を引いた微細構造のフレーム間相関。"""
    nfft = 1024
    hop = int(fs * 5 / 1000)  # 5ms = 合成フレーム間隔
    f, t, Z = stft(x, fs=fs, nperseg=nfft, noverlap=nfft - hop, padded=False, boundary=None)
    L = 10 * np.log10(np.abs(Z) ** 2 + 1e-14)
    lo = int(SKIP_HEAD_MS / 5)
    hi = L.shape[1] - int(SKIP_TAIL_MS / 5)
    L = L[:, lo:hi]
    m = (f >= 2000) & (f < 16000)
    L = L[m]
    smooth = uniform_filter1d(L, size=25, axis=0)  # 約 1kHz 幅の包絡
    fine = L - smooth
    fine = fine - fine.mean(axis=0, keepdims=True)
    res = {}
    for k in lags:
        a = fine[:, :-k]
        b = fine[:, k:]
        num = (a * b).sum(axis=0)
        den = np.sqrt((a * a).sum(axis=0) * (b * b).sum(axis=0)) + 1e-14
        res[k] = float(np.median(num / den))
    return res


def main():
    items = [arg.split("=", 1) for arg in sys.argv[1:]]
    if not items:
        print(__doc__)
        return
    print(f"{'label':<8} {'band':<12} {'envStd(dB)':>10} {'mod<5Hz':>8} {'mod5-30':>8}")
    for label, path in items:
        fs, x = load(path)
        st = band_envelope_stats(fs, x)
        for (a, b), (sd, m0, m1) in st.items():
            print(f"{label:<8} {a//1000}k-{b//1000}k{'':<6} {sd:10.2f} {m0:8.3f} {m1:8.3f}")
    print()
    print(f"{'label':<8} fine-structure frame correlation (2-16kHz)  lag1  lag2  lag3  lag4")
    for label, path in items:
        fs, x = load(path)
        c = fine_structure_corr(fs, x)
        print(f"{label:<8} {'':<44} " + "  ".join(f"{c[k]:.3f}" for k in sorted(c)))


if __name__ == "__main__":
    main()
