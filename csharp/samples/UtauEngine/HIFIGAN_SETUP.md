# HiFi-GAN モデル セットアップガイド（簡易版）

## 最も簡単な方法：事前変換済みモデルを使用

### オプション1: 公開ONNXモデルをダウンロード（推奨）

Hugging Faceから事前にONNX変換済みのモデルをダウンロード：

```powershell
# modelsディレクトリ作成
New-Item -ItemType Directory -Force -Path publish\models

# Hugging Face からダウンロード（例）
# 注: 実際のURLは利用可能なモデルに応じて変更
Invoke-WebRequest -Uri "https://huggingface.co/YOUR_MODEL_PATH/hifigan.onnx" -OutFile "publish\models\hifigan.onnx"
```

### オプション2: 手動でモデルを取得・変換

#### ステップ1: HiFi-GANリポジトリクローン
```powershell
git clone https://github.com/jik876/hifi-gan
cd hifi-gan
```

#### ステップ2: Pythonパッケージインストール（管理者権限で実行）
```powershell
# PowerShellを管理者として実行してから：
python -m pip install torch torchaudio librosa unidecode inflect onnx
```

#### ステップ3: 事前学習済みモデルダウンロード

ブラウザで以下にアクセスしてダウンロード：
https://drive.google.com/uc?id=1qpgI41wNXFcH-iKq1Y42JlBC9j0je8PW

ダウンロードした`generator_universal.pth.tar`をhifi-ganディレクトリに配置

#### ステップ4: ONNX変換

`convert_hifigan.py`を使用（既に作成済み）：
```powershell
# hifi-ganディレクトリから
Copy-Item ..\convert_hifigan.py .
python convert_hifigan.py generator_universal.pth.tar --output hifigan.onnx
```

#### ステップ5: モデルをコピー
```powershell
Copy-Item hifigan.onnx ..\publish\models\
```

### オプション3: テスト用ダミーモデル（音質向上なし）

動作確認用のダミーモデルを作成：

```python
# create_dummy_hifigan.py
import torch
import torch.nn as nn

class DummyHiFiGAN(nn.Module):
    def __init__(self):
        super().__init__()
        # 単純なパススルー（テスト用）
        self.dummy = nn.Identity()
    
    def forward(self, mel):
        # Mel: [batch, 80, time]
        # Output: [batch, time * 256] (hop_length=256)
        batch, mels, time = mel.shape
        samples = time * 256
        # ダミー音声生成
        return torch.zeros(batch, samples)

model = DummyHiFiGAN().eval()
dummy_input = torch.randn(1, 80, 100)

torch.onnx.export(
    model,
    dummy_input,
    'hifigan_dummy.onnx',
    input_names=['mel'],
    output_names=['audio'],
    dynamic_axes={'mel': {2: 'time'}, 'audio': {1: 'samples'}},
    opset_version=12
)
print("Dummy model created: hifigan_dummy.onnx")
```

```powershell
python create_dummy_hifigan.py
New-Item -ItemType Directory -Force -Path publish\models
Copy-Item hifigan_dummy.onnx publish\models\hifigan.onnx
```

## 確認

モデルが正しく配置されているか確認：
```powershell
Test-Path publish\models\hifigan.onnx
```

## 使い方

UTAUのフラグ欄に：
- `N0` : Neural Vocoder オフ（デフォルト）
- `N80` : 推奨（80%適用）
- `N100` : 最大（100%適用）

## トラブルシューティング

### "Model not found" エラー
→ `publish\models\hifigan.onnx` が存在するか確認

### Python権限エラー
→ PowerShellを管理者権限で実行

### モデルが大きすぎる
→ 量子化版を使用するか、より軽量なモデルを探す

## 代替：外部ツール使用

Neural Vocoderの代わりに、別の音質向上方法：
1. RVC (Retrieval-based Voice Conversion)
2. Diff-SVC
3. Post-processing用のVSTプラグイン

これらは別途UTAUの後処理として適用可能です。
