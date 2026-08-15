using DynamicIsland.Interop;

namespace DynamicIsland.Platform;

/// <summary>
/// Глобальное сочетание клавиш вида «Ctrl+Alt+D».
/// Разбор строки вынесен сюда же, чтобы настройки хранились в читаемом виде,
/// а не парой чисел.
/// </summary>
internal sealed class Hotkey : IDisposable
{
    private const int Id = 0xD1;

    private nint _window;
    private bool _registered;

    public string Current { get; private set; } = "";

    /// <summary>Перерегистрирует сочетание. Пустая строка — просто снять прежнее.</summary>
    public bool Apply(nint window, string combo)
    {
        _window = window;
        Unregister();
        Current = combo ?? "";

        if (string.IsNullOrWhiteSpace(Current)) return true;
        if (!TryParse(Current, out var modifiers, out var key)) return false;

        // MOD_NOREPEAT: удержание клавиши не должно сыпать событиями.
        _registered = Win32.RegisterHotKey(window, Id, modifiers | Win32.MOD_NOREPEAT, key);
        if (!_registered) Log.Write($"Горячая клавиша «{Current}» занята другим приложением");
        return _registered;
    }

    public static bool TryParse(string combo, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;

        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Win32.MOD_CONTROL; break;
                case "alt": modifiers |= Win32.MOD_ALT; break;
                case "shift": modifiers |= Win32.MOD_SHIFT; break;
                case "win": modifiers |= Win32.MOD_WIN; break;
                default:
                    if (!TryParseKey(raw, out key)) return false;
                    break;
            }
        }

        return key != 0;
    }

    private static bool TryParseKey(string token, out uint key)
    {
        key = 0;

        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') { key = c; return true; }
        }

        // Функциональные клавиши F1..F24 идут подряд от 0x70.
        if (token.Length >= 2 && (token[0] is 'F' or 'f') &&
            int.TryParse(token.AsSpan(1), out var n) && n is >= 1 and <= 24)
        {
            key = (uint)(0x70 + n - 1);
            return true;
        }

        key = token.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "escape" or "esc" => 0x1B,
            "tab" => 0x09,
            "insert" => 0x2D,
            "delete" or "del" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            _ => 0,
        };

        return key != 0;
    }

    private void Unregister()
    {
        if (!_registered) return;
        Win32.UnregisterHotKey(_window, Id);
        _registered = false;
    }

    public void Dispose() => Unregister();
}
