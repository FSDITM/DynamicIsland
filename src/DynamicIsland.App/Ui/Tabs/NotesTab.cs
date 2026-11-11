using DynamicIsland.Features;
using SkiaSharp;

namespace DynamicIsland.Ui.Tabs;

/// <summary>Заметки по папкам слева; таймер и напоминания справа.</summary>
internal sealed class NotesTab(NotesService notes, ReminderService reminders) : ITabView
{
    private string _folder = "";
    private float _scroll;

    public void Wheel(int delta) => _scroll -= delta / 120f * 38f;

    public void Activated() => _scroll = 0;

    public void Draw(UiKit ui)
    {
        var pal = ui.Pal;
        const float rightW = 188f;
        var leftW = ui.Width - rightW - 14f;

        DrawNotes(ui, leftW);

        // Разделитель.
        ui.Line(leftW + 7, 0, leftW + 7, ui.Height - 2, pal.Line);

        var x = leftW + 14f;
        DrawTimer(ui, x, ui.Width - x);
        DrawReminders(ui, x, ui.Width - x);
    }

    private void DrawNotes(UiKit ui, float w)
    {
        var pal = ui.Pal;
        ui.Text("Заметки", 0, 14, 13, pal.Primary, bold: true);

        var add = new SKRect(w - 74, 0, w, 22);
        ui.Button(add, "Новая", () =>
        {
            var note = notes.Create(_folder.Length > 0 ? _folder : "Заметки");
            NoteEditorHost.Open(notes, note.Id);
        }, primary: true);

        // Фильтр по папкам.
        var folders = new List<string> { "" };
        folders.AddRange(notes.Folders());
        var cx = 0f;
        foreach (var f in folders.Take(5))
        {
            var label = f.Length == 0 ? "Все" : f;
            var cw = ui.Measure(label, 11.5f, true);
            var r = new SKRect(cx, 30, cx + cw, 50);
            var active = _folder == f;
            ui.Text(label, r.Left, r.MidY + 4, 11.5f, active ? pal.Primary : (ui.IsHover(r) ? pal.Primary : pal.Muted), SKTextAlign.Left, true);
            if (active) ui.RoundRect(new SKRect(r.Left, r.Bottom - 1, r.Right, r.Bottom + 1), 1f, ui.Accent);
            var folder = f;
            ui.Hit(new SKRect(r.Left - 4, r.Top, r.Right + 4, r.Bottom), () => { _folder = folder; _scroll = 0; });
            cx += cw + 16f;
        }

        var items = notes.All().Where(n => _folder.Length == 0 || n.Folder.Equals(_folder, StringComparison.OrdinalIgnoreCase)).ToList();

        var top = 58f;
        var view = ui.Height - top;
        const float rowH = 38f, gap = 1f;
        var content = items.Count * (rowH + gap);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, content - view));

        if (items.Count == 0)
        {
            ui.Text("Заметок нет", 0, top + 24, 13, pal.Primary, bold: true);
            ui.Text("«Новая» создаёт первую", 0, top + 44, 11.5f, pal.Muted);
            return;
        }

        ui.BeginList(top);
        for (var i = 0; i < items.Count; i++)
        {
            var n = items[i];
            var y = top + i * (rowH + gap) - _scroll;
            if (y + rowH < top || y > ui.Height) continue;

            var row = new SKRect(0, y, w - 6, y + rowH);
            var hover = ui.IsHover(row) && ui.Cursor.Y > top;
            ui.Row(row, hover);
            ui.Text(ui.Fit(n.Title, 12.5f, w - 80f), 12, y + 16, 12.5f, pal.Primary);
            ui.Text(n.Folder + " · " + Fmt.Ago(n.Updated), 12, y + 31, 10, pal.Muted);

            var note = n;
            ui.Hit(row, () => NoteEditorHost.Open(notes, note.Id));
        }

        ui.EndList();
        ui.Scrollbar(new SKRect(w - 3, top, w, ui.Height - 2), _scroll, content, view);
    }

    private void DrawTimer(UiKit ui, float x, float w)
    {
        var pal = ui.Pal;
        ui.Text("Таймер", x, 14, 13, pal.Primary, bold: true);

        if (reminders.TimerRunning)
        {
            var left = reminders.TimerLeft;
            ui.Text(ReminderService.FormatSpan(left), x, 52, 26, new SKColor(255, 159, 10), bold: true);

            var total = Math.Max(1, reminders.TimerTotal.TotalSeconds);
            var bar = new SKRect(x, 62, x + w, 66);
            ui.RoundRect(bar, 2, pal.Hover);
            ui.RoundRect(new SKRect(bar.Left, bar.Top, bar.Left + bar.Width * (float)(1 - left.TotalSeconds / total), bar.Bottom),
                2, new SKColor(255, 159, 10));

            ui.Button(new SKRect(x, 74, x + w, 98), "Остановить", reminders.StopTimer, danger: true);
            return;
        }

        int[] presets = [5, 10, 15, 25, 45];
        const float gap = 6f;
        var cw = (w - gap * 2) / 3f;
        for (var i = 0; i < presets.Length; i++)
        {
            var col = i % 3;
            var row = i / 3;
            var r = new SKRect(x + col * (cw + gap), 24 + row * 30, x + col * (cw + gap) + cw, 24 + row * 30 + 24);
            var minutes = presets[i];
            ui.Button(r, $"{minutes} мин", () => reminders.StartTimer(TimeSpan.FromMinutes(minutes)));
        }

        var custom = new SKRect(x + 2 * (cw + gap), 54, x + w, 78);
        ui.Button(custom, "Свой…", () => CommandBarHost.Toggle("таймер "), glyph: null);
    }

    private void DrawReminders(UiKit ui, float x, float w)
    {
        var pal = ui.Pal;
        var top = 116f;
        ui.Text("Напоминания", x, top, 13, pal.Primary, bold: true);

        var items = reminders.Upcoming();
        if (items.Count == 0)
        {
            ui.Text("Нет напоминаний", x, top + 24, 11.5f, pal.Muted);
        }

        var y = top + (items.Count == 0 ? 34f : 10f);
        foreach (var r in items.Take(3))
        {
            var row = new SKRect(x, y, x + w, y + 34);
            var hover = ui.IsHover(row);
            ui.Row(row, hover);
            ui.Text(ui.Fit(r.Text, 11.5f, w - 34f), x + 9, y + 14, 11.5f, pal.Primary);
            ui.Text(ui.Fit(r.When(), 10, w - 34f), x + 9, y + 28, 10, pal.Muted);
            var id = r.Id;
            if (hover) ui.IconButton(new SKRect(row.Right - 26, y + 6, row.Right - 4, y + 28), G.Close, () => reminders.Remove(id));
            y += 38f;
        }

        var add = new SKRect(x, Math.Min(y + 2, ui.Height - 26), x + w, Math.Min(y + 2, ui.Height - 26) + 24);
        ui.Button(add, "Напомнить", () => CommandBarHost.Toggle("напомни "), glyph: G.Add);
    }
}
