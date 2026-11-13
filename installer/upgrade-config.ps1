# Brings an existing congee.conf up to date with features added since it was created,
# without touching anything the user changed. Run by setup after installing.
#
# Each step only fires when the config still has the original line(s) from an older
# default config, so customized parts are left alone. If anything changes, the old file
# is first saved next to it as congee.conf.bak (or congee.conf.<date-time>.bak if a
# .bak already exists). Running it again changes nothing.
#
#   1.2.0  temperature_unit setting; ${temp_unit} instead of a fixed degrees-C sign;
#          GPU driver line; every drive (with its own read/write) instead of only C:
param([string]$Config = (Join-Path $env:APPDATA 'congee\congee.conf'))
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Config)) { Write-Output "No config at $Config"; exit 0 }

$bytes = [IO.File]::ReadAllBytes($Config)
$hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
$utf8 = New-Object Text.UTF8Encoding($false)
$original = $utf8.GetString($bytes)
if ($hasBom) { $original = $original.Substring(1) }
$crlf = $original.Contains("`r`n")
$text = $original.Replace("`r`n", "`n")
$deg = [char]0xB0  # the degree sign; kept out of this file so Windows PowerShell 5.1 reads it right
$changes = New-Object Collections.Generic.List[string]

# --- temperature_unit setting, just above TEXT --------------------------------------------
if ($text -notmatch '(?m)^\s*temperature_unit\b' -and $text -match '(?m)^TEXT\s*$') {
    $block = "# Show temperatures in celsius or fahrenheit. `${temp_unit} below prints ${deg}C or ${deg}F to match.`n" +
             "# (Bars and graphs, and temp_bar_max, always work in ${deg}C.)`n" +
             "temperature_unit celsius`n`n"
    $text = ([regex]'(?m)^TEXT\s*$').Replace($text, { param($m) $block + $m.Value }, 1)
    $changes.Add('added the temperature_unit setting (celsius; set it to fahrenheit for degrees F)')
}

# --- ${temp_unit} instead of a fixed degrees-C sign after temperature variables -------------
$tempRe = [regex]('(\$\{(?:cpu_temp|gpu_temp|gpu_hotspot|gpu_memtemp|temp [^}]*)\})' + $deg + 'C')
if ($tempRe.IsMatch($text)) {
    $text = $tempRe.Replace($text, { param($m) $m.Groups[1].Value + '${temp_unit}' })
    $changes.Add('temperatures now follow temperature_unit')
}

# --- GPU driver line under the GPU name ------------------------------------------------------
if ($text -notmatch 'gpu_driver' -and $text -match '(?m)^\$\{gpu_name\}$') {
    $text = ([regex]'(?m)^\$\{gpu_name\}\n').Replace($text, { param($m) $m.Value + 'Driver ${gpu_driver}${alignr}${gpu_driver_date}' + "`n" }, 1)
    $changes.Add('added the GPU driver version line')
}

# --- every drive instead of only C: ------------------------------------------------------------
$oldDisk = 'C:  ${fs_used C:\} / ${fs_size C:\}${alignr}${fs_used_perc C:\}%' + "`n" +
           '${fs_bar 6,0 C:\}' + "`n" +
           'Read ${diskio_read}/s${alignr}Write ${diskio_write}/s' + "`n"
$newDisk = '${for_each_drive}${drive}  ${drive_label}${alignr}${fs_used} / ${fs_size}  ${fs_used_perc}%' + "`n" +
           '${fs_bar 6,0}' + "`n" +
           'Read ${diskio_read}/s${alignr}Write ${diskio_write}/s' + "`n" +
           '${endfor}'
if ($text -notmatch 'for_each_drive' -and $text.Contains($oldDisk)) {
    $text = $text.Replace($oldDisk, $newDisk)
    $changes.Add('DISK now lists every drive with its own read/write')
}

if ($changes.Count -eq 0) { Write-Output 'Config is up to date; nothing changed.'; exit 0 }

$backup = "$Config.bak"
if (Test-Path -LiteralPath $backup) { $backup = "$Config.$(Get-Date -Format 'yyyy-MM-dd-HHmmss').bak" }
[IO.File]::WriteAllBytes($backup, $bytes)

if ($crlf) { $text = $text.Replace("`n", "`r`n") }
$out = $utf8.GetBytes($text)
if ($hasBom) { $out = [byte[]](0xEF, 0xBB, 0xBF) + $out }
[IO.File]::WriteAllBytes($Config, $out)

Write-Output "Updated $Config (old version saved as $backup):"
$changes | ForEach-Object { Write-Output "  - $_" }
