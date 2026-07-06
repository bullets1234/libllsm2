# コーデック上限測定: 原音セグメント vs 純粋再合成（同一ピッチ・ストレッチなし）
# 帯域別エネルギー保存度 + フレーム毎スペクトル距離（LSD風）を測る
param(
  [string]$SrcFile = "G:\libllsm2\csharp\samples\UtauEngine\u+の.wav",
  [string]$OutFile = "g:\libllsm2\diag\resynth_pure.wav",
  [int]$SegmentMs = 400
)

function Read-Wav([string]$path) {
  $b = [System.IO.File]::ReadAllBytes($path)
  $sr = [BitConverter]::ToInt32($b, 24)
  $pos = 12
  while ($pos -lt $b.Length - 8) {
    $id = [System.Text.Encoding]::ASCII.GetString($b, $pos, 4)
    $sz = [BitConverter]::ToInt32($b, $pos + 4)
    if ($id -eq 'data') {
      $avail = $b.Length - ($pos + 8)
      $n = [int]([Math]::Min($sz, $avail) / 2)
      $s = New-Object double[] $n
      for ($i = 0; $i -lt $n; $i++) { $s[$i] = [BitConverter]::ToInt16($b, $pos + 8 + $i * 2) / 32768.0 }
      return @{ sr = $sr; s = $s }
    }
    $pos += 8 + $sz + ($sz % 2)
  }
  throw "no data chunk in $path"
}

$srcW = Read-Wav $SrcFile
$outW = Read-Wav $OutFile
$sr = $srcW.sr
$nSeg = [int]($sr * $SegmentMs / 1000)
$src = $srcW.s[0..([Math]::Min($nSeg, $srcW.s.Length) - 1)]
$out = $outW.s
$n = [Math]::Min($src.Length, $out.Length)
"src={0} samples, out={1} samples, compare n={2} (sr={3})" -f $src.Length, $out.Length, $n, $sr

# 全体RMS
function RmsDb([double[]]$x, [int]$n) {
  $e = 0.0; for ($i = 0; $i -lt $n; $i++) { $e += $x[$i] * $x[$i] }
  return 10 * [Math]::Log10($e / $n + 1e-30)
}
"src RMS = {0:F1} dBFS / out RMS = {1:F1} dBFS" -f (RmsDb $src $n), (RmsDb $out $n)

# 帯域別 Goertzel エネルギー（中央200msの定常部で比較）
function BandEnergy([double[]]$s, [int]$off, [int]$len, [int]$sr, [double]$lo, [double]$hi) {
  $total = 0.0; $steps = 40
  for ($k = 0; $k -lt $steps; $k++) {
    $f = $lo + ($hi - $lo) * ($k + 0.5) / $steps
    $w = 2 * [Math]::PI * $f / $sr
    $cw = 2 * [Math]::Cos($w); $s1 = 0.0; $s2 = 0.0
    for ($i = 0; $i -lt $len; $i++) {
      $win = 0.5 - 0.5 * [Math]::Cos(2 * [Math]::PI * $i / ($len - 1))
      $s0 = $s[$off + $i] * $win + $cw * $s1 - $s2; $s2 = $s1; $s1 = $s0
    }
    $total += $s1 * $s1 + $s2 * $s2 - $cw * $s1 * $s2
  }
  return $total / $steps
}

$off = [int]($sr * 0.15); $len = [int]($sr * 0.2)
if ($off + $len -gt $n) { $off = 0; $len = $n }
"--- band energy out/src (steady 150-350ms) ---"
foreach ($band in @(@(100,500),@(500,1000),@(1000,2000),@(2000,4000),@(4000,6000),@(6000,8000),@(8000,12000),@(12000,16000),@(16000,20000))) {
  $es = BandEnergy $src $off $len $sr $band[0] $band[1]
  $eo = BandEnergy $out $off $len $sr $band[0] $band[1]
  $db = 10 * [Math]::Log10(($eo + 1e-30) / ($es + 1e-30))
  "band {0,5}-{1,5}Hz : {2,7:F2} dB   (src abs {3,7:F1} dB)" -f $band[0], $band[1], $db, (10*[Math]::Log10($es+1e-30))
}
