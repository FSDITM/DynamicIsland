using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DynamicIsland.Features;

/// <summary>Преобразования текста: раскладка, регистр, JSON, Base64, URL, транслит.</summary>
internal static class TextTools
{
    private const string En = "qwertyuiop[]asdfghjkl;'zxcvbnm,./`QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>?~@#$^&";
    private const string Ru = "йцукенгшщзхъфывапролджэячсмитьбю.ёЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ,Ё\"№;:?";

    private static readonly Dictionary<char, char> EnToRu = Build(En, Ru);
    private static readonly Dictionary<char, char> RuToEn = Build(Ru, En);

    private static Dictionary<char, char> Build(string from, string to)
    {
        var map = new Dictionary<char, char>();
        for (var i = 0; i < Math.Min(from.Length, to.Length); i++) map.TryAdd(from[i], to[i]);
        return map;
    }

    /// <summary>Текст набран не в той раскладке: переводит в другую сторону по преобладающим буквам.</summary>
    public static string FixLayout(string text)
    {
        var ru = text.Count(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё');
        var en = text.Count(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
        var map = ru > en ? RuToEn : EnToRu;
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) sb.Append(map.TryGetValue(c, out var m) ? m : c);
        return sb.ToString();
    }

    public static string Upper(string t) => t.ToUpper(CultureInfo.CurrentCulture);
    public static string Lower(string t) => t.ToLower(CultureInfo.CurrentCulture);

    public static string TitleCase(string t) =>
        CultureInfo.CurrentCulture.TextInfo.ToTitleCase(t.ToLower(CultureInfo.CurrentCulture));

    public static string SentenceCase(string text)
    {
        var sb = new StringBuilder(text.ToLower(CultureInfo.CurrentCulture));
        var start = true;
        for (var i = 0; i < sb.Length; i++)
        {
            if (start && char.IsLetter(sb[i])) { sb[i] = char.ToUpper(sb[i], CultureInfo.CurrentCulture); start = false; }
            else if (sb[i] is '.' or '!' or '?' or '\n') start = true;
        }
        return sb.ToString();
    }

    public static string InvertCase(string t) =>
        new(t.Select(c => char.IsUpper(c) ? char.ToLower(c) : char.ToUpper(c)).ToArray());

    private static readonly string[] RuLat =
    [
        "а:a", "б:b", "в:v", "г:g", "д:d", "е:e", "ё:yo", "ж:zh", "з:z", "и:i", "й:y", "к:k", "л:l",
        "м:m", "н:n", "о:o", "п:p", "р:r", "с:s", "т:t", "у:u", "ф:f", "х:kh", "ц:ts", "ч:ch",
        "ш:sh", "щ:shch", "ъ:", "ы:y", "ь:", "э:e", "ю:yu", "я:ya",
    ];

    public static string Translit(string text)
    {
        var latin = text.Count(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
        var cyr = text.Count(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё');
        return cyr >= latin ? ToLatin(text) : ToCyrillic(text);
    }

    private static string ToLatin(string text)
    {
        var map = RuLat.ToDictionary(p => p[0], p => p[2..]);
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            var lower = char.ToLowerInvariant(c);
            if (map.TryGetValue(lower, out var lat))
            {
                if (char.IsUpper(c) && lat.Length > 0)
                    lat = char.ToUpperInvariant(lat[0]) + lat[1..];
                sb.Append(lat);
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string ToCyrillic(string text)
    {
        // Длинные сочетания сначала, иначе «sh» превратится в «сх».
        var pairs = RuLat.Select(p => (Ru: p[0].ToString(), Lat: p[2..]))
                         .Where(p => p.Lat.Length > 0)
                         .OrderByDescending(p => p.Lat.Length).ToList();
        var sb = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var matched = false;
            foreach (var (ru, lat) in pairs)
            {
                if (i + lat.Length > text.Length) continue;
                if (string.Compare(text, i, lat, 0, lat.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
                sb.Append(char.IsUpper(text[i]) ? ru.ToUpperInvariant() : ru);
                i += lat.Length;
                matched = true;
                break;
            }
            if (!matched) sb.Append(text[i++]);
        }
        return sb.ToString();
    }

    public static string? JsonPretty(string text) => JsonWrite(text, true);
    public static string? JsonMinify(string text) => JsonWrite(text, false);

    private static string? JsonWrite(string text, bool indent)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = indent,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch { return null; }
    }

    public static string Base64Encode(string t) => Convert.ToBase64String(Encoding.UTF8.GetBytes(t));

    public static string? Base64Decode(string t)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(t.Trim())); }
        catch { return null; }
    }

    public static string UrlEncode(string t) => Uri.EscapeDataString(t);

    public static string? UrlDecode(string t)
    {
        try { return Uri.UnescapeDataString(t); }
        catch { return null; }
    }

    public static string CleanSpaces(string t)
    {
        var lines = t.Replace("\r", "").Split('\n').Select(l => System.Text.RegularExpressions.Regex.Replace(l.Trim(), @"[ \t]{2,}", " "));
        return string.Join("\n", lines);
    }

    public static string OneLine(string t) =>
        System.Text.RegularExpressions.Regex.Replace(t.Replace("\r", " ").Replace('\n', ' '), @"\s{2,}", " ").Trim();

    /// <summary>Склонение по числу: 1 слово, 2 слова, 5 слов.</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        var m100 = n % 100;
        var m10 = n % 10;
        var word = m100 is >= 11 and <= 14 ? many : m10 == 1 ? one : m10 is >= 2 and <= 4 ? few : many;
        return $"{n} {word}";
    }

    public static string Stats(string t)
    {
        var words = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var lines = t.Length == 0 ? 0 : t.Replace("\r", "").Split('\n').Length;
        return $"{t.Length} симв. · {Plural(words, "слово", "слова", "слов")} · {Plural(lines, "строка", "строки", "строк")}";
    }
}
