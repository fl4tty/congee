using System.Text;
using System.Text.RegularExpressions;

namespace Congee;

/// <summary>Config file discovery, parsing (legacy and Lua conky formats) and tokenizing.</summary>
public static class Config
{
    public static readonly Dictionary<string, string> Defaults = new()
    {
        ["alignment"] = "top_right",
        ["gap_x"] = "20",
        ["gap_y"] = "40",
        ["minimum_width"] = "0",
        ["minimum_height"] = "0",
        ["update_interval"] = "1.0",
        ["font"] = "Consolas:size=10",
        ["default_color"] = "white",
        ["default_shade_color"] = "black",
        ["draw_shades"] = "yes",
        ["default_outline_color"] = "black",
        ["draw_outline"] = "no",
        ["draw_graph_borders"] = "yes",
        ["own_window_type"] = "desktop",
        ["own_window_transparent"] = "yes",
        ["own_window_colour"] = "#101418",
        ["own_window_argb_value"] = "200",
        ["border_inner_margin"] = "8",
        ["default_bar_height"] = "6",
        ["default_graph_height"] = "24",
        ["click_through"] = "no",
        ["draggable"] = "yes",
        ["monitor"] = "0",
        ["uppercase"] = "no",
        ["dpi_scale"] = "auto",
        ["sensors_url"] = "http://localhost:8085/data.json",
        ["temp_bar_max"] = "100",          // °C that count as a full tempbar/tempgraph
        ["temperature_unit"] = "celsius",  // or fahrenheit: how temperature values are shown
        ["gpu"] = "auto",
    };

    static readonly Dictionary<string, string> AlignAliases = new()
    {
        ["tl"] = "top_left", ["tr"] = "top_right", ["tm"] = "top_middle",
        ["bl"] = "bottom_left", ["br"] = "bottom_right", ["bm"] = "bottom_middle",
        ["ml"] = "middle_left", ["mr"] = "middle_right", ["mm"] = "middle_middle",
    };

    public static string AppDir => AppContext.BaseDirectory;
    public static string BundledConfig => Path.Combine(AppDir, "congee.conf");

    public static string UserConfig => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "congee", "congee.conf");

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Searched in order when no -c is given (same list as the Python version).</summary>
    public static string[] SearchPaths =>
    [
        UserConfig,
        Path.Combine(Home, ".config", "congee", "congee.conf"),
        Path.Combine(Home, ".congeerc"),
        BundledConfig,
    ];

    public static string Find() => SearchPaths.FirstOrDefault(File.Exists) ?? BundledConfig;

    public static (Dictionary<string, string> settings, string text) Parse(string path)
    {
        // UTF-8 with or without BOM
        string src = File.ReadAllText(path, new UTF8Encoding(false));
        var settings = new Dictionary<string, string>(Defaults);
        string text = "";

        if (src.Contains("conky.text"))
        {
            var m = Regex.Match(src, @"conky\.config\s*=\s*\{(.*?)\}", RegexOptions.Singleline);
            if (m.Success)
            {
                string body = Regex.Replace(m.Groups[1].Value, @"--[^\n]*", "");
                foreach (Match kv in Regex.Matches(body, @"(\w+)\s*=\s*(""[^""]*""|'[^']*'|[^,\n]+)"))
                {
                    string v = kv.Groups[2].Value.Trim().Trim('"', '\'');
                    settings[kv.Groups[1].Value] = v == "true" ? "yes" : v == "false" ? "no" : v;
                }
            }
            var t = Regex.Match(src, @"conky\.text\s*=\s*\[(=*)\[\r?\n?(.*?)\]\1\]", RegexOptions.Singleline);
            text = t.Success ? t.Groups[2].Value : "";
        }
        else
        {
            var lines = src.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i].Trim();
                if (s == "TEXT")
                {
                    text = string.Join("\n", lines.Skip(i + 1));
                    break;
                }
                if (s.Length == 0 || s.StartsWith('#')) continue;
                var parts = s.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                settings[parts[0]] = parts.Length > 1 ? parts[1].Trim() : "yes";
            }
        }

        string a = settings["alignment"].Trim().ToLowerInvariant();
        settings["alignment"] = AlignAliases.GetValueOrDefault(a, a);
        return (settings, text.Replace("\r\n", "\n").TrimEnd('\n'));
    }

    // ---- tokens ---------------------------------------------------------------
    public record Token(bool IsLiteral, string Text, string Name = "", string Args = "");

    static readonly Regex TokenRe = new(@"\$\$|\$\{([^}]*)\}|\$([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    public static List<Token> Tokenize(string text)
    {
        var outp = new List<Token>();
        int pos = 0;
        foreach (Match m in TokenRe.Matches(text))
        {
            if (m.Index > pos) outp.Add(new Token(true, text[pos..m.Index]));
            if (m.Value == "$$") outp.Add(new Token(true, "$"));
            else if (m.Groups[1].Success)
            {
                string inner = m.Groups[1].Value.Trim();
                int sp = inner.IndexOf(' ');
                string name = sp < 0 ? inner : inner[..sp];
                string args = sp < 0 ? "" : inner[(sp + 1)..].Trim();
                outp.Add(new Token(false, m.Value, name, args));
            }
            else outp.Add(new Token(false, m.Value, m.Groups[2].Value, ""));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) outp.Add(new Token(true, text[pos..]));
        return outp;
    }

    /// <summary>Whitespace split that keeps "quoted strings" together (quotes removed).</summary>
    public static List<string> SplitArgs(string args)
    {
        var res = new List<string>();
        var cur = new StringBuilder();
        char quote = '\0';
        foreach (char c in args)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0'; else cur.Append(c);
            }
            else if (c is '"' or '\'') quote = c;
            else if (char.IsWhiteSpace(c))
            {
                if (cur.Length > 0) { res.Add(cur.ToString()); cur.Clear(); }
            }
            else cur.Append(c);
        }
        if (cur.Length > 0) res.Add(cur.ToString());
        return res;
    }

    // ---- small helpers ------------------------------------------------------------
    public static bool Yes(string? v) => v?.Trim().ToLowerInvariant() is "yes" or "true" or "1" or "on";

    public static double Num(Dictionary<string, string> s, string key)
    {
        string v = s.GetValueOrDefault(key) ?? Defaults.GetValueOrDefault(key) ?? "0";
        return double.TryParse(v, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    static readonly Regex Hex6 = new("^#?[0-9a-fA-F]{6}$");
    public static bool IsHex6(string c) => Hex6.IsMatch(c.Trim());

    public static string NormColor(string c)
    {
        c = c.Trim();
        return IsHex6(c) && !c.StartsWith('#') ? "#" + c : c;
    }

    public static string FmtBytes(double n)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        foreach (var u in units)
        {
            if (n < 1024 || u == "TiB")
                return u == "B" ? $"{n:0}{u}" : $"{n:0.0}{u}";
            n /= 1024;
        }
        return $"{n:0.0}TiB";
    }

    public static string FmtDuration(double seconds, bool shortForm = false)
    {
        long sec = (long)seconds;
        long d = sec / 86400, h = sec % 86400 / 3600, m = sec % 3600 / 60, s = sec % 60;
        if (shortForm) return d > 0 ? $"{d}d {h}h" : $"{h}h {m}m";
        return (d > 0 ? $"{d}d " : "") + $"{h}h {m}m" + (d > 0 ? "" : $" {s}s");
    }
}
