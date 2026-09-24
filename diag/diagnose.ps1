# クリーンな倍音信号（220Hz + 数倍音、ノイズ皆無）を生成し、
# エンジンで合成 → sin/noise 分解ダンプの RMS を比較する診断スクリプト
$fs = 44100; $dur = 1.0; $n = [int]($fs * $dur); $f = 220.0
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$dataLen = $n * 2
$bw.Write([char[]]'RIFF'); $bw.Write([int](36 + $dataLen)); $bw.Write([char[]]'WAVE')
$bw.Write([char[]]'fmt '); $bw.Write([int]16); $bw.Write([int16]1); $bw.Write([int16]1)
$bw.Write([int]$fs); $bw.Write([int]($fs * 2)); $bw.Write([int16]2); $bw.Write([int16]16)
$bw.Write([char[]]'data'); $bw.Write([int]$dataLen)
for ($i = 0; $i -lt $n; $i++) {
    $env = [Math]::Min(1.0, [Math]::Min($i / 2000.0, ($n - $i) / 2000.0))
    $t = 2 * [Math]::PI * $f * $i / $fs
    # 倍音を持つクリーンな擬似声帯波（ノイズ 0）
    $v = [Math]::Sin($t) + 0.5*[Math]::Sin(2*$t) + 0.33*[Math]::Sin(3*$t) + 0.25*[Math]::Sin(4*$t) + 0.15*[Math]::Sin(5*$t)
    $bw.Write([int16]($v * $env * 8000))
}
$bw.Flush()
[System.IO.File]::WriteAllBytes("g:\libllsm2\diag\clean_tone.wav", $ms.ToArray())
$bw.Close()
Write-Host "wrote clean_tone.wav"

$env:L2R_LOG = "Info"
$env:L2R_DUMP = "g:\libllsm2\diag\dump"
$exe = "g:\libllsm2\csharp\samples\UtauEngine_NewGen\bin\Release\net8.0\win-x64\L2R.exe"

# ケース1: ストレッチあり (900ms -> 1800ms 相当)
& $exe "g:\libllsm2\diag\clean_tone.wav" "g:\libllsm2\diag\out_stretch.wav" "A3" 100 "g0" 0 1800 100 -900 100 0
Write-Host "stretch exit=$LASTEXITCODE"

function Get-WavRms([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $cnt = ($bytes.Length - 44) / 2
    $sum = 0.0; $peak = 0.0
    for ($i = 0; $i -lt $cnt; $i++) {
        $s = [BitConverter]::ToInt16($bytes, 44 + $i * 2) / 32768.0
        $sum += $s * $s
        $a = [Math]::Abs($s); if ($a -gt $peak) { $peak = $a }
    }
    $rms = [Math]::Sqrt($sum / $cnt)
    return @{ Rms = $rms; Peak = $peak; Db = 20 * [Math]::Log10($rms + 1e-12) }
}

foreach ($f in @("output", "sinusoid", "noise")) {
    $p = "g:\libllsm2\diag\dump\$f.wav"
    if (Test-Path $p) {
        $r = Get-WavRms $p
        Write-Host ("{0,-10} RMS={1:F5} ({2:F1} dBFS) Peak={3:F4}" -f $f, $r.Rms, $r.Db, $r.Peak)
    }
}
