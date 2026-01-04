# HiFi-GAN モデル セットアップ - 最終ガイド

## 🎯 最も簡単な方法

### ステップ1: モデルのダウンロード
ブラウザで以下のリンクを開いてダウンロード：
```
https://drive.google.com/uc?id=1qpgI41wNXFcH-iKq1Y42JlBC9j0je8PW
```
ダウンロードしたファイル: `generator_universal.pth.tar` (約55MB)

### ステップ2: ONNX変換
```powershell
# hifigan_work ディレクトリに移動（既に作成済み）
cd hifigan_work

# convert_hifigan.py をコピー
Copy-Item ..\convert_hifigan.py .

# 変換実行
python convert_hifigan.py ..\generator_universal.pth.tar --output hifigan.onnx
```

### ステップ3: モデル配置
```powershell
# モデルディレクトリ作成
New-Item -ItemType Directory -Force -Path ..\publish\models

# モデルコピー
Copy-Item hifigan.onnx ..\publish\models\

# 確認
cd ..
Test-Path publish\models\hifigan.onnx  # True と表示されればOK
```

## 🔧 トラブルシューティング

### エラー: "Module 'models' not found"
→ `hifigan_work` ディレクトリ内で実行してください

### エラー: "opset version 12 conversion failed"
→ **無視してOK**。モデルは opset 18 で作成されます（ONNX Runtime 1.19.2 は対応済み）

### エラー: Pythonパッケージがない
```powershell
python -m pip install torch onnx --user
```

## ✅ 動作確認

UTAUで任意のustを開いて、フラグ欄に `N80` を入力して再生してください。

### 期待される動作:
- コンソールに `[NeuralVocoder] Applying enhancement` と表示
- 音質が向上（機械的 → 自然）

### モデルが読み込まれない場合:
```powershell
# パス確認
Get-Item publish\models\hifigan.onnx

# 出力例:
# Mode  LastWriteTime  Length  Name
# ----  -------------  ------  ----
# -a---  2025/12/18     54.9MB  hifigan.onnx
```

## 📊 性能

- **レイテンシ**: ~20-50ms/秒音声 (CPU)
- **メモリ**: +200MB程度
- **音質向上**: 中程度（実際のHiFi-GANと比べてSTFT実装が簡易的）

## 🚀 今後の改善

本格的な音質向上には：
1. 高品質STFT実装（NAudio.Dsp など）
2. GPU推論対応
3. カスタムモデル（UTAU音源特化）

## ❓ よくある質問

**Q: N0とN100の違いは？**
A: N0は処理なし、N100は完全にNeural Vocoder出力（N80推奨）

**Q: 処理が遅い**
A: GPU版ONNX Runtimeを使用するか、Nフラグの値を下げてください

**Q: モデルファイルが大きい**
A: 量子化版を使用（別途作成が必要）
