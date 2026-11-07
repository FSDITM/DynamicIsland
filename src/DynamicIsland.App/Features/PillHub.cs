using SkiaSharp;

namespace DynamicIsland.Features;

/// <summary>Кнопка на карточке.</summary>
internal sealed record CardButton(string Label, Action Click, bool Primary = false);

/// <summary>
/// Карточка-уведомление: островок раскрывается, показывает её и сворачивается.
/// Карточка с кнопками «липкая» — ждёт ответа, остальные уходят сами.
/// </summary>
internal sealed class Card
{
    public string Glyph { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public SKColor? Tint { get; init; }

    /// <summary>Сколько показывать, секунд. Для липких карточек игнорируется.</summary>
    public float Seconds { get; init; } = 4f;

    public List<CardButton> Buttons { get; init; } = [];
    public SKImage? Image { get; init; }
    public Action? OnClick { get; init; }

    /// <summary>Доля 0..1 для полосы прогресса; null — полосы нет.</summary>
    public float? Progress { get; set; }

    public bool Sticky => Buttons.Count > 0;

    /// <summary>Нужно ли принимать клики мыши, пока карточка на экране.</summary>
    public bool Interactive => Sticky || OnClick is not null;

    public long ShownAt { get; set; }
}

/// <summary>Маленький индикатор в свёрнутом островке (таймер, запись, микрофон…).</summary>
internal sealed record Chip(string Key, string Glyph, string Text, SKColor? Color = null, int Priority = 0,
                            bool Pulse = false);

/// <summary>Общий почтовый ящик для событий: сервисы кладут сюда чипы и карточки, островок их рисует.</summary>
internal sealed class PillHub
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Chip> _chips = [];
    private readonly Queue<Card> _queue = new();
    private Card? _current;

    /// <summary>Что-то изменилось — разбудить цикл отрисовки.</summary>
    public event Action? Changed;

    public Card? Current
    {
        get { lock (_gate) return _current; }
    }

    public void Post(Card card)
    {
        lock (_gate)
        {
            // Карточка того же заголовка заменяет прежнюю — прогресс загрузки не должен копиться очередью.
            if (_current is not null && _current.Title == card.Title && !_current.Sticky)
            {
                card.ShownAt = Environment.TickCount64;
                _current = card;
            }
            else if (_current is null)
            {
                card.ShownAt = Environment.TickCount64;
                _current = card;
            }
            else _queue.Enqueue(card);
        }
        Changed?.Invoke();
    }

    public void Dismiss()
    {
        lock (_gate)
        {
            _current = null;
            if (_queue.Count > 0)
            {
                _current = _queue.Dequeue();
                _current.ShownAt = Environment.TickCount64;
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Вызывается из цикла отрисовки: убирает карточки, чьё время вышло.</summary>
    public bool Tick()
    {
        lock (_gate)
        {
            if (_current is null) return false;
            if (_current.Sticky) return false;
            if (Environment.TickCount64 - _current.ShownAt < _current.Seconds * 1000) return false;
        }
        Dismiss();
        return true;
    }

    /// <summary>Миллисекунд до конца текущей карточки; null — карточки нет или она липкая.</summary>
    public int? MillisecondsLeft()
    {
        lock (_gate)
        {
            if (_current is null || _current.Sticky) return null;
            return (int)Math.Max(0, _current.Seconds * 1000 - (Environment.TickCount64 - _current.ShownAt));
        }
    }

    public void SetChip(string key, Chip? chip)
    {
        lock (_gate)
        {
            if (chip is null)
            {
                if (!_chips.Remove(key)) return;
            }
            else
            {
                if (_chips.TryGetValue(key, out var old) && old == chip) return;
                _chips[key] = chip;
            }
        }
        Changed?.Invoke();
    }

    public List<Chip> TopChips(int count)
    {
        lock (_gate) return _chips.Values.OrderByDescending(c => c.Priority).Take(count).ToList();
    }

    public bool HasChips
    {
        get { lock (_gate) return _chips.Count > 0; }
    }

    public bool HasPulsingChip
    {
        get { lock (_gate) return _chips.Values.Any(c => c.Pulse); }
    }
}
