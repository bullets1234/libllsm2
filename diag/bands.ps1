param([string]$File, [int]$StartMs, [int]$WinMs = 100)
function Read-Wav([string]$path) {
  $b = [System.IO.File]::ReadAllBytes($path); $sr = [BitConverter]::ToInt32($b, 24); $pos = 12
  while ($pos -lt $b.Length - 8) {
    $id = [System.Text.Encoding]::ASCII.GetString($b, $pos, 4); $sz = [BitConverter]::ToInt32($b, $pos + 4)
    if ($id -eq 'data') { $avail = $b.Length - ($pos + 8); $n = [int]([Math]::Min($sz, $avail) / 2); $s = New-Object double[] $n
      for ($i = 0; $i -lt $n; $i++) { $s[$i] = [BitConverter]::ToInt16($b, $pos + 8 + $i * 2) / 32768.0 }; return @{ sr = $sr; s = $s } }
    $pos += 8 + $sz + ($sz % 2) }
}
$w = Read-Wav $File; $sr = $w.sr
$off = [int]($sr * $StartMs / 1000); $len = 8192
if ($off + $len -gt $w.s.Length) { $off = $w.s.Length - $len }
$re = New-Object double[] $len; $im = New-Object double[] $len
for ($i = 0; $i -lt $len; $i++) { $re[$i] = $w.s[$off + $i] * (0.5 - 0.5 * [Math]::Cos(2 * [Math]::PI * $i / ($len - 1))) }
# FFT
$n = $len; $j = 0
for ($i = 0; $i -lt $n - 1; $i++) { if ($i -lt $j) { $t = $re[$i]; $re[$i] = $re[$j]; $re[$j] = $t; $t = $im[$i]; $im[$i] = $im[$j]; $im[$j] = $t }; $k = $n / 2; while ($k -le $j) { $j -= $k; $k /= 2 }; $j += $k }
$m = 1
while ($m -lt $n) { $step = $m * 2; $ang = -[Math]::PI / $m
  for ($k = 0; $k -lt $m; $k++) { $wr = [Math]::Cos($ang * $k); $wi = [Math]::Sin($ang * $k)
    for ($i = $k; $i -lt $n; $i += $step) { $i2 = $i + $m; $tr = $wr * $re[$i2] - $wi * $im[$i2]; $ti = $wr * $im[$i2] + $wi * $re[$i2]; $re[$i2] = $re[$i] - $tr; $im[$i2] = $im[$i] - $ti; $re[$i] += $tr; $im[$i] += $ti } }
  $m = $step }
$bands = @(@(1000,2000),@(2000,4000),@(4000,6000),@(6000,8000),@(8000,12000),@(12000,16000))
$out = @()
foreach ($bd in $bands) {
  $lo = [int]($bd[0] * $n / $sr); $hi = [int]($bd[1] * $n / $sr); $p = 0.0
  for ($i = $lo; $i -lt $hi; $i++) { $p += $re[$i] * $re[$i] + $im[$i] * $im[$i] }
  $out += "{0,5}-{1,5}: {2,7:F1}" -f $bd[0], $bd[1], (10 * [Math]::Log10([Math]::Max(1e-30, $p)))
}
$out -join "  |"
