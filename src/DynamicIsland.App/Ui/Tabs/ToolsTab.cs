using DynamicIsland.Features;
using SkiaSharp;

namespace DynamicIsland.Ui.Tabs;

/// <summary>Снимок, текст и QR с экрана, пипетка, запись, вставка без форматирования.</summary>
internal sealed class ToolsTab(CaptureController capture, ClipboardService clipboard, Configuration.Settings settings,
                               Action openSettings) : ITabView
{
    /// <summary>Строка таблицы: значок, действие, сочетание клавиш справа.</summary>
    private void Line(UiKit ui, SKRect r, string glyph, string label, string hint, Action click, bool active = false)
    {
        var hover = ui.IsHover(r);
        ui.Row(r, hover);
        var tint = active ? new SKColor(255, 105, 97) : ui.Accent;
        ui.Glyph(glyph, r.Left + 14, r.MidY + 2.5f, 16, tint);

        // Название обрезается, если не хватает места рядом с сочетанием клавиш.
        var hintW = ui.Measure(hint, 11);
        var labelMax = r.Width - 38f - hintW - 22f;
        ui.Text(ui.Fit(label, 12.5f, labelMax, bold: true), r.Left + 38, r.MidY + 4.5f, 12.5f, active ? tint : ui.Pal.Primary, bold: true);
        ui.Text(hint, r.Right - 10, r.MidY + 4, 11, ui.Pal.Muted, SKTextAlign.Right);
        ui.Hit(r, click);
    }

    public void Draw(UiKit ui)
    {
        var pal = ui.Pal;
        ui.Text("Инструменты", 0, 14, 13, pal.Primary, bold: true);

        // Две колонки строк: слева съёмка экрана, справа остальное.
        const float gap = 14f, rowH = 34f;
        var cw = (ui.Width - gap) / 2f;
        SKRect Cell(int col, int row) => new(col * (cw + gap), 24 + row * rowH, col * (cw + gap) + cw, 24 + row * rowH + rowH - 1);

        Line(ui, Cell(0, 0), G.Camera, "Снимок области", settings.HotkeyScreenshot, () => capture.Run("shot"));
        Line(ui, Cell(0, 1), G.Text, "Текст с экрана", settings.HotkeyOcr, () => capture.Run("ocr"));
        Line(ui, Cell(0, 2), G.Zoom, "QR с экрана", settings.HotkeyQr, () => capture.Run("qr"));

        Line(ui, Cell(1, 0), G.Palette, "Пипетка", settings.HotkeyColor, () => capture.Run("color"));

        var rec = capture.Recorder.Active;
        var elapsed = DateTime.Now - capture.Recorder.StartedAt;
        Line(ui, Cell(1, 1), rec ? G.Stop : G.Video, rec ? "Остановить запись" : "Запись экрана",
            rec ? $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}" : settings.HotkeyRecord,
            () => capture.Run("record"), active: rec);

        Line(ui, Cell(1, 2), G.Clipboard, "Чистая вставка", settings.HotkeyPastePlain, clipboard.PastePlainFromClipboard);

        // История цветов.
        var cy = 24 + 3 * rowH + 10;
        ui.Text("Недавние цвета", 0, cy + 10, 11.5f, pal.Muted, bold: true);
        if (capture.Colors.Count == 0)
        {
            ui.Text("Пока пусто — возьмите цвет пипеткой", 110, cy + 10, 11.5f, pal.Muted);
        }
        var x = 0f;
        foreach (var hex in capture.Colors.Take(12))
        {
            var r = new SKRect(x, cy + 18, x + 24, cy + 42);
            var c = SKColor.Parse(hex);
            ui.RoundRect(r, 3, c);
            ui.Outline(r, 3, pal.Line, 1f);
            var color = hex;
            ui.Hit(r, () => capture.CopyColor(color));
            if (ui.IsHover(r)) ui.Text(hex, r.MidX, r.Bottom + 12, 9.5f, pal.Primary, SKTextAlign.Center);
            x += 30f;
        }

        // Папки и настройки.
        var by = ui.Height - 26f;
        var bw = (ui.Width - 16f) / 3f;
        ui.Button(new SKRect(0, by, bw, by + 24), "Папка снимков", () => capture.OpenFolder(false), glyph: G.Folder);
        ui.Button(new SKRect(bw + 8, by, bw * 2 + 8, by + 24), "Папка записей", () => capture.OpenFolder(true), glyph: G.Video);
        ui.Button(new SKRect((bw + 8) * 2, by, ui.Width, by + 24), "Настройки", openSettings, glyph: G.Settings);
    }
}
