param([string]$File, [int]$StartMs, [int]$WinMs = 150)
function Read-Wav([string]$path) {
  $b = [System.IO.File]::ReadAllBytes($path); $sr = [BitConverter]::ToInt32($b, 24); $pos = 12
  while ($pos -lt $b.Length - 8) {
    $id = [System.Text.Encoding]::ASCII.GetString($b, $pos, 4); $sz = [BitConverter]::ToInt32($b, $pos + 4)
    if ($id -eq 'data') { $avail = $b.Length - ($pos + 8); $n = [int]([Math]::Min($sz, $avail) / 2); $s = New-Object double[] $n
      for ($i = 0; $i -lt $n; $i++) { $s[$i] = [BitConverter]::ToInt16($b, $pos + 8 + $i * 2) / 32768.0 }; return @{ sr = $sr; s = $s } }
    $pos += 8 + $sz + ($sz % 2) }
}
$w = Read-Wav $File; $sr = $w.sr
$off = [int]($sr * $StartMs / 1000); $len = [int]($sr * $WinMs / 1000)
# envelope = |x| then remove DC
$env = New-Object double[] $len
$mean = 0.0
for ($i = 0; $i -lt $len; $i++) { $env[$i] = [Math]::Abs($w.s[$off + $i]); $mean += $env[$i] }
$mean /= $len
for ($i = 0; $i -lt $len; $i++) { $env[$i] -= $mean }
# Goertzel scan of envelope 100..1400 Hz
"modHz | rel dB (vs mean env)"
$results = @()
for ($f = 30; $f -le 700; $f += 5) {
  $wc = 2 * [Math]::PI * $f / $sr; $cw = 2 * [Math]::Cos($wc); $s1 = 0.0; $s2 = 0.0
  for ($i = 0; $i -lt $len; $i++) { $win = 0.5 - 0.5 * [Math]::Cos(2 * [Math]::PI * $i / ($len - 1)); $s0 = $env[$i] * $win + $cw * $s1 - $s2; $s2 = $s1; $s1 = $s0 }
  $p = [Math]::Sqrt([Math]::Max(1e-30, $s1 * $s1 + $s2 * $s2 - $cw * $s1 * $s2)) / ($len / 4)
  $db = 20 * [Math]::Log10($p / [Math]::Max(1e-12, $mean))
  $results += [PSCustomObject]@{ f = $f; db = $db }
}
$results | Sort-Object db -Descending | Select-Object -First 8 | ForEach-Object { "{0,5} | {1,6:F1}" -f $_.f, $_.db }

