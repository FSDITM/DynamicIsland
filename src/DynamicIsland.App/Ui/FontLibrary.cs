using System.IO;
using SkiaSharp;

namespace DynamicIsland.Ui;

/// <summary>
/// Шрифты островка.
///
/// На маках Dynamic Island набран SF Pro, но он проприетарный: лицензия Apple
/// разрешает использовать его только при разработке под платформы Apple, и
/// вшить его в приложение для Windows нельзя. Общепринятая открытая замена —
/// Inter под SIL Open Font License; оригинальный DynamicWin использовал именно её.
///
/// Начертания статические, поэтому насыщенность берётся честно из файла, а не
/// подделывается: вытащить конкретный вес из вариативного шрифта SkiaSharp 4
/// не умеет.
/// </summary>
internal static class FontLibrary
{
    private static readonly string FontsDirectory =
        Path.Combine(AppContext.BaseDirectory, "Resources", "Fonts");

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, SKTypeface?> Cache = [];

    /// <summary>Обычное начертание — основной текст островка.</summary>
    public static SKTypeface Regular => Load("Inter-Regular.ttf", SKFontStyleWeight.Normal);

    /// <summary>
    /// Полужирное — время и название трека. Именно SemiBold, а не более
    /// тяжёлые начертания: на маках строка состояния набрана этим весом,
    /// ExtraBold рядом с обычным текстом выглядит грубо.
    /// </summary>
    public static SKTypeface Bold => Load("Inter-SemiBold.ttf", SKFontStyleWeight.SemiBold);

    /// <summary>
    /// Шрифт по имени семейства из системы. Нужен, чтобы можно было указать
    /// свой — например SF Pro, если пользователь установил его сам.
    /// Пустое имя или отсутствие шрифта — вернётся вшитый Inter.
    /// </summary>
    public static SKTypeface FromFamilyOrDefault(string? family, bool bold)
    {
        if (!string.IsNullOrWhiteSpace(family))
        {
            var typeface = SKTypeface.FromFamilyName(family,
                bold ? SKFontStyleWeight.SemiBold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

            // FromFamilyName при отсутствии семейства молча отдаёт подстановку,
            // поэтому сверяем, что получили именно запрошенное.
            if (typeface is not null &&
                typeface.FamilyName.Contains(family, StringComparison.OrdinalIgnoreCase))
                return typeface;
        }

        return bold ? Bold : Regular;
    }

    private static SKTypeface Load(string fileName, SKFontStyleWeight fallbackWeight)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(fileName, out var cached))
                return cached ?? SystemFallback(fallbackWeight);

            SKTypeface? typeface = null;
            var path = Path.Combine(FontsDirectory, fileName);

            try
            {
                if (File.Exists(path)) typeface = SKTypeface.FromFile(path);
                else Log.Write("Файл шрифта не найден: " + path);
            }
            catch (Exception ex)
            {
                Log.Write($"Шрифт {fileName} не загрузился: {ex.Message}");
            }

            Cache[fileName] = typeface;
            return typeface ?? SystemFallback(fallbackWeight);
        }
    }

    private static SKTypeface SystemFallback(SKFontStyleWeight weight) =>
        SKTypeface.FromFamilyName("Segoe UI", weight,
            SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default;
}
