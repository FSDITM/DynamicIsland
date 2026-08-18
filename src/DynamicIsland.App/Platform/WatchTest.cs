using System.Diagnostics;
using System.Runtime.InteropServices;
using DynamicIsland.Interop;

namespace DynamicIsland.Platform;

/// <summary>
/// Режим <c>--watchtest N</c>: слежение за активным окном без графики.
///
/// Отвечает на три вопроса разом:
/// 1. доходят ли до потока события хука, когда он спит в MsgWaitForMultipleObjectsEx;
/// 2. сколько событий отбрасывает троттлинг;
/// 3. когда состояние окна на самом деле устаканивается — отдельный поток
///    опрашивает активное окно каждые 10 мс и пишет только изменения.
///
/// Третья дорожка нужна, чтобы отличить «событие не пришло» от «событие пришло,
/// но окно в тот момент ещё врало о своих размерах».
/// </summary>
internal static class WatchTest
{
    public static int Run(double seconds)
    {
        var clock = Stopwatch.StartNew();
        void Say(string message) => Log.Write($"[{clock.Elapsed.TotalSeconds,7:0.000}] {message}");

        Log.Write($"=== ПРОВЕРКА СЛЕЖЕНИЯ ЗА ОКНАМИ, {seconds:0} с ===");
        ForegroundWatcher.Trace = Say;

        using var watcher = new ForegroundWatcher();
        watcher.StateChanged += state => Say($">>> ВЫВОД: {state}");

        // Зона островка берётся с того же монитора и того же размера, что
        // у настоящего приложения. Раньше здесь был жёстко зашит основной
        // монитор, и при островке на втором экране диагностика уверенно
        // рассказывала про чужой.
        var settings = Configuration.Settings.Load();
        var monitors = Win32.GetMonitors();
        var mi = monitors.Count > 0
            ? monitors[Math.Clamp(settings.MonitorIndex, 0, monitors.Count - 1)]
            : new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };

        var scale = Win32.ScaleForMonitor(mi, 1f);
        var halfWidth = (int)(settings.RestWidth * scale / 2f);
        var height = (int)((settings.RestHeight + settings.TopOffset) * scale);
        var centerX = (mi.rcMonitor.Left + mi.rcMonitor.Right) / 2
                    + (int)(settings.HorizontalOffset * scale);

        Log.Write($"    зона островка: монитор {mi.rcMonitor.Left},{mi.rcMonitor.Top}.." +
                  $"{mi.rcMonitor.Right},{mi.rcMonitor.Bottom}, масштаб {scale:0.##}");

        // Если рядом работает настоящий островок, его окно надо исключить —
        // иначе диагностика находит перекрытие сама в себе.
        var live = Win32.FindWindowW("DynamicIsland.Overlay", null);
        if (live != 0) Log.Write($"    рядом работает островок: hwnd=0x{live:X}, исключён из разбора");

        watcher.SetIslandRect(new RECT
        {
            Left = centerX - halfWidth,
            Top = mi.rcMonitor.Top,
            Right = centerX + halfWidth,
            Bottom = mi.rcMonitor.Top + height,
        }, live);

        // Разбор реальных окон рабочего стола: прогоняем через ту же функцию,
        // которой пользуется приложение. Интересуют окна во весь монитор —
        // именно они раньше ошибочно уезжали в Fullscreen.
        Log.Write("--- окна во весь монитор, решение приложения ---");
        Win32.EnumWindows((window, _) =>
        {
            if (!Win32.IsWindowVisible(window)) return true;
            if (!Win32.GetWindowRect(window, out var rect)) return true;
            if (rect.Width < mi.rcMonitor.Width) return true;

            var (state, process) = watcher.Classify(window, rect);
            Log.Write($"    0x{window:X} {rect.Left},{rect.Top}..{rect.Right},{rect.Bottom}  " +
                      $"{(process.Length > 0 ? process : "—"),-24} -> {state}");
            return true;
        }, 0);

        // Независимая дорожка: чем на самом деле было активное окно в каждый момент.
        var sampling = true;
        var sampler = new Thread(() =>
        {
            var last = "";
            while (Volatile.Read(ref sampling))
            {
                var fg = Win32.GetForegroundWindow();
                Win32.GetWindowRect(fg, out var rect);
                var now = $"hwnd=0x{fg:X} {rect.Left},{rect.Top}..{rect.Right},{rect.Bottom} " +
                          $"iconic={Win32.IsIconic(fg)} visible={Win32.IsWindowVisible(fg)}";
                if (now != last) { last = now; Say("    опрос: " + now); }
                Thread.Sleep(10);
            }
        }) { IsBackground = true };
        sampler.Start();

        // Цикл повторяет холостую ветку рендер-цикла: то же ожидание, та же маска.
        var waits = 0L;
        var wokenByMessage = 0L;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            while (Win32.PeekMessageW(out var msg, 0, 0, 0, Win32.PM_REMOVE))
            {
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessageW(ref msg);
            }

            var result = Win32.MsgWaitForMultipleObjectsEx(0, [], 350, Win32.QS_ALLINPUT, 0);
            waits++;
            if (result != 258) wokenByMessage++;   // 258 = WAIT_TIMEOUT
        }

        Volatile.Write(ref sampling, false);
        sampler.Join(200);
        ForegroundWatcher.Trace = null;

        Log.Write($"=== ИТОГО: ожиданий {waits}, разбужено сообщением {wokenByMessage}, " +
                  $"по таймауту {waits - wokenByMessage} ===");
        return 0;
    }
}
