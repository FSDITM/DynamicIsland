using Microsoft.Win32;

namespace DynamicIsland.Platform;

/// <summary>
/// Автозапуск через ключ Run текущего пользователя.
///
/// Оригинал создавал ярлык в папке «Автозагрузка» через COM-обёртку
/// IWshRuntimeLibrary — лишняя COM-зависимость ради одной строки в реестре.
/// </summary>
internal static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DynamicIsland";

    private static string ExecutablePath => Environment.ProcessPath ?? "";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                return key?.GetValue(ValueName) is string s && s.Contains("DynamicIsland", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key is null) return;

            if (enabled) key.SetValue(ValueName, $"\"{ExecutablePath}\"");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) { Log.Write("Автозапуск: " + ex.Message); }
    }
}
