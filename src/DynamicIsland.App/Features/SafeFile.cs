using System.Collections.Concurrent;
using System.IO;

namespace DynamicIsland.Features;

/// <summary>
/// Запись файлов данных (заметки, напоминания, закреплённое из буфера) без порчи.
/// Пишем во временный файл и подменяем: обрыв записи не оставит обрезанный JSON.
/// Сохраняют из разных потоков, поэтому по одному на файл и у каждой записи свой временный файл.
/// </summary>
internal static class SafeFile
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static void WriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (Gates.GetOrAdd(path, _ => new object()))
        {
            var temp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>Файл не прочитался — откладываем его копию, чтобы следующее сохранение не стёрло данные насовсем.</summary>
    public static void KeepBroken(string path)
    {
        try
        {
            if (File.Exists(path)) File.Copy(path, path + ".bad", overwrite: true);
        }
        catch (Exception ex) { Log.Write("Копия повреждённого файла не сделана: " + ex.Message); }
    }
}
