# A/B比較: 平滑化ON/OFF の実声レンダ結果の差を定量化
# - 差分RMS（どれだけ変わったか）
# - 帯域別エネルギー（クリア感＝高域の保存度）
param(
  [string]$FileA = "g:\libllsm2\diag\voice_nosmooth.wav",
  [string]$FileB = "g:\libllsm2\diag\voice_smooth.wav"
)

function Read-Wav([string]$path) {
  $b = [System.IO.File]::ReadAllBytes($path)
  $sr = [BitConverter]::ToInt32($b, 24)
  # data チャンク探索
  $pos = 12
  while ($pos -lt $b.Length - 8) {
    $id = [System.Text.Encoding]::ASCII.GetString($b, $pos, 4)
    $sz = [BitConverter]::ToInt32($b, $pos + 4)
    if ($id -eq 'data') {
      $n = [int]($sz / 2)
      $s = New-Object double[] $n
      for ($i = 0; $i -lt $n; $i++) { $s[$i] = [BitConverter]::ToInt16($b, $pos + 8 + $i * 2) / 32768.0 }
      return @{ sr = $sr; s = $s }
    }
    $pos += 8 + $sz + ($sz % 2)
  }
  throw "no data chunk in $path"
}

function BandEnergy([double[]]$s, [int]$sr, [double]$lo, [double]$hi) {
  # Goertzel を帯域内で等間隔サンプル（粗いが比較には十分）
  $n = [Math]::Min($s.Length, $sr) # 最大1秒
  $total = 0.0
  $steps = 48
  for ($k = 0; $k -lt $steps; $k++) {
    $f = $lo + ($hi - $lo) * ($k + 0.5) / $steps
    $w = 2 * [Math]::PI * $f / $sr
    $cw = 2 * [Math]::Cos($w); $s1 = 0.0; $s2 = 0.0
    for ($i = 0; $i -lt $n; $i++) {
      # ハン窓
      $win = 0.5 - 0.5 * [Math]::Cos(2 * [Math]::PI * $i / ($n - 1))
      $s0 = $s[$i] * $win + $cw * $s1 - $s2; $s2 = $s1; $s1 = $s0
    }
    $p = $s1 * $s1 + $s2 * $s2 - $cw * $s1 * $s2
    $total += $p
  }
  return $total / $steps
}

$a = Read-Wav $FileA
$bw = Read-Wav $FileB
$n = [Math]::Min($a.s.Length, $bw.s.Length)

# 差分RMS
$diff = 0.0; $ref = 0.0
for ($i = 0; $i -lt $n; $i++) {
  $d = $a.s[$i] - $bw.s[$i]; $diff += $d * $d; $ref += $a.s[$i] * $a.s[$i]
}
$diffDb = 10 * [Math]::Log10(($diff + 1e-30) / ($ref + 1e-30))
"diff RMS (B-A, rel A) = {0:F1} dB" -f $diffDb

# 帯域別エネルギー比較
foreach ($band in @(@(200,1000),@(1000,3000),@(3000,6000),@(6000,10000),@(10000,16000))) {
  $ea = BandEnergy $a.s $a.sr $band[0] $band[1]
  $eb = BandEnergy $bw.s $bw.sr $band[0] $band[1]
  $ratioDb = 10 * [Math]::Log10(($eb + 1e-30) / ($ea + 1e-30))
  "band {0,5}-{1,5}Hz : B/A = {2,6:F2} dB" -f $band[0], $band[1], $ratioDb
}
