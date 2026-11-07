using DynamicIsland.Interop;

namespace DynamicIsland.Platform;

/// <summary>Набор глобальных горячих клавиш, у каждой свой номер.</summary>
internal sealed class HotkeyBank : IDisposable
{
    private readonly Dictionary<int, bool> _registered = [];
    private nint _window;

    public bool Apply(nint window, int id, string combo)
    {
        _window = window;
        if (_registered.GetValueOrDefault(id))
        {
            Win32.UnregisterHotKey(window, id);
            _registered[id] = false;
        }

        if (string.IsNullOrWhiteSpace(combo)) return true;
        if (!Hotkey.TryParse(combo, out var modifiers, out var key)) return false;

        var ok = Win32.RegisterHotKey(window, id, modifiers | Win32.MOD_NOREPEAT, key);
        _registered[id] = ok;
        if (!ok) Log.Write($"Горячая клавиша «{combo}» занята другим приложением");
        return ok;
    }

    public void Dispose()
    {
        foreach (var (id, on) in _registered)
            if (on) Win32.UnregisterHotKey(_window, id);
        _registered.Clear();
    }
}
