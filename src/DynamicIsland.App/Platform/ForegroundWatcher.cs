using System.Diagnostics;
using DynamicIsland.Interop;

namespace DynamicIsland.Platform;

/// <summary>
/// Следит за активным окном, чтобы островок мог убраться с дороги.
///
/// Специально на событиях, а не на таймере: <see cref="Win32.SetWinEventHook"/>
/// будит нас только когда активное окно сменилось, свернулось или развернулось.
/// Опрос активного окна 60 раз в секунду сам по себе был бы такой же ошибкой,
/// как WMI в рендер-цикле.
///
/// Тонкость, которая стоила бага: в момент EVENT_SYSTEM_FOREGROUND окно ещё
/// не готово. Замер показал IsWindowVisible == false и старые координаты прямо
/// на этом событии — правду сообщают события, которые приходят через 10–30 мс
/// следом. Поэтому каждый всплеск заканчивается отложенным «дозором»
/// (см. <see cref="ArmSettle"/>), а <see cref="Poll"/> служит страховкой
/// на случай потерянного события.
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

    /// <summary>
    /// Диагностический канал для <c>--watchtest</c>. В обычной работе null,
    /// поэтому стоит одну проверку ссылки на событие.
    /// </summary>
    internal static Action<string>? Trace;

    // Хук держит указатель на делегат; без поля его соберёт GC.
    private readonly Win32.WinEventProc _proc;
    private readonly nint _hookForeground;

    // Переоценка приходит из трёх мест: поток хука, таймер дозора и рендер-цикл.
    private readonly Lock _gate = new();

    private RECT _islandScreenRect;
    private nint _islandMonitor;
    private nint _selfWindow;

    // Смена окна приходит всплеском из нескольких событий подряд. Частоту
    // переоценки ограничиваем, но отброшенное событие больше не теряется:
    // за ним всё равно придёт дозор. Именно потеря таких событий и была багом.
    private long _lastEvaluateTicks;
    private static readonly long ThrottleTicks = Stopwatch.Frequency / 20;

    // Дозор: одноразовый таймер, который каждое новое событие отодвигает.
    // Всплеск из двадцати событий превращается в две проверки — сразу после
    // того как всё утихло, и ещё раз, когда доиграет анимация разворачивания.
    private readonly Timer _settle;
    private int _settlePassesLeft;
    private const int FirstSettleMs = 140;
    private const int SecondSettleMs = 380;

    // Что видела последняя настоящая переоценка. По этому Poll решает,
    // есть ли вообще смысл считать заново.
    private nint _seenForeground;
    private RECT _seenRect;
    private int _seenNotifyState;

    private long _lastPollTicks;
    private static readonly long PollTicks = Stopwatch.Frequency / 8;

    public Occlusion State { get; private set; } = Occlusion.Clear;

    /// <summary>
    /// Имя процесса окна, стоящего над островком, без .exe и в нижнем регистре.
    /// Именно по нему решается, сворачиваться ли в полоску.
    /// </summary>
    public string OccludingProcess { get; private set; } = "";

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
                using (var process = Process.GetProcessById((int)pid))
                    _lastProcess = process.ProcessName.ToLowerInvariant();
        }
        catch
        {
            // Процесс мог завершиться между вызовами — это не ошибка.
        }

        return _lastProcess;
    }

    /// <summary>Вызывается, когда состояние изменилось. Приходит не из главного потока.</summary>
    public event Action<Occlusion>? StateChanged;

    public ForegroundWatcher()
    {
        _proc = OnWinEvent;
        _settle = new Timer(OnSettle, null, Timeout.Infinite, Timeout.Infinite);

        // Диапазон 0x0003…0x0017 — это смена активного окна, сворачивание,
        // разворачивание и конец перетаскивания. Ровно то, от чего зависит,
        // мешает ли островок.
        //
        // EVENT_OBJECT_LOCATIONCHANGE сюда сознательно не входит. Он приходит
        // и от курсора: замер показал 1813 таких событий за 20 секунд, каждое
        // будило поток впустую. Перемещение окна теперь ловит Poll — на четверть
        // секунды позже, зато без девяноста пробуждений в секунду.
        _hookForeground = Win32.SetWinEventHook(
            Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_MINIMIZEEND,
            0, _proc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
    }

    /// <summary>Задаёт зону островка на экране и хэндл собственного окна.</summary>
    public void SetIslandRect(RECT screenRect, nint selfWindow)
    {
        lock (_gate)
        {
            _islandScreenRect = screenRect;
            _selfWindow = selfWindow;

            // Монитор островка нужен, чтобы не прятаться из-за полноэкранного
            // окна на соседнем экране. Центр зоны всегда внутри монитора.
            _islandMonitor = Win32.MonitorFromPoint(new POINT
            {
                X = (screenRect.Left + screenRect.Right) / 2,
                Y = (screenRect.Top + screenRect.Bottom) / 2,
            }, Win32.MONITOR_DEFAULTTONEAREST);
        }
        Reevaluate(force: true);
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild,
                           uint thread, uint time)
    {
        Trace?.Invoke($"событие 0x{eventType:X4} hwnd=0x{hwnd:X} idObject={idObject}");

        // Интересует само окно, а не его меню, полосы прокрутки и подсказки.
        if (idObject != 0) { Trace?.Invoke("   пропуск: не окно"); return; }

        // Сначала быстрая реакция — в большинстве случаев она уже верна.
        Reevaluate();

        // Затем дозор: он и есть тот, кто разберётся с окном, которое на
        // момент события ещё говорило о себе неправду.
        ArmSettle();
    }

    /// <summary>
    /// Заводит отложенную переоценку. Каждое новое событие отодвигает срок,
    /// поэтому всплеск при смене окна гасится в один заход.
    /// </summary>
    private void ArmSettle()
    {
        Volatile.Write(ref _settlePassesLeft, 2);
        try { _settle.Change(FirstSettleMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { /* приложение закрывается */ }
    }

    private void OnSettle(object? _)
    {
        Trace?.Invoke("   дозор");
        Reevaluate(force: true);

        // Второй заход — на случай долгой анимации разворачивания окна.
        if (Interlocked.Decrement(ref _settlePassesLeft) > 0)
        {
            try { _settle.Change(SecondSettleMs, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Страховка от потерянного события: два системных вызова, и если активное
    /// окно с прошлой переоценки не изменилось — сразу выход. Зовётся из
    /// рендер-цикла, но не чаще восьми раз в секунду. Она же ловит перемещение
    /// и разворачивание окна, ради которых раньше висел хук на LOCATIONCHANGE.
    ///
    /// Нужна потому, что цена ошибки несимметрична: пропущенное событие
    /// оставляет островок в неправильном виде навсегда, а лишняя проверка
    /// стоит доли микросекунды.
    /// </summary>
    public void Poll()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - _lastPollTicks < PollTicks) return;
        _lastPollTicks = now;

        var fg = Win32.GetForegroundWindow();
        Win32.GetWindowRect(fg, out var rect);

        bool windowChanged, hiding;
        int seenNotify;
        lock (_gate)
        {
            windowChanged = fg != _seenForeground || !Same(rect, _seenRect);
            hiding = State == Occlusion.Fullscreen;
            seenNotify = _seenNotifyState;
        }

        if (!windowChanged)
        {
            // Пока островок виден, переспрашивать состояние уведомлений незачем:
            // вход в полный экран сопровождается сменой окна, её ловит хук.
            // А вот ВЫХОД из полного экрана события окна может не дать вовсе —
            // и без этой проверки Fullscreen залипал бы навсегда.
            //
            // Спрашиваем только пока прячемся, и по делу: замер показал, что
            // сам опрос стоит 0.23 мс, но каждое изменение тянет за собой
            // переоценку с обходом окон, а это уже 3 мс. Состояние уведомлений
            // естественно колеблется, поэтому безусловная сверка разгоняла
            // холостой расход с 0.06 до 2.3 процента ядра.
            if (!hiding) return;
            if (QueryNotifyState() == seenNotify) return;

            Trace?.Invoke("   страховка: изменилось состояние уведомлений системы");
        }
        else
        {
            Trace?.Invoke("   страховка: активное окно разошлось с последней оценкой");
        }

        Reevaluate(force: true);
    }

    public void Reevaluate(bool force = false)
    {
        Occlusion next;

        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (!force && now - _lastEvaluateTicks < ThrottleTicks)
            {
                Trace?.Invoke("   отложено до дозора");
                return;
            }
            _lastEvaluateTicks = now;

            var previousProcess = OccludingProcess;
            next = Evaluate();

            if (next == State && previousProcess == OccludingProcess)
            {
                Trace?.Invoke($"   без изменений: {next} / «{OccludingProcess}»");
                return;
            }

            Trace?.Invoke($"   СМЕНА: {State}/«{previousProcess}» -> {next}/«{OccludingProcess}»");
            State = next;
        }

        StateChanged?.Invoke(next);
    }

    private Occlusion Evaluate()
    {
        var fg = Win32.GetForegroundWindow();
        Win32.GetWindowRect(fg, out var rect);

        // Запоминаем то, по чему считали: Poll сверяется именно с этим.
        _seenForeground = fg;
        _seenRect = rect;
        _seenNotifyState = QueryNotifyState();

        // Решает не активное окно, а то, которое реально стоит над островком.
        // На одном мониторе это почти всегда одно и то же окно, но на двух —
        // нет: стоит кликнуть что-нибудь на соседнем экране, и островок
        // разворачивался обратно поверх вкладок браузера, ради которых
        // и придумана полоска.
        var (occupant, occupantRect) = FindOccupant();
        if (occupant == 0)
        {
            OccludingProcess = "";
            return Occlusion.Clear;
        }

        var (state, process) = Classify(occupant, occupantRect);
        OccludingProcess = process;

        // Игры и полноэкранное видео Windows сообщает сама — это надёжнее,
        // чем сравнивать прямоугольники. Но сообщает на всю систему сразу,
        // без указания экрана, поэтому верим этому только когда активное окно
        // на том же мониторе, где островок: иначе игра на одном экране гасила
        // бы островок на другом.
        if (OnIslandMonitor(fg) &&
            _seenNotifyState is Win32.QUNS_RUNNING_D3D_FULL_SCREEN or Win32.QUNS_PRESENTATION_MODE)
            return Occlusion.Fullscreen;

        return state;
    }

    /// <summary>Состояние уведомлений системы; 0, если спросить не удалось.</summary>
    private static int QueryNotifyState() =>
        Win32.SHQueryUserNotificationState(out var state) == 0 ? state : 0;

    /// <summary>
    /// Верхнее по Z-порядку настоящее окно, накрывающее зону островка.
    ///
    /// EnumWindows отдаёт окна сверху вниз, и перебор обрывается на первом
    /// подходящем — обычно это первые несколько окон, потому что островок
    /// живёт у кромки экрана.
    /// </summary>
    private (nint Window, RECT Rect) FindOccupant()
    {
        nint found = 0;
        var foundRect = default(RECT);

        Win32.EnumWindows((window, _) =>
        {
            if (window == _selfWindow) return true;
            if (!Win32.IsWindowVisible(window) || Win32.IsIconic(window)) return true;
            if (!Win32.GetWindowRect(window, out var rect)) return true;
            if (rect.Width <= 0 || rect.Height <= 0) return true;
            if (!Intersects(rect, _islandScreenRect)) return true;
            if (IsShellSurface(window) || IsCloaked(window)) return true;

            // Окно, сквозь которое клики проходят насквозь, ничему не мешает:
            // это чужой оверлей вроде счётчика кадров или экранной линейки.
            // Считать его перекрытием — значит навсегда свернуть островок
            // в полоску из-за виджета, которого пользователь даже не замечает.
            if ((Win32.GetWindowLongPtrW(window, Win32.GWL_EXSTYLE) & Win32.WS_EX_TRANSPARENT) != 0)
                return true;

            found = window;
            foundRect = rect;
            return false;
        }, 0);

        return (found, foundRect);
    }

    /// <summary>
    /// Решение по одному окну. Вынесено из <see cref="Evaluate"/>, чтобы
    /// <c>--watchtest</c> мог прогнать по нему настоящие окна рабочего стола,
    /// а не проверять логику пересказом.
    /// </summary>
    internal (Occlusion State, string Process) Classify(nint window, in RECT rect)
    {
        if (window == 0 || window == _selfWindow) return (Occlusion.Clear, "");

        // Рабочий стол занимает ровно монитор, поэтому без этой проверки он
        // проходил как полноэкранное приложение и островок пропадал от одного
        // клика по обоям. Панель задач — из той же оперы.
        if (IsShellSurface(window)) return (Occlusion.Clear, "");

        var process = ProcessNameOf(window);

        // «Пуск», поиск и центр уведомлений живут в окне размером с монитор,
        // хотя рисуют панель в углу. Считать их полным экраном нельзя.
        if (Array.IndexOf(ShellProcesses, process) >= 0) return (Occlusion.Clear, "");

        if (Win32.IsIconic(window) || !Win32.IsWindowVisible(window) || IsCloaked(window))
            return (Occlusion.Clear, process);

        // Окно во весь монитор, но не развёрнутое — это полноэкранный режим
        // (F11 в браузере, игра в окне без рамки). Развёрнутое окно так считать
        // нельзя: под ним островок должен становиться полоской, а не исчезать, —
        // а на экране со скрытой панелью задач оно занимает тот же прямоугольник.
        if (!Win32.IsZoomed(window) && OnIslandMonitor(window))
        {
            var monitor = Win32.MonitorFromWindow(window, Win32.MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (Win32.GetMonitorInfoW(monitor, ref mi) &&
                rect.Left <= mi.rcMonitor.Left && rect.Top <= mi.rcMonitor.Top &&
                rect.Right >= mi.rcMonitor.Right && rect.Bottom >= mi.rcMonitor.Bottom)
                return (Occlusion.Fullscreen, process);
        }

        return (Intersects(rect, _islandScreenRect) ? Occlusion.Covered : Occlusion.Clear, process);
    }

    // Оболочка Windows: рабочий стол (Progman и его подложка WorkerW),
    // панель задач и всё, что растёт из неё, — «Пуск», поиск, центр уведомлений.
    private static readonly string[] ShellClasses =
        ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    // Оболочка живёт и в отдельных процессах: их окна размером с монитор,
    // но занимают его лишь на треть. ApplicationFrameHost сюда не входит —
    // под ним настоящие приложения из магазина, и им мешать можно.
    private static readonly string[] ShellProcesses =
        ["startmenuexperiencehost", "searchhost", "searchui", "searchapp",
         "shellexperiencehost", "textinputhost"];

    /// <summary>
    /// Окно на том же экране, где живёт островок?
    ///
    /// Пока монитор неизвестен (зона ещё не задана) считаем, что да, — иначе
    /// на старте островок ни разу не спрятался бы под полноэкранным окном.
    /// </summary>
    private bool OnIslandMonitor(nint window) =>
        _islandMonitor == 0 ||
        Win32.MonitorFromWindow(window, Win32.MONITOR_DEFAULTTONEAREST) == _islandMonitor;

    /// <summary>Скрыто композитором: другой рабочий стол или спящее приложение.</summary>
    private static bool IsCloaked(nint window) =>
        Win32.DwmGetWindowAttribute(window, Win32.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
        && cloaked != 0;

    // Буфер локальный, а не поле: Classify зовут и из диагностики, где он был бы
    // общим на два потока. Вызовов единицы в секунду, экономить тут нечего.
    private static bool IsShellSurface(nint window)
    {
        var buffer = new char[64];
        var length = Win32.GetClassNameW(window, buffer, buffer.Length);
        if (length <= 0) return false;

        return Array.IndexOf(ShellClasses, new string(buffer, 0, length)) >= 0;
    }

    private static bool Intersects(in RECT a, in RECT b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static bool Same(in RECT a, in RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    public void Dispose()
    {
        if (_hookForeground != 0) Win32.UnhookWinEvent(_hookForeground);
        _settle.Dispose();
        GC.KeepAlive(_proc);
    }
}
