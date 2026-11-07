using System.Globalization;
using System.IO;
using System.Text;

namespace DynamicIsland.Features;

/// <summary>Проверка разборщиков и инструментов без окон: <c>DynamicIsland.exe --featuretest</c>.</summary>
internal static class FeatureTests
{
    private static readonly StringBuilder Out = new();
    private static int _failed;
    private static int _passed;

    private static void Check(string name, object? actual, object? expected)
    {
        var ok = Equals(actual, expected) || actual?.ToString() == expected?.ToString();
        if (ok) _passed++; else _failed++;
        Out.AppendLine($"{(ok ? "ok  " : "FAIL")} {name}: {actual}{(ok ? "" : $"   (ожидалось {expected})")}");
    }

    /// <summary>Рисует QR-код в PNG: нужен, чтобы проверять чтение QR с экрана.</summary>
    public static void MakeQrPng(string text, string path)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var m = data.ModuleMatrix;
        const int mod = 8, quiet = 4;
        var size = (m.Count + quiet * 2) * mod;
        using var bmp = new SkiaSharp.SKBitmap(size, size);
        using (var canvas = new SkiaSharp.SKCanvas(bmp))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            using var black = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black };
            for (var y = 0; y < m.Count; y++)
            for (var x = 0; x < m.Count; x++)
                if (m[y][x]) canvas.DrawRect((x + quiet) * mod, (y + quiet) * mod, mod, mod, black);
        }
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var enc = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, enc.ToArray());
    }

    public static int Run(string reportPath)
    {
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");

        // Калькулятор
        double v;
        Check("calc 2+2", Calc.TryEvaluate("2+2", out v) ? v : double.NaN, 4d);
        Check("calc 10/4", Calc.TryEvaluate("10/4", out v) ? v : double.NaN, 2.5);
        Check("calc (3+4)*2", Calc.TryEvaluate("(3+4)*2", out v) ? v : double.NaN, 14d);
        Check("calc 2^10", Calc.TryEvaluate("2^10", out v) ? v : double.NaN, 1024d);
        Check("calc sqrt(16)", Calc.TryEvaluate("sqrt(16)", out v) ? v : double.NaN, 4d);
        Check("calc 1,5*2", Calc.TryEvaluate("1,5*2", out v) ? v : double.NaN, 3d);
        Check("calc слово не выражение", Calc.TryEvaluate("hello", out _), false);
        Check("calc число не выражение", Calc.TryEvaluate("42", out _), false);
        Check("calc firefox не выражение", Calc.TryEvaluate("firefox", out _), false);

        // Напоминания: «сейчас» — суббота 7 ноября 2026, 12:00
        var now = new DateTime(2026, 11, 7, 12, 0, 0);
        void P(string input, string text, DateTime at, Repeat rep = Repeat.None)
        {
            var r = ReminderParser.Parse(input, now);
            Check($"напоминание «{input}» → текст", r?.Text, text);
            Check($"напоминание «{input}» → время", r?.At.ToString("yyyy-MM-dd HH:mm"), at.ToString("yyyy-MM-dd HH:mm"));
            Check($"напоминание «{input}» → повтор", r?.Repeat, rep);
        }

        P("напомни позвонить маме завтра в 9", "Позвонить маме", new DateTime(2026, 11, 8, 9, 0, 0));
        P("таблетки каждый день в 23:00", "Таблетки", new DateTime(2026, 11, 7, 23, 0, 0), Repeat.Daily);
        P("через 20 минут выключить плиту", "Выключить плиту", new DateTime(2026, 11, 7, 12, 20, 0));
        P("remind me to call mum tomorrow at 7pm", "Call mum", new DateTime(2026, 11, 8, 19, 0, 0));
        P("в понедельник в 10 встреча", "Встреча", new DateTime(2026, 11, 9, 10, 0, 0));
        P("каждый понедельник в 9 планерка", "Планерка", new DateTime(2026, 11, 9, 9, 0, 0), Repeat.Weekly);
        P("через час чай", "Чай", new DateTime(2026, 11, 7, 13, 0, 0));
        P("в 11 позвонить", "Позвонить", new DateTime(2026, 11, 8, 11, 0, 0));
        P("напомни через полчаса", "Напоминание", new DateTime(2026, 11, 7, 12, 30, 0));
        Check("напоминание без времени", ReminderParser.Parse("просто текст", now), null);

        // Таймер
        Check("таймер 10", ReminderParser.ParseDuration("10"), TimeSpan.FromMinutes(10));
        Check("таймер 25m", ReminderParser.ParseDuration("25m"), TimeSpan.FromMinutes(25));
        Check("таймер 1:30", ReminderParser.ParseDuration("1:30"), TimeSpan.FromSeconds(90));
        Check("таймер 90 сек", ReminderParser.ParseDuration("90 сек"), TimeSpan.FromSeconds(90));
        Check("таймер 1ч 30м", ReminderParser.ParseDuration("1ч 30м"), TimeSpan.FromMinutes(90));
        Check("таймер 2 часа", ReminderParser.ParseDuration("2 часа"), TimeSpan.FromHours(2));
        Check("таймер мусор", ReminderParser.ParseDuration("abc"), null);

        // Текст
        Check("раскладка en→ru", TextTools.FixLayout("ghbdtn"), "привет");
        Check("раскладка ru→en", TextTools.FixLayout("руддщ"), "hello");
        Check("транслит ru→en", TextTools.Translit("привет, щука"), "privet, shchuka");
        Check("транслит en→ru", TextTools.Translit("privet"), "привет");
        Check("регистр", TextTools.TitleCase("hello world"), "Hello World");
        Check("предложения", TextTools.SentenceCase("привет. как дела? ХОРОШО"), "Привет. Как дела? Хорошо");
        Check("json pretty", TextTools.JsonPretty("{\"a\":1}")?.Replace("\r", "").Replace("\n", "|"), "{|  \"a\": 1|}");
        Check("json minify", TextTools.JsonMinify("{ \"a\" : 1 }"), "{\"a\":1}");
        Check("json мусор", TextTools.JsonPretty("не json"), null);
        Check("base64", TextTools.Base64Encode("привет"), "0L/RgNC40LLQtdGC");
        Check("base64 обратно", TextTools.Base64Decode("0L/RgNC40LLQtdGC"), "привет");
        Check("url", TextTools.UrlEncode("а б"), "%D0%B0%20%D0%B1");
        Check("в одну строку", TextTools.OneLine("a\r\nb\n c"), "a b c");

        // Единицы
        Check("10 км в мили", Units.TryConvertAsync("10 км в мили").GetAwaiter().GetResult(), "6,213712 миль");
        Check("20 c в f", Units.TryConvertAsync("20 c в f").GetAwaiter().GetResult(), "68 °F");
        Check("1 гб в мб", Units.TryConvertAsync("1 гб в мб").GetAwaiter().GetResult(), "1024 МБ");
        Check("90 мин в ч", Units.TryConvertAsync("90 мин в ч").GetAwaiter().GetResult(), "1,5 ч");
        // QR: нарисовать и прочитать обратно
        using (var gen = new QRCoder.QRCodeGenerator())
        using (var qrData = gen.CreateQrCode("https://github.com/FSDITM/DynamicIsland", QRCoder.QRCodeGenerator.ECCLevel.M))
        {
            var m = qrData.ModuleMatrix;
            const int mod = 6, quiet = 4;
            var size = (m.Count + quiet * 2) * mod;
            using var bmp = new System.Drawing.Bitmap(size, size);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.White);
                for (var y = 0; y < m.Count; y++)
                for (var x = 0; x < m.Count; x++)
                    if (m[y][x]) g.FillRectangle(System.Drawing.Brushes.Black, (x + quiet) * mod, (y + quiet) * mod, mod, mod);
            }
            Check("QR туда-обратно", OcrService.DecodeQr(bmp), "https://github.com/FSDITM/DynamicIsland");
        }

        // Полка: приём файлов (разбор CF_HDROP)
        {
            var shelf = new ShelfService();
            var target = new ShelfDropTarget(shelf);
            var obj = new System.Windows.DataObject();
            var list = new System.Collections.Specialized.StringCollection();
            list.Add(Environment.ProcessPath!);
            list.Add(System.IO.Path.GetDirectoryName(Environment.ProcessPath!)!);
            obj.SetFileDropList(list);
            var effect = 0;
            target.Drop((System.Runtime.InteropServices.ComTypes.IDataObject)obj, 0, default, ref effect);
            Check("полка приняла файлы", shelf.Count, 2);
            Check("полка: эффект копирования", effect, 1);
        }

        // Стоимость опросов (для оценки нагрузки в покое)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 20; i++) { EventMonitor.InUse("microphone"); EventMonitor.InUse("webcam"); }
            Out.AppendLine($"info реестр микрофон+камера: {sw.Elapsed.TotalMilliseconds / 20:0.0} мс за опрос");
            sw.Restart();
            for (var i = 0; i < 20; i++) SystemControl.IsVpnUp();
            Out.AppendLine($"info проверка VPN: {sw.Elapsed.TotalMilliseconds / 20:0.0} мс за опрос");
        }

        // Запись GIF: 2 секунды небольшой области
        {
            var gifSettings = new Configuration.Settings
            {
                RecordGif = true,
                RecordFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "di_gif_test"),
            };
            var rec = new ScreenRecorder(gifSettings);
            string? saved = null;
            using var done = new ManualResetEventSlim();
            rec.Finished += p => { saved = p; done.Set(); };
            rec.Start(100, 100, 300, 200);
            Thread.Sleep(2000);
            rec.Stop();
            done.Wait(20000);
            Check("GIF записан", saved is not null && File.Exists(saved), true);
            if (saved is not null && File.Exists(saved))
            {
                var bytes = File.ReadAllBytes(saved);
                Check("GIF заголовок", System.Text.Encoding.ASCII.GetString(bytes, 0, 6), "GIF89a");
                var frames = 0;
                for (var i = 0; i < bytes.Length - 1; i++) if (bytes[i] == 0x21 && bytes[i + 1] == 0xF9) frames++;
                Out.AppendLine($"info GIF: {bytes.Length / 1024} КБ, кадров с задержкой: {frames}");
                Check("GIF несколько кадров", frames >= 10, true);
                File.Delete(saved);
            }
        }

        // Тема Windows: туда и обратно
        {
            var before = SystemControl.IsDark();
            SystemControl.ToggleTheme();
            var after = SystemControl.IsDark();
            SystemControl.ToggleTheme();
            Check("тема переключается", after != before, true);
            Check("тема возвращается", SystemControl.IsDark(), before);
        }

        // Тексты песен (нужна сеть)
        var lyrics = new LyricsService();
        lyrics.Track("Queen", "Bohemian Rhapsody", TimeSpan.FromSeconds(355));
        for (var i = 0; i < 40 && !lyrics.Available; i++) Thread.Sleep(250);
        Check("слова песни получены", lyrics.Available, true);
        Out.AppendLine("info строка на 60 с: " + lyrics.LineAt(TimeSpan.FromSeconds(60)));
        var lrc = LyricsService.ParseLrc("[00:12.50]Раз\n[00:20.00]Два\n[00:30.1]");
        Check("LRC разбор", lrc.Length, 3);
        Check("LRC позиция", lrc[0].Item1, TimeSpan.FromSeconds(12.5));

        var usd = Units.TryConvertAsync("100 usd в rub").GetAwaiter().GetResult();
        Out.AppendLine("info 100 usd в rub: " + usd);

        Console.WriteLine($"\nПройдено {_passed}, провалено {_failed}");
        File.WriteAllText(reportPath, Out.ToString());
        return _failed == 0 ? 0 : 1;
    }
}
