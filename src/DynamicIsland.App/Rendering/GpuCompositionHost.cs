using System.Diagnostics;
using System.Runtime.InteropServices;
using DynamicIsland.Interop;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace DynamicIsland.Rendering;

/// <summary>
/// GPU-ядро отрисовки оверлея.
///
/// Схема: D3D12 → composition swapchain (premultiplied alpha) → DirectComposition visual
/// → окно с WS_EX_NOREDIRECTIONBITMAP.
///
/// Ключевое отличие от оригинального DynamicWin: кадр никогда не попадает в системную
/// память. Там был полноэкранный layered-window, и WPF каждый кадр гонял весь экран
/// через CPU (UpdateLayeredWindow). Здесь DWM композитит нашу поверхность напрямую,
/// а размер поверхности равен «сцене» островка, а не экрану.
/// </summary>
public sealed class GpuCompositionHost : IDisposable
{
    private const int BufferCount = 2;
    private static readonly Format SurfaceFormat = Format.B8G8R8A8_UNorm;

    private IDXGIFactory4 _factory = null!;
    private IDXGIAdapter1 _adapter = null!;
    private ID3D12Device2 _device = null!;
    private ID3D12CommandQueue _queue = null!;

    private ID3D12Fence _fence = null!;
    private readonly AutoResetEvent _fenceEvent = new(false);
    private ulong _fenceValue;
    private readonly ulong[] _frameFenceValues = new ulong[BufferCount];

    private ID3D12CommandAllocator[] _allocators = null!;
    private ID3D12GraphicsCommandList _commandList = null!;

    private IDXGISwapChain1 _swapChain = null!;
    private IDXGISwapChain2 _swapChain2 = null!;
    private IDXGISwapChain3 _swapChain3 = null!;
    private nint _frameLatencyWaitable;
    private readonly ID3D12Resource?[] _backBuffers = new ID3D12Resource?[BufferCount];

    private IDCompositionDesktopDevice _dcompDevice = null!;
    private IDCompositionTarget _dcompTarget = null!;
    private IDCompositionVisual2 _dcompVisual = null!;

    private GRVorticeD3DBackendContext _backendContext = null!;
    private GRContext _grContext = null!;

    // Обёртки Skia кэшируются на каждый back buffer. Пересоздавать их каждый кадр
    // (как было в первой версии) — значит каждый кадр создавать и разрушать
    // GPU-объекты Skia; это и было основной статьёй расхода.
    private readonly GRVorticeD3DTextureResourceInfo?[] _textureInfos = new GRVorticeD3DTextureResourceInfo?[BufferCount];
    private readonly GRBackendRenderTarget?[] _renderTargets = new GRBackendRenderTarget?[BufferCount];
    private readonly SKSurface?[] _surfaces = new SKSurface?[BufferCount];

    private ID3D12InfoQueue? _infoQueue;

    private bool _disposed;

    // Профилировка стадий кадра (мс) — чтобы оптимизировать по замерам, а не по интуиции.
    public double LastDrawMs { get; private set; }
    public double LastFlushMs { get; private set; }
    public double LastPresentMs { get; private set; }
    public double LastWaitMs { get; private set; }

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Хэндл, который сигналит, когда DXGI готов принять следующий кадр.</summary>
    public nint FrameLatencyWaitable => _frameLatencyWaitable;

    public string AdapterName { get; private set; } = "unknown";

    public void Initialize(nint hwnd, int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

#if DEBUG
        // Слой валидации ловит рассинхрон состояний ресурсов — а мы как раз делим
        // управление состоянием back buffer'а со Skia, так что это не роскошь.
        if (D3D12.D3D12GetDebugInterface(out ID3D12Debug? debug).Success && debug is not null)
        {
            debug.EnableDebugLayer();
            debug.Dispose();
        }
#endif

        _factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(debug: false);
        _adapter = PickAdapter(_factory);
        AdapterName = _adapter.Description1.Description;

        _device = D3D12.D3D12CreateDevice<ID3D12Device2>(_adapter, FeatureLevel.Level_11_0);
        _queue = _device.CreateCommandQueue(CommandListType.Direct, CommandQueuePriority.Normal,
                                            CommandQueueFlags.None, 0);

#if DEBUG
        _infoQueue = _device.QueryInterfaceOrNull<ID3D12InfoQueue>();
#endif

        _fence = _device.CreateFence(0, FenceFlags.None);

        _allocators = new ID3D12CommandAllocator[BufferCount];
        for (var i = 0; i < BufferCount; i++)
            _allocators[i] = _device.CreateCommandAllocator(CommandListType.Direct);

        _commandList = _device.CreateCommandList<ID3D12GraphicsCommandList>(
            CommandListType.Direct, _allocators[0], null);
        _commandList.Close();

        CreateSwapChain(width, height);
        CreateComposition(hwnd);
        CreateSkiaContext();
    }

    private static IDXGIAdapter1 PickAdapter(IDXGIFactory4 factory)
    {
        // Предпочитаем адаптер с наибольшей видеопамятью среди аппаратных.
        // На ноутбуках с гибридной графикой это отсекает software-адаптер.
        if (factory.QueryInterfaceOrNull<IDXGIFactory6>() is { } f6)
        {
            using (f6)
            {
                for (uint i = 0; f6.EnumAdapterByGpuPreference(i, GpuPreference.MinimumPower,
                         out IDXGIAdapter1? candidate).Success; i++)
                {
                    if (candidate is null) continue;
                    if ((candidate.Description1.Flags & AdapterFlags.Software) != 0) { candidate.Dispose(); continue; }
                    return candidate;
                }
            }
        }

        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success; i++)
        {
            if (adapter is null) continue;
            if ((adapter.Description1.Flags & AdapterFlags.Software) != 0) { adapter.Dispose(); continue; }
            return adapter;
        }

        throw new InvalidOperationException("Не найден аппаратный DXGI-адаптер.");
    }

    private void CreateSwapChain(int width, int height)
    {
        var desc = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = SurfaceFormat,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = BufferCount,
            Scaling = Scaling.Stretch,
            // Composition-swapchain обязан быть flip-модели; premultiplied alpha —
            // то, ради чего всё затевалось: настоящая попиксельная прозрачность.
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied,
            Flags = SwapChainFlags.FrameLatencyWaitableObject,
        };

        _swapChain = _factory.CreateSwapChainForComposition(_queue, desc, null);
        _swapChain2 = _swapChain.QueryInterface<IDXGISwapChain2>();
        _swapChain3 = _swapChain.QueryInterface<IDXGISwapChain3>();

        // Латентность 1 кадр: минимальная задержка между «нарисовали» и «увидели».
        _swapChain2.MaximumFrameLatency = 1;
        _frameLatencyWaitable = _swapChain2.FrameLatencyWaitableObject;

        AcquireBackBuffers();
    }

    private void AcquireBackBuffers()
    {
        for (var i = 0; i < BufferCount; i++)
            _backBuffers[i] = _swapChain.GetBuffer<ID3D12Resource>((uint)i);
    }

    private void ReleaseBackBuffers()
    {
        for (var i = 0; i < BufferCount; i++)
        {
            _surfaces[i]?.Dispose(); _surfaces[i] = null;
            _renderTargets[i]?.Dispose(); _renderTargets[i] = null;
            _textureInfos[i]?.Dispose(); _textureInfos[i] = null;
            _backBuffers[i]?.Dispose(); _backBuffers[i] = null;
        }
    }

    /// <summary>
    /// Обёртка Skia над back buffer'ом, созданная один раз на буфер.
    /// Состояние объявляем RenderTarget и сами гарантируем, что Skia всегда
    /// застаёт ресурс именно в нём — барьеры в обе стороны делает этот класс.
    /// Так внутренний учёт состояний в Skia никогда не расходится с реальностью.
    /// </summary>
    private SKSurface GetOrCreateSurface(int index, ID3D12Resource backBuffer)
    {
        if (_surfaces[index] is { } cached) return cached;

        var info = new GRVorticeD3DTextureResourceInfo
        {
            Resource = backBuffer,
            ResourceState = ResourceStates.RenderTarget,
            Format = SurfaceFormat,
            LevelCount = 1,
            SampleCount = 1,
        };
        var target = new GRBackendRenderTarget(Width, Height, info);
        var surface = SKSurface.Create(_grContext, target, GRSurfaceOrigin.TopLeft, SKColorType.Bgra8888)
            ?? throw new InvalidOperationException("SKSurface.Create вернул null.");

        _textureInfos[index] = info;
        _renderTargets[index] = target;
        _surfaces[index] = surface;
        return surface;
    }

    private void Barrier(int index, ID3D12Resource resource, ResourceStates from, ResourceStates to)
    {
        _commandList.Reset(_allocators[index]);
        _commandList.ResourceBarrierTransition(resource, from, to);
        _commandList.Close();
        _queue.ExecuteCommandList(_commandList);
    }

    /// <summary>Выгребает сообщения слоя валидации D3D12. Пусто — значит состояния сходятся.</summary>
    public IReadOnlyList<string> DrainValidationMessages()
    {
        if (_infoQueue is null) return [];

        var result = new List<string>();
        var count = _infoQueue.NumStoredMessages;
        for (ulong i = 0; i < count; i++)
        {
            var msg = _infoQueue.GetMessage(i);
            result.Add($"[{msg.Severity}] {msg.Id}: {msg.Description}");
        }
        _infoQueue.ClearStoredMessages();
        return result;
    }

    private void CreateComposition(nint hwnd)
    {
        // D3D12-устройство не является IDXGIDevice, поэтому используем DCompositionCreateDevice2
        // с null: дерево визуалов нам нужно только чтобы прицепить swapchain к окну.
        var iid = typeof(IDCompositionDesktopDevice).GUID;
        Marshal.ThrowExceptionForHR(Win32.DCompositionCreateDevice2(0, in iid, out var devicePtr));
        _dcompDevice = new IDCompositionDesktopDevice(devicePtr);
        _dcompDevice.CreateTargetForHwnd(hwnd, true, out _dcompTarget).CheckError();
        _dcompVisual = _dcompDevice.CreateVisual();
        _dcompVisual.SetContent(_swapChain).CheckError();
        _dcompTarget.SetRoot(_dcompVisual).CheckError();
        _dcompDevice.Commit().CheckError();
    }

    private void CreateSkiaContext()
    {
        _backendContext = new GRVorticeD3DBackendContext
        {
            Adapter = _adapter,
            Device = _device,
            Queue = _queue,
        };

        _grContext = GRContext.CreateDirect3D(_backendContext)
            ?? throw new InvalidOperationException("GRContext.CreateDirect3D вернул null.");

        // Оверлей маленький — большой кэш ресурсов Skia тут только держал бы память.
        _grContext.SetResourceCacheLimit(32 * 1024 * 1024);
    }

    /// <summary>
    /// Рисует кадр. <paramref name="draw"/> получает канву с уже очищенным
    /// прозрачным фоном в физических пикселях.
    /// </summary>
    public void RenderFrame(Action<SKCanvas> draw)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var index = (int)_swapChain3.CurrentBackBufferIndex;

        var t0 = Stopwatch.GetTimestamp();
        WaitForFrame(_frameFenceValues[index]);
        var t1 = Stopwatch.GetTimestamp();

        var backBuffer = _backBuffers[index]
            ?? throw new InvalidOperationException("Back buffer не получен.");

        // Аллокатор можно сбрасывать только после того, как GPU доработал прошлый
        // кадр с этим индексом — ожидание выше это гарантирует.
        _allocators[index].Reset();

        // Present -> RenderTarget: приводим ресурс в то состояние, которое объявлено Skia.
        Barrier(index, backBuffer, ResourceStates.Present, ResourceStates.RenderTarget);

        var surface = GetOrCreateSurface(index, backBuffer);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        draw(canvas);
        var t2 = Stopwatch.GetTimestamp();

        _grContext.Flush();
        _grContext.Submit(false);
        var t3 = Stopwatch.GetTimestamp();

        // RenderTarget -> Present: Present требует именно этого состояния,
        // а Skia про swapchain ничего не знает.
        Barrier(index, backBuffer, ResourceStates.RenderTarget, ResourceStates.Present);

        _swapChain.Present(1, PresentFlags.None).CheckError();

        _frameFenceValues[index] = ++_fenceValue;
        _queue.Signal(_fence, _fenceValue).CheckError();
        var t4 = Stopwatch.GetTimestamp();

        var toMs = 1000.0 / Stopwatch.Frequency;
        LastWaitMs = (t1 - t0) * toMs;
        LastDrawMs = (t2 - t1) * toMs;
        LastFlushMs = (t3 - t2) * toMs;
        LastPresentMs = (t4 - t3) * toMs;
    }

    private void WaitForFrame(ulong target)
    {
        if (target == 0 || _fence.CompletedValue >= target) return;
        _fence.SetEventOnCompletion(target, _fenceEvent).CheckError();
        _fenceEvent.WaitOne();
    }

    private void WaitForGpuIdle()
    {
        var target = ++_fenceValue;
        _queue.Signal(_fence, target);
        WaitForFrame(target);
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height) return;

        WaitForGpuIdle();
        ReleaseBackBuffers();
        Array.Clear(_frameFenceValues);

        // Skia держит ссылки на старые back buffer'ы через свой кэш — сбрасываем,
        // иначе ResizeBuffers упрётся в живые ссылки на ресурсы swapchain.
        _grContext.PurgeResources();

        Width = width;
        Height = height;

        _swapChain.ResizeBuffers((uint)BufferCount, (uint)width, (uint)height,
                                 SurfaceFormat, SwapChainFlags.FrameLatencyWaitableObject).CheckError();

        AcquireBackBuffers();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { WaitForGpuIdle(); } catch { /* устройство могло уже уйти */ }

        _grContext?.Dispose();
        _backendContext?.Dispose();

        _dcompVisual?.Dispose();
        _dcompTarget?.Dispose();
        _dcompDevice?.Dispose();

        ReleaseBackBuffers();
        if (_frameLatencyWaitable != 0) Win32.CloseHandle(_frameLatencyWaitable);
        _swapChain3?.Dispose();
        _swapChain2?.Dispose();
        _swapChain?.Dispose();

        _commandList?.Dispose();
        if (_allocators is not null)
            foreach (var a in _allocators) a?.Dispose();

        _fence?.Dispose();
        _fenceEvent.Dispose();

        _infoQueue?.Dispose();
        _queue?.Dispose();
        _device?.Dispose();
        _adapter?.Dispose();
        _factory?.Dispose();
    }
}
