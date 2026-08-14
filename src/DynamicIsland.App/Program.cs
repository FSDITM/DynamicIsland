using System.Diagnostics;
using DynamicIsland.Interop;
using DynamicIsland.Platform;
using DynamicIsland.Rendering;
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

        // --selftest N: погонять анимацию N секунд, записать метрики в лог и выйти.
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
            File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
    }
}

internal sealed class IslandApp : IDisposable
{
    // Логический размер «сцены» — окна, внутри которого живёт островок.
    // Запас нужен под раскрытое состояние, тень и будущие меню.
    private const int StageLogicalWidth = 1000;
    private const int StageLogicalHeight = 300;

    private readonly OverlayWindow _window = new();
    private readonly GpuCompositionHost _gpu = new();
    private readonly Island _island = new();

    private bool _running = true;
    private bool _needsRedraw = true;

    // Метрики
    private double _cpuFrameMs;
    private double _fps;
    private long _framesRendered;
    private readonly Stopwatch _fpsWindow = Stopwatch.StartNew();
    private int _fpsCounter;
    private bool _showHud = true;

    private readonly SKPaint _hudPaint = new() { Color = new SKColor(180, 255, 190, 230), IsAntialias = true };
    private readonly SKFont _hudFont = new(SKTypeface.Default, 12);

    // Самопроверка
    private readonly double _selfTestSeconds;
    private double _worstFrameMs;
    private long _worstFrameIndex;
    private double _sumFrameMs;
    private double _firstFrameMs;

    // Установившийся режим замеряем отдельно от прогрева: первые кадры включают
    // компиляцию шейдеров и JIT, и они смазывают картину.
    private const long WarmupFrames = 30;
    private double _steadyCpuStartMs;
    private long _steadyStartTicks;
    private long _steadyStartFrames;

    public IslandApp(double selfTestSeconds = 0) => _selfTestSeconds = selfTestSeconds;

    public void Run()
    {
        var (x, y, w, h) = ComputeStageBounds();

        _window.Create(x, y, w, h);
        _window.HitTest = HitTest;
        _window.MouseMoved += () => { _needsRedraw = true; };
        _window.MouseLeft += () => { _island.IsHovered = false; _needsRedraw = true; };
        _window.RightClicked += () => { _running = false; };
        _window.Closed += () => { _running = false; };

        _gpu.Initialize(_window.Handle, w, h);
        Log.Write($"GPU: {_gpu.AdapterName}; сцена {w}x{h} @ dpi {_window.Dpi}");

        RenderLoop();
    }

    private (int x, int y, int w, int h) ComputeStageBounds()
    {
        // Монитор под курсором на старте; позже это станет настройкой.
        Win32.GetCursorPos(out var cursor);
        var monitor = Win32.MonitorFromPoint(cursor, Win32.MONITOR_DEFAULTTOPRIMARY);

        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        Win32.GetMonitorInfoW(monitor, ref info);

        // DPI окна ещё не известен (окна нет) — берём масштаб из геометрии монитора
        // позже, а стартуем от 100%. После создания окна пересчитаем.
        var w = StageLogicalWidth;
        var h = StageLogicalHeight;
        var x = info.rcMonitor.Left + (info.rcMonitor.Width - w) / 2;
        var y = info.rcMonitor.Top;
        return (x, y, w, h);
    }

    private bool HitTest(int clientX, int clientY)
    {
        // Интерактивна только фигура островка плюс небольшая кайма — всё
        // остальное окно прозрачно для мыши, клики уходят в окна под нами.
        var rect = _island.GetRect(_gpu.Width, _window.Scale);
        var margin = 14f * _window.Scale;
        rect.Inflate(margin, margin);

        var inside = rect.Contains(clientX, clientY);

        if (inside != _island.IsHovered)
        {
            _island.IsHovered = inside;
            _needsRedraw = true;
        }

        return inside;
    }

    private void RenderLoop()
    {
        var waitHandles = new[] { _gpu.FrameLatencyWaitable };
        var empty = Array.Empty<nint>();
        var last = Stopwatch.GetTimestamp();

        var testClock = Stopwatch.StartNew();
        var nextToggle = 0.0;
        var idleWaits = 0L;

        while (_running)
        {
            if (!_window.PumpMessages()) break;

            if (_selfTestSeconds > 0)
            {
                if (testClock.Elapsed.TotalSeconds >= _selfTestSeconds) break;
                if (testClock.Elapsed.TotalSeconds >= nextToggle)
                {
                    // Гоняем островок между состояниями, чтобы измерить именно
                    // анимацию, а не покой.
                    _island.IsHovered = !_island.IsHovered;
                    _island.Collapsed = !_island.IsHovered && nextToggle > 2.0;
                    nextToggle += 0.7;
                    _needsRedraw = true;
                }
            }

            var now = Stopwatch.GetTimestamp();
            var dt = (float)((now - last) / (double)Stopwatch.Frequency);
            last = now;
            // После сна поток может проснуться с огромным dt — пружина от такого
            // шага улетает. Ограничиваем.
            dt = Math.Min(dt, 0.1f);

            _island.Update(dt);

            var animating = _island.IsAnimating;
            if (animating || _needsRedraw)
            {
                var t0 = Stopwatch.GetTimestamp();
                _gpu.RenderFrame(DrawFrame);
                _cpuFrameMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

                _needsRedraw = false;
                _framesRendered++;
                _fpsCounter++;
                if (_framesRendered == 1) _firstFrameMs = _cpuFrameMs;
                else
                {
                    // Первый кадр всегда дорогой: Skia компилирует шейдеры и создаёт
                    // pipeline state. В статистику установившегося режима он не входит.
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
                // Ждём, когда DXGI будет готов принять следующий кадр — это
                // и есть привязка к вертикальной развёртке.
                OverlayWindow.WaitForInputOrHandles(waitHandles, 100);
            }
            else
            {
                // Простой: поток спит до ввода. Ноль кадров, ноль CPU.
                // Оригинал в этом состоянии продолжал рисовать 60 раз в секунду.
                _fps = 0;
                idleWaits++;
                // В самопроверке нельзя засыпать навсегда — некому разбудить.
                OverlayWindow.WaitForInputOrHandles(empty, _selfTestSeconds > 0 ? 8u : Win32.INFINITE);
                last = Stopwatch.GetTimestamp();
            }
        }

        if (_selfTestSeconds > 0)
        {
            var cpu = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
            Log.Write($"SELFTEST длительность={testClock.Elapsed.TotalSeconds:0.00}s " +
                      $"кадров={_framesRendered} " +
                      $"кадр_сред={_sumFrameMs / Math.Max(1, _framesRendered - 1):0.000}ms " +
                      $"кадр_худший={_worstFrameMs:0.000}ms (кадр №{_worstFrameIndex}) " +
                      $"первый_кадр={_firstFrameMs:0.000}ms " +
                      $"засыпаний={idleWaits} " +
                      $"CPU_всего={cpu:0}ms");

            if (_steadyStartTicks != 0)
            {
                var wallMs = (Stopwatch.GetTimestamp() - _steadyStartTicks) * 1000.0 / Stopwatch.Frequency;
                var cpuMs = cpu - _steadyCpuStartMs;
                var frames = _framesRendered - _steadyStartFrames;
                Log.Write($"УСТАНОВИВШИЙСЯ РЕЖИМ кадров={frames} " +
                          $"fps={frames * 1000.0 / wallMs:0.0} " +
                          $"CPU={cpuMs / wallMs * 100:0.0}% одного ядра " +
                          $"({cpuMs / Math.Max(1, frames):0.00}ms CPU на кадр)");
            }
            Log.Write($"СТАДИИ (последний кадр) ожидание={_gpu.LastWaitMs:0.000} " +
                      $"отрисовка={_gpu.LastDrawMs:0.000} " +
                      $"flush={_gpu.LastFlushMs:0.000} " +
                      $"present={_gpu.LastPresentMs:0.000}");

            var messages = _gpu.DrainValidationMessages();
            if (messages.Count == 0)
                Log.Write("ВАЛИДАЦИЯ D3D12: сообщений нет — состояния ресурсов сходятся");
            else
                foreach (var m in messages.Take(25)) Log.Write("ВАЛИДАЦИЯ " + m);
        }
    }

    private void DrawFrame(SKCanvas canvas)
    {
        _island.Draw(canvas, _gpu.Width, _window.Scale);
        if (_showHud) DrawHud(canvas);
    }

    private void DrawHud(SKCanvas canvas)
    {
        var scale = _window.Scale;
        _hudFont.Size = 12 * scale;

        Span<string> lines =
        [
            $"GPU   {_gpu.AdapterName}",
            $"сцена {_gpu.Width}x{_gpu.Height} px  |  dpi {_window.Dpi} ({scale:0.##}x)",
            $"кадр  {_cpuFrameMs:0.00} ms   fps {_fps:0}   всего {_framesRendered}",
            $"режим {(_island.IsAnimating ? "АНИМАЦИЯ" : "ПРОСТОЙ — поток спит")}",
            "ПКМ по островку — выход",
        ];

        var y = _gpu.Height - (lines.Length * 16f + 8f) * scale;
        foreach (var line in lines)
        {
            canvas.DrawText(line, 12 * scale, y, SKTextAlign.Left, _hudFont, _hudPaint);
            y += 16f * scale;
        }
    }

    public void Dispose()
    {
        _hudPaint.Dispose();
        _hudFont.Dispose();
        _island.Dispose();
        _gpu.Dispose();
        _window.Dispose();
    }
}
