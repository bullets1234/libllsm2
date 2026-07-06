# HNR風測定: 倍音ピーク vs 倍音間フロア（指定窓）
param(
  [string]$File,
  [double]$F0 = 659.3,
  [int]$StartMs = 0,
  [int]$WinMs = 150,
  [int]$NHar = 8
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
$off = [int]($sr * $StartMs / 1000)
$len = [int]($sr * $WinMs / 1000)
if ($off + $len -gt $w.s.Length) { $len = $w.s.Length - $off }
"$File  F0=$F0  win=${StartMs}ms+${WinMs}ms"
"har |  peak dB | floor dB | HNR dB"
$hnrSum = 0.0; $cnt = 0
for ($h = 1; $h -le $NHar; $h++) {
  $peak = Goertzel $w.s $off $len $sr ($F0 * $h)
  $floor1 = Goertzel $w.s $off $len $sr ($F0 * ($h + 0.5))
  $hnr = $peak - $floor1
  "{0,3} | {1,8:F1} | {2,8:F1} | {3,6:F1}" -f $h, $peak, $floor1, $hnr
  $hnrSum += $hnr; $cnt++
}
"mean HNR = {0:F1} dB" -f ($hnrSum / $cnt)
