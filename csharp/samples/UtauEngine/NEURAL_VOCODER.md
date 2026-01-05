# Neural Vocoder (HiFi-GAN) 統合ガイド

## 概要
HiFi-GANを使用して、LLSM合成結果の音質を向上させます。

## 必要なもの

### 1. HiFi-GANモデル (ONNX形式)
事前学習済みモデルをONNX形式に変換する必要があります。

#### モデルの取得と変換手順:

```bash
# 1. HiFi-GAN公式リポジトリをクローン
git clone https://github.com/jik876/hifi-gan
cd hifi-gan

# 2. 依存関係インストール
pip install torch librosa unidecode inflect
pip install onnx onnxruntime

# 3. 事前学習済みモデルをダウンロード
# https://github.com/jik876/hifi-gan#pretrained-model
# 例: Universal V1モデル
wget https://drive.google.com/uc?id=1qpgI41wNXFcH-iKq1Y42JlBC9j0je8PW -O generator_universal.pth

# 4. PyTorchモデルをONNXに変換
python convert_to_onnx.py --checkpoint generator_universal.pth --output hifigan.onnx
```

#### convert_to_onnx.py (作成が必要):
```python
import torch
import argparse
from models import Generator
from env import AttrDict
import json

def convert_to_onnx(checkpoint_path, output_path):
    # HiFi-GAN設定（Universal V1）
    h = AttrDict({
        'resblock': '1',
        'upsample_rates': [8, 8, 2, 2],
        'upsample_kernel_sizes': [16, 16, 4, 4],
        'upsample_initial_channel': 512,
        'resblock_kernel_sizes': [3, 7, 11],
        'resblock_dilation_sizes': [[1, 3, 5], [1, 3, 5], [1, 3, 5]]
    })
    
    # モデルロード
    generator = Generator(h).eval()
    state_dict = torch.load(checkpoint_path, map_location='cpu')
    generator.load_state_dict(state_dict['generator'])
    
    # ダミー入力
    dummy_input = torch.randn(1, 80, 100)  # [batch, mels, time]
    
    # ONNX変換
    torch.onnx.export(
        generator,
        dummy_input,
        output_path,
        input_names=['mel'],
        output_names=['audio'],
        dynamic_axes={
            'mel': {2: 'time'},
            'audio': {2: 'samples'}
        },
        opset_version=12
    )
    print(f"Model exported to {output_path}")

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--checkpoint', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    
    convert_to_onnx(args.checkpoint, args.output)
```

### 2. モデルの配置
変換したONNXモデルを以下に配置:
```
G:\libllsm2\csharp\samples\UtauEngine\publish\models\hifigan.onnx
```

## 使い方

### UTAUでの使用
```
Nフラグで音質向上の強度を指定:

N0   = オフ (デフォルト、処理なし)
N50  = 50%適用 (LLSM音質とブレンド)
N100 = 100%適用 (完全にNeural Vocoder出力)
```

### 推奨設定
- **通常**: `N80` - 自然な音質向上
- **最大品質**: `N100` - Neural Vocoder全適用
- **比較用**: `N0` - オリジナルLLSM出力

## 技術詳細

### 処理フロー
```
LLSM合成 (44.1kHz WAV)
  ↓
Mel-Spectrogram変換
  - nFFT: 1024
  - hopLength: 256
  - nMels: 80
  ↓
HiFi-GAN推論 (ONNX Runtime)
  ↓
Enhanced WAV
  ↓
ブレンド (元音声とミックス)
```

### パフォーマンス
- **レイテンシ**: 約10～50ms/秒音声 (CPUベース)
- **メモリ**: 約200MB追加
- **GPU対応**: ONNX Runtime GPUプロバイダーで高速化可能

### GPU有効化 (オプション)
```bash
# GPU版ONNX Runtimeに切り替え
dotnet add package Microsoft.ML.OnnxRuntime.Gpu --version 1.19.2
```

NeuralVocoder.csの SessionOptions を修正:
```csharp
var options = new SessionOptions();
options.AppendExecutionProvider_CUDA(0);  // GPU使用
```

## トラブルシューティング

### モデルが見つからない
```
[NeuralVocoder] Model not found at ...\models\hifigan.onnx, skipping
```
→ モデルファイルが正しい場所に配置されているか確認

### メモリ不足
→ Nフラグの値を下げる (N50など)

### 音質が悪い
→ STFT実装が簡易版のため、高品質なSTFTライブラリ (NAudio.Dspなど) への置き換えを推奨

## 今後の改善
- [ ] 高品質STFTライブラリ統合
- [ ] GPU推論対応
- [ ] リアルタイムストリーミング対応
- [ ] カスタムモデル対応（音源特化）
- [ ] 複数モデル切り替え機能
