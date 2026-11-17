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
#   1.2.2  logging setting (yes; the tray menu's Logging item switches it)
#   1.2.3  rendering setting (software: the .NET edition draws with the CPU, using less memory)
#   1.3.0  congee's own sensor service replaces LibreHardwareMonitor: the start_lhm / stop_lhm
#          settings (and their comments) are removed, and the no-sensors hint no longer says to
#          start LibreHardwareMonitor
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

# --- logging setting, just above TEXT --------------------------------------------------------
if ($text -notmatch '(?m)^\s*logging\b' -and $text -match '(?m)^TEXT\s*$') {
    $block = "# Write congee.log (starts, stops and crashes) to %APPDATA%\congee: yes or no.`n" +
             "# The Logging item in the tray menu switches this too.`n" +
             "logging yes`n`n"
    $text = ([regex]'(?m)^TEXT\s*$').Replace($text, { param($m) $block + $m.Value }, 1)
    $changes.Add('added the logging setting (on; switch it from the tray menu)')
}

# --- rendering setting, just above TEXT ------------------------------------------------------
if ($text -notmatch '(?m)^\s*rendering\b' -and $text -match '(?m)^TEXT\s*$') {
    $block = "# .NET edition only: draw with the CPU (software, uses less memory) or the graphics card`n" +
             "# (hardware). Takes effect when congee restarts.`n" +
             "rendering software`n`n"
    $text = ([regex]'(?m)^TEXT\s*$').Replace($text, { param($m) $block + $m.Value }, 1)
    $changes.Add('added the rendering setting (software: the .NET edition uses less memory)')
}

# --- start_lhm / stop_lhm: gone with LibreHardwareMonitor -----------------------------------
# Removes those settings (whatever their value) and the comment lines congee wrote above them,
# plus one blank line after, but only in the settings part (above TEXT).
$lhmComments = @(
    "# Start LibreHardwareMonitor (needed for temperatures, fans and GPU stats) if it isn't",
    "# running when congee starts: ask, yes (start it; Windows asks for admin permission) or no.",
    "# When you choose Exit congee, also stop LibreHardwareMonitor: ask, yes or no.",
    "# Start LibreHardwareMonitor with congee if it isn't running: ask, yes or no",
    "# Stop LibreHardwareMonitor too when you choose Exit congee: ask, yes or no")
$m = [regex]::Match($text, '(?m)^TEXT\s*$')
if ($m.Success) {
    $head = $text.Substring(0, $m.Index)
    $lines = New-Object Collections.Generic.List[string]
    $dropBlank = $false
    $removed = $false
    foreach ($l in $head.Split("`n")) {
        if ($lhmComments -contains $l.TrimEnd() -or $l -match '^\s*(start_lhm|stop_lhm)\b') {
            $removed = $true
            $dropBlank = $l -match '^\s*(start_lhm|stop_lhm)\b'
            continue
        }
        if ($dropBlank -and $l.Trim() -eq '') { $dropBlank = $false; continue }
        $dropBlank = $false
        $lines.Add($l)
    }
    if ($removed) {
        $text = [string]::Join("`n", $lines) + $text.Substring($m.Index)
        $changes.Add('removed the start_lhm / stop_lhm settings (congee no longer uses LibreHardwareMonitor)')
    }
}

# --- no-sensors hint: congee's sensor service replaces LibreHardwareMonitor ------------------
$oldHint = 'Temps/fans: start LibreHardwareMonitor (as admin)'
if ($text.Contains($oldHint)) {
    $text = $text.Replace($oldHint, 'Temps/fans: no sensor data (see README)')
    $changes.Add('the no-sensors hint now points to the README instead of LibreHardwareMonitor')
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
