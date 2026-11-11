using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland.Features;

/// <summary>Перевод единиц и валют: «10 км в мили», «100 usd в rub», «20 c в f».</summary>
internal static class Units
{
    private sealed record Unit(string Group, double Factor, string[] Names, string Title);

    // Фактор — сколько базовых единиц группы в одной единице.
    private static readonly Unit[] All =
    [
        new("длина", 1, ["м", "метр", "метров", "метра", "m", "meter", "meters"], "м"),
        new("длина", 1000, ["км", "километр", "километров", "километра", "km", "kilometer", "kilometers"], "км"),
        new("длина", 0.01, ["см", "сантиметр", "сантиметров", "cm"], "см"),
        new("длина", 0.001, ["мм", "миллиметр", "миллиметров", "mm"], "мм"),
        new("длина", 1609.344, ["миля", "мили", "миль", "mi", "mile", "miles"], "миль"),
        new("длина", 0.3048, ["фут", "футов", "фута", "ft", "feet", "foot"], "фут"),
        new("длина", 0.0254, ["дюйм", "дюймов", "дюйма", "in", "inch", "inches"], "дюйм"),
        new("длина", 0.9144, ["ярд", "ярдов", "yd", "yard", "yards"], "ярд"),

        new("масса", 1, ["кг", "килограмм", "килограммов", "kg"], "кг"),
        new("масса", 0.001, ["г", "грамм", "граммов", "гр", "g"], "г"),
        new("масса", 1000, ["т", "тонна", "тонн", "тонны"], "т"),
        new("масса", 0.45359237, ["фунт", "фунтов", "lb", "lbs", "pound", "pounds"], "фунт"),
        new("масса", 0.0283495, ["унция", "унций", "oz", "ounce", "ounces"], "унций"),

        new("объём", 1, ["л", "литр", "литров", "литра", "l", "liter", "liters"], "л"),
        new("объём", 0.001, ["мл", "миллилитр", "миллилитров", "ml"], "мл"),
        new("объём", 3.785411784, ["галлон", "галлонов", "gal", "gallon", "gallons"], "галлон"),

        new("данные", 1, ["б", "байт", "байтов", "byte", "bytes"], "Б"),
        new("данные", 1024, ["кб", "kb", "kib"], "КБ"),
        new("данные", 1048576, ["мб", "mb", "mib"], "МБ"),
        new("данные", 1073741824, ["гб", "gb", "gib"], "ГБ"),
        new("данные", 1099511627776, ["тб", "tb", "tib"], "ТБ"),

        new("время", 1, ["сек", "секунд", "секунды", "с", "s", "sec", "second", "seconds"], "сек"),
        new("время", 60, ["мин", "минут", "минуты", "min", "minute", "minutes"], "мин"),
        new("время", 3600, ["час", "часов", "часа", "ч", "h", "hour", "hours"], "ч"),
        new("время", 86400, ["день", "дней", "дня", "d", "day", "days"], "дн."),
        new("время", 604800, ["неделя", "недель", "недели", "week", "weeks"], "нед."),

        new("скорость", 1, ["мс", "м/с", "m/s"], "м/с"),
        new("скорость", 0.277777778, ["км/ч", "kmh", "km/h", "кмч"], "км/ч"),
        new("скорость", 0.44704, ["миль/ч", "mph"], "миль/ч"),

        new("площадь", 1, ["м2", "м²", "m2"], "м²"),
        new("площадь", 10000, ["га", "гектар", "гектаров", "ha"], "га"),
        new("площадь", 1000000, ["км2", "км²", "km2"], "км²"),
    ];

    private static readonly string[] Temperature = ["c", "с", "f", "k", "°c", "°f", "цельсий", "фаренгейт", "кельвин"];

    private static readonly Regex Pattern = new(
        @"^\s*(?<n>-?\d+(?:[.,]\d+)?)\s*(?<from>[^\s\d][^\s]*)\s+(?:в|to|in|->|=>|→)\s+(?<to>[^\s]+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static async Task<string?> TryConvertAsync(string input)
    {
        var m = Pattern.Match(input);
        if (!m.Success) return null;

        if (!double.TryParse(m.Groups["n"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return null;
        var from = m.Groups["from"].Value.ToLowerInvariant().Trim('.');
        var to = m.Groups["to"].Value.ToLowerInvariant().Trim('.');

        var temp = TryTemperature(n, from, to);
        if (temp is not null) return temp;

        var a = All.FirstOrDefault(u => u.Names.Contains(from));
        var b = All.FirstOrDefault(u => u.Names.Contains(to));
        if (a is not null && b is not null && a.Group == b.Group)
        {
            var value = n * a.Factor / b.Factor;
            return $"{Calc.Format(Math.Round(value, 6))} {b.Title}";
        }

        return await TryCurrencyAsync(n, from, to);
    }

    private static string? TryTemperature(double n, string from, string to)
    {
        static string? Kind(string s) => s switch
        {
            "c" or "с" or "°c" or "цельсий" => "C",
            "f" or "°f" or "фаренгейт" => "F",
            "k" or "кельвин" => "K",
            _ => null,
        };

        var f = Kind(from);
        var t = Kind(to);
        if (f is null || t is null || f == t) return null;

        var celsius = f switch { "F" => (n - 32) * 5 / 9, "K" => n - 273.15, _ => n };
        var result = t switch { "F" => celsius * 9 / 5 + 32, "K" => celsius + 273.15, _ => celsius };
        return $"{Math.Round(result, 2).ToString("0.##", CultureInfo.CurrentCulture)} °{t}";
    }

    // ---------- Валюты: курсы ЦБ РФ ----------

    private static readonly Dictionary<string, string> CurrencyAliases = new()
    {
        ["руб"] = "RUB", ["рублей"] = "RUB", ["рубля"] = "RUB", ["рубль"] = "RUB", ["₽"] = "RUB", ["rub"] = "RUB",
        ["usd"] = "USD", ["доллар"] = "USD", ["долларов"] = "USD", ["доллара"] = "USD", ["$"] = "USD",
        ["eur"] = "EUR", ["евро"] = "EUR", ["€"] = "EUR",
        ["cny"] = "CNY", ["юань"] = "CNY", ["юаней"] = "CNY", ["юаня"] = "CNY",
        ["gbp"] = "GBP", ["фунт"] = "GBP", ["фунтов"] = "GBP",
        ["kzt"] = "KZT", ["тенге"] = "KZT", ["byn"] = "BYN", ["uah"] = "UAH", ["гривна"] = "UAH", ["гривен"] = "UAH",
        ["jpy"] = "JPY", ["иена"] = "JPY", ["иен"] = "JPY", ["try"] = "TRY", ["лира"] = "TRY", ["лир"] = "TRY",
        ["chf"] = "CHF", ["франк"] = "CHF", ["франков"] = "CHF", ["pln"] = "PLN", ["злотый"] = "PLN",
        ["aed"] = "AED", ["дирхам"] = "AED", ["inr"] = "INR", ["рупия"] = "INR", ["amd"] = "AMD", ["драм"] = "AMD",
    };

    private static Dictionary<string, double>? _rates; // сколько рублей за единицу валюты
    private static DateTime _ratesAt = DateTime.MinValue;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static async Task<string?> TryCurrencyAsync(double n, string from, string to)
    {
        // Код валюты — три латинские буквы. Любое трёхбуквенное слово («5 кот в дом») раньше уходило
        // в запрос курсов, и без сети это стоило восьми секунд ожидания.
        static string? Code(string word) =>
            CurrencyAliases.GetValueOrDefault(word) ??
            (word.Length == 3 && word.All(char.IsAsciiLetter) ? word.ToUpperInvariant() : null);

        var a = Code(from);
        var b = Code(to);
        if (a is null || b is null) return null;

        var rates = await GetRatesAsync();
        if (rates is null) return "Курсы валют недоступны (нет сети)";
        if (!rates.TryGetValue(a, out var ra) || !rates.TryGetValue(b, out var rb)) return null;

        var result = n * ra / rb;
        return $"{result.ToString("N2", CultureInfo.CurrentCulture)} {b}";
    }

    private static async Task<Dictionary<string, double>?> GetRatesAsync()
    {
        if (_rates is not null && DateTime.Now - _ratesAt < TimeSpan.FromHours(6)) return _rates;

        try
        {
            using var response = await Http.GetAsync("https://www.cbr-xml-daily.ru/daily_json.js");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var rates = new Dictionary<string, double> { ["RUB"] = 1 };
            foreach (var cur in doc.RootElement.GetProperty("Valute").EnumerateObject())
            {
                var nominal = cur.Value.GetProperty("Nominal").GetDouble();
                var value = cur.Value.GetProperty("Value").GetDouble();
                rates[cur.Name] = value / nominal;
            }
            _rates = rates;
            _ratesAt = DateTime.Now;
            return rates;
        }
        catch (Exception ex)
        {
            Log.Write("Курсы валют не получены: " + ex.Message);
            return _rates;
        }
    }
}
