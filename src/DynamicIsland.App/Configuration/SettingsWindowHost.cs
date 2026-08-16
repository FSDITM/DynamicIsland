using System.Windows.Threading;

namespace DynamicIsland.Configuration;

/// <summary>
/// Держит окно настроек на собственном STA-потоке со своим диспетчером.
///
/// Главный поток занят циклом отрисовки островка с ожиданием на
/// MsgWaitForMultipleObjects, и насос сообщений WPF туда не встроить.
/// Отдельный поток решает это без компромиссов: окно живёт своей жизнью,
/// а островок продолжает рисоваться, пока в настройках двигают ползунки.
/// </summary>
internal static class SettingsWindowHost
{
    private static readonly Lock Gate = new();
    private static SettingsWindow? _window;
    private static bool _opening;

    public static void Show(Settings settings)
    {
        lock (Gate)
        {
            if (_opening) return;

            if (_window is { } existing)
            {
                existing.Dispatcher.BeginInvoke(() =>
                {
                    if (existing.WindowState == System.Windows.WindowState.Minimized)
                        existing.WindowState = System.Windows.WindowState.Normal;
                    existing.Activate();
                });
                return;
            }

            _opening = true;
        }

        var thread = new Thread(() =>
        {
            try
            {
                // Обработчики WPF вызываются из оконной процедуры, и исключение,
                // вышедшее за её границу, Windows считает фатальным сбоем
                // в callback — процесс умирает мгновенно, снаружи не поймать.
                Dispatcher.CurrentDispatcher.UnhandledException += (_, args) =>
                {
                    Log.Write("Ошибка в окне настроек: " + args.Exception);
                    args.Handled = true;
                };

                var window = new SettingsWindow(settings);

                lock (Gate)
                {
                    _window = window;
                    _opening = false;
                }

                window.Closed += (_, _) =>
                {
                    lock (Gate) _window = null;
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                };

                window.Show();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                Log.Write("Окно настроек не открылось: " + ex);
                lock (Gate) { _window = null; _opening = false; }
            }
        })
        {
            IsBackground = true,
            Name = "DynamicIsland.Settings",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Закрывает окно, если оно открыто. Вызывается при выходе из приложения.</summary>
    public static void CloseIfOpen()
    {
        SettingsWindow? window;
        lock (Gate) window = _window;

        try { window?.Dispatcher.Invoke(window.Close, DispatcherPriority.Normal); }
        catch { /* поток мог уже завершиться */ }
    }
}
