# sin/noise 成分の時系列RMS比較（20ms窓）。ガラガラ=NM過大区間の特定用
param(
  [string]$Dir = "g:\libllsm2\diag\dump_renri",
  [int]$WinMs = 20
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
$sin = Read-Wav "$Dir\sinusoid.wav"
$noi = Read-Wav "$Dir\noise.wav"
$sr = $sin.sr
$winN = [int]($sr * $WinMs / 1000)
$n = [Math]::Min($sin.s.Length, $noi.s.Length)
"time(ms) | sin dBFS | noise dBFS | N/S dB"
for ($off = 0; $off + $winN -le $n; $off += $winN) {
  $es = 0.0; $en = 0.0
  for ($i = 0; $i -lt $winN; $i++) {
    $es += $sin.s[$off + $i] * $sin.s[$off + $i]
    $en += $noi.s[$off + $i] * $noi.s[$off + $i]
  }
  $sdb = 10 * [Math]::Log10($es / $winN + 1e-30)
  $ndb = 10 * [Math]::Log10($en / $winN + 1e-30)
  "{0,7} | {1,8:F1} | {2,9:F1} | {3,6:F1}" -f [int]($off * 1000 / $sr), $sdb, $ndb, ($ndb - $sdb)
}
