# -*- coding: utf-8 -*-
r"""HTS full-context labels + qst.hed -> linguistic features (gold dump).

nnsvs.gen.predict_timelag / predict_duration / predict_acoustic と全く同じ
前処理 (nnmnkwii.frontend.merlin.linguistic_features 呼び出し + log_f0_conditioning
による音高列変換) を、スケーラ正規化を適用する **前** の生の特徴量として実行し、
C# 実装 (LinguisticFeatures.cs) の数値検証用ゴールドとして .bin (float32 LE) +
メタ JSON にダンプする。

参照実装 (読み取りのみ、変更しない):
  G:\SimpleEnunu-0.3.1_TunedWavOut\nnsvs-master\nnsvs\gen.py
    (predict_timelag, predict_duration, predict_acoustic, _midi_to_hz)
  G:\SimpleEnunu-0.3.1_TunedWavOut\nnsvs-master\nnsvs\io\hts.py
    (get_note_indices, get_pitch_indices)
  G:\SimpleEnunu-0.3.1_TunedWavOut\python-3.9.13-embed-amd64\Lib\site-packages\nnmnkwii\
    io\hts.py (HTSLabelFile, load_question_set, wildcards2regex)
    frontend\merlin.py (linguistic_features, coarse coding)
    preprocessing\f0.py (interp1d)

ラベル実データ (実際の ENUNU 実行時の一時ファイル。この波音リツモデル専用の
qst.hed に対して生成された本物の full-context label):
  G:\0_enutemp\0_score.full   -- timelag/duration 用 (score-level, 継続時間予測前)
  G:\0_enutemp\0_timing.full  -- acoustic 用 (duration_modified_labels 相当,
                                  実際に予測された音素長を反映した frame-level 元)

実行:
  G:\SimpleEnunu-0.3.1_TunedWavOut\python-3.9.13-embed-amd64\python.exe dump_linguistic_gold.py
"""
import json
import os
import sys

sys.path.insert(0, r"G:\SimpleEnunu-0.3.1_TunedWavOut\nnsvs-master")

import librosa
import numpy as np
from nnmnkwii.frontend import merlin as fe
from nnmnkwii.io import hts
from nnmnkwii.preprocessing.f0 import interp1d
from nnsvs.io.hts import get_note_indices, get_pitch_indices

MODEL_DIR = r"G:\ENUNU_波音リツ_4CC_#3019_WD_SiFiGAN"
QST_PATH = os.path.join(MODEL_DIR, "qst.hed")

LABEL_SOURCES = [
    dict(
        tag="0",
        score_lab=r"G:\0_enutemp\0_score.full",
        timing_lab=r"G:\0_enutemp\0_timing.full",
    ),
    dict(
        tag="1",
        score_lab=r"G:\_enutemp\_score.full",
        timing_lab=r"G:\_enutemp\_timing.full",
    ),
]

OUT_DIR = r"g:\libllsm2\csharp\samples\NnsvsOnnx\testdata"
FRAME_PERIOD_MS = 5
HTS_FRAME_SHIFT = int(FRAME_PERIOD_MS * 1e4)  # 50000 (100ns units) = 5ms


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


def write_bin(name, arr):
    path = os.path.join(OUT_DIR, name)
    arr.astype(np.float32).tofile(path)
    return name


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    binary_dict, numeric_dict = hts.load_question_set(QST_PATH)
    pitch_indices = get_pitch_indices(binary_dict, numeric_dict)
    dict_size = len(binary_dict) + len(numeric_dict)
    print(f"binary_dict: {len(binary_dict)} entries")
    print(f"numeric_dict: {len(numeric_dict)} entries")
    print(f"score-level dim (binary+numeric): {dict_size}")
    print(f"pitch_indices: {pitch_indices}")
    assert dict_size == 109, f"expected 109-dim score-level features, got {dict_size}"

    # Copy qst.hed for the C# side (self-contained testdata, read-only copy).
    with open(QST_PATH, "r", encoding="utf-8", errors="replace") as f:
        qst_text = f.read()
    with open(os.path.join(OUT_DIR, "linguistic_qst.hed"), "w", encoding="utf-8") as f:
        f.write(qst_text)

    manifest = {
        "binary_dict_size": len(binary_dict),
        "numeric_dict_size": len(numeric_dict),
        "score_dim": dict_size,
        "acoustic_dim": dict_size + 4,
        "pitch_indices": pitch_indices,
        "frame_period_ms": FRAME_PERIOD_MS,
        "hts_frame_shift": HTS_FRAME_SHIFT,
        "cases": [],
    }

    for src in LABEL_SOURCES:
        tag = src["tag"]

        # Copy label files as-is for C# to load directly.
        for kind in ("score_lab", "timing_lab"):
            with open(src[kind], "r", encoding="utf-8", errors="replace") as f:
                text = f.read()
            out_name = f"linguistic_{tag}_{kind}.lab"
            with open(os.path.join(OUT_DIR, out_name), "w", encoding="utf-8") as f:
                f.write(text)

        # --- timelag: note-level, add_frame_features=False, subphone=None ---
        labels = hts.load(src["score_lab"])
        labels.frame_shift = HTS_FRAME_SHIFT
        labels.round_()
        note_indices = get_note_indices(labels)
        note_labels = labels[note_indices]
        timelag_feats = fe.linguistic_features(
            note_labels,
            binary_dict,
            numeric_dict,
            add_frame_features=False,
            subphone_features=None,
            frame_shift=HTS_FRAME_SHIFT,
        ).astype(np.float32)
        apply_log_f0_conditioning(timelag_feats, pitch_indices)
        assert timelag_feats.shape[1] == 109

        # --- duration: phone-level, add_frame_features=False, subphone=None ---
        duration_feats = fe.linguistic_features(
            labels,
            binary_dict,
            numeric_dict,
            add_frame_features=False,
            subphone_features=None,
            frame_shift=HTS_FRAME_SHIFT,
        ).astype(np.float32)
        apply_log_f0_conditioning(duration_feats, pitch_indices)
        assert duration_feats.shape[1] == 109

        # --- acoustic: frame-level, add_frame_features=True, coarse_coding ---
        timing_labels = hts.load(src["timing_lab"])
        acoustic_feats = fe.linguistic_features(
            timing_labels,
            binary_dict,
            numeric_dict,
            add_frame_features=True,
            subphone_features="coarse_coding",
            frame_shift=HTS_FRAME_SHIFT,
        )
        apply_log_f0_conditioning(acoustic_feats, pitch_indices)
        assert acoustic_feats.shape[1] == 113

        timelag_bin = write_bin(f"linguistic_{tag}_timelag_gold.bin", timelag_feats)
        duration_bin = write_bin(f"linguistic_{tag}_duration_gold.bin", duration_feats)
        acoustic_bin = write_bin(f"linguistic_{tag}_acoustic_gold.bin", acoustic_feats)

        case = {
            "tag": tag,
            "score_lab": f"linguistic_{tag}_score_lab.lab",
            "timing_lab": f"linguistic_{tag}_timing_lab.lab",
            "num_phones_score": len(labels),
            "num_notes": len(note_indices),
            "num_phones_timing": len(timing_labels),
            "num_frames_acoustic": acoustic_feats.shape[0],
            "timelag": {"file": timelag_bin, "shape": list(timelag_feats.shape)},
            "duration": {"file": duration_bin, "shape": list(duration_feats.shape)},
            "acoustic": {"file": acoustic_bin, "shape": list(acoustic_feats.shape)},
        }
        manifest["cases"].append(case)

        print(
            f"[{tag}] notes={len(note_indices)} phones(score)={len(labels)} "
            f"phones(timing)={len(timing_labels)} frames(acoustic)={acoustic_feats.shape[0]}"
        )

    with open(os.path.join(OUT_DIR, "linguistic_manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)

    print("Wrote", os.path.join(OUT_DIR, "linguistic_manifest.json"))


if __name__ == "__main__":
    main()
