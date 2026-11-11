using System.IO;
using DynamicIsland.Features;
using QRCoder;
using SkiaSharp;

namespace DynamicIsland.Ui.Tabs;

/// <summary>Обработка текста из буфера: раскладка, регистр, JSON, Base64, URL, транслит, QR.</summary>
internal sealed class TextTab(ClipboardService clipboard, Configuration.Settings settings) : ITabView
{
    private string _text = "";
    private bool _dirty = true;
    private bool _hooked;
    private string? _previous;
    private string _status = "";
    private long _statusUntil;

    private SKImage? _qr;
    private byte[]? _qrPng;

    public void Activated()
    {
        Refresh();
        RenderTrash.Add(_qr);
        _qr = null;
    }

    public void Refresh()
    {
        try { _text = ClipboardService.ReadText() ?? ""; }
        catch { _text = ""; }
    }

    private void Status(string text)
    {
        _status = text;
        _statusUntil = Environment.TickCount64 + 2500;
    }

    private void Apply(Func<string, string?> op, string done)
    {
        if (_text.Length == 0) { Status("В буфере нет текста"); return; }
        var result = op(_text);
        if (result is null) { Status("Не получилось: текст не подходит"); return; }
        _previous = _text;
        _text = result;
        clipboard.SetText(result);
        Status(done);
    }

    private void MakeQr()
    {
        if (_text.Length == 0) { Status("В буфере нет текста"); return; }
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(_text, QRCodeGenerator.ECCLevel.M);
            var matrix = data.ModuleMatrix;
            const int module = 8, quiet = 3;
            var size = (matrix.Count + quiet * 2) * module;

            using var bmp = new SKBitmap(size, size);
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.White);
                using var black = new SKPaint { Color = SKColors.Black };
                for (var y = 0; y < matrix.Count; y++)
                for (var x = 0; x < matrix.Count; x++)
                    if (matrix[y][x])
                        canvas.DrawRect((x + quiet) * module, (y + quiet) * module, module, module, black);
            }

            _qr?.Dispose();
            _qr = SKImage.FromBitmap(bmp);
            using var encoded = _qr.Encode(SKEncodedImageFormat.Png, 100);
            _qrPng = encoded.ToArray();
        }
        catch (Exception ex)
        {
            Log.Write("QR не построен: " + ex.Message);
            Status("Текст слишком длинный для QR");
        }
    }

    private void SaveQr()
    {
        if (_qrPng is null) return;
        try
        {
            var dir = ScreenshotPaths.Folder(settings);
            var path = Path.Combine(dir, $"QR {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
            File.WriteAllBytes(path, _qrPng);
            Status("Сохранено в папку снимков");
        }
        catch (Exception ex) { Status("Не сохранилось: " + ex.Message); }
    }

    public void Draw(UiKit ui)
    {
        var pal = ui.Pal;

        // Буфер изменился (мы сами, другое приложение, команда) — предпросмотр обновляется сам.
        if (!_hooked)
        {
            _hooked = true;
            clipboard.Changed += () => _dirty = true;
        }
        if (_dirty && _qr is null)
        {
            _dirty = false;
            Refresh();
        }

        if (_qr is not null)
        {
            DrawQr(ui);
            return;
        }

        ui.Text("Текст", 0, 14, 13, pal.Primary, bold: true);

        // Заголовок справа: «Отменить» появляется, когда есть что откатить, затем обновление.
        var refresh = new SKRect(ui.Width - 34, 0, ui.Width, 22);
        ui.Button(refresh, "", Refresh, glyph: null);
        ui.Glyph(G.Refresh, ui.Width - 17, 11, 12, pal.Primary);

        var rightEdge = ui.Width - 42f;
        if (_previous is not null)
        {
            var undo = new SKRect(rightEdge - 84, 0, rightEdge, 22);
            ui.Button(undo, "Отменить", () =>
            {
                var back = _previous;
                _previous = null;
                if (back is null) return;
                _text = back;
                clipboard.SetText(back);
                Status("Возвращено");
            });
            rightEdge -= 92f;
        }

        if (Environment.TickCount64 < _statusUntil)
            ui.Text(_status, rightEdge, 14, 11.5f, ui.Accent, SKTextAlign.Right, true);
        else
            ui.Text(ui.Fit("работает с тем, что в буфере обмена", 11, rightEdge - 56f), 56, 14, 11, pal.Muted);

        // Предпросмотр буфера.
        var card = new SKRect(0, 30, ui.Width, 98);
        ui.Outline(card, 6, pal.Line, 1f);
        if (_text.Length == 0)
        {
            ui.Text("В буфере нет текста", card.MidX, card.MidY + 4, 12, pal.Muted, SKTextAlign.Center);
        }
        else
        {
            ui.Paragraph(_text.Length > 600 ? _text[..600] : _text, 12, 36, ui.Width - 24, 12, pal.Primary, 15.5f, 3);
            ui.Text(TextTools.Stats(_text), 12, 92, 10.5f, pal.Muted);
        }

        var cols = 4;
        var w = (ui.Width - 8f * (cols - 1)) / cols;
        var y = 108f;
        const float h = 30f;

        void Btn(int index, string label, Action act, string? glyph = null)
        {
            var col = index % cols;
            var row = index / cols;
            var r = new SKRect(col * (w + 8), y + row * (h + 8), col * (w + 8) + w, y + row * (h + 8) + h);
            ui.Button(r, label, act, glyph: glyph);
        }

        Btn(0, "Раскладка", () => Apply(t => TextTools.FixLayout(t), "Раскладка исправлена"));
        Btn(1, "ВЕРХНИЙ", () => Apply(t => TextTools.Upper(t), "Готово"));
        Btn(2, "нижний", () => Apply(t => TextTools.Lower(t), "Готово"));
        Btn(3, "Заглавные", () => Apply(t => TextTools.TitleCase(t), "Готово"));
        Btn(4, "Предложения", () => Apply(t => TextTools.SentenceCase(t), "Готово"));
        Btn(5, "иНВЕРСИЯ", () => Apply(t => TextTools.InvertCase(t), "Готово"));
        Btn(6, "Транслит", () => Apply(t => TextTools.Translit(t), "Готово"));
        Btn(7, "В одну строку", () => Apply(t => TextTools.OneLine(t), "Готово"));
        Btn(8, "JSON красиво", () => Apply(TextTools.JsonPretty, "JSON отформатирован"));
        Btn(9, "JSON сжать", () => Apply(TextTools.JsonMinify, "JSON сжат"));
        Btn(10, "В Base64", () => Apply(t => TextTools.Base64Encode(t), "Закодировано"));
        Btn(11, "Из Base64", () => Apply(TextTools.Base64Decode, "Раскодировано"));
        Btn(12, "В URL", () => Apply(t => TextTools.UrlEncode(t), "Закодировано"));
        Btn(13, "Из URL", () => Apply(TextTools.UrlDecode, "Раскодировано"));
        Btn(14, "Чистка пробелов", () => Apply(t => TextTools.CleanSpaces(t), "Готово"));
        Btn(15, "QR-код", MakeQr);
    }

    private void DrawQr(UiKit ui)
    {
        var pal = ui.Pal;
        var side = Math.Min(ui.Height - 8f, 210f);
        var r = new SKRect(0, 4, side, 4 + side);
        ui.RoundRect(r, 6, SKColors.White);
        ui.Image(_qr!, new SKRect(r.Left + 6, r.Top + 6, r.Right - 6, r.Bottom - 6), 8);

        var x = side + 20f;
        var w = ui.Width - x;
        ui.Text("QR-код", x, 22, 14, pal.Primary, bold: true);
        ui.Text(ui.Fit(_text, 11.5f, w), x, 42, 11.5f, pal.Muted);

        ui.Button(new SKRect(x, 62, x + w, 92), "Скопировать картинку", () =>
        {
            if (_qrPng is not null) { clipboard.SetImage(_qrPng); Status("QR скопирован"); }
        }, primary: true, glyph: G.Copy);
        ui.Button(new SKRect(x, 100, x + w, 130), "Сохранить в файл", SaveQr, glyph: G.Save);
        ui.Button(new SKRect(x, 138, x + w, 168), "Назад", () => { _qr?.Dispose(); _qr = null; }, glyph: null);

        if (Environment.TickCount64 < _statusUntil)
            ui.Text(_status, x, 192, 11.5f, ui.Accent, bold: true);
    }
}

/// <summary>Папки для снимков и записей.</summary>
internal static class ScreenshotPaths
{
    public static string Folder(Configuration.Settings s)
    {
        var dir = string.IsNullOrWhiteSpace(s.ScreenshotFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "DynamicIsland")
            : s.ScreenshotFolder;
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string RecordFolder(Configuration.Settings s)
    {
        var dir = string.IsNullOrWhiteSpace(s.RecordFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "DynamicIsland")
            : s.RecordFolder;
        Directory.CreateDirectory(dir);
        return dir;
    }
}
