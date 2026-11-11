using DynamicIsland.Configuration;
using Microsoft.Win32;

namespace DynamicIsland.Features;

/// <summary>
/// Настройки уведомлений Windows для мессенджеров: включить их и, по желанию, убрать системные
/// баннеры, чтобы сообщения показывались только на острове. Уведомления при этом всё равно
/// попадают в центр уведомлений Windows — оттуда их и читает остров.
///
/// Прежнее значение «показывать баннер» запоминается и возвращается при выходе из острова,
/// при отключении настройки и при удалении программы: баннеры не пропадут насовсем.
/// </summary>
internal static class NotificationSetup
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    /// <summary>Идентификаторы приложений, у которых запись в реестре появляется только после первого уведомления.</summary>
    private static readonly Dictionary<string, string> KnownIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Telegram"] = "Telegram.TelegramDesktop",
        ["Discord"] = "com.squirrel.Discord.Discord",
    };

    public static string[] ParseList(string text) =>
        text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Какие записи реестра относятся к перечисленным приложениям.</summary>
    private static List<string> Keys(IEnumerable<string> tokens)
    {
        var result = new List<string>();
        using var root = Registry.CurrentUser.OpenSubKey(Root);
        var existing = root?.GetSubKeyNames() ?? [];

        foreach (var token in tokens)
        {
            // Слишком короткое слово («e», «co») зацепило бы чужие приложения, и у них пропали бы баннеры.
            if (token.Length < 4) continue;

            var matches = existing.Where(k => k.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0 && KnownIds.TryGetValue(token, out var id)) matches.Add(id);
            foreach (var m in matches) if (!result.Contains(m, StringComparer.OrdinalIgnoreCase)) result.Add(m);
        }
        return result;
    }

    /// <summary>Применяет настройку (сохранять настройки на диск должен вызывающий): включает уведомления у приложений и прячет или возвращает баннеры.</summary>
    public static void Apply(Settings settings)
    {
        try
        {
            var keys = Keys(settings.IslandOnlyApps);
            var backup = settings.BannerBackup;

            // Приложения, убранные из списка, получают баннеры обратно.
            foreach (var gone in backup.Keys.Where(k => !keys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
                RestoreOne(settings, gone);

            foreach (var key in keys)
            {
                using var app = Registry.CurrentUser.CreateSubKey($@"{Root}\{key}", writable: true);
                if (app is null) continue;

                // Уведомления должны быть включены и попадать в центр уведомлений — иначе острову нечего читать.
                app.SetValue("Enabled", 1, RegistryValueKind.DWord);
                app.SetValue("ShowInActionCenter", 1, RegistryValueKind.DWord);

                if (settings.IslandOnlyNotifications)
                {
                    if (!backup.ContainsKey(key))
                        backup[key] = app.GetValue("ShowBanner") is int original ? original : -1;
                    app.SetValue("ShowBanner", 0, RegistryValueKind.DWord);
                }
                else RestoreOne(settings, key);
            }
        }
        catch (Exception ex) { Log.Write("Настройка уведомлений: " + ex.Message); }
    }

    /// <summary>Возвращает баннеры всем приложениям, которым мы их прятали.</summary>
    public static void RestoreAll(Settings settings)
    {
        try
        {
            foreach (var key in settings.BannerBackup.Keys.ToList()) RestoreOne(settings, key);
        }
        catch (Exception ex) { Log.Write("Возврат баннеров: " + ex.Message); }
    }

    private static void RestoreOne(Settings settings, string key)
    {
        if (!settings.BannerBackup.TryGetValue(key, out var original)) return;

        using var app = Registry.CurrentUser.OpenSubKey($@"{Root}\{key}", writable: true);
        if (app is not null)
        {
            if (original < 0) app.DeleteValue("ShowBanner", throwOnMissingValue: false);
            else app.SetValue("ShowBanner", original, RegistryValueKind.DWord);
        }
        settings.BannerBackup.Remove(key);
    }
}
