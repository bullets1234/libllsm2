param([string]$File, [int]$StartMs = 500, [int]$WinMs = 1200)
function Read-Wav([string]$path) {
  $b = [System.IO.File]::ReadAllBytes($path); $sr = [BitConverter]::ToInt32($b, 24); $pos = 12
  while ($pos -lt $b.Length - 8) {
    $id = [System.Text.Encoding]::ASCII.GetString($b, $pos, 4); $sz = [BitConverter]::ToInt32($b, $pos + 4)
    if ($id -eq 'data') { $avail = $b.Length - ($pos + 8); $n = [int]([Math]::Min($sz, $avail) / 2); $s = New-Object double[] $n
      for ($i = 0; $i -lt $n; $i++) { $s[$i] = [BitConverter]::ToInt16($b, $pos + 8 + $i * 2) / 32768.0 }; return @{ sr = $sr; s = $s } }
    $pos += 8 + $sz + ($sz % 2) }
}
$w = Read-Wav $File; $sr = $w.sr; $s = $w.s
$off = [int]($sr * $StartMs / 1000); $len = [int]($sr * $WinMs / 1000)
if ($off + $len -gt $s.Length) { $len = $s.Length - $off }
# normalized cross-correlation at specific lags
$lags = @(19600, 19700, 19800, 19850, 19872, 19900, 19950, 20000, 20128, 15000, 10000)
"lag(samples) | lag(ms) | corr"
foreach ($lag in $lags) {
  $n = $len - $lag; if ($n -lt 4000) { continue }
  $sum = 0.0; $e1 = 0.0; $e2 = 0.0
  for ($i = 0; $i -lt $n; $i++) { $a = $s[$off + $i]; $b2 = $s[$off + $i + $lag]; $sum += $a * $b2; $e1 += $a * $a; $e2 += $b2 * $b2 }
  $c = $sum / [Math]::Sqrt($e1 * $e2 + 1e-30)
  "{0,10} | {1,7:F1} | {2,7:F4}" -f $lag, ($lag * 1000.0 / $sr), $c
}
