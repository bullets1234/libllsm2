"""
書き出した rmvpe.onnx が正しい F0 を返すか、既知周波数の合成音で確認する。
テスト用 wav が無くても単体で回せる。

  python check_rmvpe_onnx.py ../models/rmvpe.onnx
"""

import sys

import numpy as np
import onnxruntime as ort

SR = 16000
HOP = 160
CENTS_BASE = 1997.3794084376191


def align_frames(n: int) -> int:
    frames = n // HOP + 1
    padded = ((frames + 31) // 32) * 32
    return (padded - 1) * HOP


def decode(salience: np.ndarray, threshold: float = 0.03) -> np.ndarray:
    cents_map = 20.0 * np.arange(360) + CENTS_BASE
    out = np.zeros(salience.shape[0], dtype=np.float64)
    for i, frame in enumerate(salience):
        center = int(frame.argmax())
        if frame[center] <= threshold:
            continue
        lo, hi = max(0, center - 4), min(360, center + 5)
        w = frame[lo:hi]
        cents = (w * cents_map[lo:hi]).sum() / w.sum()
        out[i] = 10.0 * 2.0 ** (cents / 1200.0)
    return out


def make_tone(f0: float, seconds: float = 1.0) -> np.ndarray:
    n = align_frames(int(SR * seconds))
    t = np.arange(n) / SR
    x = np.zeros(n)
    # 単純な正弦だと現実の声と離れるので、倍音を持つのこぎり波状にする
    for k in range(1, 21):
        if f0 * k >= SR / 2:
            break
        x += np.sin(2 * np.pi * f0 * k * t) / k
    x /= np.abs(x).max()
    return (x * 0.5).astype(np.float32)


def main() -> None:
    path = sys.argv[1] if len(sys.argv) > 1 else "../models/rmvpe.onnx"
    sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    print(f"loaded: {path}")
    print(f"  input : {[(i.name, i.shape) for i in sess.get_inputs()]}")
    print(f"  output: {[(o.name, o.shape) for o in sess.get_outputs()]}")

    ok = True
    for f0 in (80.0, 110.0, 220.0, 440.0, 660.0):
        audio = make_tone(f0)[None, :]
        salience = sess.run(None, {"audio": audio})[0][0]
        est = decode(salience)
        voiced = est[est > 0]
        if len(voiced) == 0:
            print(f"  {f0:6.1f} Hz -> 無声判定 (FAIL)")
            ok = False
            continue
        med = float(np.median(voiced))
        cent_err = 1200.0 * np.log2(med / f0)
        vuv = len(voiced) / len(est)
        flag = "ok" if abs(cent_err) < 50 and vuv > 0.8 else "FAIL"
        if flag == "FAIL":
            ok = False
        print(f"  {f0:6.1f} Hz -> {med:7.2f} Hz  ({cent_err:+6.1f} cent, voiced {vuv:.0%})  {flag}")

    # 無音は無声で返るべき
    sil = np.zeros((1, align_frames(SR)), dtype=np.float32)
    est = decode(sess.run(None, {"audio": sil})[0][0])
    vuv = float((est > 0).mean())
    print(f"  silence  -> voiced {vuv:.0%}  {'ok' if vuv < 0.1 else 'FAIL'}")
    if vuv >= 0.1:
        ok = False

    print("RESULT:", "PASS" if ok else "FAIL")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
