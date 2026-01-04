# HiFi-GAN簡易セットアップ（修正版）
Write-Host "=== HiFi-GAN Quick Setup ===" -ForegroundColor Cyan

# モデルダウンロード
Write-Host "`n[1/3] Downloading pretrained model..." -ForegroundColor Yellow
$modelUrl = "https://huggingface.co/spaces/akhaliq/DiffSinger/resolve/main/checkpoints/0109_pm_fix/model_ckpt_steps_1000000.ckpt"
$tempModel = "generator_temp.pth"

# 代替: 軽量なモデルを使用（実際にはHugging Faceなどから取得）
Write-Host "Please download the model manually from:" -ForegroundColor Yellow
Write-Host "https://drive.google.com/uc?id=1qpgI41wNXFcH-iKq1Y42JlBC9j0je8PW" -ForegroundColor White
Write-Host "`nSave as: generator_universal.pth.tar" -ForegroundColor White
Write-Host "Then press Enter to continue..." -ForegroundColor Yellow
Read-Host

if (-not (Test-Path "generator_universal.pth.tar")) {
    Write-Host "ERROR: Model file not found!" -ForegroundColor Red
    exit 1
}

# convert_hifigan.pyをhifigan_workにコピー
Copy-Item "convert_hifigan.py" "hifigan_work\"

# ONNX変換
Write-Host "`n[2/3] Converting to ONNX..." -ForegroundColor Yellow
Push-Location hifigan_work
python ..\convert_hifigan.py ..\generator_universal.pth.tar --output hifigan.onnx
Pop-Location

# モデルコピー
Write-Host "`n[3/3] Installing model..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path "publish\models" | Out-Null
Copy-Item "hifigan_work\hifigan.onnx" "publish\models\hifigan.onnx" -Force

$size = (Get-Item "publish\models\hifigan.onnx").Length / 1MB
Write-Host "`n✓ Setup complete!" -ForegroundColor Green
Write-Host "  Model: publish\models\hifigan.onnx ($([math]::Round($size, 2)) MB)" -ForegroundColor Cyan
Write-Host "`nUsage in UTAU:" -ForegroundColor Yellow
Write-Host "  N80  - Recommended enhancement" -ForegroundColor White
Write-Host "  N100 - Maximum enhancement" -ForegroundColor White
