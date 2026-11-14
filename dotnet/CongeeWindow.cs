using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Congee;

/// <summary>Draws a prepared Drawing; the whole widget is one of these.</summary>
sealed class Surface : FrameworkElement
{
    public Drawing? Content;
    protected override void OnRender(DrawingContext dc)
    {
        if (Content != null) dc.DrawDrawing(Content);
    }
}

/// <summary>
/// Borderless, per-pixel-transparent WPF window pinned just above the desktop
/// wallpaper. Unlike the Python/Tk version there is no colour key, so text is
/// anti-aliased against whatever is behind it with no dark fringe.
/// </summary>
public sealed class CongeeWindow : Window
{
    readonly string configPath;
    readonly Stats stats = new();
    readonly Surface surface = new();
    readonly DispatcherTimer tickTimer = new(), pinTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly Dictionary<string, Brush> brushes = [];
    readonly Dictionary<string, (Typeface face, double size)> fonts = [];
    readonly Dictionary<string, (double ascent, double height)> fontMetrics = [];

    Dictionary<string, string> settings = new(Config.Defaults);
    List<Config.Token> tokens = [];
    Evaluator evaluator = null!;
    string? configError;
    DateTime mtime;
    IntPtr hwnd;
    string windowType = "desktop";
    (int x, int y)? userPos;
    (int x, int y, double w, double h)? geom;
    double dpiScale = 1.0;

    public CongeeWindow(string configPath)
    {
        this.configPath = Path.GetFullPath(configPath);
        Title = "congee";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000; Top = -10000; Width = 10; Height = 10;
        Content = surface;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(surface, TextFormattingMode.Display);
        RenderOptions.SetEdgeMode(surface, EdgeMode.Aliased);

        // One menu, shown from the tray icon and (when click_through is off) by right-clicking the widget
        var items = new List<TrayIcon.Item>
        {
            new("Hide widget", ToggleVisible, () => IsVisible ? "Hide widget" : "Show widget"),
            new("Edit config", () => Shell(this.configPath)),
            new("Open config folder", () => Shell(Path.GetDirectoryName(this.configPath)!)),
            new("Reload config", Reload),
            new("Reset position", () => { userPos = null; geom = null; Render(); }),
            new("Logging", ToggleLogging, Checked: () => Log.Enabled),
            new("", null),
            new("Exit congee", () => { Log.StopReason("Exit congee chosen from the menu"); Application.Current.Shutdown(); }),
        };
        var menu = new ContextMenu();
        var refresh = new List<Action>();  // labels and checkmarks that change, updated when the menu opens
        foreach (var it in items)
        {
            if (it.Action == null) { menu.Items.Add(new Separator()); continue; }
            var mi = new MenuItem { Header = it.Text };
            mi.Click += (_, _) => it.Action();
            menu.Items.Add(mi);
            refresh.Add(() =>
            {
                mi.Header = it.DynamicText?.Invoke() ?? it.Text;
                mi.IsChecked = it.Checked?.Invoke() == true;
            });
        }
        menu.Opened += (_, _) => refresh.ForEach(r => r());
        ContextMenu = menu;
        string name = Path.GetFileName(this.configPath);
        TrayIcon? tray = null;
        Loaded += (_, _) => tray ??= new TrayIcon(name == "congee.conf" ? "congee" : $"congee - {name}", items);
        Closed += (_, _) => tray?.Dispose();

        MouseLeftButtonDown += (_, e) =>
        {
            if (!Config.Yes(settings["draggable"]) || e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { return; }
            if (Native.GetWindowRect(hwnd, out var r)) userPos = (r.Left, r.Top);
            geom = null;
        };

        tickTimer.Tick += (_, _) => Tick();
        pinTimer.Tick += (_, _) => PinToDesktop();
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            ApplyWindowStyles();
        };
        DpiChanged += (_, e) => { dpiScale = e.NewDpi.DpiScaleX; geom = null; ResetCaches(); Render(); };
        Loaded += (_, _) =>
        {
            dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Reload();
        };
    }

    /// <summary>Flips the "logging" setting in the config file (so it sticks) and applies it.</summary>
    void ToggleLogging()
    {
        bool on = !Log.Enabled;
        try
        {
            Config.SetSetting(configPath, "logging", on ? "yes" : "no",
                "Write congee.log (starts, stops and crashes) to %APPDATA%\\congee: yes or no");
        }
        catch (Exception) { Log.SetEnabled(on); return; }  // config can't be saved: switch for this run only
        Reload();  // picks up the new setting
    }

    static void Shell(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    void ToggleVisible()
    {
        if (IsVisible) { Hide(); return; }
        Show();
        ApplyWindowStyles();
    }

    // ---- config -------------------------------------------------------------------------------
    void ResetCaches()
    {
        fonts.Clear();
        fontMetrics.Clear();
    }

    void Reload()
    {
        string text;
        try
        {
            (settings, text) = Config.Parse(configPath);
            configError = null;
            Log.SetEnabled(Config.Yes(settings["logging"]));
        }
        catch (Exception e)
        {
            settings = new(Config.Defaults);
            text = "";
            configError = "Config error: " + e.Message;
            Log.Once(configError);
        }
        try { mtime = File.GetLastWriteTimeUtc(configPath); } catch { }
        tokens = Config.Tokenize(text);
        stats.Sensors.Configure(settings["sensors_url"], Config.Num(settings, "update_interval"));
        ResetCaches();
        brushes.Clear();

        double scale = settings["dpi_scale"].Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? 1.0 : Config.Num(settings, "dpi_scale") / dpiScale;   // WPF units already follow the DPI
        evaluator = new Evaluator(stats, settings, scale, IsColor);

        Background = Config.Yes(settings["own_window_transparent"])
            ? Brushes.Transparent
            : MakeBrush(Config.NormColor(settings["own_window_colour"]),
                        Math.Clamp(Config.Num(settings, "own_window_argb_value"), 0, 255) / 255);
        windowType = settings["own_window_type"].Trim().ToLowerInvariant();
        Topmost = windowType is "override" or "topmost" or "panel" or "dock";

        geom = null;
        if (hwnd != IntPtr.Zero) ApplyWindowStyles();
        tickTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.1, Config.Num(settings, "update_interval")));
        tickTimer.Start();
        if (windowType == "desktop") pinTimer.Start(); else pinTimer.Stop();
        Tick();
    }

    // ---- win32 --------------------------------------------------------------------------------------
    void ApplyWindowStyles()
    {
        int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        ex = (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE) & ~Native.WS_EX_APPWINDOW;
        ex = Config.Yes(settings["click_through"]) ? ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED
                                                  : ex & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex);
        if (windowType == "desktop") PinToDesktop();
    }

    /// <summary>Keep the window under every visible app window but above the wallpaper.
    /// Invisible helper windows (e.g. LibreHardwareMonitor parks one right above the
    /// desktop) are skipped, so congee doesn't fight other apps over the exact slot.</summary>
    void PinToDesktop()
    {
        if (hwnd == IntPtr.Zero) return;
        IntPtr progman = Native.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return;
        IntPtr above = Native.GetWindow(progman, Native.GW_HWNDPREV);
        while (above != IntPtr.Zero && above != hwnd && !Native.IsWindowVisible(above))
            above = Native.GetWindow(above, Native.GW_HWNDPREV);
        if (above == hwnd) return;
        Native.SetWindowPos(hwnd, above == IntPtr.Zero ? Native.HWND_TOP : above, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // ---- fonts / colours ---------------------------------------------------------------------------------
    static double PtToDip(double pt) => pt * 96.0 / 72.0;

    (Typeface face, double size) Font(string spec)
    {
        if (fonts.TryGetValue(spec, out var f)) return f;
        string family = "Consolas";
        double size = PtToDip(10);
        var weight = FontWeights.Normal;
        var style = FontStyles.Normal;
        var parts = spec.Split(':').Select(p => p.Trim()).ToArray();
        var m = System.Text.RegularExpressions.Regex.Match(spec, @"^(.*\S)\s+(\d+(?:\.\d+)?)$");
        if (parts.Length == 1 && m.Success)
        {
            family = m.Groups[1].Value;
            size = PtToDip(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        }
        else
        {
            if (parts[0].Length > 0) family = parts[0];
            foreach (var p in parts.Skip(1).Select(x => x.ToLowerInvariant()))
            {
                if (p.StartsWith("size=") && double.TryParse(p[5..], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) size = PtToDip(s);
                else if (p.StartsWith("pixelsize=") && double.TryParse(p[10..], NumberStyles.Float, CultureInfo.InvariantCulture, out var px)) size = px;
                else if (p == "bold") weight = FontWeights.Bold;
                else if (p is "italic" or "oblique") style = FontStyles.Italic;
            }
        }
        var face = new Typeface(new FontFamily(family), style, weight, FontStretches.Normal);
        fonts[spec] = (face, size);
        var ft = MakeText("Xg", spec, Brushes.White);
        fontMetrics[spec] = (ft.Baseline, ft.Height);
        return fonts[spec];
    }

    (double ascent, double height) Metrics(string spec)
    {
        Font(spec);
        return fontMetrics[spec];
    }

    FormattedText MakeText(string s, string fontSpec, Brush brush)
    {
        var (face, size) = Font(fontSpec);
        return new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, dpiScale);
    }

    static bool IsColor(string c)
    {
        try { return ColorConverter.ConvertFromString(c) is Color; }
        catch { return false; }
    }

    static Color ParseColor(string c)
    {
        try { return ColorConverter.ConvertFromString(c) is Color col ? col : Colors.White; }
        catch { return Colors.White; }
    }

    Brush MakeBrush(string c, double opacity = 1.0)
    {
        string key = c + "|" + opacity.ToString(CultureInfo.InvariantCulture);
        if (brushes.TryGetValue(key, out var b)) return b;
        var col = ParseColor(c);
        col.A = (byte)Math.Round(255 * opacity);
        b = new SolidColorBrush(col);
        b.Freeze();
        return brushes[key] = b;
    }

    Brush Mix(string c1, string c2, double t)
    {
        t = Math.Round(Math.Clamp(t, 0, 1) * 64) / 64;  // quantize so the brush cache stays small
        string key = $"mix|{c1}|{c2}|{t}";
        if (brushes.TryGetValue(key, out var b)) return b;
        Color a = ParseColor(c1), z = ParseColor(c2);
        b = new SolidColorBrush(Color.FromRgb((byte)(a.R + (z.R - a.R) * t), (byte)(a.G + (z.G - a.G) * t), (byte)(a.B + (z.B - a.B) * t)));
        b.Freeze();
        return brushes[key] = b;
    }

    // ---- layout ---------------------------------------------------------------------------------------------
    const double MinFlex = 20;

    double SegWidth(Seg s, double flex) => s switch
    {
        TextSeg t => MakeText(t.S, t.Font, Brushes.White).WidthIncludingTrailingWhitespace,
        BarSeg b => b.W > 0 ? b.W : flex,
        GraphSeg g => g.W > 0 ? g.W : flex,
        HrSeg => flex,
        OffsetSeg o => o.X,
        _ => 0,
    };

    static bool IsFlex(Seg s) => s is HrSeg || s is BarSeg { W: <= 0 } || s is GraphSeg { W: <= 0 };

    enum GroupKind { Left, Center, Right }

    List<(GroupKind kind, List<(Seg seg, double x, double w)> items, double width)> LayoutLine(List<Seg> line, double flex)
    {
        var groups = new List<(GroupKind, List<Seg>)> { (GroupKind.Left, []) };
        foreach (var s in line)
        {
            if (s is AlignSeg a) groups.Add((a.Right ? GroupKind.Right : GroupKind.Center, []));
            else groups[^1].Item2.Add(s);
        }
        var placed = new List<(GroupKind, List<(Seg, double, double)>, double)>();
        foreach (var (kind, segs) in groups)
        {
            double x = 0;
            var items = new List<(Seg, double, double)>();
            foreach (var s in segs)
            {
                if (s is GotoSeg g)
                {
                    if (kind == GroupKind.Left) x = Math.Max(x, g.X);
                    continue;
                }
                double w = SegWidth(s, flex);
                items.Add((s, x, w));
                x += w;
            }
            placed.Add((kind, items, x));
        }
        return placed;
    }

    (double h, double asc, double desc) LineHeight(List<Seg> line)
    {
        double asc = 0, desc = 0, widget = 0;
        bool hasText = false;
        foreach (var s in line)
        {
            if (s is TextSeg t)
            {
                var m = Metrics(t.Font);
                asc = Math.Max(asc, m.ascent);
                desc = Math.Max(desc, m.height - m.ascent);
                hasText = true;
            }
            else if (s is BarSeg b) widget = Math.Max(widget, b.H + 2);
            else if (s is GraphSeg g) widget = Math.Max(widget, g.H + 2);
            else if (s is HrSeg hr) widget = Math.Max(widget, hr.H + 2);
        }
        if (!hasText)
        {
            var m = Metrics(settings["font"]);
            asc = m.ascent;
            desc = m.height - m.ascent;
        }
        return (Math.Max(asc + desc, widget), asc, desc);
    }

    // ---- tick / render ------------------------------------------------------------------------------------------
    void Tick()
    {
        try
        {
            if (File.GetLastWriteTimeUtc(configPath) != mtime) { Reload(); return; }
        }
        catch { }
        stats.Sample();
        Render();
    }

    void Render()
    {
        List<List<Seg>> lines;
        try
        {
            if (configError != null) throw new Exception(configError);
            lines = evaluator.Build(tokens);
        }
        catch (Exception e)
        {
            if (configError == null) Log.Once("display error: " + e);
            lines = [[new TextSeg("congee: " + e.Message, "#ff6060", settings.GetValueOrDefault("font", Config.Defaults["font"]))]];
        }

        var s = settings;
        double px(string k) => Config.Num(s, k) * (s["dpi_scale"].Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) ? 1 : Config.Num(s, "dpi_scale") / dpiScale);
        double pad = px("border_inner_margin");

        // pass 1: size
        double contentW = px("minimum_width");
        foreach (var line in lines) contentW = Math.Max(contentW, LayoutLine(line, MinFlex).Sum(g => g.width));
        var heights = lines.Select(LineHeight).ToList();
        double contentH = Math.Max(px("minimum_height"), heights.Sum(h => h.h));
        double W = Math.Ceiling(contentW + 2 * pad), H = Math.Ceiling(contentH + 2 * pad);

        // pass 2: draw
        var dg = new DrawingGroup();
        using (var dc = dg.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));  // fixes the drawing's bounds at 0,0
            Brush? shade = Config.Yes(s["draw_shades"]) ? MakeBrush(s["default_shade_color"]) : null;
            Brush? outline = Config.Yes(s["draw_outline"]) ? MakeBrush(s["default_outline_color"]) : null;
            bool borders = Config.Yes(s["draw_graph_borders"]);
            double y = pad;
            for (int li = 0; li < lines.Count; li++)
            {
                var line = lines[li];
                var (lh, asc, desc) = heights[li];
                int nflex = line.Count(IsFlex);
                double fixedTotal = LayoutLine(line, 0).Sum(g => g.width);
                double flex = nflex > 0 ? Math.Max(0, Math.Floor((contentW - fixedTotal) / nflex)) : 0;
                double textTop = y + Math.Floor((lh - (asc + desc)) / 2);

                foreach (var (kind, items, gw) in LayoutLine(line, flex))
                {
                    double gx = kind switch
                    {
                        GroupKind.Left => 0,
                        GroupKind.Center => Math.Floor((contentW - gw) / 2),
                        _ => contentW - gw,
                    };
                    foreach (var (seg, sx, w) in items)
                    {
                        double X = Math.Round(pad + gx + sx);
                        switch (seg)
                        {
                            case TextSeg t:
                            {
                                double ty = Math.Round(textTop + asc - Metrics(t.Font).ascent);
                                if (outline != null)
                                    foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                                        dc.DrawText(MakeText(t.S, t.Font, outline), new Point(X + dx, ty + dy));
                                if (shade != null) dc.DrawText(MakeText(t.S, t.Font, shade), new Point(X + 1, ty + 1));
                                dc.DrawText(MakeText(t.S, t.Font, MakeBrush(t.Color)), new Point(X, ty));
                                break;
                            }
                            case HrSeg hr:
                            {
                                double wy = Math.Round(y + lh / 2);
                                var pen = new Pen(MakeBrush(hr.Color), hr.H);
                                if (hr.Dash) pen.DashStyle = new DashStyle([2, 2], 0);
                                if (shade != null) dc.DrawLine(new Pen(shade, hr.H) { DashStyle = pen.DashStyle }, new Point(X + 1, wy + 1), new Point(X + w + 1, wy + 1));
                                dc.DrawLine(pen, new Point(X, wy), new Point(X + w, wy));
                                break;
                            }
                            case BarSeg b when w > 1:
                            {
                                double wy = Math.Round(y + (lh - b.H) / 2);
                                var col = MakeBrush(b.Color);
                                if (shade != null) dc.DrawRectangle(null, new Pen(shade, 1), new Rect(X + 1.5, wy + 1.5, w - 1, b.H - 1));
                                dc.DrawRectangle(null, new Pen(col, 1), new Rect(X + 0.5, wy + 0.5, w - 1, b.H - 1));
                                double fw = Math.Floor((w - 1) * b.Frac);
                                if (fw > 0) dc.DrawRectangle(col, null, new Rect(X, wy, fw + 1, b.H));
                                break;
                            }
                            case GraphSeg g when w > 2:
                            {
                                double wy = Math.Round(y + (lh - g.H) / 2), bottom = wy + g.H - 1;
                                var col = MakeBrush(g.Color);
                                if (borders)
                                {
                                    if (shade != null) dc.DrawRectangle(null, new Pen(shade, 1), new Rect(X + 1.5, wy + 1.5, w - 1, g.H - 1));
                                    dc.DrawRectangle(null, new Pen(col, 1), new Rect(X + 0.5, wy + 0.5, w - 1, g.H - 1));
                                }
                                int inner = (int)w - 2;
                                var vals = g.Vals.Length > inner ? g.Vals[^inner..] : g.Vals;
                                double start = X + 1 + inner - vals.Length;
                                for (int i = 0; i < vals.Length; i++)
                                {
                                    double v = Math.Clamp(vals[i], 0, 1), hpx = Math.Floor(v * (g.H - 2));
                                    if (hpx <= 0) continue;
                                    var fill = g.Color2 != null ? Mix(g.Color, g.Color2, v) : col;
                                    dc.DrawRectangle(fill, null, new Rect(start + i, bottom - hpx, 1, hpx));
                                }
                                break;
                            }
                        }
                    }
                }
                y += lh;
            }
        }
        dg.Freeze();
        surface.Content = dg;
        surface.Width = W;
        surface.Height = H;
        surface.InvalidateVisual();

        // position (physical pixels, so it lands right on any monitor/DPI)
        int x, yPos;
        var areas = Native.MonitorWorkAreas();
        int idx = (int)Config.Num(s, "monitor");
        var (work, monScale) = areas.Count > 0 ? areas[idx >= 0 && idx < areas.Count ? idx : 0] : (new Native.RECT { Right = 1920, Bottom = 1080 }, 1.0);
        int pw = (int)Math.Ceiling(W * monScale), ph = (int)Math.Ceiling(H * monScale);
        if (userPos is { } up) (x, yPos) = up;
        else
        {
            int gxp = (int)Math.Round(px("gap_x") * monScale), gyp = (int)Math.Round(px("gap_y") * monScale);
            var parts = s["alignment"].Split('_');
            string vert = parts[0], horiz = parts.Length > 1 ? parts[1] : "right";
            yPos = vert == "top" ? work.Top + gyp : vert == "bottom" ? work.Bottom - ph - gyp : work.Top + (work.Bottom - work.Top - ph) / 2 + gyp;
            x = horiz == "left" ? work.Left + gxp : horiz == "right" ? work.Right - pw - gxp : work.Left + (work.Right - work.Left - pw) / 2 + gxp;
        }
        if (geom is not { } gm || gm.x != x || gm.y != yPos || gm.w != W || gm.h != H)
        {
            Width = W;
            Height = H;
            if (hwnd != IntPtr.Zero)
                Native.SetWindowPos(hwnd, IntPtr.Zero, x, yPos, pw, ph, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            geom = (x, yPos, W, H);
        }
    }

    // ---- text mode (--once) ----------------------------------------------------------------------------------------
    public static string RenderText(List<List<Seg>> lines)
    {
        const string blocks = " ▁▂▃▄▅▆▇█";
        var sb = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            foreach (var s in line)
            {
                switch (s)
                {
                    case TextSeg t: sb.Append(t.S); break;
                    case BarSeg b:
                        int k = (int)Math.Round(b.Frac * 20);
                        sb.Append('[').Append('#', k).Append('-', 20 - k).Append(']');
                        break;
                    case GraphSeg g:
                        foreach (var v in g.Vals.TakeLast(20)) sb.Append(blocks[(int)(Math.Clamp(v, 0, 1) * 8)]);
                        break;
                    case HrSeg: sb.Append('-', 20); break;
                    case AlignSeg: sb.Append("  "); break;
                }
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }
}
