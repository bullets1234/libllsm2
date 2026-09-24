# オリジナル vs NewGen 同一入力比較（ストレッチなし）
$old = "G:\libllsm2\csharp\samples\UtauEngine\bin\Release\net8.0\win-x64\L2R.exe"
$new = "g:\libllsm2\csharp\samples\UtauEngine_NewGen\bin\Release\net8.0\win-x64\L2R.exe"
Remove-Item Env:L2R_DUMP -ErrorAction SilentlyContinue
$env:L2R_LOG = "Warn"

& $old "g:\libllsm2\diag\clean_tone.wav" "g:\libllsm2\diag\out_old.wav" "A3" 100 "g0" 0 900 100 -900 100 0 | Out-Null
Write-Host "old exit=$LASTEXITCODE"
& $new "g:\libllsm2\diag\clean_tone.wav" "g:\libllsm2\diag\out_new.wav" "A3" 100 "g0" 0 900 100 -900 100 0 | Out-Null
Write-Host "new exit=$LASTEXITCODE"

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
    for ($i = 0; $i -lt $len; $i++) {
        $w = 0.5 - 0.5 * [Math]::Cos(2 * [Math]::PI * $i / ($len - 1))
        $x[$i] = ([BitConverter]::ToInt16($bytes, 44 + ($start + $i) * 2) / 32768.0) * $w
    }
    Write-Host "=== $label ==="
    foreach ($f in @(@(220,"H1"),@(440,"H2"),@(660,"H3"),@(420,"H1+200"),@(240,"H2-200"),@(640,"H2+200"),@(460,"H3-200"),@(860,"H3+200"),@(330,"mid1.5"),@(550,"mid2.5"))) {
        $m = Goertzel $x $f[0] 44100
        Write-Host ("  {0,-8} {1,5}Hz : {2,7:F1} dB" -f $f[1], $f[0], (20*[Math]::Log10($m+1e-12)))
    }
}

Analyze "g:\libllsm2\diag\out_old.wav" "ORIGINAL UtauEngine"
Analyze "g:\libllsm2\diag\out_new.wav" "NewGen"
