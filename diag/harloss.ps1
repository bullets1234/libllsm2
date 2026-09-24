param([string]$SrcFile, [string]$SynFile, [double]$F0 = 464.6, [int]$SrcStartMs = 150, [int]$SynStartMs = 150, [int]$WinMs = 200, [int]$NHar = 40)
function Read-Wav([string]$path) {
  $b = [System.IO.File]::ReadAllBytes($path); $sr = [BitConverter]::ToInt32($b, 24); $pos = 12
  while ($pos -lt $b.Length - 8) {
    $id = [System.Text.Encoding]::ASCII.GetString($b, $pos, 4); $sz = [BitConverter]::ToInt32($b, $pos + 4)
    if ($id -eq 'data') { $avail = $b.Length - ($pos + 8); $n = [int]([Math]::Min($sz, $avail) / 2); $s = New-Object double[] $n
      for ($i = 0; $i -lt $n; $i++) { $s[$i] = [BitConverter]::ToInt16($b, $pos + 8 + $i * 2) / 32768.0 }; return @{ sr = $sr; s = $s } }
    $pos += 8 + $sz + ($sz % 2) }
}
function HarPeaks([object]$w, [int]$startMs, [int]$winMs, [double]$f0, [int]$nhar) {
  $sr = $w.sr; $off = [int]($sr * $startMs / 1000); $len = [int]($sr * $winMs / 1000)
  $res = New-Object double[] ($nhar + 1)
  for ($k = 1; $k -le $nhar; $k++) {
    $fc = $f0 * $k; if ($fc -gt $sr / 2 * 0.95) { $res[$k] = [double]::NaN; continue }
    # search +-0.3*f0 around fc with Goertzel at 1Hz steps
    $best = -999.0
    for ($f = $fc - 0.3 * $f0; $f -le $fc + 0.3 * $f0; $f += 2.0) {
      $wc = 2 * [Math]::PI * $f / $sr; $cw = 2 * [Math]::Cos($wc); $s1 = 0.0; $s2 = 0.0
      for ($i = 0; $i -lt $len; $i++) { $win = 0.5 - 0.5 * [Math]::Cos(2 * [Math]::PI * $i / ($len - 1)); $s0 = $w.s[$off + $i] * $win + $cw * $s1 - $s2; $s2 = $s1; $s1 = $s0 }
      $p = [Math]::Sqrt([Math]::Max(1e-30, $s1 * $s1 + $s2 * $s2 - $cw * $s1 * $s2))
      $db = 20 * [Math]::Log10($p / ($len / 4))
      if ($db -gt $best) { $best = $db }
    }
    $res[$k] = $best
  }
  return $res
}
$src = Read-Wav $SrcFile; $syn = Read-Wav $SynFile
$a = HarPeaks $src $SrcStartMs $WinMs $F0 $NHar
$b = HarPeaks $syn $SynStartMs $WinMs $F0 $NHar
"har |  freq Hz | src dB | syn dB | loss dB"
for ($k = 1; $k -le $NHar; $k++) {
  if ([double]::IsNaN($a[$k]) -or [double]::IsNaN($b[$k])) { continue }
  "{0,3} | {1,8:F0} | {2,6:F1} | {3,6:F1} | {4,7:F2}" -f $k, ($F0 * $k), $a[$k], $b[$k], ($b[$k] - $a[$k])
}
