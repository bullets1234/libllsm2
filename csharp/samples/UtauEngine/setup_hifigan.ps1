# HiFi-GAN ONNX モデルセットアップスクリプト

$ErrorActionPreference = "Stop"

Write-Host "=== HiFi-GAN Model Setup ===" -ForegroundColor Cyan

# 作業ディレクトリ
$workDir = "hifigan_temp"
$modelsDir = "publish\models"

# モデルディレクトリ作成
New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null
Write-Host "Models directory: $modelsDir" -ForegroundColor Green

# Python環境確認
Write-Host "`nChecking Python environment..." -ForegroundColor Yellow
try {
    $pythonVersion = python --version 2>&1
    Write-Host "Found: $pythonVersion" -ForegroundColor Green
} catch {
    Write-Host "ERROR: Python not found. Please install Python 3.8+" -ForegroundColor Red
    exit 1
}

# 作業ディレクトリ作成
if (Test-Path $workDir) {
    Write-Host "`nCleaning up old files..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $workDir
}
New-Item -ItemType Directory -Path $workDir | Out-Null

Push-Location $workDir

try {
    # HiFi-GAN リポジトリクローン
    Write-Host "`nCloning HiFi-GAN repository..." -ForegroundColor Yellow
    git clone https://github.com/jik876/hifi-gan
    Set-Location hifi-gan

    # 依存関係インストール
    Write-Host "`nInstalling dependencies..." -ForegroundColor Yellow
    python -m pip install torch torchaudio librosa unidecode inflect onnx onnxruntime numpy --quiet

    # 事前学習済みモデルダウンロード
    Write-Host "`nDownloading pretrained model..." -ForegroundColor Yellow
    $modelUrl = "https://drive.google.com/uc?id=1qpgI41wNXFcH-iKq1Y42JlBC9j0je8PW"
    $modelFile = "generator_universal.pth.tar"
    
    # gdown でGoogle Driveからダウンロード
    python -m pip install gdown --quiet
    python -m gdown $modelUrl -O $modelFile
    
    if (-not (Test-Path $modelFile)) {
        Write-Host "ERROR: Model download failed" -ForegroundColor Red
        exit 1
    }
    
    Write-Host "Model downloaded: $modelFile" -ForegroundColor Green

    # ONNX変換スクリプト作成
    Write-Host "`nCreating ONNX conversion script..." -ForegroundColor Yellow
    $conversionScript = @"
import torch
import json
from models import Generator
from env import AttrDict

# Universal V1 設定
h = AttrDict({
    'resblock': '1',
    'upsample_rates': [8, 8, 2, 2],
    'upsample_kernel_sizes': [16, 16, 4, 4],
    'upsample_initial_channel': 512,
    'resblock_kernel_sizes': [3, 7, 11],
    'resblock_dilation_sizes': [[1, 3, 5], [1, 3, 5], [1, 3, 5]],
    'num_mels': 80
})

print('Loading model...')
generator = Generator(h).eval()
state_dict = torch.load('generator_universal.pth.tar', map_location='cpu')
generator.load_state_dict(state_dict['generator'])

print('Converting to ONNX...')
dummy_input = torch.randn(1, 80, 100)

torch.onnx.export(
    generator,
    dummy_input,
    'hifigan_universal.onnx',
    input_names=['mel'],
    output_names=['audio'],
    dynamic_axes={
        'mel': {2: 'time'},
        'audio': {1: 'samples'}
    },
        opset_version=18,
    python convert_to_onnx.py

    if (-not (Test-Path "hifigan_universal.onnx")) {
        Write-Host "ERROR: ONNX conversion failed" -ForegroundColor Red
        exit 1
    }

    # モデルをコピー
    Write-Host "`nCopying model to publish directory..." -ForegroundColor Yellow
    Copy-Item "hifigan_universal.onnx" "..\..\$modelsDir\hifigan.onnx" -Force
    
    Pop-Location
    Pop-Location

    Write-Host "`n=== Setup Complete ===" -ForegroundColor Green
    Write-Host "Model installed at: $modelsDir\hifigan.onnx" -ForegroundColor Green
    Write-Host "`nYou can now use the N flag in UTAU:" -ForegroundColor Cyan
    Write-Host "  N0   = Off (default)" -ForegroundColor White
    Write-Host "  N80  = Recommended enhancement" -ForegroundColor White
    Write-Host "  N100 = Maximum enhancement" -ForegroundColor White

} catch {
    Write-Host "`nERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    # クリーンアップ
    if (Test-Path "..\$workDir") {
        Write-Host "`nCleaning up temporary files..." -ForegroundColor Yellow
        Set-Location ..
        Remove-Item -Recurse -Force $workDir -ErrorAction SilentlyContinue
    }
}
