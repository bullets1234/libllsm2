#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
RMVPE を「16kHz 波形入力 / cent salience 出力」の単一 ONNX へ書き出す。

なぜ波形入力にするか
--------------------
素の RMVPE はメルスペクトログラム (1, 128, T) を入力に取る。それを C# 側で
librosa 互換に再現するのは (slaney 正規化メルフィルタ・reflect パディング・
periodic Hann) の一致確認が必要で壊れやすい。ここでは STFT を conv1d の
固定重みとして表現し、メル変換・log クランプまでグラフに畳み込む。
結果として C# 側の責務は「16kHz へリサンプルするだけ」になる。

torch.stft を使わず conv1d で書くのは、opset/実行環境を問わず確実に
エクスポートできるようにするため（matmul と conv しか使わない）。

必要なもの
----------
1. RMVPE の重み `rmvpe.pt`
     Hugging Face: lj1995/VoiceConversionWebUI → rmvpe.pt
2. RMVPE のモデル定義（RVC 同梱の infer/lib/rmvpe.py）
     git clone https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI
   ※ 再配布を避けるためモデル定義はこのスクリプトに同梱していない。
3. pip install torch librosa numpy onnx onnxruntime

使い方
------
  python export_rmvpe_onnx.py \
      --rmvpe-pt  D:/models/rmvpe.pt \
      --rvc-root  D:/src/Retrieval-based-Voice-Conversion-WebUI \
      --out       ../models/rmvpe.onnx

  # 生成後の検証（PyTorch と ONNX の F0 を突き合わせる）
  python export_rmvpe_onnx.py ... --verify-wav some.wav

出力
----
  input   audio    float32 (1, N)       16kHz, [-1,1], N は 160 の倍数
  output  salience float32 (1, T, 360)  T = N/160 + 1（32 の倍数であること）
"""

from __future__ import annotations

import argparse
import importlib.util
import os
import sys

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

N_FFT = 1024
WIN_LENGTH = 1024
HOP_LENGTH = 160
N_MELS = 128
SR = 16000
MEL_FMIN = 30.0
MEL_FMAX = 8000.0
CLAMP = 1e-5
N_BINS = 360
CENTS_BASE = 1997.3794084376191


def load_e2e_class(rvc_root: str | None, model_py: str | None):
    """RVC の rmvpe.py から E2E クラスを取り込む。"""
    if model_py:
        path = model_py
    elif rvc_root:
        for rel in (
            os.path.join("infer", "rmvpe.py"),
            os.path.join("infer", "lib", "rmvpe.py"),
            "rmvpe.py",
        ):
            cand = os.path.join(rvc_root, rel)
            if os.path.exists(cand):
                path = cand
                break
        else:
            path = os.path.join(rvc_root, "infer", "rmvpe.py")
    else:
        # 同梱の thirdparty/rmvpe.py を既定とする
        path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "thirdparty", "rmvpe.py")

    if not os.path.exists(path):
        raise SystemExit(f"rmvpe.py が見つかりません: {path}")

    _stub_rvc_only_imports()

    spec = importlib.util.spec_from_file_location("_rmvpe_defs", path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    sys.modules["_rmvpe_defs"] = module
    spec.loader.exec_module(module)

    if not hasattr(module, "E2E"):
        raise SystemExit(f"{path} に E2E クラスがありません")
    return module.E2E


def _stub_rvc_only_imports() -> None:
    """
    rmvpe.py は `from tools.cuda_graph import run_cuda_graph` を import するが、
    これは RVC WebUI ツリー内にしか存在しない。E2E / DeepUnet の定義自体は
    この関数を使わないので、ダミーを sys.modules へ先に入れて import を通す。
    """
    import types

    if "tools.cuda_graph" in sys.modules:
        return
    pkg = sys.modules.get("tools")
    if pkg is None:
        pkg = types.ModuleType("tools")
        pkg.__path__ = []  # type: ignore[attr-defined]
        sys.modules["tools"] = pkg
    sub = types.ModuleType("tools.cuda_graph")

    def run_cuda_graph(*args, **kwargs):  # pragma: no cover - エクスポートでは未使用
        raise RuntimeError("run_cuda_graph is not available in the export environment")

    sub.run_cuda_graph = run_cuda_graph  # type: ignore[attr-defined]
    sys.modules["tools.cuda_graph"] = sub
    setattr(pkg, "cuda_graph", sub)


class MelFrontend(nn.Module):
    """conv1d ベースの STFT + slaney メル + log クランプ。"""

    def __init__(self) -> None:
        super().__init__()
        import librosa

        cutoff = N_FFT // 2 + 1
        fourier = np.fft.fft(np.eye(N_FFT))
        basis = np.vstack([np.real(fourier[:cutoff]), np.imag(fourier[:cutoff])])
        window = np.hanning(WIN_LENGTH + 1)[:-1]  # torch.hann_window(periodic=True) と同一
        weight = torch.from_numpy(basis).float().unsqueeze(1)
        weight = weight * torch.from_numpy(window).float().view(1, 1, -1)
        self.register_buffer("stft_weight", weight)  # (2*cutoff, 1, n_fft)

        mel_basis = librosa.filters.mel(
            sr=SR, n_fft=N_FFT, n_mels=N_MELS, fmin=MEL_FMIN, fmax=MEL_FMAX, htk=True
        )
        self.register_buffer("mel_basis", torch.from_numpy(mel_basis).float())
        self.cutoff = cutoff

    def forward(self, audio: torch.Tensor) -> torch.Tensor:
        # audio: (B, N) -> logmel: (B, 128, T)
        x = audio.unsqueeze(1)
        x = F.pad(x, (N_FFT // 2, N_FFT // 2), mode="reflect")
        spec = F.conv1d(x, self.stft_weight, stride=HOP_LENGTH)
        real = spec[:, : self.cutoff]
        imag = spec[:, self.cutoff :]
        # RVC 実装と完全に揃えるため eps を足さない（推論専用なので勾配の心配はない）
        magnitude = torch.sqrt(real * real + imag * imag)
        mel = torch.matmul(self.mel_basis, magnitude)
        return torch.log(torch.clamp(mel, min=CLAMP))


class RmvpeAudioModel(nn.Module):
    """波形 → salience (B, T, 360)。"""

    def __init__(self, e2e: nn.Module) -> None:
        super().__init__()
        self.frontend = MelFrontend()
        self.e2e = e2e

    def forward(self, audio: torch.Tensor) -> torch.Tensor:
        return self.e2e(self.frontend(audio))


def build_model(e2e_cls, ckpt_path: str) -> RmvpeAudioModel:
    e2e = e2e_cls(4, 1, (2, 2))
    try:
        ckpt = torch.load(ckpt_path, map_location="cpu", weights_only=True)
    except TypeError:  # torch < 2.0
        ckpt = torch.load(ckpt_path, map_location="cpu")
    if isinstance(ckpt, dict) and "model" in ckpt and not any(k.startswith("unet") for k in ckpt):
        ckpt = ckpt["model"]
    e2e.load_state_dict(ckpt)
    e2e.eval()

    model = RmvpeAudioModel(e2e)
    model.eval()
    for p in model.parameters():
        p.requires_grad_(False)
    return model


def decode_f0(salience: np.ndarray, threshold: float = 0.03) -> np.ndarray:
    """C# 側 Decode と同じ手順（検証用）。"""
    mapping = 20.0 * np.arange(N_BINS) + CENTS_BASE
    argmax = salience.argmax(axis=-1)
    peak = salience.max(axis=-1)
    f0 = np.zeros(len(salience), dtype=np.float64)
    for i, c in enumerate(argmax):
        lo, hi = max(0, c - 4), min(N_BINS - 1, c + 4)
        w = salience[i, lo : hi + 1]
        cents = float((w * mapping[lo : hi + 1]).sum() / max(w.sum(), 1e-12))
        f0[i] = 10.0 * 2.0 ** (cents / 1200.0)
    f0[peak <= threshold] = 0.0
    return f0


def align_frames(n_samples: int) -> int:
    """フレーム数を 32 の倍数に切り上げたときのサンプル長。"""
    frames = n_samples // HOP_LENGTH + 1
    padded = ((frames + 31) // 32) * 32
    return (padded - 1) * HOP_LENGTH


def check_frontend(wav_path: str | None) -> None:
    """
    自作 MelFrontend（conv1d 版）と RVC の MelSpectrogram（torch.stft 版）の一致を確かめる。
    htk / 窓 / パディングのどれかがずれるとここで大きな差として出る。
    """
    defs = sys.modules.get("_rmvpe_defs")
    if defs is None or not hasattr(defs, "MelSpectrogram"):
        print("frontend check: skipped (MelSpectrogram not found)")
        return

    if wav_path:
        import librosa

        audio, _ = librosa.load(wav_path, sr=SR, mono=True)
        n = align_frames(len(audio))
        buf = np.zeros(n, dtype=np.float32)
        buf[: min(n, len(audio))] = audio[:n]
    else:
        rng = np.random.default_rng(0)
        buf = rng.standard_normal(align_frames(SR)).astype(np.float32) * 0.1

    x = torch.from_numpy(buf[None, :])
    ref_mel = defs.MelSpectrogram(False, N_MELS, SR, WIN_LENGTH, HOP_LENGTH, None, MEL_FMIN, MEL_FMAX)
    with torch.no_grad():
        ref = ref_mel(x).numpy()
        got = MelFrontend()(x).numpy()

    if ref.shape != got.shape:
        raise SystemExit(f"frontend shape mismatch: RVC {ref.shape} vs ours {got.shape}")

    diff = np.abs(ref - got).max()
    print(f"frontend check  : shape {got.shape}, max|d| {diff:.3e}")
    if diff > 1e-3:
        raise SystemExit(
            f"frontend mismatch too large ({diff:.3e}). "
            "mel の htk/norm、窓関数、パディングのいずれかが RVC 実装と違います。"
        )


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--rmvpe-pt", required=True, help="rmvpe.pt のパス")
    ap.add_argument("--rvc-root", help="RVC WebUI のチェックアウト（infer/rmvpe.py を含む）")
    ap.add_argument("--model-py", help="rmvpe.py を直接指定する場合（既定: tools/thirdparty/rmvpe.py）")
    ap.add_argument("--out", default="rmvpe.onnx", help="出力 ONNX パス")
    ap.add_argument("--opset", type=int, default=17)
    ap.add_argument(
        "--dynamo",
        action="store_true",
        help="torch.export ベースの新エクスポータを使う（DeepUnet の skip 接続で"
             "動的形状の等価性を証明できず失敗しやすい）",
    )
    ap.add_argument("--verify-wav", help="書き出し後、この wav で PyTorch と ONNX を比較")
    args = ap.parse_args()

    e2e_cls = load_e2e_class(args.rvc_root, args.model_py)
    model = build_model(e2e_cls, args.rmvpe_pt)

    # 移植ミスの主な温床はフロントエンドなので、書き出す前に必ず照合する
    check_frontend(args.verify_wav)

    dummy_len = align_frames(SR * 2)
    dummy = torch.zeros(1, dummy_len)

    os.makedirs(os.path.dirname(os.path.abspath(args.out)) or ".", exist_ok=True)

    export_kwargs = dict(
        input_names=["audio"],
        output_names=["salience"],
        dynamic_axes={"audio": {1: "n_samples"}, "salience": {1: "n_frames"}},
        opset_version=args.opset,
        do_constant_folding=True,
    )
    # torch 2.6+ の既定は dynamo エクスポータだが、DeepUnet の
    # アップサンプル後の skip 接続でシンボリック形状の一致を証明できず落ちる。
    # トレースベースの旧エクスポータなら問題ない。
    if not args.dynamo:
        export_kwargs["dynamo"] = False

    torch.onnx.export(model, dummy, args.out, **export_kwargs)
    size_mb = os.path.getsize(args.out) / 1024 / 1024
    print(f"exported: {args.out} ({size_mb:.1f} MB, opset {args.opset})")

    if args.verify_wav:
        verify(model, args.out, args.verify_wav)


def verify(model: nn.Module, onnx_path: str, wav_path: str) -> None:
    import librosa
    import onnxruntime as ort

    audio, _ = librosa.load(wav_path, sr=SR, mono=True)
    n = align_frames(len(audio))
    buf = np.zeros(n, dtype=np.float32)
    buf[: min(n, len(audio))] = audio[:n]
    x = buf[None, :]

    with torch.no_grad():
        ref = model(torch.from_numpy(x)).numpy()[0]

    sess = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
    got = sess.run(["salience"], {"audio": x})[0][0]

    f0_ref = decode_f0(ref)
    f0_got = decode_f0(got)
    voiced = (f0_ref > 0) & (f0_got > 0)
    cent_err = 1200.0 * np.abs(np.log2(f0_got[voiced] / f0_ref[voiced])) if voiced.any() else np.array([0.0])

    print(f"frames          : {len(f0_ref)}  voiced: {int(voiced.sum())}")
    print(f"salience max|d| : {np.abs(ref - got).max():.3e}")
    print(f"f0 cent err     : mean {cent_err.mean():.4f}  max {cent_err.max():.4f}")
    print(f"vuv mismatch    : {int(((f0_ref > 0) != (f0_got > 0)).sum())}")


if __name__ == "__main__":
    # Windows の既定コンソールは cp932 なので、torch が出す記号で落ちないようにする
    for _stream in (sys.stdout, sys.stderr):
        try:
            _stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[union-attr]
        except Exception:
            pass
    main()
