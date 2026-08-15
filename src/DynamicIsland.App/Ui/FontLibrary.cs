using System.IO;
using SkiaSharp;

namespace DynamicIsland.Ui;

/// <summary>
/// Шрифты островка.
///
/// Rubik лежит рядом с приложением одним вариативным файлом — статических
/// начертаний Google Fonts больше не публикует.
///
/// Выбрать конкретную насыщенность из вариативного файла средствами SkiaSharp 4
/// нельзя: <c>SKFontManager.CreateTypeface</c> не принимает <c>SKFontArguments</c>,
/// такой перегрузки в API нет. Поэтому берём начертание по умолчанию (для Rubik
/// это Regular), а где нужен вес потяжелее — включаем синтетическое утолщение
/// у <see cref="SKFont"/>.
/// </summary>
internal static class FontLibrary
{
    private static readonly string RubikPath =
        Path.Combine(AppContext.BaseDirectory, "Resources", "Fonts", "Rubik-Variable.ttf");

    private static readonly Lock Gate = new();
    private static SKTypeface? _rubik;
    private static bool _attempted;

    /// <summary>
    /// Rubik, либо системный шрифт, если файл не на месте.
    /// Из-за отсутствующего шрифта приложение падать не должно.
    /// </summary>
    public static SKTypeface Rubik
    {
        get
        {
            lock (Gate)
            {
                if (_attempted) return _rubik ?? Fallback(400);
                _attempted = true;

                try
                {
                    if (File.Exists(RubikPath))
                    {
                        _rubik = SKTypeface.FromFile(RubikPath);
                        if (_rubik is null) Log.Write("Rubik не удалось разобрать: " + RubikPath);
                    }
                    else
                    {
                        Log.Write("Файл шрифта Rubik не найден: " + RubikPath);
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("Rubik не загрузился: " + ex.Message);
                }

                return _rubik ?? Fallback(400);
            }
        }
    }

    /// <summary>Системный шрифт интерфейса.</summary>
    public static SKTypeface Fallback(int weight) =>
        SKTypeface.FromFamilyName("Segoe UI", (SKFontStyleWeight)weight,
            SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default;
}
