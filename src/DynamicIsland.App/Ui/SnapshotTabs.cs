using System.IO;
using System.Reflection;
using DynamicIsland.Configuration;
using DynamicIsland.Features;
using DynamicIsland.Services;
using DynamicIsland.Ui.Tabs;
using SkiaSharp;

namespace DynamicIsland.Ui;

/// <summary>Снимки вкладок и таблица значков — проверка вёрстки без экрана.</summary>
internal static class SnapshotTabs
{
    public static void RenderAll(string directory, float scale = 1.5f, bool light = false)
    {
        Directory.CreateDirectory(directory);

        var settings = new Settings { Theme = light ? IslandTheme.Light : IslandTheme.Dark, ClipboardHistory = true };
        var clipboard = new ClipboardService(settings);
        clipboard.Add(new ClipEntry { Kind = ClipKind.Text, Text = "https://github.com/FSDITM/DynamicIsland/releases/latest", Pinned = true });
        clipboard.Add(new ClipEntry { Kind = ClipKind.Text, Text = "Привет! Это тестовый текст из буфера обмена, чтобы проверить длинную строку, которая обязана обрезаться." });
        clipboard.Add(new ClipEntry { Kind = ClipKind.Files, Files = [@"C:\Users\test\Documents\отчёт.docx"] });
        clipboard.Add(new ClipEntry { Kind = ClipKind.Text, Text = "{\"name\":\"island\",\"version\":2}" });
        clipboard.Add(new ClipEntry { Kind = ClipKind.Text, Text = "ghbdtn vbh" });

        var shelf = new ShelfService();
        shelf.Add([Environment.ProcessPath ?? @"C:\Windows\notepad.exe", Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                   @"C:\Windows\System32\notepad.exe"]);

        var views = new Dictionary<int, ITabView>
        {
            [1] = new ClipboardTab(clipboard),
            [2] = new ShelfTab(shelf),
            [3] = new TextTab(clipboard, settings),
        };
        // Остальные вкладки: сервисы создаются как в приложении, но без запуска сети и таймеров.
        var hub = new PillHub();
        var reminders = new ReminderService(settings, hub);
        // Снимки идут в публичный репозиторий: заметки и громкость — выдуманные, а не пользовательские.
        var notes = new NotesService(loadFromDisk: false);
        notes.Seed("Заметки", "План релиза: проверить установщик, обновить README", TimeSpan.FromMinutes(12));
        notes.Seed("Задачи", "Купить кофе и записать демо острова", TimeSpan.FromHours(3));
        notes.Seed("Идеи", "Темы для виджета погоды", TimeSpan.FromDays(1));
        var audio = new AudioService();
        var controls = new ControlsService(settings, hub);
        var weather = new WeatherService(settings, hub);
        var capture = new CaptureController(settings, clipboard, hub);
        capture.Colors.AddRange(["#FF453A", "#30D158", "#0A84FF", "#FFD60A"]);
        views[4] = new NotesTab(notes, reminders);
        views[5] = new ControlsTab(controls, audio);
        views[6] = new WeatherTab(weather, settings);
        views[7] = new ToolsTab(capture, clipboard, settings, () => { });

        foreach (var (index, view) in views.OrderBy(v => v.Key))
        {
            var name = $"tab{index}-{IslandContent.TabNames[index]}{(light ? "-light" : "")}.png";
            RenderTab(Path.Combine(directory, name), settings, view, index, scale);
        }
    }

    private static void RenderTab(string path, Settings settings, ITabView view, int index, float scale)
    {
        var width = (int)(620 * scale);
        var height = (int)(380 * scale);

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(settings.Theme == IslandTheme.Light ? new SKColor(220, 220, 228) : new SKColor(40, 40, 46));

        var island = new Island(settings);
        island.SetMode(IslandMode.Expanded);
        island.ExpandedTarget = new Vec2(settings.PanelWidth, settings.PanelHeight);
        for (var i = 0; i < 800; i++) island.Update(1f / 120f);

        using var content = new IslandContent(settings);
        content.Views[index] = view;
        content.ActiveTab = index;

        var power = new PowerSnapshot(72, false, true);
        content.Draw(canvas, island, width, height, scale, MediaSnapshot.Empty, null, power);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    /// <summary>Вкладка в режиме эмодзи и со своими цветами.</summary>
    public static void RenderCustom(string path)
    {
        var settings = new Settings
        {
            TabStyle = TabStyle.Emoji,
            IconColor = 0xFFFF9F0A,
            EqualizerColor = 0xFF30D158,
        };
        var clipboard = new ClipboardService(settings);
        clipboard.Add(new ClipEntry { Kind = ClipKind.Text, Text = "Пример записи буфера" });
        RenderTab(path, settings, new ClipboardTab(clipboard), 1, 1.5f);

        var settings2 = new Settings { IconColor = 0xFFFF9F0A, EqualizerColor = 0xFF30D158 };
        var clip2 = new ClipboardService(settings2);
        RenderTab(path.Replace(".png", "-icons.png"), settings2, new ClipboardTab(clip2), 1, 1.5f);
    }

    /// <summary>Таблица всех значков с подписями — чтобы увидеть, какие коды в шрифте на самом деле есть.</summary>
    public static void RenderGlyphSheet(string path)
    {
        var fields = typeof(G).GetFields(BindingFlags.Public | BindingFlags.Static).ToList();
        const int cols = 6, cellW = 150, cellH = 54;
        var rows = (fields.Count + cols - 1) / cols;

        using var ui = new UiKit("");
        using var surface = SKSurface.Create(new SKImageInfo(cols * cellW, rows * cellH + 10));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(20, 20, 24));

        ui.Begin(canvas, new SKRect(0, 0, cols * cellW, rows * cellH + 10), 1f, 1f, new SKPoint(-1, -1));
        for (var i = 0; i < fields.Count; i++)
        {
            var x = i % cols * cellW;
            var y = i / cols * cellH + 5;
            ui.Glyph((string)fields[i].GetValue(null)!, x + 24, y + 22, 24, ui.Pal.Primary);
            ui.Text(fields[i].Name, x + 46, y + 18, 11, ui.Pal.Muted);
            ui.Text("U+" + ((int)((string)fields[i].GetValue(null)!)[0]).ToString("X4"), x + 46, y + 34, 10, ui.Pal.Muted);
        }
        ui.End();

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }
}
