using DynamicIsland.Interop;

namespace DynamicIsland.Platform;

/// <summary>
/// Следит за активным окном, чтобы островок мог убраться с дороги.
///
/// Специально на событиях, а не на таймере: <see cref="Win32.SetWinEventHook"/>
/// будит нас только когда окно реально сменилось или переехало. Опрос активного
/// окна 60 раз в секунду сам по себе был бы такой же ошибкой, как WMI в рендер-цикле.
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    /// <summary>Состояние окна под островком.</summary>
    public enum Occlusion
    {
        /// <summary>Рабочий стол свободен — островок в полной форме.</summary>
        Clear,

        /// <summary>Под островком развёрнутое окно — сворачиваемся в «чёлку».</summary>
        Covered,

        /// <summary>Полный экран (видео, игра, презентация) — прячемся совсем.</summary>
        Fullscreen,
    }

    // Хук держит указатель на делегат; без поля его соберёт GC.
    private readonly Win32.WinEventProc _proc;
    private readonly nint _hookForeground;
    private readonly nint _hookLocation;

    private RECT _islandScreenRect;
    private nint _selfWindow;

    // EVENT_OBJECT_LOCATIONCHANGE во время перетаскивания окна сыплется десятками
    // раз в секунду. Ограничиваем частоту переоценки — состояние перекрытия
    // не меняется быстрее.
    private long _lastEvaluateTicks;
    private static readonly long ThrottleTicks = System.Diagnostics.Stopwatch.Frequency / 20;

    public Occlusion State { get; private set; } = Occlusion.Clear;

    /// <summary>Имя процесса активного окна без .exe, в нижнем регистре.</summary>
    public string ForegroundProcess { get; private set; } = "";

    // Определять процесс по окну — не самая дешёвая операция, а активное окно
    // между событиями не меняется. Помним последнее.
    private nint _lastForeground;
    private string _lastProcess = "";

    private string ProcessNameOf(nint window)
    {
        if (window == _lastForeground) return _lastProcess;

        _lastForeground = window;
        _lastProcess = "";

        try
        {
            Win32.GetWindowThreadProcessId(window, out var pid);
            if (pid != 0)
                using (var process = System.Diagnostics.Process.GetProcessById((int)pid))
                    _lastProcess = process.ProcessName.ToLowerInvariant();
        }
        catch
        {
            // Процесс мог завершиться между вызовами — это не ошибка.
        }

        return _lastProcess;
    }

    /// <summary>Вызывается, когда состояние изменилось. Приходит из потока хука.</summary>
    public event Action<Occlusion>? StateChanged;

    public ForegroundWatcher()
    {
        _proc = OnWinEvent;

        _hookForeground = Win32.SetWinEventHook(
            Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_MINIMIZEEND,
            0, _proc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);

        _hookLocation = Win32.SetWinEventHook(
            Win32.EVENT_OBJECT_LOCATIONCHANGE, Win32.EVENT_OBJECT_LOCATIONCHANGE,
            0, _proc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
    }

    /// <summary>Задаёт зону островка на экране и хэндл собственного окна.</summary>
    public void SetIslandRect(RECT screenRect, nint selfWindow)
    {
        _islandScreenRect = screenRect;
        _selfWindow = selfWindow;
        Reevaluate(force: true);
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild,
                           uint thread, uint time)
    {
        // LOCATIONCHANGE сыплется и от курсора, и от подсказок — интересует
        // только само окно (idObject == OBJID_WINDOW) и только активное.
        if (eventType == Win32.EVENT_OBJECT_LOCATIONCHANGE)
        {
            if (idObject != 0) return;
            if (hwnd != Win32.GetForegroundWindow()) return;
        }

        Reevaluate();
    }

    public void Reevaluate(bool force = false)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!force && now - _lastEvaluateTicks < ThrottleTicks) return;
        _lastEvaluateTicks = now;

        var previousProcess = ForegroundProcess;
        var next = Evaluate();

        if (next == State && previousProcess == ForegroundProcess) return;

        State = next;
        StateChanged?.Invoke(next);
    }

    private Occlusion Evaluate()
    {
        // Игры и полноэкранное видео Windows сообщает сама — это надёжнее,
        // чем сравнивать прямоугольники.
        if (Win32.SHQueryUserNotificationState(out var notifyState) == 0)
        {
            if (notifyState is Win32.QUNS_RUNNING_D3D_FULL_SCREEN or Win32.QUNS_PRESENTATION_MODE)
                return Occlusion.Fullscreen;
        }

        var fg = Win32.GetForegroundWindow();
        if (fg == 0 || fg == _selfWindow) { ForegroundProcess = ""; return Occlusion.Clear; }

        ForegroundProcess = ProcessNameOf(fg);

        if (Win32.IsIconic(fg) || !Win32.IsWindowVisible(fg)) return Occlusion.Clear;
        if (!Win32.GetWindowRect(fg, out var rect)) return Occlusion.Clear;

        // Окно во весь монитор, но без рамки — это полноэкранный режим (F11 в браузере).
        var monitor = Win32.MonitorFromWindow(fg, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (Win32.GetMonitorInfoW(monitor, ref mi))
        {
            if (rect.Left <= mi.rcMonitor.Left && rect.Top <= mi.rcMonitor.Top &&
                rect.Right >= mi.rcMonitor.Right && rect.Bottom >= mi.rcMonitor.Bottom)
                return Occlusion.Fullscreen;
        }

        return Intersects(rect, _islandScreenRect) ? Occlusion.Covered : Occlusion.Clear;
    }

    private static bool Intersects(in RECT a, in RECT b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    public void Dispose()
    {
        if (_hookForeground != 0) Win32.UnhookWinEvent(_hookForeground);
        if (_hookLocation != 0) Win32.UnhookWinEvent(_hookLocation);
        GC.KeepAlive(_proc);
    }
}
