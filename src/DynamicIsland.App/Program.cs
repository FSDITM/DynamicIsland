using System.Diagnostics;
using System.Runtime.InteropServices;
using DynamicIsland.Interop;
using DynamicIsland.Platform;
using DynamicIsland.Rendering;
using DynamicIsland.Services;
using DynamicIsland.Ui;
using SkiaSharp;

namespace DynamicIsland;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // До создания любого окна: иначе Windows отдаст нам виртуализованные
        // координаты и всё поедет на мониторах с масштабированием.
        Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        // Второй экземпляр не нужен: две иконки в трее и два островка поверх друг друга.
        using var mutex = new Mutex(true, @"Local\DynamicIsland.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Win32.MessageBoxW(0, "DynamicIsland уже запущен.", "DynamicIsland", 0x40);
            return 0;
        }

        // --snapshot <папка>: отрисовать состояния островка в PNG тем же кодом,
        // что рисует на экране. Позволяет проверять вёрстку без снимков экрана.
        var snapIdx = Array.IndexOf(args, "--snapshot");
        if (snapIdx >= 0 && snapIdx + 1 < args.Length)
        {
            Ui.SnapshotRenderer.RenderAll(args[snapIdx + 1]);
            return 0;
        }

        var selfTest = 0.0;
        var idx = Array.IndexOf(args, "--selftest");
        if (idx >= 0 && idx + 1 < args.Length) double.TryParse(args[idx + 1], out selfTest);

        try
        {
            using var app = new IslandApp(selfTest);
            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write("FATAL " + ex);
            Win32.MessageBoxW(0, ex.ToString(), "DynamicIsland — ошибка запуска", 0x10);
            return 1;
        }
    }
}

internal static class Log
{
    private static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "dynamicisland.log");
    private static readonly Lock Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}"); }
            catch { /* лог не должен ронять приложение */ }
        }
    }
}

internal sealed class IslandApp : IDisposable
{
    // Логический размер «сцены» — окна, внутри которого живёт островок.
    // Запас нужен под раскрытое состояние и тень.
    private const int StageLogicalWidth = 620;
    private const int StageLogicalHeight = 210;

    private const int MenuAutostart = 1;
    private const int MenuMetrics = 2;
    private const int MenuExit = 4;

    private readonly OverlayWindow _window = new();
    private readonly GpuCompositionHost _gpu = new();
    private readonly Island _island = new();
    private readonly IslandContent _content = new();
    private readonly TrayIcon _tray = new();
    private readonly ForegroundWatcher _watcher = new();
    private readonly MediaService _media = new();
    private readonly PowerService _power = new();

    private Timer? _clockTimer;

    private bool _running = true;
    private bool _needsRedraw = true;
    private bool _scrubbing;
    private bool _showHud;

    // Курсор должен задержаться над островком, прежде чем тот раскроется.
    private static readonly long DwellTicks = Stopwatch.Frequency * 220 / 1000;
    private bool _cursorInside;
    private long _cursorEnteredAt;

    private SKImage? _artwork;
    private int _artworkVersion;

    private ForegroundWatcher.Occlusion _occlusion = ForegroundWatcher.Occlusion.Clear;

    // Показ нового трека: островок сам раскрывается на пару секунд, когда
    // сменилась песня, и сворачивается обратно.
    private static readonly TimeSpan PeekDuration = TimeSpan.FromSeconds(3.5);
    private string _lastTrackKey = "";
    private long _peekUntilTicks;
    private Timer? _peekTimer;

    // Метрики
    private double _cpuFrameMs;
    private double _fps;
    private long _framesRendered;
    private readonly Stopwatch _fpsWindow = Stopwatch.StartNew();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    /// <summary>
    /// Пока играет музыка, эквалайзер в компактном виде должен шевелиться.
    /// Держим для него ~18 кадров в секунду вместо 60: движение читается,
    /// а расход остаётся в районе процента ядра.
    /// </summary>
    private const uint MusicFrameIntervalMs = 55;
    private int _fpsCounter;

    private readonly SKPaint _hudPaint = new() { Color = new SKColor(150, 255, 165, 235), IsAntialias = true };
    private readonly SKFont _hudFont = new(SKTypeface.Default, 11);

    // Самопроверка
    private readonly double _selfTestSeconds;
    private bool _selfTestExpanded;
    private double _worstFrameMs;
    private long _worstFrameIndex;
    private double _sumFrameMs;
    private double _firstFrameMs;
    private const long WarmupFrames = 30;
    private double _steadyCpuStartMs;
    private long _steadyStartTicks;
    private long _steadyStartFrames;

    public IslandApp(double selfTestSeconds = 0)
    {
        _selfTestSeconds = selfTestSeconds;
        _showHud = selfTestSeconds > 0;
    }

    public void Run()
    {
        // Окно создаём заглушкой у верхнего края нужного монитора: узнать его DPI
        // можно только по готовому окну, а от DPI зависит размер сцены.
        var (x, y) = MonitorTopCenter();
        _window.Create(x, y, 200, 100);

        _window.HitTest = HitTest;
        _window.MouseMoved += OnMouseMoved;
        _window.MouseLeft += OnMouseLeft;
        _window.MouseButton += OnMouseButton;
        _window.RightClicked += ShowTrayMenu;
        _window.CaptureLost += () =>
        {
            _scrubbing = false;
            _content.ScrubProgress = null;
            _needsRedraw = true;
        };
        _window.TrayMessage += OnTrayMessage;
        _window.Woken += () => _needsRedraw = true;
        _window.Closed += () => _running = false;
        _window.DisplayChanged += RepositionStage;
        _window.DpiChanged += _ => RepositionStage();

        // Теперь DPI известен — задаём настоящий размер сцены до создания
        // swapchain, иначе островок окажется шире окна и обрежется по краям.
        ApplyStageBounds();
        var (w, h) = (_window.Width, _window.Height);

        _gpu.Initialize(_window.Handle, w, h);
        Log.Write($"GPU: {_gpu.AdapterName}; сцена {w}x{h} @ dpi {_window.Dpi}; " +
                  $"слой валидации: {_gpu.DebugLayerStatus}");

        _tray.Add(_window.Handle, "DynamicIsland");

        // Сервисы будят цикл отрисовки сообщением — сами ничего не рисуют.
        _power.Changed += _window.Wake;
        _media.Changed += OnMediaChanged;
        _power.Start();
        _ = _media.StartAsync();

        _watcher.StateChanged += state => { _occlusion = state; _window.Wake(); };
        UpdateWatcherRect();

        StartClockTimer();
        RenderLoop();
    }

    private static MONITORINFO CurrentMonitor()
    {
        Win32.GetCursorPos(out var cursor);
        var monitor = Win32.MonitorFromPoint(cursor, Win32.MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        Win32.GetMonitorInfoW(monitor, ref info);
        return info;
    }

    private static (int x, int y) MonitorTopCenter()
    {
        var info = CurrentMonitor();
        return (info.rcMonitor.Left + info.rcMonitor.Width / 2, info.rcMonitor.Top);
    }

    /// <summary>
    /// Ставит сцену по центру верхнего края монитора, переводя логический размер
    /// в физические пиксели по текущему DPI. Сцена должна быть заметно шире
    /// островка — в запас идут тень и раскрытое состояние.
    /// </summary>
    private void ApplyStageBounds()
    {
        var info = CurrentMonitor();
        var scale = _window.Scale;

        var w = (int)MathF.Round(StageLogicalWidth * scale);
        var h = (int)MathF.Round(StageLogicalHeight * scale);

        // Если монитор узкий, сцену шире него делать нельзя.
        w = Math.Min(w, info.rcMonitor.Width);

        var x = info.rcMonitor.Left + (info.rcMonitor.Width - w) / 2;
        var y = info.rcMonitor.Top;

        _window.SetBounds(x, y, w, h);
    }

    private void RepositionStage()
    {
        ApplyStageBounds();
        _gpu.Resize(_window.Width, _window.Height);
        UpdateWatcherRect();
        _needsRedraw = true;
    }

    /// <summary>
    /// Сообщает наблюдателю, какую зону экрана занимает островок.
    ///
    /// Зона считается по размеру покоя и не зависит от текущей анимации: нас
    /// интересует «есть ли окно там, где островок живёт», а не его сиюминутные
    /// габариты. Иначе пришлось бы обновлять зону каждый кадр — а это системные
    /// вызовы в горячем цикле.
    /// </summary>
    private void UpdateWatcherRect()
    {
        var scale = _window.Scale;
        var w = _island.RestSize.X * scale;
        var h = (_island.RestSize.Y + _island.TopOffset) * scale;
        var rect = new SKRect((_gpu.Width - w) / 2f, 0, (_gpu.Width + w) / 2f, h);

        Win32.GetWindowRect(_window.Handle, out var windowRect);

        var screenRect = new RECT
        {
            Left = windowRect.Left + (int)rect.Left,
            Top = windowRect.Top + (int)rect.Top,
            Right = windowRect.Left + (int)rect.Right,
            Bottom = windowRect.Top + (int)rect.Bottom,
        };
        _watcher.SetIslandRect(screenRect, _window.Handle);
    }

    private void StartClockTimer()
    {
        // Просыпаемся на границе минуты, а не каждую секунду: в компактном виде
        // на экране только часы и минуты.
        void Schedule()
        {
            var now = DateTime.Now;
            var delay = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond);
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            _clockTimer?.Change(delay, Timeout.InfiniteTimeSpan);
        }

        _clockTimer = new Timer(_ => { _window.Wake(); Schedule(); }, null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Schedule();

        // Одноразовый будильник на момент, когда показ нового трека истечёт.
        _peekTimer = new Timer(_ => _window.Wake(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Сменился трек — показываем его на несколько секунд и убираемся обратно.
    /// Приходит из потока WinRT, поэтому только выставляем срок и будим цикл.
    /// </summary>
    private void OnMediaChanged()
    {
        var snapshot = _media.Snapshot;
        var key = snapshot.HasSession ? $"{snapshot.AppId}|{snapshot.Title}|{snapshot.Artist}" : "";

        if (key != _lastTrackKey)
        {
            var wasFirst = _lastTrackKey.Length == 0;
            _lastTrackKey = key;

            // На старте не раскрываемся: приложение только что запустилось,
            // это не «сменился трек».
            if (!wasFirst && key.Length > 0)
            {
                Volatile.Write(ref _peekUntilTicks, (DateTime.UtcNow + PeekDuration).Ticks);
                // Разбудить в момент истечения показа, иначе островок останется
                // раскрытым до следующего движения мыши.
                _peekTimer?.Change(PeekDuration + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
            }
        }

        _window.Wake();
    }

    private bool IsPeeking => DateTime.UtcNow.Ticks < Volatile.Read(ref _peekUntilTicks);

    private IslandMode ResolveMode()
    {
        // В самопроверке меряем анимацию, а не реакцию на чужие окна: иначе
        // результат зависит от того, что было развёрнуто на экране в момент запуска.
        if (_selfTestSeconds > 0) return _selfTestExpanded ? IslandMode.Expanded : IslandMode.Rest;

        // В полноэкранном режиме молчим даже про новый трек — там играют или смотрят.
        if (_occlusion == ForegroundWatcher.Occlusion.Fullscreen) return IslandMode.Hidden;
        // Пока тянут ползунок, островок не сворачивается, даже если курсор
        // ушёл за его край.
        if (HoverEngaged || _scrubbing || IsPeeking) return IslandMode.Expanded;
        if (_occlusion == ForegroundWatcher.Occlusion.Covered) return IslandMode.Notch;
        return IslandMode.Rest;
    }

    /// <summary>Зона, попадание в которую начинает отсчёт до раскрытия.</summary>
    private SKRect TriggerRect()
    {
        var rect = _island.GetRect(_gpu.Width, _window.Scale);
        // У полоски своя высота в семь пикселей — в неё мышью не попасть,
        // поэтому по вертикали зона чуть больше самой фигуры.
        var marginY = (_island.Mode == IslandMode.Notch ? 10f : 6f) * _window.Scale;
        rect.Inflate(6f * _window.Scale, marginY);
        return rect;
    }

    /// <summary>Фигура островка — только здесь клики принадлежат нам.</summary>
    private SKRect ShapeRect()
    {
        var rect = _island.GetRect(_gpu.Width, _window.Scale);
        rect.Inflate(4f * _window.Scale, 4f * _window.Scale);
        return rect;
    }

    /// <summary>
    /// Ответ на WM_NCHITTEST.
    ///
    /// Клики забираем только когда островок уже раскрыт и курсор на его фигуре.
    /// Всё остальное время окно прозрачно для мыши: раньше оно возвращало
    /// HTCLIENT для раздутого прямоугольника, и нажатия в чужих окнах у верхнего
    /// края экрана молча пропадали — окно их съедало, не забирая фокус.
    ///
    /// Само сообщение приходит и при HTTRANSPARENT, поэтому следить за курсором
    /// это не мешает.
    /// </summary>
    private bool HitTest(int clientX, int clientY)
    {
        if (_island.Mode == IslandMode.Hidden)
        {
            UpdateCursorPresence(false);
            return false;
        }

        UpdateCursorPresence(TriggerRect().Contains(clientX, clientY));

        if (_scrubbing) return true;

        // Условие именно «пользователь навёл», а не «островок раскрыт»: при
        // автопоказе нового трека он раскрывается сам, и брать на себя клики
        // в это время нельзя — они принадлежат тому окну, где человек работает.
        return HoverEngaged && ShapeRect().Contains(clientX, clientY);
    }

    /// <summary>
    /// Курсор вошёл в зону или вышел из неё. Раскрытие происходит не сразу:
    /// без задержки островок распахивался от любого движения мыши вдоль верха
    /// экрана и закрывал собой чужие окна.
    /// </summary>
    private void UpdateCursorPresence(bool inside)
    {
        if (inside == _cursorInside) return;

        _cursorInside = inside;
        _cursorEnteredAt = inside ? Stopwatch.GetTimestamp() : 0;
        _needsRedraw = true;
    }

    private bool HoverEngaged => _cursorInside &&
        Stopwatch.GetTimestamp() - _cursorEnteredAt >= DwellTicks;

    /// <summary>
    /// Пока островок раскрыт, проверяем курсор сами: за пределами его фигуры
    /// окно прозрачно для мыши и WM_MOUSELEAVE может не прийти.
    /// </summary>
    private void PollCursorWhileExpanded()
    {
        if (_scrubbing) return;
        if (_island.Mode != IslandMode.Expanded && !_cursorInside) return;

        Win32.GetCursorPos(out var screen);
        var point = screen;
        Win32.ScreenToClient(_window.Handle, ref point);

        UpdateCursorPresence(TriggerRect().Contains(point.X, point.Y));
    }

    private void OnMouseLeft()
    {
        // Во время перетаскивания курсор законно уходит за край островка —
        // сворачиваться нельзя.
        if (_scrubbing) return;

        UpdateCursorPresence(false);
        _needsRedraw = true;
    }

    private void OnMouseButton(bool down)
    {
        var x = _window.CursorClient.X;
        var y = _window.CursorClient.Y;

        if (down)
        {
            // Нажатие на полосу — сразу переходим в режим перетаскивания и
            // перехватываем мышь, чтобы не потерять её за краем островка.
            if (_media.Snapshot.HasTimeline && _content.SeekHitRect.Contains(x, y))
            {
                _scrubbing = true;
                _content.ScrubProgress = _content.ProgressFromX(x);
                _window.CaptureMouse();
                _needsRedraw = true;
            }
            return;
        }

        if (_scrubbing)
        {
            var target = _content.ScrubProgress ?? 0f;
            _scrubbing = false;
            _content.ScrubProgress = null;
            OverlayWindow.ReleaseMouse();
            _ = _media.SeekAsync(target);
            _needsRedraw = true;
            return;
        }

        switch (_content.HitButton(x, y))
        {
            case IslandButton.PlayPause: _ = _media.TogglePlayPauseAsync(); break;
            case IslandButton.Next: _ = _media.NextAsync(); break;
            case IslandButton.Previous: _ = _media.PreviousAsync(); break;
        }
    }

    private void OnMouseMoved()
    {
        if (_scrubbing)
            _content.ScrubProgress = _content.ProgressFromX(_window.CursorClient.X);

        _needsRedraw = true;
    }

    private void OnTrayMessage(uint mouseMessage)
    {
        if (mouseMessage is Win32.WM_RBUTTONUP or Win32.WM_LBUTTONUP) ShowTrayMenu();
    }

    private void ShowTrayMenu()
    {
        var items = new List<(int, string, bool)>
        {
            (MenuAutostart, "Запускать при входе в систему", StartupRegistration.IsEnabled),
            (MenuMetrics, "Показывать метрики", _showHud),
            (0, "", false),
            (MenuExit, "Выход", false),
        };

        switch (TrayIcon.ShowMenu(_window.Handle, items))
        {
            case MenuAutostart:
                StartupRegistration.Set(!StartupRegistration.IsEnabled);
                break;
            case MenuMetrics:
                _showHud = !_showHud;
                _needsRedraw = true;
                break;
            case MenuExit:
                _running = false;
                break;
        }
    }

    private void RenderLoop()
    {
        var waitHandles = new[] { _gpu.FrameLatencyWaitable };
        var empty = Array.Empty<nint>();
        var last = Stopwatch.GetTimestamp();

        var testClock = Stopwatch.StartNew();
        var nextToggle = 0.0;
        var idleWaits = 0L;
        var lastMode = _island.Mode;

        while (_running)
        {
            if (!_window.PumpMessages()) break;

            if (_selfTestSeconds > 0)
            {
                if (testClock.Elapsed.TotalSeconds >= _selfTestSeconds) break;
                if (testClock.Elapsed.TotalSeconds >= nextToggle)
                {
                    _selfTestExpanded = !_selfTestExpanded;
                    nextToggle += 0.7;
                    _needsRedraw = true;
                }
            }

            PollCursorWhileExpanded();

            var mode = ResolveMode();
            if (mode != lastMode)
            {
                _island.SetMode(mode);
                lastMode = mode;
                _needsRedraw = true;
            }

            // Забираем обложку во владение — сервис после этого её не трогает.
            if (_media.TryTakeArtwork(ref _artworkVersion, out var newArtwork))
            {
                _artwork?.Dispose();
                _artwork = newArtwork;
                _needsRedraw = true;
            }

            var now = Stopwatch.GetTimestamp();
            var dt = (float)((now - last) / (double)Stopwatch.Frequency);
            last = now;
            dt = Math.Min(dt, 0.1f);

            _island.Update(dt);

            var animating = _island.IsAnimating;
            if (animating || _needsRedraw)
            {
                _gpu.RenderFrame(DrawFrame);

                // Ожидание fence — это сон в ожидании GPU, а не работа процессора.
                // В стоимость кадра его включать нельзя, иначе метрика врёт.
                _cpuFrameMs = _gpu.LastDrawMs + _gpu.LastFlushMs + _gpu.LastPresentMs;

                _needsRedraw = false;
                _framesRendered++;
                _fpsCounter++;

                if (_framesRendered == 1) _firstFrameMs = _cpuFrameMs;
                else
                {
                    _sumFrameMs += _cpuFrameMs;
                    if (_cpuFrameMs > _worstFrameMs)
                    {
                        _worstFrameMs = _cpuFrameMs;
                        _worstFrameIndex = _framesRendered;
                    }
                }

                if (_framesRendered == WarmupFrames)
                {
                    _steadyCpuStartMs = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
                    _steadyStartTicks = Stopwatch.GetTimestamp();
                    _steadyStartFrames = _framesRendered;
                }
            }

            if (_fpsWindow.ElapsedMilliseconds >= 500)
            {
                _fps = _fpsCounter * 1000.0 / _fpsWindow.ElapsedMilliseconds;
                _fpsCounter = 0;
                _fpsWindow.Restart();
            }

            if (animating)
            {
                // Ждём готовности DXGI принять следующий кадр — привязка к развёртке.
                OverlayWindow.WaitForInputOrHandles(waitHandles, 100);
            }
            else if (_media.Snapshot.IsPlaying &&
                     _island.Mode is IslandMode.Rest or IslandMode.Expanded)
            {
                // Играет музыка: в компактном виде крутится эквалайзер,
                // в раскрытом ползёт полоса перемотки. И то и другое —
                // на пониженной частоте.
                OverlayWindow.WaitForInputOrHandles(empty, MusicFrameIntervalMs);
                _needsRedraw = true;
                last = Stopwatch.GetTimestamp();
            }
            else if (_cursorInside || _island.Mode == IslandMode.Expanded || _scrubbing)
            {
                // Курсор рядом или островок раскрыт: нужно вовремя заметить
                // истечение задержки перед раскрытием и уход курсора.
                OverlayWindow.WaitForInputOrHandles(empty, 100);
                last = Stopwatch.GetTimestamp();
            }
            else
            {
                // Простой: поток спит до ввода. Ноль кадров, ноль CPU.
                _fps = 0;
                idleWaits++;
                OverlayWindow.WaitForInputOrHandles(empty, _selfTestSeconds > 0 ? 8u : Win32.INFINITE);
                last = Stopwatch.GetTimestamp();
            }
        }

        if (_selfTestSeconds > 0) WriteSelfTestReport(testClock, idleWaits);
    }

    private void WriteSelfTestReport(Stopwatch testClock, long idleWaits)
    {
        var cpu = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
        Log.Write($"SELFTEST длительность={testClock.Elapsed.TotalSeconds:0.00}s " +
                  $"кадров={_framesRendered} " +
                  $"кадр_сред={_sumFrameMs / Math.Max(1, _framesRendered - 1):0.000}ms " +
                  $"кадр_худший={_worstFrameMs:0.000}ms (кадр №{_worstFrameIndex}) " +
                  $"первый_кадр={_firstFrameMs:0.000}ms " +
                  $"засыпаний={idleWaits} CPU_всего={cpu:0}ms");

        if (_steadyStartTicks != 0)
        {
            var wallMs = (Stopwatch.GetTimestamp() - _steadyStartTicks) * 1000.0 / Stopwatch.Frequency;
            var cpuMs = cpu - _steadyCpuStartMs;
            var frames = _framesRendered - _steadyStartFrames;
            Log.Write($"УСТАНОВИВШИЙСЯ РЕЖИМ кадров={frames} fps={frames * 1000.0 / wallMs:0.0} " +
                      $"CPU={cpuMs / wallMs * 100:0.0}% одного ядра " +
                      $"({cpuMs / Math.Max(1, frames):0.00}ms CPU на кадр)");
        }

        Log.Write($"СТАДИИ (последний кадр) ожидание={_gpu.LastWaitMs:0.000} " +
                  $"отрисовка={_gpu.LastDrawMs:0.000} flush={_gpu.LastFlushMs:0.000} " +
                  $"present={_gpu.LastPresentMs:0.000}");

        if (!_gpu.ValidationEnabled)
        {
            // Важно не выдавать молчание отключённого слоя за успешную проверку.
            Log.Write($"ВАЛИДАЦИЯ D3D12: НЕ ВЫПОЛНЕНА — слой {_gpu.DebugLayerStatus}");
            Log.Write($"ЗАМЕНА ПРОВЕРКИ: {_gpu.DeviceHealth} (ловит только грубые ошибки)");
        }
        else
        {
            var messages = _gpu.DrainValidationMessages();
            if (messages.Count == 0)
                Log.Write("ВАЛИДАЦИЯ D3D12: сообщений нет — состояния ресурсов сходятся");
            else
                foreach (var m in messages.Take(25)) Log.Write("ВАЛИДАЦИЯ " + m);
        }
    }

    private void DrawFrame(SKCanvas canvas)
    {
        // Прямоугольники кнопок известны с прошлого кадра — задержка в один кадр
        // при наведении незаметна, зато не нужно считать раскладку дважды.
        _content.HoveredButton = _content.HitButton(_window.CursorClient.X, _window.CursorClient.Y);
        _content.SeekHovered = _scrubbing ||
            _content.SeekHitRect.Contains(_window.CursorClient.X, _window.CursorClient.Y);
        _content.MusicPhase = (float)_uptime.Elapsed.TotalSeconds;

        _content.Draw(canvas, _island, _gpu.Width, _window.Scale,
            _media.Snapshot, _artwork, _power.Snapshot);

        if (_showHud) DrawHud(canvas);
    }

    private void DrawHud(SKCanvas canvas)
    {
        var scale = _window.Scale;
        _hudFont.Size = 11 * scale;

        Span<string> lines =
        [
            $"{_gpu.AdapterName}  |  сцена {_gpu.Width}x{_gpu.Height}  dpi {_window.Dpi}",
            $"кадр {_cpuFrameMs:0.00} ms   fps {_fps:0}   всего {_framesRendered}",
            $"режим {_island.Mode}  окна: {_occlusion}  {(_island.IsAnimating ? "анимация" : "простой")}",
        ];

        var y = _gpu.Height - (lines.Length * 15f + 6f) * scale;
        foreach (var line in lines)
        {
            canvas.DrawText(line, 10 * scale, y, SKTextAlign.Left, _hudFont, _hudPaint);
            y += 15f * scale;
        }
    }

    public void Dispose()
    {
        _clockTimer?.Dispose();
        _peekTimer?.Dispose();
        _tray.Dispose();
        _watcher.Dispose();
        _power.Dispose();
        _media.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));

        _artwork?.Dispose();
        _hudPaint.Dispose();
        _hudFont.Dispose();
        _content.Dispose();
        _gpu.Dispose();
        _window.Dispose();
    }
}
