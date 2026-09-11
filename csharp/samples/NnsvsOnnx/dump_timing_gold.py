# -*- coding: utf-8 -*-
r"""timelag/duration MDN post-processing + timing-label generation (gold dump).

nnsvs.gen.predict_timelag / predict_duration / postprocess_duration (called in
exactly the same order/arguments as nnsvs.gen.predict_timing) を実行し、その
中間値・最終出力を C# 実装 (TimingLabels.cs) の数値検証用ゴールドとして
.bin (float32 LE / int64 LE) + メタ JSON にダンプする。

参照実装 (読み取りのみ、変更しない):
  G:\SimpleEnunu-0.3.1_TunedWavOut\nnsvs-master\nnsvs\gen.py
    (predict_timelag, predict_duration, postprocess_duration, predict_timing)
  G:\SimpleEnunu-0.3.1_TunedWavOut\nnsvs-master\nnsvs\io\hts.py
    (get_note_indices, get_pitch_indices)
  G:\SimpleEnunu-0.3.1_TunedWavOut\python-3.9.13-embed-amd64\Lib\site-packages\nnmnkwii\
    io\hts.py (HTSLabelFile.round_/append/set_durations)
    frontend\merlin.py (linguistic_features, duration_features)
  g:\libllsm2\csharp\samples\WorldToLlsm\export_onnx_simple.py (model loading pattern)

入力ラベル (自己完結のため testdata コピーを使用):
  g:\libllsm2\csharp\samples\NnsvsOnnx\testdata\linguistic_0_score_lab.lab
  g:\libllsm2\csharp\samples\NnsvsOnnx\testdata\linguistic_1_score_lab.lab

モデル/スケーラ: G:\ENUNU_波音リツ_4CC_#3019_WD_SiFiGAN
  timelag.allowed_range=[-20,20], allowed_range_rest=[-40,40] (config.yaml)
  frame_period=5ms -> hts_frame_shift=50000 (100ns units)

実行:
  G:\SimpleEnunu-0.3.1_TunedWavOut\python-3.9.13-embed-amd64\python.exe dump_timing_gold.py
"""
import json
import os
import sys

sys.path.insert(0, r"G:\SimpleEnunu-0.3.1_TunedWavOut\nnsvs-master")

import librosa
import numpy as np
import torch
from hydra.utils import instantiate
from omegaconf import OmegaConf
from sklearn.preprocessing import MinMaxScaler, StandardScaler

from nnmnkwii.frontend import merlin as fe
from nnmnkwii.io import hts
from nnmnkwii.preprocessing.f0 import interp1d
from nnsvs.gen import postprocess_duration, predict_duration, predict_timelag
from nnsvs.io.hts import get_note_indices, get_pitch_indices

MODEL_DIR = r"G:\ENUNU_波音リツ_4CC_#3019_WD_SiFiGAN"
QST_PATH = os.path.join(MODEL_DIR, "qst.hed")
OUT_DIR = r"g:\libllsm2\csharp\samples\NnsvsOnnx\testdata"

FRAME_PERIOD_MS = 5
HTS_FRAME_SHIFT = int(FRAME_PERIOD_MS * 1e4)  # 50000
ALLOWED_RANGE = [-20, 20]
ALLOWED_RANGE_REST = [-40, 40]

CASES = [
    dict(tag="0", score_lab=os.path.join(OUT_DIR, "linguistic_0_score_lab.lab")),
    dict(tag="1", score_lab=os.path.join(OUT_DIR, "linguistic_1_score_lab.lab")),
]


def _midi_to_hz(x, idx, log_f0=False):
    z = np.zeros(len(x))
    indices = x[:, idx] > 0
    z[indices] = librosa.midi_to_hz(x[indices, idx])
    if log_f0:
        z[indices] = np.log(z[indices])
    return z


def apply_log_f0_conditioning(feats, pitch_indices):
    for idx in pitch_indices:
        feats[:, idx] = interp1d(_midi_to_hz(feats, idx, log_f0=True), kind="slinear")


def make_minmax_scaler(name, dim):
    sc = MinMaxScaler()
    sc.min_ = np.load(os.path.join(MODEL_DIR, f"in_{name}_scaler_min.npy")).astype(np.float64)
    sc.scale_ = np.load(os.path.join(MODEL_DIR, f"in_{name}_scaler_scale.npy")).astype(np.float64)
    sc.feature_range = (0.0, 1.0)
    sc.n_features_in_ = dim
    return sc


def make_standard_scaler(name, dim):
    sc = StandardScaler()
    sc.mean_ = np.load(os.path.join(MODEL_DIR, f"out_{name}_scaler_mean.npy")).astype(np.float64)
    sc.scale_ = np.load(os.path.join(MODEL_DIR, f"out_{name}_scaler_scale.npy")).astype(np.float64)
    sc.var_ = np.load(os.path.join(MODEL_DIR, f"out_{name}_scaler_var.npy")).astype(np.float64)
    sc.with_mean = True
    sc.with_std = True
    sc.n_features_in_ = dim
    return sc


def load_mdn(name):
    cfg = OmegaConf.load(os.path.join(MODEL_DIR, name + "_model.yaml"))
    model = instantiate(cfg.netG)
    ckpt = torch.load(os.path.join(MODEL_DIR, name + "_model.pth"), map_location="cpu")
    model.load_state_dict(ckpt["state_dict"])
    model.eval()
    return model, cfg


def compute_raw_mdn(model, labels_for_model, binary_dict, numeric_dict, in_scaler, pitch_indices):
    """Re-derives the RAW (normalized, pre-denorm) MDN mu/sigma for the given
    labels, mirroring the linguistic-feature-extraction + normalization +
    model.inference() steps performed inside predict_timelag/predict_duration
    (deterministic function of labels_for_model + in_scaler, so this is
    bit-identical to what those functions computed internally)."""
    feats = fe.linguistic_features(
        labels_for_model,
        binary_dict,
        numeric_dict,
        add_frame_features=False,
        subphone_features=None,
        frame_shift=HTS_FRAME_SHIFT,
    ).astype(np.float32)
    for idx in pitch_indices:
        feats[:, idx] = interp1d(_midi_to_hz(feats, idx, log_f0=True), kind="slinear")

    x = in_scaler.transform(feats)
    if isinstance(in_scaler, MinMaxScaler):
        non_pitch = [i for i in range(x.shape[1]) if i not in pitch_indices]
        x[:, non_pitch] = np.clip(x[:, non_pitch], in_scaler.feature_range[0], in_scaler.feature_range[1])

    xt = torch.from_numpy(x).unsqueeze(0)
    with torch.no_grad():
        max_mu, max_sigma = model.inference(xt, [xt.shape[1]])
    return max_mu.squeeze(0).numpy(), max_sigma.squeeze(0).numpy()


def write_bin_f32(name, arr):
    path = os.path.join(OUT_DIR, name)
    np.ascontiguousarray(arr, dtype="<f4").tofile(path)
    return name


def write_bin_i64(name, arr):
    path = os.path.join(OUT_DIR, name)
    np.ascontiguousarray(arr, dtype="<i8").tofile(path)
    return name


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    device = torch.device("cpu")
    torch.manual_seed(0)

    binary_dict, numeric_dict = hts.load_question_set(QST_PATH)
    pitch_indices = get_pitch_indices(binary_dict, numeric_dict)

    timelag_model, timelag_cfg = load_mdn("timelag")
    duration_model, duration_cfg = load_mdn("duration")

    timelag_in_scaler = make_minmax_scaler("timelag", timelag_cfg.netG.in_dim)
    timelag_out_scaler = make_standard_scaler("timelag", timelag_cfg.netG.out_dim)
    duration_in_scaler = make_minmax_scaler("duration", duration_cfg.netG.in_dim)
    duration_out_scaler = make_standard_scaler("duration", duration_cfg.netG.out_dim)

    manifest = {
        "hts_frame_shift": HTS_FRAME_SHIFT,
        "allowed_range": ALLOWED_RANGE,
        "allowed_range_rest": ALLOWED_RANGE_REST,
        "cases": [],
    }

    for c in CASES:
        tag = c["tag"]
        labels = hts.load(c["score_lab"])

        # predict_timelag rounds `labels` in-place (sets frame_shift + rounds
        # start/end times); predict_duration and postprocess_duration operate
        # on this SAME (now-rounded) labels object, exactly as predict_timing does.
        lag = predict_timelag(
            device=device,
            labels=labels,
            timelag_model=timelag_model,
            timelag_config=timelag_cfg,
            timelag_in_scaler=timelag_in_scaler,
            timelag_out_scaler=timelag_out_scaler,
            binary_dict=binary_dict,
            numeric_dict=numeric_dict,
            pitch_indices=pitch_indices,
            log_f0_conditioning=True,
            allowed_range=ALLOWED_RANGE,
            allowed_range_rest=ALLOWED_RANGE_REST,
            force_clip_input_features=True,
            frame_period=FRAME_PERIOD_MS,
        )

        durations = predict_duration(
            device=device,
            labels=labels,
            duration_model=duration_model,
            duration_config=duration_cfg,
            duration_in_scaler=duration_in_scaler,
            duration_out_scaler=duration_out_scaler,
            binary_dict=binary_dict,
            numeric_dict=numeric_dict,
            pitch_indices=pitch_indices,
            log_f0_conditioning=True,
            force_clip_input_features=True,
            frame_period=FRAME_PERIOD_MS,
        )
        duration_mu, duration_sigma_sq = durations

        timing_labels = postprocess_duration(labels, durations, lag)

        assert list(timing_labels.contexts) == list(labels.contexts), (
            f"[{tag}] postprocess_duration output contexts must equal input "
            "contexts in order"
        )

        # --- raw (normalized, pre-denorm) MDN outputs, for C#'s own
        # MdnModel+scaler chain verification (Path B) ---
        note_indices = get_note_indices(labels)
        note_labels = labels[note_indices]
        timelag_raw_mu, timelag_raw_sigma = compute_raw_mdn(
            timelag_model, note_labels, binary_dict, numeric_dict, timelag_in_scaler, pitch_indices
        )
        duration_raw_mu, duration_raw_sigma = compute_raw_mdn(
            duration_model, labels, binary_dict, numeric_dict, duration_in_scaler, pitch_indices
        )

        n_notes = len(note_indices)
        n_phones = len(labels)
        assert lag.shape == (n_notes, 1)
        assert duration_mu.shape == (n_phones, 1)
        assert duration_sigma_sq.shape == (n_phones, 1)
        assert len(timing_labels) == n_phones

        prefix = f"timing_{tag}"
        case = {
            "tag": tag,
            "score_lab": f"linguistic_{tag}_score_lab.lab",
            "num_notes": n_notes,
            "num_phones": n_phones,
            "timelag_raw_mu": write_bin_f32(f"{prefix}_timelag_raw_mu.bin", timelag_raw_mu),
            "timelag_raw_sigma": write_bin_f32(f"{prefix}_timelag_raw_sigma.bin", timelag_raw_sigma),
            "duration_raw_mu": write_bin_f32(f"{prefix}_duration_raw_mu.bin", duration_raw_mu),
            "duration_raw_sigma": write_bin_f32(f"{prefix}_duration_raw_sigma.bin", duration_raw_sigma),
            "timelag_lag": write_bin_f32(f"{prefix}_timelag_lag.bin", lag),
            "duration_mu": write_bin_f32(f"{prefix}_duration_mu.bin", duration_mu),
            "duration_sigma_sq": write_bin_f32(f"{prefix}_duration_sigma_sq.bin", duration_sigma_sq),
            "timing_start": write_bin_i64(f"{prefix}_final_start.bin", np.asarray(timing_labels.start_times, dtype=np.int64)),
            "timing_end": write_bin_i64(f"{prefix}_final_end.bin", np.asarray(timing_labels.end_times, dtype=np.int64)),
            "timing_count": n_phones,
        }
        manifest["cases"].append(case)

        print(
            f"[{tag}] notes={n_notes} phones={n_phones} "
            f"lag_range=[{lag.min():.0f},{lag.max():.0f}] "
            f"dur_range=[{np.asarray(timing_labels.end_times, dtype=np.int64).max() * 1e-7:.3f}s]"
        )

    with open(os.path.join(OUT_DIR, "timing_manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)

    print("Wrote", os.path.join(OUT_DIR, "timing_manifest.json"))


if __name__ == "__main__":
    main()
