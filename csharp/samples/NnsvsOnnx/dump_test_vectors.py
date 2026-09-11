# -*- coding: utf-8 -*-
r"""NNSVS timelag/duration/acoustic モデルのテストベクタダンプ (C# 検証用)。

出力: g:\libllsm2\csharp\samples\NnsvsOnnx\testdata\
  manifest.json                     -- 全ケースのメタデータ(形状/ファイル名)
  timelag_T{T}_{x,mu,sigma}.bin      -- float32 リトルエンディアン生バイナリ
  duration_T{T}_{x,mu,sigma}.bin
  acoustic_T{T}_{...}.bin            -- x, lf0_t1, mgc_t1, bap_t1, vuv_t1, out,
                                         mgc/bap の init_noise + step_noises
  scaler_*.bin                       -- MinMax/Standard スケーラ単体テスト用

参照実装 (変更しない):
  g:\libllsm2\csharp\samples\WorldToLlsm\export_onnx_acoustic.py
  g:\libllsm2\csharp\samples\WorldToLlsm\export_onnx_simple.py

実行:
  G:\SimpleEnunu-0.3.1_TunedWavOut\python-3.9.13-embed-amd64\python.exe dump_test_vectors.py
"""
import json
import os
import sys

sys.path.insert(0, r"G:\SimpleEnunu-0.3.1_TunedWavOut\nnsvs-master")

import numpy as np
import torch
import torch.nn.functional as F

MODEL_DIR = r"G:\ENUNU_波音リツ_4CC_#3019_WD_SiFiGAN"
OUT_DIR = r"g:\libllsm2\csharp\samples\NnsvsOnnx\testdata"

# ---------------------------------------------------------------------------
# Same monkeypatches as export_onnx_acoustic.py (export-script-local only;
# nnsvs source files are NOT modified). See that file for the rationale.
# ---------------------------------------------------------------------------
_orig_dropout = F.dropout


def _patched_dropout(input, p=0.5, training=False, inplace=False):
    return _orig_dropout(input, p=p, training=False, inplace=inplace)


F.dropout = _patched_dropout

import nnsvs.model as nnsvs_model_mod  # noqa: E402
import nnsvs.acoustic_models.tacotron_f0 as taco_mod  # noqa: E402


def _identity_pack(x, lengths, batch_first=True, enforce_sorted=True):
    return x


def _identity_pad(x, batch_first=True, total_length=None):
    return x, None


nnsvs_model_mod.pack_padded_sequence = _identity_pack
nnsvs_model_mod.pad_packed_sequence = _identity_pad
taco_mod.pack_padded_sequence = _identity_pack
taco_mod.pad_packed_sequence = _identity_pad

from omegaconf import OmegaConf  # noqa: E402
from hydra.utils import instantiate  # noqa: E402


def load_acoustic():
    cfg = OmegaConf.load(os.path.join(MODEL_DIR, "acoustic_model.yaml"))
    model = instantiate(cfg.netG)
    ckpt = torch.load(os.path.join(MODEL_DIR, "acoustic_model.pth"), map_location="cpu")
    model.load_state_dict(ckpt["state_dict"])
    model.eval()
    model._set_lf0_params()
    return model


def load_mdn(name):
    cfg = OmegaConf.load(os.path.join(MODEL_DIR, name + "_model.yaml"))
    model = instantiate(cfg.netG)
    ckpt = torch.load(os.path.join(MODEL_DIR, name + "_model.pth"), map_location="cpu")
    model.load_state_dict(ckpt["state_dict"])
    model.eval()
    return model, cfg.netG.in_dim


def save_bin(name, arr):
    path = os.path.join(OUT_DIR, name)
    np.ascontiguousarray(arr, dtype="<f4").tofile(path)
    return name


# ---------------------------------------------------------------------------
# Input generation helpers
# ---------------------------------------------------------------------------
def make_acoustic_input_np(T, seed):
    """(T, 113) normalized input: ph onehot [3:50), in_lf0_idx=78 in [0,1],
    in_rest_idx=0 in {0,1} per frame."""
    rng = np.random.RandomState(seed)
    x = rng.rand(T, 113).astype(np.float32)
    x[:, 0] = (rng.rand(T) < 0.5).astype(np.float32)
    ph = np.zeros((T, 47), dtype=np.float32)
    idx = rng.randint(0, 47, size=T)
    ph[np.arange(T), idx] = 1.0
    x[:, 3:50] = ph
    x[:, 78] = rng.rand(T).astype(np.float32)
    return x


def make_plain_input_np(T, dim, seed):
    rng = np.random.RandomState(seed)
    return rng.rand(T, dim).astype(np.float32)


def replicate_pad_np(x, pad):
    if pad <= 0:
        return x
    last = x[-1:]
    reps = np.repeat(last, pad, axis=0)
    return np.concatenate([x, reps], axis=0)


# ---------------------------------------------------------------------------
# MDN (timelag/duration) dump
# ---------------------------------------------------------------------------
class MdnWrapper(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, x):
        log_pi, log_sigma, mu = self.model(x, None)
        idx = log_pi.argmax(dim=2)
        idx_e = idx.unsqueeze(-1).unsqueeze(-1).expand(
            idx.shape[0], idx.shape[1], 1, mu.shape[-1]
        )
        mu_sel = mu.gather(2, idx_e).squeeze(2)
        sigma_sel = log_sigma.gather(2, idx_e).squeeze(2).exp()
        return mu_sel, sigma_sel


def dump_mdn(name, manifest, seed0):
    model, in_dim = load_mdn(name)
    wrapper = MdnWrapper(model).eval()
    cases = []
    for T in (57, 124):
        x = make_plain_input_np(T, in_dim, seed0 + T)
        with torch.no_grad():
            mu, sigma = wrapper(torch.from_numpy(x).unsqueeze(0))
        mu = mu.numpy()[0]  # (T,1)
        sigma = sigma.numpy()[0]
        cases.append(
            {
                "T": T,
                "x": save_bin(f"{name}_T{T}_x.bin", x),
                "mu": save_bin(f"{name}_T{T}_mu.bin", mu),
                "sigma": save_bin(f"{name}_T{T}_sigma.bin", sigma),
            }
        )
    manifest[name] = {"in_dim": in_dim, "out_dim": 1, "cases": cases}


# ---------------------------------------------------------------------------
# Acoustic full-cascade gold computation (mirrors
# NPSSMDNMultistreamParametricModel.forward's is_inference branch +
# the outer/inner pad_inference cascade, verified in export_onnx_acoustic.py)
# ---------------------------------------------------------------------------
def diffusion_loop_torch(diff_model, cond_t, init_noise_np, step_noises_np):
    with torch.no_grad():
        x = torch.from_numpy(init_noise_np)
        for i in reversed(range(diff_model.K_step)):
            t = torch.full((1,), i, dtype=torch.long)
            noise_i = torch.from_numpy(step_noises_np[i])

            def nf(*shape_, device=None, dtype=None, _noise=noise_i):
                return _noise

            x = diff_model.p_sample(x, t, cond_t, noise_fn=nf)
        out = diff_model._denorm(x[:, 0].transpose(1, 2), diff_model.norm_scale)
    return out.numpy()[0]  # (T,out_dim)


def make_noise_queue(shape, k_step, seed):
    rng = np.random.RandomState(seed)
    init_noise = rng.randn(*shape).astype(np.float32)
    step_noises = np.stack(
        [rng.randn(*shape).astype(np.float32) for _ in range(k_step)]
    )
    return init_noise, step_noises


def compute_acoustic_cascade(model, x_np, seed):
    T = x_np.shape[0]
    r = model.reduction_factor
    pad_outer = r - (T % r)
    T1 = T + pad_outer
    x1 = replicate_pad_np(x_np, pad_outer)  # (T1,113)
    x1_t = torch.from_numpy(x1).unsqueeze(0)

    with torch.no_grad():
        lf0_t1 = model.lf0_model.inference(x1_t, [T1]).numpy()[0]  # (T1,1)

    mgc_inp = np.concatenate([x1, lf0_t1], axis=-1).astype(np.float32)  # (T1,114)
    bap_inp = mgc_inp  # same construction (x1, lf0_t1)

    with torch.no_grad():
        mgc_cond = model.mgc_model.encoder(torch.from_numpy(mgc_inp).unsqueeze(0), [T1])
        mgc_cond_t = mgc_cond.transpose(1, 2)  # (1,256,T1)
        bap_cond = model.bap_model.encoder(torch.from_numpy(bap_inp).unsqueeze(0), [T1])
        bap_cond_t = bap_cond.transpose(1, 2)  # (1,128,T1)

    mgc_shape = (1, 1, model.mgc_model.out_dim, T1)
    bap_shape = (1, 1, model.bap_model.out_dim, T1)
    mgc_init, mgc_steps = make_noise_queue(mgc_shape, model.mgc_model.K_step, seed + 1)
    bap_init, bap_steps = make_noise_queue(bap_shape, model.bap_model.K_step, seed + 2)

    mgc_t1 = diffusion_loop_torch(model.mgc_model, mgc_cond_t, mgc_init, mgc_steps)  # (T1,60)
    bap_t1 = diffusion_loop_torch(model.bap_model, bap_cond_t, bap_init, bap_steps)  # (T1,5)

    vuv_inp = np.concatenate([x1, mgc_t1, lf0_t1], axis=-1).astype(np.float32)  # (T1,174)
    with torch.no_grad():
        vuv_t1 = model.vuv_model(torch.from_numpy(vuv_inp).unsqueeze(0), [T1]).numpy()[0]  # (T1,1)

    out_t1 = np.concatenate([mgc_t1, lf0_t1, vuv_t1, bap_t1], axis=-1)  # (T1,67)
    out_final = out_t1[: T1 - pad_outer]  # -> (T,67)

    return {
        "pad_outer": int(pad_outer),
        "T1": int(T1),
        "x1": x1,
        "lf0_t1": lf0_t1,
        "mgc_t1": mgc_t1,
        "bap_t1": bap_t1,
        "vuv_t1": vuv_t1,
        "out_final": out_final,
        "mgc_init_noise": mgc_init,
        "mgc_step_noises": mgc_steps,
        "bap_init_noise": bap_init,
        "bap_step_noises": bap_steps,
    }


def dump_acoustic(manifest):
    model = load_acoustic()
    cases = []
    for T in (57, 124):
        x = make_acoustic_input_np(T, 1000 + T)
        r = compute_acoustic_cascade(model, x, seed=2000 + T)
        prefix = f"acoustic_T{T}"
        cases.append(
            {
                "T": T,
                "pad_outer": r["pad_outer"],
                "T1": r["T1"],
                "x": save_bin(f"{prefix}_x.bin", x),
                "lf0_t1": save_bin(f"{prefix}_lf0_t1.bin", r["lf0_t1"]),
                "mgc_t1": save_bin(f"{prefix}_mgc_t1.bin", r["mgc_t1"]),
                "bap_t1": save_bin(f"{prefix}_bap_t1.bin", r["bap_t1"]),
                "vuv_t1": save_bin(f"{prefix}_vuv_t1.bin", r["vuv_t1"]),
                "out_final": save_bin(f"{prefix}_out.bin", r["out_final"]),
                "mgc_init_noise": save_bin(f"{prefix}_mgc_init_noise.bin", r["mgc_init_noise"]),
                "mgc_step_noises": save_bin(f"{prefix}_mgc_step_noises.bin", r["mgc_step_noises"]),
                "bap_init_noise": save_bin(f"{prefix}_bap_init_noise.bin", r["bap_init_noise"]),
                "bap_step_noises": save_bin(f"{prefix}_bap_step_noises.bin", r["bap_step_noises"]),
            }
        )
    manifest["acoustic"] = {
        "in_dim": 113,
        "out_dim": 67,
        "reduction_factor": model.reduction_factor,
        "mgc_out_dim": model.mgc_model.out_dim,
        "mgc_cond_dim": model.mgc_model.encoder.out_dim,
        "mgc_k_step": model.mgc_model.K_step,
        "bap_out_dim": model.bap_model.out_dim,
        "bap_cond_dim": model.bap_model.encoder.out_dim,
        "bap_k_step": model.bap_model.K_step,
        "vuv_in_dim": model.vuv_model.in_dim,
        "cases": cases,
    }


# ---------------------------------------------------------------------------
# Scaler unit-test vectors (arithmetic only; verifies Scalers.cs formulas)
# ---------------------------------------------------------------------------
def dump_scalers(manifest):
    entries = []
    for name, dim in (("acoustic", 113), ("timelag", 109), ("duration", 109)):
        mn = np.load(os.path.join(MODEL_DIR, f"in_{name}_scaler_min.npy")).astype(np.float64)
        sc = np.load(os.path.join(MODEL_DIR, f"in_{name}_scaler_scale.npy")).astype(np.float64)
        rng = np.random.RandomState(abs(hash(name)) % (2 ** 31))
        raw = rng.uniform(-5, 5, size=(5, dim))
        norm = raw * sc + mn
        entries.append(
            {
                "name": f"in_{name}",
                "dim": dim,
                "kind": "minmax",
                "min": save_bin(f"scaler_in_{name}_min.bin", mn),
                "scale": save_bin(f"scaler_in_{name}_scale.bin", sc),
                "raw": save_bin(f"scaler_in_{name}_raw.bin", raw),
                "expected": save_bin(f"scaler_in_{name}_expected.bin", norm),
                "rows": 5,
            }
        )

    mean = np.load(os.path.join(MODEL_DIR, "out_acoustic_scaler_mean.npy")).astype(np.float64)
    scale = np.load(os.path.join(MODEL_DIR, "out_acoustic_scaler_scale.npy")).astype(np.float64)
    rng = np.random.RandomState(999)
    normed = rng.uniform(-2, 2, size=(5, 67))
    raw = normed * scale + mean
    entries.append(
        {
            "name": "out_acoustic",
            "dim": 67,
            "kind": "standard_inverse",
            "mean": save_bin("scaler_out_acoustic_mean.bin", mean),
            "scale": save_bin("scaler_out_acoustic_scale.bin", scale),
            "normed": save_bin("scaler_out_acoustic_normed.bin", normed),
            "expected": save_bin("scaler_out_acoustic_expected.bin", raw),
            "rows": 5,
        }
    )
    manifest["scalers"] = entries


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    torch.manual_seed(0)
    manifest = {}

    print("=== timelag/duration ===")
    dump_mdn("timelag", manifest, seed0=10)
    dump_mdn("duration", manifest, seed0=20)

    print("=== acoustic cascade ===")
    dump_acoustic(manifest)

    print("=== scalers ===")
    dump_scalers(manifest)

    with open(os.path.join(OUT_DIR, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
    print("wrote manifest.json")


if __name__ == "__main__":
    main()
