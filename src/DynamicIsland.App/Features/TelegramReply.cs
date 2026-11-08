using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DynamicIsland.Interop;

namespace DynamicIsland.Features;

/// <summary>Куда отвечать: данные из самого уведомления Telegram.</summary>
internal sealed record TelegramTarget(string Kind, long Id, long MessageId, string ChatTitle, string Body = "")
{
    public string DeepLink => $"tg://openmessage?{Kind}={Id}&message_id={MessageId}";
}

/// <summary>
/// Ответ в Telegram без входа и без ключей: остров открывает нужный чат через ссылку tg://,
/// сверяет по заголовку окна, что открылся именно он, печатает ответ и нажимает Enter,
/// после чего возвращает пользователя в прежнее окно.
/// Windows не даёт нажимать кнопки чужих уведомлений, поэтому отвечаем самим Telegram Desktop.
/// </summary>
internal static partial class TelegramReply
{
    private const long ChatMask = 0xFFFFFFFFFFFFL;

    [GeneratedRegex("launch=\"([^\"]+)\"")]
    private static partial Regex Launch();

    /// <summary>
    /// Разбирает аргументы запуска из уведомления: peer, msg, topic. null — это не то уведомление,
    /// на которое можно ответить (канал, топик форума, закрытый чат без сообщения).
    /// </summary>
    public static TelegramTarget? Parse(string toastXml, string title, string body = "")
    {
        var m = Launch().Match(toastXml);
        if (!m.Success) return null;

        var args = WebUtility.HtmlDecode(m.Groups[1].Value);
        var map = args.Split('&', StringSplitOptions.RemoveEmptyEntries)
                      .Select(p => p.Split('=', 2))
                      .Where(p => p.Length == 2)
                      .ToDictionary(p => p[0], p => p[1]);

        if (!map.TryGetValue("peer", out var peerText) || !long.TryParse(peerText, out var peer) || peer == 0) return null;
        if (!map.TryGetValue("msg", out var msgText) || !long.TryParse(msgText, out var msg) || msg == 0) return null;
        if (map.TryGetValue("monoforumpeer", out var mono) && mono != "0") return null;

        // Тип чата зашит в старших битах номера: 0 — человек, 1 — группа, 2 — канал.
        var kind = (peer >> 48) switch { 0 => "user_id", 1 => "chat_id", 2 => "channel_id", _ => "" };
        if (kind.Length == 0) return null;

        // В группах заголовок уведомления — «Группа → отправитель»: чат — первая часть.
        var chat = title.Split(" → ", 2)[0].Trim();
        return new TelegramTarget(kind, peer & ChatMask, msg, chat, body);
    }

    /// <summary>Отправляет ответ. Возвращает текст ошибки или null, если всё ушло.</summary>
    public static async Task<string?> SendAsync(TelegramTarget target, string text, bool dryRun = false)
    {
        text = text.Replace("\r", " ").Replace('\n', ' ').Trim();
        if (text.Length == 0) return "Пустой ответ";

        var telegram = Process.GetProcessesByName("Telegram").FirstOrDefault(p => p.MainWindowHandle != 0 || p.Threads.Count > 0);
        if (telegram is null) return "Telegram не запущен";

        var previous = Native.GetForegroundWindow();
        var wasMinimized = TelegramMainWindow() is { } w0 && IsIconic(w0);

        try
        {
            Process.Start(new ProcessStartInfo(target.DeepLink) { UseShellExecute = true });
        }
        catch (Exception ex) { return "Не открылась ссылка Telegram: " + ex.Message; }

        // Ждём, пока Telegram выйдет на передний план.
        nint window = 0;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(100);
            var fg = Native.GetForegroundWindow();
            if (fg != 0 && IsTelegramWindow(fg)) { window = fg; break; }

            // Windows иногда не отдаёт окно сам: после полусекунды выводим его на передний план.
            if (i >= 5 && i % 5 == 0 && TelegramMainWindow() is { } main)
            {
                if (IsIconic(main)) ShowWindow(main, 9 /* SW_RESTORE */);
                Native.ForceForeground(main);
            }
        }
        if (window == 0) return "Telegram не открылся: ответ не отправлен";

        // Печатать вслепую нельзя: сообщение должно уйти именно в тот чат, откуда пришло уведомление.
        // Заголовок окна Telegram показывает аккаунт, а не чат, поэтому читаем шапку чата с экрана.
        var verified = false;
        for (var attempt = 0; attempt < 5 && !verified; attempt++)
        {
            await Task.Delay(attempt == 0 ? 700 : 450);
            if (Native.GetForegroundWindow() != window) return "Окно Telegram потеряло фокус: ответ не отправлен";
            verified = await ChatMatchesAsync(window, target);
        }
        if (!verified) return "Не удалось убедиться, что открыт нужный чат: ответ не отправлен";

        // Курсор должен стоять в поле сообщения, а не в поиске или другом месте.
        if (!FocusMessageInput(window)) return "Не найдено поле ввода (канал или чат только для чтения): ответ не отправлен";
        await Task.Delay(120);
        if (Native.GetForegroundWindow() != window) return "Окно Telegram потеряло фокус: ответ не отправлен";

        TypeText(text);
        await Task.Delay(120);

        // Пробный прогон для проверок: печатаем и стираем, ничего не отправляя.
        if (dryRun)
        {
            await Task.Delay(1500);
            for (var i = 0; i < text.Length; i++) PressKey(0x08);
        }
        else PressEnter();
        await Task.Delay(350);

        // Возвращаем пользователя туда, где он был.
        if (previous != 0 && previous != window) Native.ForceForeground(previous);
        if (wasMinimized) ShowWindow(window, 6 /* SW_MINIMIZE */);
        return null;
    }

    // ---------- Окна ----------

    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(nint hwnd);

    private static string WindowTitle(nint hwnd)
    {
        var sb = new StringBuilder(GetWindowTextLengthW(hwnd) + 2);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool IsTelegramWindow(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        try { return Process.GetProcessById((int)pid).ProcessName.Equals("Telegram", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static nint? TelegramMainWindow() =>
        Process.GetProcessesByName("Telegram").Select(p => p.MainWindowHandle).FirstOrDefault(h => h != 0) is var h && h != 0 ? h : null;

    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// Читает с экрана правую часть окна Telegram (шапку и переписку) и ищет там признаки нужного чата:
    /// название чата или текст сообщения, на которое отвечаем (ссылка перематывает чат именно к нему).
    /// Достаточно любого из двух; не нашлось ни одного — отправлять нельзя.
    /// </summary>
    private static async Task<bool> ChatMatchesAsync(nint window, TelegramTarget target)
    {
        try
        {
            if (!GetWindowRect(window, out var r)) return false;
            var width = r.Right - r.Left;
            var height = r.Bottom - r.Top;
            var x = r.Left + width * 30 / 100;

            // Координаты виртуального экрана: окно может стоять на втором мониторе, где они отрицательные.
            using var bmp = ScreenGrab.Region(x, r.Top, Math.Max(100, r.Right - x), Math.Max(100, height));
            var text = await OcrService.RecognizeAsync(bmp);

            // Для отладки: что именно увидели. Включается переменной окружения, обычно выключено.
            if (Environment.GetEnvironmentVariable("DYNAMICISLAND_TG_DEBUG") == "1")
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "di_tg_header.txt"),
                    $"rect={r.Left},{r.Top},{r.Right},{r.Bottom}{Environment.NewLine}ocr=[{text}]{Environment.NewLine}want=[{target.ChatTitle}] / [{target.Body}]");
            }

            if (text is null) return false;
            if (ChatNameMatches(text, target.ChatTitle)) return true;
            return target.Body.Length > 0 && ChatNameMatches(text, target.Body);
        }
        catch (Exception ex)
        {
            Log.Write("Telegram, сверка чата: " + ex.Message);
            return false;
        }
    }

    /// <summary>Только буквы и цифры, стилизованные начертания приведены к обычным (𝐑𝐢𝐜𝐡 → rich).</summary>
    private static string Norm(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>
    /// Встречается ли название чата в прочитанном тексте шапки. Распознавание неточное, поэтому допускаем
    /// ошибки в четверти букв. Названия короче трёх букв (одни эмодзи) проверить нельзя — это отказ.
    /// </summary>
    public static bool ChatNameMatches(string header, string expected)
    {
        var want = Norm(expected);
        var have = Norm(header);
        if (want.Length < 3 || have.Length == 0) return false;
        if (have.Contains(want)) return true;
        if (want.Length > 40) want = want[..40];

        var allowed = want.Length / 4;
        for (var start = 0; start < have.Length; start++)
        {
            foreach (var len in new[] { want.Length - 1, want.Length, want.Length + 1 })
            {
                if (len < 2 || start + len > have.Length) continue;
                if (Distance(want, have.Substring(start, len)) <= allowed) return true;
            }
        }
        return false;
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    /// <summary>Находит поле сообщения Telegram через UI Automation и убеждается, что в нём курсор.</summary>
    private static bool FocusMessageInput(nint window)
    {
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(window);
            var input = root.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ClassNameProperty, "Ui::InputField::Inner"));
            if (input is null) return false;
            if (!input.Current.HasKeyboardFocus) input.SetFocus();
            Thread.Sleep(80);
            return input.Current.HasKeyboardFocus;
        }
        catch (Exception ex)
        {
            Log.Write("Telegram, поле ввода: " + ex.Message);
            return false;
        }
    }

    // ---------- Ввод ----------

    private const uint KEYEVENTF_UNICODE = 0x0004;

    private static void TypeText(string text)
    {
        var inputs = new List<Native.INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            inputs.Add(Key(0, ch, KEYEVENTF_UNICODE));
            inputs.Add(Key(0, ch, KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP));
        }
        Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Native.INPUT>());
    }

    private static void PressEnter() => PressKey(0x0D);

    private static void PressKey(ushort vk)
    {
        Native.INPUT[] keys = [Key(vk, 0, 0), Key(vk, 0, Native.KEYEVENTF_KEYUP)];
        Native.SendInput(2, keys, Marshal.SizeOf<Native.INPUT>());
    }

    private static Native.INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = Native.INPUT_KEYBOARD,
        U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };
}
