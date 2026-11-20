using System.Globalization;
using System.Text;

namespace XRStudy.Task1
{
    /// <summary>RFC 4180 style CSV with invariant number formatting. Missing values are empty, never 0.</summary>
    public static class Csv
    {
        static readonly CultureInfo k_Inv = CultureInfo.InvariantCulture;

        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            bool quote = s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0
                         || s[0] == ' ' || s[s.Length - 1] == ' ';
            return quote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        public static string Row(params string[] fields)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Escape(fields[i]));
            }
            return sb.ToString();
        }

        public static string T(double? seconds) => seconds.HasValue ? seconds.Value.ToString("F6", k_Inv) : "";
        public static string F(float v) => v.ToString("0.######", k_Inv);
        public static string F(double v) => v.ToString("0.######", k_Inv);
        public static string F(float? v) => v.HasValue ? F(v.Value) : "";
        public static string I(int v) => v.ToString(k_Inv);
        public static string I(int? v) => v.HasValue ? v.Value.ToString(k_Inv) : "";
        public static string L(long v) => v.ToString(k_Inv);
        public static string B(bool v) => v ? "1" : "0";
    }
}
