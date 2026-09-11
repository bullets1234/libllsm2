# -*- coding: utf-8 -*-
"""NNSVS timelag/duration (VariancePredictor+MDN) -> ONNX export + verification.

実行:
  G:\SimpleEnunu-0.3.1_TunedWavOut\python-3.9.13-embed-amd64\python.exe export_onnx_simple.py ^
    "G:\ENUNU_波音リツ_4CC_#3019_WD_SiFiGAN" out_onnx

出力: out_onnx/timelag.onnx, out_onnx/duration.onnx
  入力  x: float32 [1, T, in_dim] (正規化済み言語特徴量)
  出力  mu: float32 [1, T, out_dim], sigma: float32 [1, T, out_dim] (MDN最尤成分)
"""
import os
import sys

sys.path.insert(0, r"G:\SimpleEnunu-0.3.1_TunedWavOut\nnsvs-master")

import numpy as np
import torch
from omegaconf import OmegaConf
from hydra.utils import instantiate

from nnsvs.mdn import mdn_get_most_probable_sigma_and_mu


class MdnWrapper(torch.nn.Module):
    """VariancePredictor(MDN) を (mu, sigma) 出力の単一グラフにする (trace安全版)"""

    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, x):
        log_pi, log_sigma, mu = self.model(x, None)
        # log_pi: (B,T,G), mu/log_sigma: (B,T,G,D) — 最尤成分を gather で選択
        idx = log_pi.argmax(dim=2)  # (B,T)
        idx_e = idx.unsqueeze(-1).unsqueeze(-1).expand(
            idx.shape[0], idx.shape[1], 1, mu.shape[-1])  # (B,T,1,D)
        mu_sel = mu.gather(2, idx_e).squeeze(2)
        sigma_sel = log_sigma.gather(2, idx_e).squeeze(2).exp()
        return mu_sel, sigma_sel


def export_one(model_dir, name, out_dir):
    yaml_path = os.path.join(model_dir, name + "_model.yaml")
    pth_path = os.path.join(model_dir, name + "_model.pth")
    cfg = OmegaConf.load(yaml_path)
    model = instantiate(cfg.netG)
    ckpt = torch.load(pth_path, map_location="cpu")
    model.load_state_dict(ckpt["state_dict"])
    model.eval()
    in_dim = cfg.netG.in_dim

    wrapper = MdnWrapper(model).eval()
    T = 37
    dummy = torch.randn(1, T, in_dim)

    onnx_path = os.path.join(out_dir, name + ".onnx")
    torch.onnx.export(
        wrapper, dummy, onnx_path,
        input_names=["x"], output_names=["mu", "sigma"],
        dynamic_axes={"x": {1: "T"}, "mu": {1: "T"}, "sigma": {1: "T"}},
        opset_version=17)
    print("exported %s (in_dim=%d)" % (onnx_path, in_dim))

    # 検証: nnsvs オリジナル実装 vs onnxruntime
    import onnxruntime as ort
    sess = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
    for T2 in (17, 211):
        x = torch.randn(1, T2, in_dim)
        with torch.no_grad():
            log_pi, log_sigma, mu = model(x, None)
            sigma_t, mu_t = mdn_get_most_probable_sigma_and_mu(
                log_pi, log_sigma, mu)
        mu_o, sigma_o = sess.run(None, {"x": x.numpy()})
        err_mu = np.abs(mu_t.numpy() - mu_o).max()
        err_sg = np.abs(sigma_t.numpy() - sigma_o).max()
        print("  T=%d verify: max|dmu|=%.3e max|dsigma|=%.3e %s"
              % (T2, err_mu, err_sg, "OK" if max(err_mu, err_sg) < 1e-4 else "NG!"))


def main():
    model_dir = sys.argv[1]
    out_dir = sys.argv[2]
    os.makedirs(out_dir, exist_ok=True)
    torch.manual_seed(0)
    for name in ("timelag", "duration"):
        export_one(model_dir, name, out_dir)


if __name__ == "__main__":
    main()
