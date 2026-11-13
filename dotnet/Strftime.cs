using System.Globalization;
using System.Text;

namespace Congee;

/// <summary>C strftime() formatting, so ${time %H:%M} configs work unchanged.</summary>
public static class Strftime
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Format(DateTime t, string fmt)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < fmt.Length; i++)
        {
            char c = fmt[i];
            if (c != '%' || i + 1 >= fmt.Length) { sb.Append(c); continue; }
            char f = fmt[++i];
            // %-d / %#d style "no padding" flags
            bool noPad = false;
            if ((f == '-' || f == '#') && i + 1 < fmt.Length) { noPad = true; f = fmt[++i]; }
            string P(int v) => noPad ? v.ToString(Inv) : v.ToString("00", Inv);
            sb.Append(f switch
            {
                'a' => t.ToString("ddd", Inv),
                'A' => t.ToString("dddd", Inv),
                'b' or 'h' => t.ToString("MMM", Inv),
                'B' => t.ToString("MMMM", Inv),
                'c' => t.ToString("ddd MMM d HH:mm:ss yyyy", Inv),
                'd' => P(t.Day),
                'e' => t.Day.ToString(Inv).PadLeft(2),
                'D' => t.ToString("MM/dd/yy", Inv),
                'F' => t.ToString("yyyy-MM-dd", Inv),
                'H' => P(t.Hour),
                'I' => P(t.Hour % 12 == 0 ? 12 : t.Hour % 12),
                'j' => noPad ? t.DayOfYear.ToString(Inv) : t.DayOfYear.ToString("000", Inv),
                'm' => P(t.Month),
                'M' => P(t.Minute),
                'n' => "\n",
                'p' => t.Hour < 12 ? "AM" : "PM",
                'r' => t.ToString("hh:mm:ss tt", Inv),
                'R' => t.ToString("HH:mm", Inv),
                'S' => P(t.Second),
                't' => "\t",
                'T' => t.ToString("HH:mm:ss", Inv),
                'u' => (((int)t.DayOfWeek + 6) % 7 + 1).ToString(Inv),
                'w' => ((int)t.DayOfWeek).ToString(Inv),
                'V' => ISOWeek.GetWeekOfYear(t).ToString("00", Inv),
                'x' => t.ToString("MM/dd/yy", Inv),
                'X' => t.ToString("HH:mm:ss", Inv),
                'y' => t.ToString("yy", Inv),
                'Y' => t.Year.ToString(Inv),
                'z' => t.ToString("zzz", Inv).Replace(":", ""),
                'Z' => TimeZoneInfo.Local.IsDaylightSavingTime(t) ? TimeZoneInfo.Local.DaylightName : TimeZoneInfo.Local.StandardName,
                '%' => "%",
                _ => "%" + f,
            });
        }
        return sb.ToString();
    }
}
