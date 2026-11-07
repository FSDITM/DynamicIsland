using System.Windows.Threading;

namespace DynamicIsland.Platform;

/// <summary>
/// Общий STA-поток с диспетчером WPF для всех вспомогательных окон:
/// командная строка, заметки, выделение области экрана, пипетка.
///
/// Главный поток занят циклом отрисовки островка, поэтому WPF живёт отдельно.
/// Тот же поток читает и пишет буфер обмена — ему нужен STA.
/// </summary>
internal static class UiThread
{
    private static readonly Lock Gate = new();
    private static Dispatcher? _dispatcher;

    public static Dispatcher Dispatcher
    {
        get
        {
            lock (Gate)
            {
                if (_dispatcher is not null) return _dispatcher;

                var ready = new ManualResetEventSlim();
                var thread = new Thread(() =>
                {
                    var d = Dispatcher.CurrentDispatcher;
                    d.UnhandledException += (_, args) =>
                    {
                        Log.Write("Ошибка в окне: " + args.Exception);
                        args.Handled = true;
                    };
                    _dispatcher = d;
                    ready.Set();
                    Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name = "DynamicIsland.UI",
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                ready.Wait();
                return _dispatcher!;
            }
        }
    }

    /// <summary>Выполнить на UI-потоке без ожидания; исключения уходят в лог.</summary>
    public static void Post(Action action) => Dispatcher.BeginInvoke(() =>
    {
        try { action(); }
        catch (Exception ex) { Log.Write("Ошибка UI-потока: " + ex); }
    });

    /// <summary>Выполнить на UI-потоке и дождаться результата.</summary>
    public static T Run<T>(Func<T> func) => Dispatcher.Invoke(func);

    public static void Shutdown()
    {
        lock (Gate)
        {
            try { _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Background); }
            catch { /* уже закрыт */ }
        }
    }
}
