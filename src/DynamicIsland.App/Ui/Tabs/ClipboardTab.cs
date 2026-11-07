using DynamicIsland.Features;
using SkiaSharp;

namespace DynamicIsland.Ui.Tabs;

internal static class Fmt
{
    public static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 45) return "сейчас";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} мин";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} ч";
        return t.ToString("d MMM");
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} МБ",
        _ => $"{bytes / 1073741824.0:0.##} ГБ",
    };
}

/// <summary>История буфера: клик вставляет, правый клик закрепляет.</summary>
internal sealed class ClipboardTab(ClipboardService clipboard) : ITabView
{
    private float _scroll;
    private float _content;
    private const float RowH = 38f;
    private const float Gap = 1f;

    public void Wheel(int delta) => _scroll -= delta / 120f * 42f;

    public void Activated() => _scroll = 0;

    public void Draw(UiKit ui)
    {
        var pal = ui.Pal;
        var items = clipboard.Snapshot();

        ui.Text("Буфер обмена", 0, 14, 13, pal.Primary, bold: true);
        ui.Text($"{items.Count}", 98, 14, 11, pal.Muted);

        var clear = new SKRect(ui.Width - 84, 0, ui.Width, 22);
        ui.Button(clear, "Очистить", clipboard.ClearUnpinned);

        // Закреплённые наверх, остальные по времени.
        var ordered = items.OrderByDescending(i => i.Pinned).ThenByDescending(i => i.Time).ToList();

        var top = 30f;
        var view = ui.Height - top;
        _content = ordered.Count * (RowH + Gap);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _content - view));

        if (ordered.Count == 0)
        {
            ui.Text("История пуста", 0, top + 28, 13, pal.Primary, bold: true);
            ui.Text("Скопированное появится в этом списке", 0, top + 48, 11.5f, pal.Muted);
            return;
        }

        var listW = ui.Width - 10f;
        for (var i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            var y = top + i * (RowH + Gap) - _scroll;
            if (y + RowH < top || y > ui.Height) continue;

            var row = new SKRect(0, y, listW, y + RowH);
            var hover = ui.IsHover(row) && ui.Cursor.Y > top;
            ui.Row(row, hover);

            // Значок или миниатюра.
            var icon = new SKRect(8, y + 6, 36, y + RowH - 6);
            if (e.Kind == ClipKind.Image && e.Thumb is not null)
                ui.Image(e.Thumb, icon, 3);
            else
                ui.Glyph(e.Kind == ClipKind.Files ? G.Folder : G.Text, icon.MidX, icon.MidY + 2f, 16, pal.Muted);

            var rightPad = hover ? 70f : 84f;
            var textW = listW - 46f - rightPad;
            ui.Text(ui.Fit(e.Preview, 12.5f, textW), 46, y + RowH / 2 + 4.5f, 12.5f, pal.Primary);

            if (!hover) ui.Text(Fmt.Ago(e.Time), listW - 10, y + RowH / 2 + 4, 10.5f, pal.Muted, SKTextAlign.Right);
            if (e.Pinned && !hover) ui.Glyph(G.Pinned, listW - 66, y + RowH / 2, 12, ui.Accent);

            var entry = e;
            if (hover)
            {
                var pin = new SKRect(listW - 58, y + 8, listW - 34, y + RowH - 8);
                var del = new SKRect(listW - 30, y + 8, listW - 6, y + RowH - 8);
                ui.IconButton(pin, entry.Pinned ? G.Pinned : G.Pin, () => clipboard.TogglePin(entry), entry.Pinned);
                ui.IconButton(del, G.Delete, () => clipboard.Remove(entry));
            }

            ui.Hit(row, () => clipboard.Paste(entry), () => clipboard.TogglePin(entry));
        }

        ui.Scrollbar(new SKRect(ui.Width - 4, top, ui.Width, ui.Height - 2), _scroll, _content, view);
    }
}
