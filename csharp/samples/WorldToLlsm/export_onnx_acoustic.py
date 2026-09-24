# -*- coding: utf-8 -*-
"""NNSVS singing acoustic model (NPSSMDNMultistreamParametricModel) -> ONNX export
+ numerical verification against the original PyTorch inference.

実行:
  G:\SimpleEnunu-0.3.1_TunedWavOut\python-3.9.13-embed-amd64\python.exe ^
      export_onnx_acoustic.py

出力 (g:\libllsm2\csharp\samples\WorldToLlsm\onnx\):
  acoustic_lf0_encoder.onnx       x[1,T,113] -> enc[1,T/4,129]
  acoustic_lf0_decoder_step.onnx  enc_t[1,129], lf0_score_seg[1,4], prev_out[1,1],
                                  h0[1,256], c0[1,256]
                                  -> out_frames[1,4], h1[1,256], c1[1,256]
  acoustic_mgc_encoder.onnx       x[1,T,114] -> cond[1,T,256]
  acoustic_mgc_denoise.onnx       x_t[1,1,60,T], t[1](int64), cond[1,256,T]
                                  -> noise_pred[1,1,60,T]
  acoustic_bap_encoder.onnx       x[1,T,114] -> cond[1,T,128]
  acoustic_bap_denoise.onnx       x_t[1,1,5,T], t[1](int64), cond[1,128,T]
                                  -> noise_pred[1,1,5,T]
  acoustic_vuv.onnx               x[1,T,174] -> vuv[1,T,1]  (raw logit, no sigmoid)
  acoustic_diffusion_params.json  betas/alphas_cumprod/posterior coefs etc.

lf0_model の自己回帰デコーダ (LSTMCell ベース) は単一グラフでは系列長に汎化しない
ことを実測で確認済みのため、エンコーダ (非自己回帰, 動的T) + デコーダ1ステップ
(固定shape, 状態入出力) に分離してエクスポートする。

nnsvs-master のソースコードは変更しない。以下の2点のみ、このスクリプト内で
モンキーパッチする (詳細はコード内コメント参照):
  1. torch.nn.functional.dropout を強制的に training=False にする
     (ResF0NonAttentiveDecoder が prenet_layers==0 のとき prev_out に
     F.dropout(..., training=True) を無条件適用するバグがあり、これにより
     model.eval() でも lf0_model.inference() が非決定的になる。実測で2回の
     呼び出し結果が最大1.9ずれることを確認済み)。
  2. pack_padded_sequence / pad_packed_sequence を恒等関数に置き換える
     (B=1, lengths=[T] (実長そのもの) の場合、pack/pad は数学的に恒等写像
     であることを実測で確認済み: 実装との差は最大1.7e-6の浮動小数点誤差のみ)。
"""
import json
import math
import os
import sys

sys.path.insert(0, r"G:\SimpleEnunu-0.3.1_TunedWavOut\nnsvs-master")

import numpy as np
import torch
import torch.nn.functional as F

MODEL_DIR = r"G:\ENUNU_波音リツ_4CC_#3019_WD_SiFiGAN"
OUT_DIR = r"g:\libllsm2\csharp\samples\WorldToLlsm\onnx"
OPSET = 17

# ---------------------------------------------------------------------------
# Patches (export-script-local only; nnsvs source files are NOT modified)
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

MAX_LF0_RATIO = 600 * math.log(2) / 1200  # residual_f0_max_cent=600 (hardcoded in nnsvs)


# ---------------------------------------------------------------------------
# Model loading / test-input helpers
# ---------------------------------------------------------------------------
def load_model():
    cfg = OmegaConf.load(os.path.join(MODEL_DIR, "acoustic_model.yaml"))
    model = instantiate(cfg.netG)
    ckpt = torch.load(os.path.join(MODEL_DIR, "acoustic_model.pth"), map_location="cpu")
    model.load_state_dict(ckpt["state_dict"])
    model.eval()
    model._set_lf0_params()
    return model


def make_test_input(T, seed):
    """Plausible normalized input feature matrix (1, T, 113)."""
    g = torch.Generator().manual_seed(seed)
    x = torch.rand(1, T, 113, generator=g)
    x[:, :, 0] = 0.0  # in_rest_idx
    ph = torch.zeros(1, T, 47)
    idx = torch.randint(0, 47, (T,), generator=g)
    for t in range(T):
        ph[0, t, idx[t]] = 1.0
    x[:, :, 3:50] = ph
    x[:, :, 78] = torch.rand(T, generator=g)  # in_lf0_idx, normalized [0,1]
    return x


def replicate_pad(x, pad):
    if pad <= 0:
        return x
    return F.pad(x, (0, 0, 0, pad), mode="replicate")


def make_generic_test_input(module, T, in_dim, seed):
    """Random (1,T,in_dim) input honoring the module's phoneme one-hot slot
    (in_ph_start_idx:in_ph_start_idx+num_vocab) when module.embed_dim is not
    None (required by FFConvLSTM.forward's `assert (x_ph_onehot.sum(-1) <=
    1).all()`)."""
    g = torch.Generator().manual_seed(seed)
    x = torch.rand(1, T, in_dim, generator=g)
    if getattr(module, "embed_dim", None) is not None:
        start = module.in_ph_start_idx
        nv = module.num_vocab
        ph = torch.zeros(1, T, nv)
        idx = torch.randint(0, nv, (T,), generator=g)
        for t in range(T):
            ph[0, t, idx[t]] = 1.0
        x[:, :, start : start + nv] = ph
    return x


# ---------------------------------------------------------------------------
# LF0 wrappers (encoder: dynamic-T, non-autoregressive / decoder: single step)
# ---------------------------------------------------------------------------
class LF0EncoderWrapper(torch.nn.Module):
    """emb+fc_in -> ff -> cat(lf0_score) -> conv -> BiLSTM -> cat(lf0_score)
    -> conv_downsample (reduction_factor). Mirrors BiLSTMResF0NonAttentiveDecoder
    .forward() up to (and including) the reduction step done inside
    ResF0NonAttentiveDecoder.forward(); references the original submodules
    directly (no weight copies)."""

    def __init__(self, lf0m):
        super().__init__()
        self.emb = lf0m.emb
        self.fc_in = lf0m.fc_in
        self.ff = lf0m.ff
        self.conv = lf0m.conv
        self.lstm = lf0m.lstm
        self.conv_downsample = lf0m.decoder.conv_downsample
        self.in_ph_start_idx = lf0m.in_ph_start_idx
        self.num_vocab = lf0m.num_vocab
        self.in_dim = lf0m.in_dim
        self.in_lf0_idx = lf0m.in_lf0_idx

    def forward(self, x):
        lf0_score = x[:, :, self.in_lf0_idx].unsqueeze(-1)
        x_first, x_ph_onehot, x_last = torch.split(
            x,
            [
                self.in_ph_start_idx,
                self.num_vocab,
                self.in_dim - self.num_vocab - self.in_ph_start_idx,
            ],
            dim=-1,
        )
        x_ph = torch.argmax(x_ph_onehot, dim=-1)
        xemb = self.emb(x_ph) + self.fc_in(torch.cat([x_first, x_last], dim=-1))

        out = self.ff(xemb)
        out = torch.cat([out, lf0_score], dim=-1)
        out = self.conv(out.transpose(1, 2)).transpose(1, 2)
        out, _ = self.lstm(out)
        out = torch.cat([out, lf0_score], dim=-1)

        # reduction (ResF0NonAttentiveDecoder.forward, downsample_by_conv branch)
        enc = self.conv_downsample(out.transpose(1, 2)).transpose(1, 2)
        return enc


class LF0DecoderStepWrapper(torch.nn.Module):
    """One autoregressive step of ResF0NonAttentiveDecoder (decoder_layers=1),
    producing `reduction_factor` (=4) output frames per call. References the
    original lstm cell / feat_out submodules directly."""

    def __init__(self, lf0m):
        super().__init__()
        dec = lf0m.decoder
        assert len(dec.lstm) == 1, "decoder-step wrapper assumes decoder_layers=1"
        self.lstm_cell = dec.lstm[0]  # ZoneOutCell
        self.feat_out = dec.feat_out
        self.out_lf0_idx = dec.out_lf0_idx
        self.scaled_tanh = dec.scaled_tanh
        self.out_dim = dec.out_dim
        self.reduction_factor = dec.reduction_factor
        self.in_lf0_min = float(lf0m.in_lf0_min)
        self.in_lf0_max = float(lf0m.in_lf0_max)
        self.out_lf0_mean = float(lf0m.out_lf0_mean)
        self.out_lf0_scale = float(lf0m.out_lf0_scale)

    def forward(self, enc_t, lf0_score_seg, prev_out, h0, c0):
        # enc_t: (1,129) lf0_score_seg: (1,r) raw normalized  prev_out: (1,out_dim)
        # h0/c0: (1,decoder_hidden_dim)
        lf0_score_denorm_seg = (
            lf0_score_seg * (self.in_lf0_max - self.in_lf0_min) + self.in_lf0_min
        )  # (1,r)

        prenet_out = prev_out  # prenet is None branch; F.dropout patched to identity
        xs = torch.cat([enc_t, prenet_out], dim=1)
        h1, c1 = self.lstm_cell(xs, (h0, c0))

        hcs = torch.cat([h1, enc_t], dim=1)
        out = self.feat_out(hcs).view(enc_t.size(0), self.out_dim, self.reduction_factor)

        if self.scaled_tanh:
            lf0_residual = MAX_LF0_RATIO * torch.tanh(out[:, self.out_lf0_idx, :]).unsqueeze(1)
        else:
            lf0_residual = out[:, self.out_lf0_idx, :].unsqueeze(1)

        lf0_pred_denorm = lf0_score_denorm_seg.unsqueeze(1) + lf0_residual  # (1,1,r)
        lf0_pred = (lf0_pred_denorm - self.out_lf0_mean) / self.out_lf0_scale
        out_frames = lf0_pred.squeeze(1)  # (1,r) == (1, out_dim*r) since out_dim=1
        return out_frames, h1, c1


def export_lf0(model, verify_results):
    lf0m = model.lf0_model
    r = lf0m.decoder.reduction_factor
    hidden_dim = lf0m.decoder.lstm[0].hidden_size

    enc_wrapper = LF0EncoderWrapper(lf0m).eval()
    dec_wrapper = LF0DecoderStepWrapper(lf0m).eval()

    T0 = 24
    dummy_x = make_test_input(T0, 1)
    enc_path = os.path.join(OUT_DIR, "acoustic_lf0_encoder.onnx")
    torch.onnx.export(
        enc_wrapper,
        dummy_x,
        enc_path,
        input_names=["x"],
        output_names=["enc"],
        dynamic_axes={"x": {1: "T"}, "enc": {1: "Tr"}},
        opset_version=OPSET,
    )
    print("exported", enc_path, os.path.getsize(enc_path))

    dummy_enc_t = torch.zeros(1, 129)
    dummy_seg = torch.zeros(1, r)
    dummy_prev = torch.zeros(1, 1)
    dummy_h0 = torch.zeros(1, hidden_dim)
    dummy_c0 = torch.zeros(1, hidden_dim)
    dec_path = os.path.join(OUT_DIR, "acoustic_lf0_decoder_step.onnx")
    torch.onnx.export(
        dec_wrapper,
        (dummy_enc_t, dummy_seg, dummy_prev, dummy_h0, dummy_c0),
        dec_path,
        input_names=["enc_t", "lf0_score_seg", "prev_out", "h0", "c0"],
        output_names=["out_frames", "h1", "c1"],
        opset_version=OPSET,
    )
    print("exported", dec_path, os.path.getsize(dec_path))

    verify_lf0(model, enc_path, dec_path, verify_results)


def onnx_lf0_pipeline(enc_sess, dec_sess, x_np, r, hidden_dim):
    """Full lf0 forward using the two ONNX graphs + a numpy AR loop."""
    T = x_np.shape[1]
    assert T % r == 0
    enc = enc_sess.run(None, {"x": x_np})[0]  # (1, T/r, 129)
    lf0_score = x_np[:, :, 78:79]  # (1,T,1) raw normalized score

    h = np.zeros((1, hidden_dim), dtype=np.float32)
    c = np.zeros((1, hidden_dim), dtype=np.float32)
    prev_out = np.zeros((1, 1), dtype=np.float32)
    out_frames_all = []
    Tr = T // r
    for t in range(Tr):
        enc_t = enc[:, t, :]
        seg = lf0_score[:, t * r : (t + 1) * r, 0]
        out_frames, h, c = dec_sess.run(
            None,
            {
                "enc_t": enc_t.astype(np.float32),
                "lf0_score_seg": seg.astype(np.float32),
                "prev_out": prev_out.astype(np.float32),
                "h0": h.astype(np.float32),
                "c0": c.astype(np.float32),
            },
        )
        out_frames_all.append(out_frames)  # (1,r)
        prev_out = out_frames[:, -1:]
    lf0_out = np.concatenate(out_frames_all, axis=1).reshape(1, T, 1)
    return lf0_out


def verify_lf0(model, enc_path, dec_path, verify_results):
    import onnxruntime as ort

    lf0m = model.lf0_model
    r = lf0m.decoder.reduction_factor
    hidden_dim = lf0m.decoder.lstm[0].hidden_size

    enc_sess = ort.InferenceSession(enc_path, providers=["CPUExecutionProvider"])
    dec_sess = ort.InferenceSession(dec_path, providers=["CPUExecutionProvider"])

    for T in (24, 57):  # 57 is not a multiple of 4
        x = make_test_input(T, 100 + T)

        # replicate the full nested pad_inference cascade for the lf0 stream
        mod_outer = T % 4
        pad_outer = 4 - mod_outer
        x_outer = replicate_pad(x, pad_outer)
        T1 = T + pad_outer
        pad_inner = 4  # always, since T1 % 4 == 0 after outer padding
        x_lf0 = replicate_pad(x_outer, pad_inner)
        T2 = T1 + pad_inner

        with torch.no_grad():
            gold_full = model.lf0_model(x_lf0, [T2])[0]  # (1,T2,1)
        gold = gold_full[:, :-pad_inner].numpy()  # -> T1
        gold = gold[:, : T1 - pad_outer]  # -> T

        onnx_out = onnx_lf0_pipeline(enc_sess, dec_sess, x_lf0.numpy(), r, hidden_dim)
        onnx_out = onnx_out[:, :-pad_inner]  # -> T1
        onnx_out = onnx_out[:, : T1 - pad_outer]  # -> T

        d = float(np.abs(gold - onnx_out).max())
        ok = d < 1e-4
        verify_results.append(("lf0 (encoder+decoder-step, full pad cascade)", T, d, ok))
        print(
            "  [lf0] T=%d pad_outer=%d max|diff|=%.3e %s"
            % (T, pad_outer, d, "OK" if ok else "NG!")
        )


# ---------------------------------------------------------------------------
# MGC / BAP: FFConvLSTM encoder + DiffNet denoise_fn export
# ---------------------------------------------------------------------------
class EncoderOnlyWrapper(torch.nn.Module):
    def __init__(self, m):
        super().__init__()
        self.m = m

    def forward(self, x):
        T = x.shape[1]
        return self.m(x, [T])


def export_ffconvlstm(module, name, in_dim, verify_results, out_label):
    wrapper = EncoderOnlyWrapper(module).eval()
    T0 = 24
    dummy = make_generic_test_input(module, T0, in_dim, 7)
    path = os.path.join(OUT_DIR, name)
    torch.onnx.export(
        wrapper,
        dummy,
        path,
        input_names=["x"],
        output_names=["out"],
        dynamic_axes={"x": {1: "T"}, "out": {1: "T"}},
        opset_version=OPSET,
    )
    print("exported", path, os.path.getsize(path))

    import onnxruntime as ort

    sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    for T2 in (17, 63):
        x2 = make_generic_test_input(module, T2, in_dim, 100 + T2)
        with torch.no_grad():
            ref = wrapper(x2)
        out = sess.run(None, {"x": x2.numpy()})[0]
        d = float(np.abs(ref.numpy() - out).max())
        ok = d < 1e-4
        verify_results.append((out_label, T2, d, ok))
        print("  [%s] T=%d max|diff|=%.3e %s" % (out_label, T2, d, "OK" if ok else "NG!"))
    return path


def export_diffnet(denoise_fn, name, out_dim, cond_dim, verify_results, out_label):
    denoise_fn = denoise_fn.eval()
    T0 = 24
    dummy_spec = torch.randn(1, 1, out_dim, T0)
    dummy_t = torch.tensor([37], dtype=torch.long)
    dummy_cond = torch.randn(1, cond_dim, T0)
    path = os.path.join(OUT_DIR, name)
    torch.onnx.export(
        denoise_fn,
        (dummy_spec, dummy_t, dummy_cond),
        path,
        input_names=["x_t", "t", "cond"],
        output_names=["noise_pred"],
        dynamic_axes={
            "x_t": {3: "T"},
            "cond": {2: "T"},
            "noise_pred": {3: "T"},
        },
        opset_version=OPSET,
    )
    print("exported", path, os.path.getsize(path))

    import onnxruntime as ort

    sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    for T2 in (17, 63):
        spec2 = torch.randn(1, 1, out_dim, T2)
        t2 = torch.tensor([13], dtype=torch.long)
        cond2 = torch.randn(1, cond_dim, T2)
        with torch.no_grad():
            ref = denoise_fn(spec2, t2, cond2)
        out = sess.run(
            None, {"x_t": spec2.numpy(), "t": t2.numpy(), "cond": cond2.numpy()}
        )[0]
        d = float(np.abs(ref.numpy() - out).max())
        ok = d < 1e-4
        verify_results.append((out_label, T2, d, ok))
        print("  [%s] T=%d max|diff|=%.3e %s" % (out_label, T2, d, "OK" if ok else "NG!"))
    return path


# ---------------------------------------------------------------------------
# Diffusion loop (p_sample) verification with injected noise, using the
# original module's public p_sample() method (no monkeypatch of torch.randn
# needed: p_sample accepts a noise_fn override).
# ---------------------------------------------------------------------------
def make_noise_queue(shape, k_step, seed):
    rng = np.random.RandomState(seed)
    init_noise = rng.randn(*shape).astype(np.float32)
    step_noises = [rng.randn(*shape).astype(np.float32) for _ in range(k_step)]
    return init_noise, step_noises


def torch_diffusion_loop(diff_model, cond_raw, T, init_noise_np, step_noises_np):
    with torch.no_grad():
        cond = diff_model.encoder(cond_raw, [T])
        cond = cond.transpose(1, 2)  # (1,M,T)

        x = torch.from_numpy(init_noise_np)
        for i in reversed(range(diff_model.K_step)):
            t = torch.full((1,), i, dtype=torch.long)
            noise_i = torch.from_numpy(step_noises_np[i])

            def nf(*shape_, device=None, dtype=None, _noise=noise_i):
                return _noise

            x = diff_model.p_sample(x, t, cond, noise_fn=nf)
        out = diff_model._denorm(x[:, 0].transpose(1, 2), diff_model.norm_scale)
    return out.numpy(), cond.numpy()


def onnx_diffusion_loop(enc_sess, denoise_sess, cond_raw_np, T, init_noise_np,
                         step_noises_np, diff_params):
    cond = enc_sess.run(None, {"x": cond_raw_np})[0]  # (1,T,M)
    cond_t = np.transpose(cond, (0, 2, 1)).astype(np.float32)  # (1,M,T)

    betas = np.array(diff_params["betas"], dtype=np.float64)
    alphas = 1.0 - betas
    alphas_cumprod = np.cumprod(alphas)
    alphas_cumprod_prev = np.append(1.0, alphas_cumprod[:-1])
    sqrt_recip_alphas_cumprod = np.sqrt(1.0 / alphas_cumprod)
    sqrt_recipm1_alphas_cumprod = np.sqrt(1.0 / alphas_cumprod - 1)
    posterior_variance = betas * (1.0 - alphas_cumprod_prev) / (1.0 - alphas_cumprod)
    posterior_log_variance_clipped = np.log(np.maximum(posterior_variance, 1e-20))
    posterior_mean_coef1 = betas * np.sqrt(alphas_cumprod_prev) / (1.0 - alphas_cumprod)
    posterior_mean_coef2 = (1.0 - alphas_cumprod_prev) * np.sqrt(alphas) / (1.0 - alphas_cumprod)

    K_step = diff_params["K_step"]
    x = init_noise_np.copy()
    for i in reversed(range(K_step)):
        t_np = np.array([i], dtype=np.int64)
        noise_pred = denoise_sess.run(
            None, {"x_t": x.astype(np.float32), "t": t_np, "cond": cond_t}
        )[0]
        x_recon = (
            sqrt_recip_alphas_cumprod[i] * x - sqrt_recipm1_alphas_cumprod[i] * noise_pred
        )
        x_recon = np.clip(x_recon, -1.0, 1.0)
        model_mean = posterior_mean_coef1[i] * x_recon + posterior_mean_coef2[i] * x
        model_log_var = posterior_log_variance_clipped[i]
        noise = step_noises_np[i]
        nonzero_mask = 0.0 if i == 0 else 1.0
        x = model_mean + nonzero_mask * np.exp(0.5 * model_log_var) * noise
        x = x.astype(np.float32)

    out = x[:, 0].transpose(0, 2, 1) * diff_params["norm_scale"]
    return out, cond_t


def verify_diffusion_loop(diff_model, enc_path, denoise_path, in_dim, out_dim,
                           diff_params, verify_results, out_label, T=20, seed=42):
    import onnxruntime as ort

    enc_sess = ort.InferenceSession(enc_path, providers=["CPUExecutionProvider"])
    denoise_sess = ort.InferenceSession(denoise_path, providers=["CPUExecutionProvider"])

    cond_raw = make_generic_test_input(diff_model.encoder, T, in_dim, seed)
    shape = (1, 1, out_dim, T)
    init_noise_np, step_noises_np = make_noise_queue(shape, diff_model.K_step, seed)

    torch_out, torch_cond = torch_diffusion_loop(
        diff_model, cond_raw, T, init_noise_np, step_noises_np
    )
    onnx_out, onnx_cond = onnx_diffusion_loop(
        enc_sess, denoise_sess, cond_raw.numpy(), T, init_noise_np, step_noises_np, diff_params
    )

    d_cond = float(np.abs(torch_cond - onnx_cond).max())
    d_out = float(np.abs(torch_out - onnx_out).max())
    ok = d_out < 1e-3
    verify_results.append((out_label + " [full 100-step p_sample loop]", T, d_out, ok))
    print(
        "  [%s] T=%d cond_max|diff|=%.3e loop_max|diff|=%.3e %s"
        % (out_label, T, d_cond, d_out, "OK" if ok else "NG!")
    )


def dump_diffusion_params(model):
    def params_for(diff_model):
        return {
            "K_step": int(diff_model.K_step),
            "norm_scale": float(diff_model.norm_scale),
            "betas": diff_model.betas.numpy().astype(np.float64).tolist(),
            "alphas_cumprod": diff_model.alphas_cumprod.numpy().astype(np.float64).tolist(),
            "alphas_cumprod_prev": diff_model.alphas_cumprod_prev.numpy().astype(np.float64).tolist(),
            "sqrt_recip_alphas_cumprod": diff_model.sqrt_recip_alphas_cumprod.numpy().astype(np.float64).tolist(),
            "sqrt_recipm1_alphas_cumprod": diff_model.sqrt_recipm1_alphas_cumprod.numpy().astype(np.float64).tolist(),
            "posterior_variance": diff_model.posterior_variance.numpy().astype(np.float64).tolist(),
            "posterior_log_variance_clipped": diff_model.posterior_log_variance_clipped.numpy().astype(np.float64).tolist(),
            "posterior_mean_coef1": diff_model.posterior_mean_coef1.numpy().astype(np.float64).tolist(),
            "posterior_mean_coef2": diff_model.posterior_mean_coef2.numpy().astype(np.float64).tolist(),
        }

    params = {
        "note": (
            "p_sample: noise_pred=denoise_fn(x,t,cond); "
            "x_recon=sqrt_recip_alphas_cumprod[t]*x - sqrt_recipm1_alphas_cumprod[t]*noise_pred; "
            "x_recon=clip(x_recon,-1,1); "
            "model_mean=posterior_mean_coef1[t]*x_recon + posterior_mean_coef2[t]*x; "
            "model_log_var=posterior_log_variance_clipped[t]; "
            "x = model_mean + (t>0 ? 1:0) * exp(0.5*model_log_var) * noise; "
            "loop t from K_step-1 down to 0, x0 ~ N(0,1) shape (1,1,out_dim,T); "
            "_norm(x)=x/norm_scale, _denorm(x)=x*norm_scale; "
            "final = _denorm(x[:,0].transpose(-1,-2), norm_scale)"
        ),
        "mgc": params_for(model.mgc_model),
        "bap": params_for(model.bap_model),
    }
    path = os.path.join(OUT_DIR, "acoustic_diffusion_params.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(params, f, ensure_ascii=False, indent=2)
    print("dumped", path, os.path.getsize(path))
    return params


# ---------------------------------------------------------------------------
# VUV
# ---------------------------------------------------------------------------
def export_vuv(model, verify_results):
    path = export_ffconvlstm(
        model.vuv_model, "acoustic_vuv.onnx", model.vuv_model.in_dim, verify_results, "vuv"
    )
    return path


# ---------------------------------------------------------------------------
def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    torch.manual_seed(0)
    model = load_model()
    print("model loaded ok. in_dim=%d out_dim=%d" % (model.in_dim, model.out_dim))

    verify_results = []

    print("\n=== LF0 ===")
    export_lf0(model, verify_results)

    print("\n=== MGC encoder/denoise ===")
    mgc_enc_path = export_ffconvlstm(
        model.mgc_model.encoder,
        "acoustic_mgc_encoder.onnx",
        model.mgc_model.encoder.in_dim,
        verify_results,
        "mgc_encoder",
    )
    mgc_denoise_path = export_diffnet(
        model.mgc_model.denoise_fn,
        "acoustic_mgc_denoise.onnx",
        model.mgc_model.out_dim,
        model.mgc_model.encoder.out_dim,
        verify_results,
        "mgc_denoise",
    )

    print("\n=== BAP encoder/denoise ===")
    bap_enc_path = export_ffconvlstm(
        model.bap_model.encoder,
        "acoustic_bap_encoder.onnx",
        model.bap_model.encoder.in_dim,
        verify_results,
        "bap_encoder",
    )
    bap_denoise_path = export_diffnet(
        model.bap_model.denoise_fn,
        "acoustic_bap_denoise.onnx",
        model.bap_model.out_dim,
        model.bap_model.encoder.out_dim,
        verify_results,
        "bap_denoise",
    )

    print("\n=== VUV ===")
    export_vuv(model, verify_results)

    print("\n=== diffusion params dump ===")
    diff_params = dump_diffusion_params(model)

    print("\n=== full diffusion loop (100-step p_sample, injected noise) ===")
    verify_diffusion_loop(
        model.mgc_model, mgc_enc_path, mgc_denoise_path,
        model.mgc_model.in_dim, model.mgc_model.out_dim,
        diff_params["mgc"], verify_results, "mgc", T=20, seed=42,
    )
    verify_diffusion_loop(
        model.bap_model, bap_enc_path, bap_denoise_path,
        model.bap_model.in_dim, model.bap_model.out_dim,
        diff_params["bap"], verify_results, "bap", T=20, seed=43,
    )

    print("\n=== SUMMARY ===")
    all_ok = True
    for name, T, d, ok in verify_results:
        all_ok = all_ok and ok
        print("%-45s T=%-4d max|diff|=%.3e %s" % (name, T, d, "OK" if ok else "NG!"))
    print("\nALL OK" if all_ok else "\nSOME CHECKS FAILED")

    total_size = 0
    for fn in os.listdir(OUT_DIR):
        if fn.endswith(".onnx"):
            total_size += os.path.getsize(os.path.join(OUT_DIR, fn))
    print("total onnx size: %.1f MB" % (total_size / 1e6))


if __name__ == "__main__":
    main()
