# オンセットのガラガラ声（サブハーモニック/周期倍化）検出
# 出力WAVをスライディング窓で分析し、F0/2, 3F0/2 のサブハーモニックと
# 倍音レベルの時系列を表示する
param(
  [string]$File = "g:\libllsm2\diag\onset_A3.wav",
  [double]$F0 = 220.0,
  [int]$WinMs = 80,
  [int]$StepMs = 40,
  [int]$MaxMs = 800
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
  throw "no data chunk"
}

function Goertzel([double[]]$s, [int]$off, [int]$len, [int]$sr, [double]$f) {
  $w = 2 * [Math]::PI * $f / $sr
  $cw = 2 * [Math]::Cos($w); $s1 = 0.0; $s2 = 0.0
  for ($i = 0; $i -lt $len; $i++) {
    $win = 0.5 - 0.5 * [Math]::Cos(2 * [Math]::PI * $i / ($len - 1))
    $s0 = $s[$off + $i] * $win + $cw * $s1 - $s2; $s2 = $s1; $s1 = $s0
  }
  $p = $s1 * $s1 + $s2 * $s2 - $cw * $s1 * $s2
  return 10 * [Math]::Log10($p / ($len * $len / 16.0) + 1e-30)
}

$w = Read-Wav $File
$sr = $w.sr
$winN = [int]($sr * $WinMs / 1000)
$stepN = [int]($sr * $StepMs / 1000)
$maxN = [Math]::Min($w.s.Length - $winN, [int]($sr * $MaxMs / 1000))

"file=$File F0=$F0 sr=$sr"
"time(ms) |  F0/2   3F0/2   5F0/2 (sub)  |   H1     H2     H3  | sub-H1 diff"
for ($off = 0; $off -le $maxN; $off += $stepN) {
  $t = [int]($off * 1000 / $sr)
  $sub1 = Goertzel $w.s $off $winN $sr ($F0 * 0.5)
  $sub2 = Goertzel $w.s $off $winN $sr ($F0 * 1.5)
  $sub3 = Goertzel $w.s $off $winN $sr ($F0 * 2.5)
  $h1 = Goertzel $w.s $off $winN $sr $F0
  $h2 = Goertzel $w.s $off $winN $sr ($F0 * 2)
  $h3 = Goertzel $w.s $off $winN $sr ($F0 * 3)
  $subMax = [Math]::Max($sub1, [Math]::Max($sub2, $sub3))
  "{0,7} | {1,6:F1} {2,6:F1} {3,6:F1}      | {4,6:F1} {5,6:F1} {6,6:F1} | {7,6:F1}" -f $t, $sub1, $sub2, $sub3, $h1, $h2, $h3, ($subMax - $h1)
}
