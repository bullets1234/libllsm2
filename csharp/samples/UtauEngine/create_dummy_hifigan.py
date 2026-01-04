"""
HiFi-GAN ダミーモデル作成（動作確認用）
実際の音質向上効果はありませんが、パイプラインのテストに使用可能
"""

import torch
import torch.nn as nn

class DummyHiFiGAN(nn.Module):
    """
    ダミーHiFi-GANモデル
    Mel-Spectrogramをそのままアップサンプリングして音声に変換（品質向上なし）
    """
    def __init__(self):
        super().__init__()
        # 簡単なアップサンプリング層
        self.conv1 = nn.ConvTranspose1d(80, 256, kernel_size=16, stride=8)
        self.conv2 = nn.ConvTranspose1d(256, 1, kernel_size=8, stride=8)
        self.tanh = nn.Tanh()
    
    def forward(self, mel):
        """
        Args:
            mel: [batch, 80, time] - Mel-spectrogram
        Returns:
            audio: [batch, samples] - Audio waveform
        """
        # mel: [batch, 80, time]
        x = self.conv1(mel)  # [batch, 256, time*8]
        x = torch.nn.functional.relu(x)
        x = self.conv2(x)     # [batch, 1, time*64]
        x = self.tanh(x)
        # [batch, samples]
        return x.squeeze(1)

print("Creating dummy HiFi-GAN model...")
model = DummyHiFiGAN().eval()

# テスト
dummy_input = torch.randn(1, 80, 100)
with torch.no_grad():
    output = model(dummy_input)
    print(f"Test: Input {dummy_input.shape} → Output {output.shape}")

# ONNX エクスポート
print("Exporting to ONNX...")
torch.onnx.export(
    model,
    dummy_input,
    'hifigan_dummy.onnx',
    input_names=['mel'],
    output_names=['audio'],
    dynamic_axes={
        'mel': {2: 'time'},
        'audio': {1: 'samples'}
    },
    opset_version=12,
    do_constant_folding=True
)

print("✓ Dummy model created: hifigan_dummy.onnx")
print("\nThis is a TEST model only - no quality improvement")
print("Copy to: publish/models/hifigan.onnx")
print("\nFor real enhancement, use an actual HiFi-GAN model")
print("See HIFIGAN_SETUP.md for instructions")
