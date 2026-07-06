# ストレッチあり/なしで sinusoid 成分のスペクトルを比較
# フレームレート(200Hz)変調があれば倍音±200Hzにサイドバンドが出る
$exe = "g:\libllsm2\csharp\samples\UtauEngine_NewGen\bin\Release\net8.0\win-x64\L2R.exe"
$env:L2R_LOG = "Warn"

function Goertzel([double[]]$x, [double]$freq, [int]$fs) {
    $w = 2 * [Math]::PI * $freq / $fs
    $c = 2 * [Math]::Cos($w)
    $s1 = 0.0; $s2 = 0.0
    foreach ($v in $x) { $s0 = $v + $c * $s1 - $s2; $s2 = $s1; $s1 = $s0 }
    $re = $s1 - $s2 * [Math]::Cos($w); $im = $s2 * [Math]::Sin($w)
    return [Math]::Sqrt($re*$re + $im*$im) / ($x.Length / 2)
}

function Analyze([string]$path, [string]$label) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $cnt = ($bytes.Length - 44) / 2
    $start = [int]($cnt * 0.35); $len = [Math]::Min(32768, [int]($cnt * 0.3))
    $x = New-Object 'double[]' $len
    # ハン窓を掛けてリーク抑制
    for ($i = 0; $i -lt $len; $i++) {
        $w = 0.5 - 0.5 * [Math]::Cos(2 * [Math]::PI * $i / ($len - 1))
        $x[$i] = ([BitConverter]::ToInt16($bytes, 44 + ($start + $i) * 2) / 32768.0) * $w
    }
    Write-Host "=== $label ($path) ==="
    $freqs = @(
        @(220, "H1"), @(440, "H2"), @(660, "H3"), @(880, "H4"), @(1100, "H5"),
        @(420, "H1+200"), @(20, "H1-200"), @(640, "H2+200"), @(240, "H2-200"),
        @(860, "H3+200"), @(460, "H3-200"), @(1080, "H4+200"), @(680, "H4-200"),
        @(330, "mid1.5"), @(550, "mid2.5"), @(770, "mid3.5")
    )
    foreach ($f in $freqs) {
        $m = Goertzel $x $f[0] 44100
        $db = 20 * [Math]::Log10($m + 1e-12)
        Write-Host ("  {0,-8} {1,6}Hz : {2,7:F1} dB" -f $f[1], $f[0], $db)
    }
}

# ケースA: ストレッチなし (900ms 要求 = 等倍)
$env:L2R_DUMP = "g:\libllsm2\diag\dump_nostretch"
& $exe "g:\libllsm2\diag\clean_tone.wav" "g:\libllsm2\diag\out_ns.wav" "A3" 100 "g0" 0 900 100 -900 100 0 | Out-Null
Analyze "g:\libllsm2\diag\dump_nostretch\sinusoid.wav" "NO-STRETCH sinusoid"

# ケースB: ストレッチ x2.125 (既存ダンプ)
Analyze "g:\libllsm2\diag\dump\sinusoid.wav" "STRETCH x2.125 sinusoid"

# 参考: 原音
Analyze "g:\libllsm2\diag\clean_tone.wav" "SOURCE clean_tone"
