using System.IO;
using System.Text.Json;

namespace DynamicIsland.Features;

internal sealed class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Folder { get; set; } = "Заметки";

    /// <summary>Содержимое в формате XAML (RichTextBox): жирный, курсив, списки сохраняются.</summary>
    public string Xaml { get; set; } = "";

    public string Plain { get; set; } = "";
    public DateTime Updated { get; set; } = DateTime.Now;

    public string Title
    {
        get
        {
            var line = Plain.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
            line = line.TrimStart('☐', '☑', '•', ' ');
            return line.Length == 0 ? "Пустая заметка" : line;
        }
    }
}

/// <summary>Заметки в папках «Заметки», «Задачи», «Идеи» и своих. Хранятся одним файлом в AppData.</summary>
internal sealed class NotesService
{
    public static readonly string[] DefaultFolders = ["Заметки", "Задачи", "Идеи"];

    private readonly Lock _gate = new();
    private readonly List<Note> _notes = [];

    public event Action? Changed;

    public static string StorePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DynamicIsland", "notes.json");

    public NotesService(bool loadFromDisk = true)
    {
        if (loadFromDisk) Load();
    }

    /// <summary>Добавляет заметку без записи на диск: для снимков и тестов.</summary>
    public void Seed(string folder, string text, TimeSpan age)
    {
        lock (_gate) _notes.Add(new Note { Folder = folder, Plain = text, Updated = DateTime.Now - age });
    }

    public List<Note> All()
    {
        lock (_gate) return _notes.OrderByDescending(n => n.Updated).ToList();
    }

    public List<string> Folders()
    {
        lock (_gate)
            return DefaultFolders.Concat(_notes.Select(n => n.Folder)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Note? Find(Guid id)
    {
        lock (_gate) return _notes.FirstOrDefault(n => n.Id == id);
    }

    public Note Create(string folder)
    {
        var note = new Note { Folder = folder };
        lock (_gate) _notes.Add(note);
        Save();
        Changed?.Invoke();
        return note;
    }

    public void Update(Guid id, string xaml, string plain, string folder)
    {
        lock (_gate)
        {
            var n = _notes.FirstOrDefault(x => x.Id == id);
            if (n is null) return;
            n.Xaml = xaml;
            n.Plain = plain;
            n.Folder = string.IsNullOrWhiteSpace(folder) ? "Заметки" : folder.Trim();
            n.Updated = DateTime.Now;
        }
        Save();
        Changed?.Invoke();
    }

    public void Delete(Guid id)
    {
        lock (_gate) _notes.RemoveAll(n => n.Id == id);
        Save();
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            _notes.AddRange(JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(StorePath)) ?? []);
            // Пустые заметки остаются от прежних версий, где «Новая» создавала запись сразу.
            _notes.RemoveAll(n => string.IsNullOrWhiteSpace(n.Plain));
        }
        catch (Exception ex) { Log.Write("Заметки не прочитаны: " + ex.Message); }
    }

    private void Save()
    {
        try
        {
            List<Note> copy;
            lock (_gate) copy = [.. _notes];
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var temp = StorePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(copy));
            File.Move(temp, StorePath, overwrite: true);
        }
        catch (Exception ex) { Log.Write("Заметки не сохранены: " + ex.Message); }
    }
}
