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
    private bool _hovered;
    private bool _showHud;

    private SKImage? _artwork;
    private int _artworkVersion;

    private ForegroundWatcher.Occlusion _occlusion = ForegroundWatcher.Occlusion.Clear;

    // Метрики
    private double _cpuFrameMs;
    private double _fps;
    private long _framesRendered;
    private readonly Stopwatch _fpsWindow = Stopwatch.StartNew();
    private int _fpsCounter;

    private readonly SKPaint _hudPaint = new() { Color = new SKColor(150, 255, 165, 235), IsAntialias = true };
    private readonly SKFont _hudFont = new(SKTypeface.Default, 11);

    // Самопроверка
    private readonly double _selfTestSeconds;
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
        var (x, y, w, h) = ComputeStageBounds();

        _window.Create(x, y, w, h);
        _window.HitTest = HitTest;
        _window.MouseMoved += () => _needsRedraw = true;
        _window.MouseLeft += OnMouseLeft;
        _window.MouseButton += OnMouseButton;
        _window.RightClicked += ShowTrayMenu;
        _window.TrayMessage += OnTrayMessage;
        _window.Woken += () => _needsRedraw = true;
        _window.Closed += () => _running = false;
        _window.DisplayChanged += RepositionStage;

        _gpu.Initialize(_window.Handle, w, h);
        Log.Write($"GPU: {_gpu.AdapterName}; сцена {w}x{h} @ dpi {_window.Dpi}; " +
                  $"слой валидации: {_gpu.DebugLayerStatus}");

        _tray.Add(_window.Handle, "DynamicIsland");

        // Сервисы будят цикл отрисовки сообщением — сами ничего не рисуют.
        _power.Changed += _window.Wake;
        _media.Changed += _window.Wake;
        _power.Start();
        _ = _media.StartAsync();

        _watcher.StateChanged += state => { _occlusion = state; _window.Wake(); };
        UpdateWatcherRect();

        StartClockTimer();
        RenderLoop();
    }

    private (int x, int y, int w, int h) ComputeStageBounds()
    {
        Win32.GetCursorPos(out var cursor);
        var monitor = Win32.MonitorFromPoint(cursor, Win32.MONITOR_DEFAULTTOPRIMARY);

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        Win32.GetMonitorInfoW(monitor, ref info);

        // Масштаб монитора берём из его геометрии: окна ещё нет, спросить
        // GetDpiForWindow не у чего.
        var scale = _window.Handle != 0 ? _window.Scale : GuessScale(info);
        var w = (int)(StageLogicalWidth * scale);
        var h = (int)(StageLogicalHeight * scale);
        var x = info.rcMonitor.Left + (info.rcMonitor.Width - w) / 2;
        var y = info.rcMonitor.Top;
        return (x, y, w, h);
    }

    private static float GuessScale(in MONITORINFO info)
    {
        // Физические пиксели монитора нам известны, логические — нет.
        // Стартуем от 100%; после создания окна DPI уточняется и сцена
        // пересчитывается в RepositionStage.
        return 1f;
    }

    private void RepositionStage()
    {
        var (x, y, w, h) = ComputeStageBounds();
        _window.SetBounds(x, y, w, h);
        _gpu.Resize(w, h);
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
    }

    private IslandMode ResolveMode()
    {
        // В самопроверке меряем анимацию, а не реакцию на чужие окна: иначе
        // результат зависит от того, что было развёрнуто на экране в момент запуска.
        if (_selfTestSeconds > 0) return _hovered ? IslandMode.Expanded : IslandMode.Rest;

        if (_occlusion == ForegroundWatcher.Occlusion.Fullscreen) return IslandMode.Hidden;
        if (_hovered) return IslandMode.Expanded;
        if (_occlusion == ForegroundWatcher.Occlusion.Covered) return IslandMode.Notch;
        return IslandMode.Rest;
    }

    private bool HitTest(int clientX, int clientY)
    {
        // В полноэкранном режиме окно полностью прозрачно для мыши.
        if (_island.Mode == IslandMode.Hidden && _island.Visibility < 0.05f)
        {
            SetHovered(false);
            return false;
        }

        var rect = _island.GetRect(_gpu.Width, _window.Scale);

        // В свёрнутом виде зона реакции выше самой полоски, иначе в неё
        // невозможно попасть мышью.
        var marginY = (_island.Mode == IslandMode.Notch ? 16f : 12f) * _window.Scale;
        var marginX = 14f * _window.Scale;
        rect.Inflate(marginX, marginY);

        var inside = rect.Contains(clientX, clientY);
        SetHovered(inside);
        return inside;
    }

    private void SetHovered(bool value)
    {
        if (_hovered == value) return;
        _hovered = value;
        _needsRedraw = true;
    }

    private void OnMouseLeft()
    {
        SetHovered(false);
        _needsRedraw = true;
    }

    private void OnMouseButton(bool down)
    {
        if (down) return;

        var button = _content.HitButton(_window.CursorClient.X, _window.CursorClient.Y);
        switch (button)
        {
            case IslandButton.PlayPause: _ = _media.TogglePlayPauseAsync(); break;
            case IslandButton.Next: _ = _media.NextAsync(); break;
            case IslandButton.Previous: _ = _media.PreviousAsync(); break;
        }
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
                    _hovered = !_hovered;
                    nextToggle += 0.7;
                    _needsRedraw = true;
                }
            }

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
