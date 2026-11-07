using System.IO;
using System.Windows.Media;

namespace DynamicIsland.Ui;

/// <summary>Шрифты для вспомогательных окон WPF: тот же Rubik, что и в самом острове.</summary>
internal static class WpfFonts
{
    public static readonly FontFamily Text = Create();

    private static FontFamily Create()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Resources", "Fonts") + Path.DirectorySeparatorChar;
            if (Directory.Exists(dir))
            {
                var family = new FontFamily(new Uri(dir), "./#Rubik");
                if (family.FamilyNames.Count > 0) return family;
            }
        }
        catch { /* упадём на системный шрифт */ }
        return new FontFamily("Segoe UI Variable Text, Segoe UI");
    }
}
