param([string]$File, [int]$StartMs, [int]$WinMs = 60, [double]$Lo = 450, [double]$Hi = 620)
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
$best = 0.0; $bestF = 0.0
for ($f = $Lo; $f -le $Hi; $f += 0.5) {
  $wc = 2 * [Math]::PI * $f / $sr; $cw = 2 * [Math]::Cos($wc); $s1 = 0.0; $s2 = 0.0
  for ($i = 0; $i -lt $len; $i++) { $win = 0.5 - 0.5 * [Math]::Cos(2 * [Math]::PI * $i / ($len - 1)); $s0 = $w.s[$off + $i] * $win + $cw * $s1 - $s2; $s2 = $s1; $s1 = $s0 }
  $p = $s1 * $s1 + $s2 * $s2 - $cw * $s1 * $s2
  if ($p -gt $best) { $best = $p; $bestF = $f }
}
"{0}ms: F0 = {1:F1} Hz" -f $StartMs, $bestF
