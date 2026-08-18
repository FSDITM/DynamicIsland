using System.Runtime.InteropServices;
using DynamicIsland.Interop;

namespace DynamicIsland.Platform;

/// <summary>
/// Win32-окно оверлея: без рамки, не в панели задач, не забирает фокус,
/// без redirection-поверхности (содержимое отдаётся DirectComposition).
///
/// Размер окна — «сцена» вокруг островка, а не весь экран. Это принципиально:
/// сцена 1000×420 вместо 1920×1080 — это в ~5 раз меньше пикселей на кадр
/// и отсутствие перехвата мыши по всему экрану.
/// </summary>
internal sealed class OverlayWindow : IDisposable
{
    private const string ClassName = "DynamicIsland.Overlay";

    // Делегат обязан пережить окно: если его соберёт GC, любое сообщение
    // прилетит в освобождённый thunk и процесс упадёт.
    private readonly WndProcDelegate _wndProc;
    private readonly nint _hInstance;
    private bool _classRegistered;
    private bool _trackingMouse;

    public nint Handle { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint Dpi { get; private set; } = 96;
    public float Scale => Dpi / 96f;

    /// <summary>Позиция курсора в клиентских координатах, в физических пикселях.</summary>
    public POINT CursorClient { get; private set; }
    public bool MouseInside { get; private set; }

    public event Action? MouseMoved;
    public event Action? MouseLeft;
    public event Action<bool>? MouseButton;
    public event Action? RightClicked;

    /// <summary>Захват мыши отобрали — незавершённое перетаскивание надо отменить.</summary>
    public event Action? CaptureLost;
    public event Action<int>? MouseWheel;
    public event Action? Closed;
    public event Action? DisplayChanged;
    public event Action<uint>? DpiChanged;

    /// <summary>Клик по иконке в трее; аргумент — код сообщения мыши.</summary>
    public event Action<uint>? TrayMessage;

    /// <summary>Фоновый поток попросил перерисовать кадр.</summary>
    public event Action? Woken;

    /// <summary>Нажата зарегистрированная глобальная горячая клавиша.</summary>
    public event Action? HotkeyPressed;

    /// <summary>Будит цикл отрисовки из любого потока.</summary>
    public void Wake() => Win32.PostMessageW(Handle, Win32.WM_APP_WAKE, 0, 0);

    private bool _clickThrough = true;

    /// <summary>
    /// Полная прозрачность окна для ввода через WS_EX_TRANSPARENT.
    ///
    /// Отвечать HTTRANSPARENT на WM_NCHITTEST оказалось недостаточно надёжно:
    /// это просьба к системе пропустить клик дальше, и она срабатывает не во
    /// всех путях доставки ввода. С этим стилем окно ввод получить физически
    /// не может — терять чужие нажатия нечем.
    /// </summary>
    public void SetClickThrough(bool value)
    {
        if (Handle == 0 || _clickThrough == value) return;
        _clickThrough = value;

        var style = Win32.GetWindowLongPtrW(Handle, Win32.GWL_EXSTYLE);
        var updated = value
            ? style | (nint)Win32.WS_EX_TRANSPARENT
            : style & ~(nint)Win32.WS_EX_TRANSPARENT;

        Win32.SetWindowLongPtrW(Handle, Win32.GWL_EXSTYLE, updated);
    }

    private (int L, int T, int R, int B) _region = (-1, -1, -1, -1);

    /// <summary>
    /// Ограничивает окно фигурой островка.
    ///
    /// Это главная защита от перехвата чужих нажатий: окно-сцена размером
    /// 930x315 занимало треть экрана, и всё это время оно физически лежало
    /// поверх чужих окон. Регион сжимает его до самого островка с запасом на
    /// тень — за пределами региона окна для системы просто нет.
    /// </summary>
    public void SetShape(int left, int top, int right, int bottom, int radius)
    {
        // Совпало точно — пересобирать нечего. Порога в несколько пикселей тут
        // быть не должно: каждый лишний пиксель региона отбирает нажатия
        // у окна под островком.
        if (_region == (left, top, right, bottom)) return;

        var rgn = Win32.CreateRoundRectRgn(left, top, right, bottom, radius, radius);

        // Кэш обновляем только после удачи. Иначе отказ GDI навсегда развёл бы
        // кэш с настоящим регионом: следующий такой же запрос отсеялся бы
        // как «уже применён».
        if (rgn == 0) return;
        _region = (left, top, right, bottom);

        // Регион переходит во владение системы — удалять его нельзя.
        Win32.SetWindowRgn(Handle, rgn, false);
    }

    /// <summary>Перехватить мышь: сообщения продолжат приходить и вне окна.</summary>
    public void CaptureMouse() => Win32.SetCapture(Handle);

    public static void ReleaseMouse() => Win32.ReleaseCapture();

    /// <summary>
    /// Возвращает true, если точка (клиентские координаты) попадает в интерактивную
    /// область. Всё остальное отдаётся окнам под нами — курсор и клики проходят
    /// насквозь, поэтому оверлей не мешает выделять текст в браузере.
    /// </summary>
    public Func<int, int, bool>? HitTest { get; set; }

    public OverlayWindow()
    {
        _wndProc = WindowProc;
        _hInstance = Win32.GetModuleHandleW(null);
    }

    public void Create(int x, int y, int width, int height)
    {
        Width = width;
        Height = height;

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            lpszClassName = ClassName,
        };

        if (Win32.RegisterClassExW(ref wc) == 0)
        {
            var err = Marshal.GetLastWin32Error();
            // 1410 = класс уже зарегистрирован; это не ошибка при перезапуске рендера.
            if (err != 1410) throw new InvalidOperationException($"RegisterClassEx failed: {err}");
        }
        _classRegistered = true;

        // WS_EX_TRANSPARENT с самого начала: пока пользователь не навёлся
        // намеренно, окно вообще не участвует в доставке ввода.
        const uint exStyle = Win32.WS_EX_NOREDIRECTIONBITMAP
                           | Win32.WS_EX_TOOLWINDOW
                           | Win32.WS_EX_NOACTIVATE
                           | Win32.WS_EX_TOPMOST
                           | Win32.WS_EX_TRANSPARENT;

        Handle = Win32.CreateWindowExW(exStyle, ClassName, "DynamicIsland",
            Win32.WS_POPUP, x, y, width, height,
            0, 0, _hInstance, 0);

        if (Handle == 0)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        Dpi = Win32.GetDpiForWindow(Handle);
        Win32.ShowWindow(Handle, Win32.SW_SHOWNOACTIVATE);
    }

    public void SetBounds(int x, int y, int width, int height)
    {
        Width = width;
        Height = height;
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, x, y, width, height,
            Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    /// <summary>Возвращает окно наверх, не отбирая фокус у активного приложения.</summary>
    public void RestoreTopmost() =>
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);

    private nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32.WM_NCHITTEST:
            {
                if (HitTest is null) return Win32.HTCLIENT;

                // lParam — экранные координаты, переводим в клиентские.
                var pt = new POINT { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
                Win32.ScreenToClient(hWnd, ref pt);
                return HitTest(pt.X, pt.Y) ? Win32.HTCLIENT : Win32.HTTRANSPARENT;
            }

            case Win32.WM_MOUSEMOVE:
            {
                CursorClient = new POINT { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
                MouseInside = true;

                if (!_trackingMouse)
                {
                    var tme = new TRACKMOUSEEVENT
                    {
                        cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                        dwFlags = Win32.TME_LEAVE,
                        hwndTrack = hWnd,
                        dwHoverTime = 0,
                    };
                    Win32.TrackMouseEvent(ref tme);
                    _trackingMouse = true;
                }

                MouseMoved?.Invoke();
                return 0;
            }

            case Win32.WM_MOUSELEAVE:
                _trackingMouse = false;
                MouseInside = false;
                MouseLeft?.Invoke();
                return 0;

            // Захват мыши могли отобрать (переключение окна, Alt+Tab, диалог).
            // Без этого перетаскивание ползунка залипло бы, а вместе с ним
            // залипло бы и удержание мыши на нашем окне.
            case Win32.WM_CAPTURECHANGED:
                CaptureLost?.Invoke();
                return 0;

            case Win32.WM_LBUTTONDOWN: MouseButton?.Invoke(true); return 0;
            case Win32.WM_LBUTTONUP: MouseButton?.Invoke(false); return 0;
            case Win32.WM_RBUTTONUP: RightClicked?.Invoke(); return 0;

            case Win32.WM_MOUSEWHEEL:
                MouseWheel?.Invoke((short)((wParam >> 16) & 0xFFFF));
                return 0;

            case Win32.WM_HOTKEY:
                HotkeyPressed?.Invoke();
                return 0;

            case Win32.WM_APP_WAKE:
                Woken?.Invoke();
                return 0;

            case Win32.WM_TRAYICON:
                TrayMessage?.Invoke((uint)(lParam & 0xFFFF));
                return 0;

            case Win32.WM_DPICHANGED:
                Dpi = (uint)(wParam & 0xFFFF);
                DpiChanged?.Invoke(Dpi);
                return 0;

            case Win32.WM_DISPLAYCHANGE:
                DisplayChanged?.Invoke();
                return 0;

            case Win32.WM_CLOSE:
                Win32.DestroyWindow(hWnd);
                return 0;

            case Win32.WM_DESTROY:
                Closed?.Invoke();
                Win32.PostQuitMessage(0);
                return 0;
        }

        return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>Разбирает очередь сообщений без блокировки. false — пришёл WM_QUIT.</summary>
    public bool PumpMessages()
    {
        while (Win32.PeekMessageW(out var msg, 0, 0, 0, Win32.PM_REMOVE))
        {
            if (msg.message == Win32.WM_QUIT) return false;
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }
        return true;
    }

    /// <summary>
    /// Спит до прихода сообщения или сигнала одного из <paramref name="handles"/>.
    /// Это и есть idle-режим: когда анимаций нет, поток стоит на нуле CPU,
    /// а не крутит 60 кадров в секунду впустую, как оригинал.
    /// </summary>
    public static uint WaitForInputOrHandles(nint[] handles, uint timeoutMs)
        => Win32.MsgWaitForMultipleObjectsEx((uint)handles.Length, handles, timeoutMs, Win32.QS_ALLINPUT, 0);

    public void Dispose()
    {
        if (Handle != 0)
        {
            Win32.DestroyWindow(Handle);
            Handle = 0;
        }
        GC.KeepAlive(_wndProc);
        GC.KeepAlive(_classRegistered);
    }
}
