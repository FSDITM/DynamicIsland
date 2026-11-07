using System.IO;
using DynamicIsland.Features;
using SkiaSharp;

namespace DynamicIsland.Ui.Tabs;

/// <summary>Полка с файлами: бросили на островок — достали в любом окне.</summary>
internal sealed class ShelfTab(ShelfService shelf) : ITabView
{
    private float _scroll;
    private float _content;

    /// <summary>Что начнут тащить, если мышь сдвинется с зажатой кнопкой. Ставит нажатие.</summary>
    public string[]? ArmedDrag { get; set; }

    public void Wheel(int delta) => _scroll -= delta / 120f * 42f;

    public void Activated()
    {
        _scroll = 0;
        shelf.Prune();
    }

    public void Draw(UiKit ui)
    {
        var pal = ui.Pal;
        var files = shelf.Snapshot();

        ui.Text("Полка", 0, 14, 13, pal.Primary, bold: true);
        ui.Text($"{files.Count}", 50, 14, 11, pal.Muted);

        if (files.Count > 0)
        {
            ui.Button(new SKRect(ui.Width - 84, 0, ui.Width, 22), "Очистить", shelf.Clear);
            var dragAll = new SKRect(ui.Width - 214, 0, ui.Width - 92, 22);
            ui.Button(dragAll, "Забрать все", () => { }, glyph: G.Share);
            ui.Hit(dragAll, () => { }, null, () => ArmedDrag = files.ToArray());
        }

        var top = 30f;
        if (files.Count == 0)
        {
            ui.Text("Полка пуста", 0, top + 28, 13, pal.Primary, bold: true);
            ui.Text("Бросьте файлы на островок — он подержит их, пока не понадобятся", 0, top + 48, 11.5f, pal.Muted);
            return;
        }

        const float rowH = 34f;
        var view = ui.Height - top;
        _content = files.Count * rowH;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _content - view));
        var listW = ui.Width - 10f;

        for (var i = 0; i < files.Count; i++)
        {
            var path = files[i];
            var y = top + i * rowH - _scroll;
            if (y + rowH < top || y > ui.Height) continue;

            var row = new SKRect(0, y, listW, y + rowH - 1);
            var hover = ui.IsHover(row) && ui.Cursor.Y > top;
            ui.Row(row, hover);

            var isDir = Directory.Exists(path);
            ui.Glyph(isDir ? G.Folder : G.File, 16, row.MidY + 2f, 15, isDir ? ui.Accent : pal.Muted);
            ui.Text(ui.Fit(Path.GetFileName(path), 12.5f, listW - 150f), 36, row.MidY + 4.5f, 12.5f, pal.Primary);

            var sub = "";
            try { if (!isDir) sub = Fmt.Size(new FileInfo(path).Length); } catch { /* файл мог исчезнуть */ }
            if (!hover) ui.Text(sub, listW - 10, row.MidY + 4, 10.5f, pal.Muted, SKTextAlign.Right);

            var p = path;
            ui.Hit(row, () => OpenFile(p), null, () => ArmedDrag = [p]);
            if (hover) ui.IconButton(new SKRect(listW - 30, row.Top + 5, listW - 6, row.Bottom - 5), G.Close, () => shelf.Remove(p));
        }

        ui.Scrollbar(new SKRect(ui.Width - 4, top, ui.Width, ui.Height - 2), _scroll, _content, view);
    }

    private static void OpenFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("Не открылся файл с полки: " + ex.Message); }
    }
}
