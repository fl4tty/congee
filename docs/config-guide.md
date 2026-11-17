# Editing your congee config

A step-by-step guide to changing what congee shows and how it looks. For the full list of every setting and
variable, see [Settings](https://github.com/fl4tty/congee#settings) and [Variables](https://github.com/fl4tty/congee#variables) in the README.

- [Before you start](#before-you-start)
- [How the file is laid out](#how-the-file-is-laid-out)
- [Variables](#variables)
- [Lines, and how line breaks work](#lines-and-how-line-breaks-work)
- [Layout: columns, alignment, lines](#layout-columns-alignment-lines)
- [Colours and fonts](#colours-and-fonts)
- [Bars and graphs](#bars-and-graphs)
- [Showing things only sometimes](#showing-things-only-sometimes)
- [Finding the right names](#finding-the-right-names)
- [Running commands](#running-commands)
- [Recipes](#recipes)
- [When something goes wrong](#when-something-goes-wrong)

## Before you start

**Open it:** right-click the tray icon → **Edit config**. That always opens the file congee is using, normally
`%APPDATA%\congee\congee.conf`. Any text editor works.

**Save to see the change.** congee watches the file and redraws within a second. There's nothing to restart (except the
`rendering` setting, which needs one).

**Keep a copy first.** Before a big change, copy `congee.conf` to something like `congee.conf.bak-mine`. Setup does the
same when it updates your config, as `congee.conf.<date>.bak`. To undo, copy a backup back over `congee.conf`.

**Save as UTF-8.** Notepad does this by default. Another encoding turns `°` and other non-English characters into
garbage.

**Try changes without touching your widget.** Make a copy, edit the copy, and print what it would show as plain text:

```
congee.exe -c C:\path\to\test.conf --once
```

(Python edition: `.venv\Scripts\python congee.py -c test.conf --once`.) Bars and graphs don't show in this text
output, and `execi` commands may not have finished yet, but everything else does.

## How the file is laid out

The file has two parts, split by a line that says just `TEXT`:

```
# settings: one "name value" per line
alignment top_right
gap_x 24
font Consolas:size=10
color1 88c0d0

TEXT
${color1}CPU${color} ${cpu}%
RAM ${mem} / ${memmax}
```

- **Above `TEXT`: settings.** One per line: the name, a space, the value. Lines starting with `#` are comments and
  are ignored. A setting you leave out uses its default ([list](https://github.com/fl4tty/congee#settings)). A setting with no value
  counts as `yes`.
- **Below `TEXT`: the template.** This is what the widget shows, line for line. Plain text is shown as it is, and
  `${variables}` are replaced with live values.
- **There are no comments below `TEXT`.** A `#` there is shown as a `#`. To hide a line temporarily, cut it and paste
  it above `TEXT` behind a `#`, or wrap it in `${if_existing C:\nothing-here}` … `${endif}`, which is never true.

conky's newer Lua format (`conky.config = { … }` and `conky.text = [[ … ]]`) works too, so you can paste in most conky
configs.

## Variables

Two ways to write one:

| Form | Use it for | Example |
|---|---|---|
| `$name` | Variables with no options | `$uptime`, `$mem` |
| `${name options}` | Variables with options, or when text follows immediately | `${cpu cpu1}`, `${time %H:%M}`, `${mem}MB` |

- `$$` shows a dollar sign.
- Options are separated by spaces. Everything after the variable's name, up to the `}`, is its options, so
  `${addr PIA OpenVPN WinTUN Adapter}` works even though the name has spaces in it.
- **You can't put one variable inside another.** `${execi 60 echo ${addr}}` doesn't work: the first `}` ends the
  variable. The same goes for any `}` in a command.
- **A misspelled or unknown variable is shown exactly as written**, e.g. `${cpu_tmp}` appears on the widget as
  `${cpu_tmp}`. That's the quickest way to spot a typo.
- A value that can't be read right now (a sensor that's missing, a drive that isn't there) shows `N/A`.

## Lines, and how line breaks work

Every line break below `TEXT` is a line break on the widget. That matters most with `${if_…}` (see
[below](#showing-things-only-sometimes)): if a line is hidden but its line break isn't, you get an empty line.

The trick the default config uses everywhere: **start the hidden part on the line it hides, and put `${endif}` at
the start of the next line**, so the line break is inside the condition:

```
IP${alignr}${addr Ethernet}
${if_up PIA OpenVPN WinTUN Adapter}VPN${alignr}${addr PIA OpenVPN WinTUN Adapter}
${endif}Down ${downspeed}/s${alignr}Up ${upspeed}/s
```

With the VPN off, the middle line disappears completely. Written like this instead:

```
${if_up PIA OpenVPN WinTUN Adapter}VPN${alignr}${addr PIA OpenVPN WinTUN Adapter}${endif}
```

it leaves an empty line when the VPN is off, because the line break after `${endif}` is always shown.

## Layout: columns, alignment, lines

| Variable | What it does | Example |
|---|---|---|
| `${alignr}` | Pushes the rest of the line to the right edge | `RAM${alignr}${memperc}%` |
| `${alignc}` | Centres the rest of the line | `${alignc}${time %A}` |
| `${goto X}` | Jumps to X pixels from the left edge, for columns | `CPU${goto 120}${cpu_temp}°C` |
| `${offset X}` | Moves right by X pixels from where it is | `A${offset 20}B` |
| `${hr}`, `${hr 2}` | A horizontal line across the rest of the line, optionally 2 px thick | `${color1}CPU ${hr 1}${color}` |
| `${stippled_hr}` | A dotted horizontal line | |

Use `${goto}` with the same number on several lines to line up a column, like the temperature list in the default
config. Pixel values follow your display scaling (`dpi_scale auto`), so 120 is 120 at 100% and 180 at 150%.

**Widget size and place** are settings: `alignment` (which corner or edge, e.g. `top_left`, `bottom_right`,
`middle_middle`), `gap_x` / `gap_y` (distance from that edge), `monitor` (0 = main screen, 1, 2… = the others),
`minimum_width` (wider widget), `border_inner_margin` (padding). You can also drag the widget with the mouse; tray
menu → **Reset position** puts it back where the settings say.

## Colours and fonts

**Colours.** Give up to ten colours names in the settings, then switch between them in the template:

```
default_color d8dee9
color1 88c0d0
color4 bf616a

TEXT
${color1}NETWORK${color}       <- color1, then back to the default
${color4}Warning${color}
${color ffcc00}Any colour on the spot${color}
```

Colours are hex (`88c0d0` or `#88c0d0`) or names (`white`, `red`, `orange`…). `${color}` with nothing after it goes
back to `default_color`. A colour lasts until the next `${color…}`, even across lines.

**Fonts.** `font` in the settings is the default; `${font …}` changes it until the next `${font}`:

```
font Consolas:size=10

TEXT
${font Segoe UI:bold:size=26}${time %H:%M}${font}
```

The format is `Family:size=N`, plus `:bold` and/or `:italic` if you want them. The family is the name Windows shows in
Settings → Personalisation → Fonts. Monospaced fonts (Consolas, Cascadia Mono) keep columns tidy.

Other look settings: `draw_shades` (a 1-pixel shadow that helps on bright wallpapers), `draw_outline`, `uppercase`,
and a background with `own_window_transparent no`, `own_window_colour 101418` and `own_window_argb_value 180`
(0 = invisible, 255 = solid).

## Bars and graphs

Any variable ending in `bar` or `graph` draws one. They take an optional size, `height,width`, in pixels:

```
${cpubar}                  default height, as wide as the rest of the line
${cpubar 6,120}            6 px tall, 120 px wide
${cpugraph 30,0}           30 px tall, full width (0 = fill the line)
${cpugraph cpu0 30,0 a3be8c bf616a}    two colours: low values green, high values red
```

For a bar on the right with a label on the left: `Usage ${cpu}%${alignr}${cpubar 6,180}`.

A graph scrolls left as new values come in, one per update. It starts empty when congee starts (saving the config
keeps it).

## Showing things only sometimes

Wrap lines in a condition and they only show when it's true:

```
${if_running Discord}Discord is open
${endif}
```

Add `${else}` to show something different otherwise:

```
${if_battery}Battery ${battery_percent}%${else}On mains power${endif}
```

| Condition | True when… |
|---|---|
| `${if_up NAME}` | The network adapter NAME is connected ([finding names](#finding-the-right-names)) |
| `${if_running NAME}` | A program called NAME is running (`Discord`, `obs64`; `.exe` is optional) |
| `${if_existing PATH}` | A file or folder exists, e.g. `${if_existing Z:\}` |
| `${if_mounted PATH}` | Same as `if_existing` (conky's name for it) |
| `${if_battery}` | The PC has a battery |
| `${if_sensors}` | The sensor service is giving data |
| `${if_gpu}` | The sensor service found a GPU |
| `${if_gpu_driver}` | Windows reports a GPU driver |
| `${if_sensor TYPE NAME}` | A sensor exists, e.g. `${if_sensor fan Fan #3}` |
| `${if_gpu_sensor TYPE NAME}` | The chosen GPU has that sensor |

- Every `${if_…}` needs its own `${endif}`. **If one is missing, everything after it disappears**, which is the
  usual cause of "half my widget vanished".
- Conditions can be nested: `${if_sensors}${if_gpu}…${endif}${endif}`.
- An unknown condition (a typo like `${if_runing …}`) counts as false, so its lines just don't show.
- Variables in a hidden part aren't worked out at all, so a slow command inside a false condition doesn't run.

## Finding the right names

Several variables need the exact name of something on your PC.

**Network adapters** (`addr`, `if_up`, `downspeed`…): run

```
congee.exe --interfaces
```

It lists every adapter, whether it's up, and its address. Names must match exactly, spaces included. Some VPNs add
their adapter only while connected, so run it with the VPN on. Without a name, `${addr}` shows the first connected
adapter, which may be the VPN while it's on; give the name to always get the same one.

**Sensors** (`temp`, `fan`, `sensor`, `if_sensor`): run `congee.exe --sensors`. It lists every sensor with its type,
current value and path:

```
temperature        45.0 °C   AMD Ryzen 9 7950X3D/Temperatures/Core (Tctl/Tdie)
fan              1271 RPM    Gigabyte X870E AORUS ELITE WIFI7/ITE IT8696E/Fans/Fan #1
```

Use the name at the end (`${temp Core (Tctl/Tdie)}`, `${fan Fan #1}`) or any part of the path that's unique
(`${temp Tctl}`). See [Sensors in your config](https://github.com/fl4tty/congee#sensors-in-your-config) in the README for more.

**Drives:** `${for_each_drive}` … `${endfor}` repeats its lines for every drive letter. For just one drive, use its
letter: `${fs_used D:\}`, `${fs_bar 6,0 D:\}`, `${diskio D:}`.

**Programs** (`if_running`, `top`): the name in Task Manager's Details tab, with or without `.exe`.

**Time formats** (`${time FORMAT}`): `%H:%M` → 14:05, `%I:%M %p` → 02:05 PM, `%A, %d %B %Y` → Monday, 16 November
2026, `%a %d %b` → Mon 16 Nov, `%F` → 2026-11-16, `%T` → 14:05:09, `%j` → day of the year. Anything else in the
format is shown as it is.

## Running commands

`${exec COMMAND}` and `${execi SECONDS COMMAND}` show the output of a command:

```
${execi 3600 powershell -NoProfile -Command "(Get-Date).DayOfYear"}
${execi 60 ver}
```

- **Use `execi`, not `exec`.** `exec` runs the command on every update (every second); `execi 60` runs it at most
  once a minute and shows the last result in between. Pick the longest interval that's still useful.
- Commands run through `cmd.exe` in the background, so a slow one never freezes the widget. Until the first run
  finishes, the variable shows nothing. A command is stopped after 30 seconds.
- `%APPDATA%`, `%USERPROFILE%` and other `%VARIABLES%` work. Put paths with spaces in double quotes.
- A command can't contain `}` (it would end the variable). For anything longer, put it in a `.cmd` or `.ps1` file
  next to your config and run that:

  ```
  ${execi 300 "%APPDATA%\congee\public-ip.cmd"}
  ${execi 300 powershell -NoProfile -ExecutionPolicy Bypass -File "%APPDATA%\congee\my-script.ps1"}
  ```
- Only the command's normal output is shown, not its errors. If it prints several lines, the widget shows several
  lines.
- `powershell` takes about half a second to start. For something you check every few seconds, a `.cmd` file or a
  small program is lighter.

## Recipes

**VPN address, only while it's connected** (adapter name from `--interfaces` with the VPN on):

```
IP${alignr}${addr Ethernet}
${if_up PIA OpenVPN WinTUN Adapter}VPN${alignr}${addr PIA OpenVPN WinTUN Adapter}
${endif}
```

**Public IP address** (what websites see), checked every 5 minutes with a lookup service:

```
Public IP${alignr}${execi 300 curl.exe -s --max-time 5 https://icanhazip.com}
```

It needs the internet and sends a request to icanhazip.com each time. While a VPN is on, this shows the VPN's
address, not yours.

**A line only while a program is open:**

```
${if_running obs64}${color4}OBS is running${color}
${endif}
```

**One drive instead of all of them:**

```
D: ${fs_used D:\} / ${fs_size D:\}${alignr}${fs_used_perc D:\}%
${fs_bar 6,0 D:\}
```

**Top 3 programs by memory:**

```
${top_mem name 1}${alignr}${top_mem mem_res 1}
${top_mem name 2}${alignr}${top_mem mem_res 2}
${top_mem name 3}${alignr}${top_mem mem_res 3}
```

**A specific sensor with a bar:**

```
Water${goto 120}${temp Water}°C${alignr}${tempbar 6,120 Water}
```

**Clock in 12-hour time:** `${time %I:%M %p}`

**NVIDIA GPU stats without the sensor service:**

```
${execi 2 nvidia-smi --query-gpu=utilization.gpu,temperature.gpu --format=csv,noheader}
```

## When something goes wrong

| You see | Likely cause |
|---|---|
| `${something}` shown as text | The variable's name is misspelled, or it isn't one congee knows ([list](https://github.com/fl4tty/congee#variables)) |
| Everything after some point is missing | An `${if_…}` without its `${endif}` |
| An empty line where a hidden line was | The line break is outside the condition: move `${endif}` to the start of the next line ([why](#lines-and-how-line-breaks-work)) |
| `N/A` | The thing isn't there: a sensor, drive or adapter name that doesn't match (check `--sensors` / `--interfaces`), or the sensor service isn't running |
| `No address` | That adapter isn't connected, or the name doesn't match |
| An `execi` shows nothing | It hasn't finished its first run yet, or it failed. Run the command in a Command Prompt to see its error |
| Garbage instead of `°` | The file was saved in an encoding other than UTF-8 |
| A red error in the widget | congee couldn't read the file at all (e.g. it's open and locked by another program). The message says what's wrong |
| Widget in the wrong place, or off screen | Tray menu → **Reset position**, then check `alignment`, `gap_x`, `gap_y` and `monitor` |

Still stuck? Copy a backup back over `congee.conf`, or delete `congee.conf` and run `congee.exe --create-config` to
start again from the default.
