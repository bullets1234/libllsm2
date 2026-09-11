param([string]$File, [double]$Freq = 3252, [double]$SearchHz = 90, [int]$StartMs = 100, [int]$EndMs = 380, [int]$WinMs = 25, [int]$HopMs = 10)
function Read-Wav([string]$path) {
  $b = [System.IO.File]::ReadAllBytes($path); $sr = [BitConverter]::ToInt32($b, 24); $pos = 12
  while ($pos -lt $b.Length - 8) {
    $id = [System.Text.Encoding]::ASCII.GetString($b, $pos, 4); $sz = [BitConverter]::ToInt32($b, $pos + 4)
    if ($id -eq 'data') { $avail = $b.Length - ($pos + 8); $n = [int]([Math]::Min($sz, $avail) / 2); $s = New-Object double[] $n
      for ($i = 0; $i -lt $n; $i++) { $s[$i] = [BitConverter]::ToInt16($b, $pos + 8 + $i * 2) / 32768.0 }; return @{ sr = $sr; s = $s } }
    $pos += 8 + $sz + ($sz % 2) }
}
$w = Read-Wav $File; $sr = $w.sr; $len = [int]($sr * $WinMs / 1000)
$dbs = @()
for ($t = $StartMs; $t -le $EndMs; $t += $HopMs) {
  $off = [int]($sr * $t / 1000); if ($off + $len -ge $w.s.Length) { break }
  $best = -999.0
  for ($f = $Freq - $SearchHz; $f -le $Freq + $SearchHz; $f += 15.0) {
    $wc = 2 * [Math]::PI * $f / $sr; $cw = 2 * [Math]::Cos($wc); $s1 = 0.0; $s2 = 0.0
    for ($i = 0; $i -lt $len; $i++) { $win = 0.5 - 0.5 * [Math]::Cos(2 * [Math]::PI * $i / ($len - 1)); $s0 = $w.s[$off + $i] * $win + $cw * $s1 - $s2; $s2 = $s1; $s1 = $s0 }
    $p = [Math]::Sqrt([Math]::Max(1e-30, $s1 * $s1 + $s2 * $s2 - $cw * $s1 * $s2))
    $db = 20 * [Math]::Log10($p / ($len / 4))
    if ($db -gt $best) { $best = $db }
  }
  $dbs += $best
}
$sorted = $dbs | Sort-Object
$median = $sorted[[int]($sorted.Count / 2)]
$max = ($dbs | Measure-Object -Maximum).Maximum
$emean = 10 * [Math]::Log10((($dbs | ForEach-Object { [Math]::Pow(10, $_ / 10) }) | Measure-Object -Average).Average)
"n={0}  median={1:F1}  max={2:F1}  energy-mean={3:F1} dB" -f $dbs.Count, $median, $max, $emean
"timeline: " + (($dbs | ForEach-Object { "{0,6:F1}" -f $_ }) -join "")
