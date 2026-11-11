using System.Collections.Concurrent;

namespace DynamicIsland.Ui;

/// <summary>
/// Картинки, которые больше не нужны, но в эту секунду их ещё может рисовать поток островка.
/// Освобождать их с другого потока опасно — это обращение к уже освобождённой памяти и падение.
/// Поэтому их кладут сюда, а поток отрисовки освобождает их между кадрами.
/// </summary>
internal static class RenderTrash
{
    private static readonly ConcurrentQueue<IDisposable> Queue = new();

    public static void Add(IDisposable? item)
    {
        if (item is not null) Queue.Enqueue(item);
    }

    /// <summary>Вызывается потоком отрисовки вне кадра.</summary>
    public static void Drain()
    {
        while (Queue.TryDequeue(out var item))
        {
            try { item.Dispose(); } catch { /* уже освобождено */ }
        }
    }
}
