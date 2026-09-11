"""Generates gold reference outputs for the GV post-filter + trajectory
smoothing step (nnsvs.gen.postprocess_acoustic), for cross-checking against
the C# port in AcousticPostfilter.cs.

Reads the debug dump produced by `dotnet run -- --pipeline-dump`
(testdata/postfilter_debug/case{tag}_acoustic_physical.bin,
case{tag}_score_pitch_raw.bin, case{tag}_manifest.json) and writes gold
.bin files (float32, little-endian) for every intermediate stage:
  case{tag}_gold_mgc_gv.bin        (T, 60) -- after GV post-filter
  case{tag}_gold_lf0_prefilter.bin (T,)    -- lf0 after round-trip + interp1d, before smoothing
  case{tag}_gold_lf0_smoothed.bin  (T,)    -- lf0 after Butterworth trajectory smoothing
  case{tag}_gold_mgc_final.bin     (T, 60) -- mgc after GV + trajectory smoothing
  case{tag}_gold_bap_clipped.bin   (T, 5)  -- bap after trajectory smoothing + [-60,0] clip
  case{tag}_gold_f0_final.bin      (T,)    -- final F0 in Hz (exp(lf0_smoothed), gated by vuv)

Run with: G:\\SimpleEnunu-0.3.1_TunedWavOut\\python-3.9.13-embed-amd64\\python.exe dump_postfilter_gold.py
"""
import json
import sys

import numpy as np

sys.path.insert(0, r"G:\SimpleEnunu-0.3.1_TunedWavOut\nnsvs-master")

from nnmnkwii.preprocessing.f0 import interp1d
from nnsvs.dsp import lowpass_filter
from nnsvs.postfilters import variance_scaling

TESTDATA_DIR = r"g:\libllsm2\csharp\samples\NnsvsOnnx\testdata"
DBG_DIR = TESTDATA_DIR + r"\postfilter_debug"
MODEL_DIR = r"G:\ENUNU_波音リツ_4CC_#3019_WD_SiFiGAN"

MGC_DIM = 60
BAP_DIM = 5
OUT_DIM = MGC_DIM + 2 + BAP_DIM
VUV_THRESHOLD = 0.5
MODFS = 200
MGC_CUTOFF = 50
BAP_CUTOFF = 50
LF0_CUTOFF = 20


def load_bin(path, dtype=np.float32):
    return np.fromfile(path, dtype=dtype)


def save_bin(path, arr):
    arr.astype(np.float32).tofile(path)


def process(tag):
    with open(f"{DBG_DIR}\\case{tag}_manifest.json", "r", encoding="utf-8") as f:
        manifest = json.load(f)
    t = manifest["T"]

    physical = load_bin(f"{DBG_DIR}\\case{tag}_acoustic_physical.bin").reshape(t, OUT_DIM).astype(np.float64)
    raw_pitch = load_bin(f"{DBG_DIR}\\case{tag}_score_pitch_raw.bin").astype(np.float64)

    note_frame_indices = np.where(raw_pitch > 0)[0]

    gv_full = np.load(f"{MODEL_DIR}\\out_acoustic_scaler_var.npy").reshape(-1)
    gv = gv_full[:MGC_DIM]

    mgc = physical[:, :MGC_DIM].copy()
    lf0_raw = physical[:, MGC_DIM].copy()
    vuv = (physical[:, MGC_DIM + 1] >= VUV_THRESHOLD).astype(np.float64)
    bap = physical[:, MGC_DIM + 2:MGC_DIM + 2 + BAP_DIM].copy()

    # GV post-filter (nnsvs.postfilters.variance_scaling, offset=2)
    mgc_gv = variance_scaling(gv, mgc, offset=2, note_frame_indices=note_frame_indices)

    # f0/lf0 round-trip (gen_spsvs_static_features, relative_f0=False path)
    f0 = lf0_raw.copy()
    f0[vuv < VUV_THRESHOLD] = 0
    f0[np.nonzero(f0)] = np.exp(f0[np.nonzero(f0)])

    lf0 = f0.copy()
    lf0[np.nonzero(lf0)] = np.log(f0[np.nonzero(lf0)])
    lf0 = interp1d(lf0, kind="slinear")
    lf0_prefilter = lf0.copy()

    # Trajectory smoothing
    lf0_smoothed = lowpass_filter(lf0_prefilter, MODFS, cutoff=LF0_CUTOFF)

    mgc_smoothed = mgc_gv.copy()
    for d in range(MGC_DIM):
        mgc_smoothed[:, d] = lowpass_filter(mgc_gv[:, d], MODFS, cutoff=MGC_CUTOFF)

    bap_smoothed = bap.copy()
    for d in range(BAP_DIM):
        bap_smoothed[:, d] = lowpass_filter(bap[:, d], MODFS, cutoff=BAP_CUTOFF)

    bap_clipped = np.clip(bap_smoothed, -60, 0)

    f0_final = np.exp(lf0_smoothed)
    f0_final[vuv < VUV_THRESHOLD] = 0

    save_bin(f"{DBG_DIR}\\case{tag}_gold_mgc_gv.bin", mgc_gv)
    save_bin(f"{DBG_DIR}\\case{tag}_gold_lf0_prefilter.bin", lf0_prefilter)
    save_bin(f"{DBG_DIR}\\case{tag}_gold_lf0_smoothed.bin", lf0_smoothed)
    save_bin(f"{DBG_DIR}\\case{tag}_gold_mgc_final.bin", mgc_smoothed)
    save_bin(f"{DBG_DIR}\\case{tag}_gold_bap_clipped.bin", bap_clipped)
    save_bin(f"{DBG_DIR}\\case{tag}_gold_f0_final.bin", f0_final)

    print(f"case {tag}: T={t} note_frame_indices={len(note_frame_indices)}/{t} "
          f"({100.0 * len(note_frame_indices) / t:.1f}%)")


if __name__ == "__main__":
    for tag in ["0", "1"]:
        process(tag)
    print("done")
