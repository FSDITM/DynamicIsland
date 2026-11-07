using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Features;
using DynamicIsland.Interop;
using DynamicIsland.Platform;

namespace DynamicIsland.Ui;

/// <summary>Окно редактора заметки с форматированием. Одно окно на все заметки.</summary>
internal static class NoteEditorHost
{
    private static NoteEditorWindow? _window;

    public static void Open(NotesService notes, Guid id) => UiThread.Post(() =>
    {
        _window ??= new NoteEditorWindow(notes);
        _window.Edit(id);
    });
}

internal sealed class NoteEditorWindow : Window
{
    private static readonly Brush Bg = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x0F, 0x0D)));
    private static readonly Brush Edge = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Primary = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xED, 0xE6)));
    private static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x96, 0x96, 0x9E)));
    private static readonly Brush Chip = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xF0, 0xED, 0xE6)));
    private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xED, 0xE6)));
    private static readonly Brush Mark = Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xD6, 0x0A)));
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily TextFont = WpfFonts.Text;

    private static T Freeze<T>(T b) where T : Freezable { b.Freeze(); return b; }

    private readonly NotesService _notes;
    private readonly RichTextBox _rtb = new();
    private readonly StackPanel _folderBar = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _placeholder = new();
    private string _folderName = "Заметки";
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private Guid _id;
    private bool _loading;

    public NoteEditorWindow(NotesService notes)
    {
        _notes = notes;

        Title = "Заметка — DynamicIsland";
        Width = 560;
        Height = 620;
        MinWidth = 380;
        MinHeight = 320;
        Background = Bg;
        Foreground = Primary;
        FontFamily = TextFont;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        _rtb.Background = Brushes.Transparent;
        _rtb.Foreground = Primary;
        _rtb.CaretBrush = Primary;
        _rtb.BorderThickness = new Thickness(0);
        _rtb.Padding = new Thickness(28, 14, 28, 14);
        _rtb.FontSize = 15;
        _rtb.FontFamily = TextFont;
        _rtb.AcceptsTab = true;
        _rtb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _rtb.SpellCheck.IsEnabled = true;
        _rtb.Document.PagePadding = new Thickness(0);
        _rtb.Document.LineHeight = 24;
        _rtb.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = new TextRange(_rtb.Document.ContentStart, _rtb.Document.ContentEnd).Text.Trim().Length == 0
                ? Visibility.Visible : Visibility.Collapsed;
            if (!_loading) { _save.Stop(); _save.Start(); }
        };
        _rtb.PreviewMouseLeftButtonUp += ToggleTask;
        DataObject.AddPastingHandler(_rtb, (_, e) =>
        {
            // Чужое форматирование (чёрный текст из Word) на тёмном фоне нечитаемо — вставляем чистый текст.
            if (e.DataObject.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = (string)e.DataObject.GetData(DataFormats.UnicodeText)!;
                e.DataObject = new DataObject(DataFormats.UnicodeText, text);
            }
        });

        _placeholder.Text = "Начните писать. Список и задачи — кнопками справа.";
        _placeholder.Foreground = Muted;
        _placeholder.FontSize = 15;
        _placeholder.IsHitTestVisible = false;
        _placeholder.Margin = new Thickness(30, 18, 30, 0);
        _placeholder.TextWrapping = TextWrapping.Wrap;
        _placeholder.VerticalAlignment = VerticalAlignment.Top;

        var bar = new DockPanel { Margin = new Thickness(22, 12, 14, 8), LastChildFill = false };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(tools, Dock.Right);
        DockPanel.SetDock(_folderBar, Dock.Left);

        tools.Children.Add(Tool(G.Bold, "Жирный (Ctrl+B)", () => EditingCommands.ToggleBold.Execute(null, _rtb)));
        tools.Children.Add(Tool(G.Italic, "Курсив (Ctrl+I)", () => EditingCommands.ToggleItalic.Execute(null, _rtb)));
        tools.Children.Add(Tool("S", "Зачёркнутый", ToggleStrike, text: true, strike: true));
        tools.Children.Add(Tool("A", "Цветная метка", ToggleMark, text: true, mark: true));
        tools.Children.Add(Separator());
        tools.Children.Add(Tool("\uE8FD", "Список", () => EditingCommands.ToggleBullets.Execute(null, _rtb)));
        tools.Children.Add(Tool("\uE9D5", "Нумерованный список", () => EditingCommands.ToggleNumbering.Execute(null, _rtb)));
        tools.Children.Add(Tool("\uE73A", "Задача с галочкой", InsertTask));
        tools.Children.Add(Separator());
        tools.Children.Add(Tool(G.Delete, "Удалить заметку", DeleteNote, danger: true));
        bar.Children.Add(tools);
        bar.Children.Add(_folderBar);

        var line = new Border { Height = 1, Background = Edge };
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(line, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(line);
        var body = new Grid();
        body.Children.Add(_rtb);
        body.Children.Add(_placeholder);
        dock.Children.Add(body);
        Content = dock;
        DarkTitleBar.Apply(this);

        _save.Tick += (_, _) => { _save.Stop(); Persist(); };
        Closing += (_, e) =>
        {
            Persist();
            // Пустую заметку не храним: иначе каждое «Новая» без текста оставляло бы мусор в списке.
            if (_id != Guid.Empty && _notes.Find(_id) is { Plain.Length: 0 })
            {
                _notes.Delete(_id);
                _id = Guid.Empty;
            }
            e.Cancel = true;
            Hide();
        };
    }

    private static readonly Brush HoverBg = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0xF0, 0xED, 0xE6)));

    private static Border Separator() => new()
    {
        Width = 1, Height = 18, Background = Edge, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Кнопка панели: без подложки, подсвечивается только под курсором.</summary>
    private Border Tool(string glyph, string tip, Action click, bool text = false, bool danger = false,
                        bool strike = false, bool mark = false)
    {
        var fg = danger ? new SolidColorBrush(Color.FromRgb(0xFF, 0x69, 0x61)) : Primary;
        var label = new TextBlock
        {
            Text = glyph, FontFamily = text ? TextFont : IconFont, FontSize = text ? 15 : 14, Foreground = fg,
            FontWeight = text ? FontWeights.SemiBold : FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        if (strike) label.TextDecorations = TextDecorations.Strikethrough;

        var host = new Border
        {
            Width = 32, Height = 30, CornerRadius = new CornerRadius(4), Background = Brushes.Transparent,
            ToolTip = tip, Cursor = Cursors.Hand, Child = label,
        };
        if (mark)
        {
            host.Child = null;
            label.Foreground = Brushes.Black;
            label.Margin = new Thickness(0);
            host.Child = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(3), Background = Mark,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = label,
            };
        }

        host.MouseEnter += (_, _) => host.Background = HoverBg;
        host.MouseLeave += (_, _) => host.Background = Brushes.Transparent;
        host.MouseLeftButtonUp += (_, _) => { click(); _rtb.Focus(); };
        return host;
    }

    /// <summary>Папки заметки слева: текст с подчёркиванием у выбранной.</summary>
    private void RebuildFolders()
    {
        _folderBar.Children.Clear();
        foreach (var name in _notes.Folders().Take(5))
        {
            var active = name.Equals(_folderName, StringComparison.OrdinalIgnoreCase);
            var tab = new Border
            {
                BorderBrush = active ? Primary : Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 2),
                Padding = new Thickness(0, 4, 0, 5), Margin = new Thickness(0, 0, 16, 0), Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = name, FontSize = 13, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = active ? Primary : Muted,
                },
            };
            var folder = name;
            tab.MouseLeftButtonUp += (_, _) => { _folderName = folder; RebuildFolders(); Persist(); };
            _folderBar.Children.Add(tab);
        }
    }

    public void Edit(Guid id)
    {
        Persist();
        if (_id != Guid.Empty && _id != id && _notes.Find(_id) is { Plain.Length: 0 }) _notes.Delete(_id);
        var note = _notes.Find(id);
        if (note is null) return;

        _id = id;
        _loading = true;
        try
        {
            _folderName = note.Folder;
            RebuildFolders();

            _rtb.Document = new FlowDocument { PagePadding = new Thickness(0), LineHeight = 24 };
            if (note.Xaml.Length > 0)
            {
                try
                {
                    var range = new TextRange(_rtb.Document.ContentStart, _rtb.Document.ContentEnd);
                    using var ms = new MemoryStream(Encoding.UTF8.GetBytes(note.Xaml));
                    range.Load(ms, DataFormats.Xaml);
                }
                catch { _rtb.Document = new FlowDocument(new Paragraph(new Run(note.Plain))) { PagePadding = new Thickness(0) }; }
            }
            // Цвет по умолчанию задаём для всего документа: светлый текст на тёмном фоне.
            _rtb.Document.Foreground = Primary;
        }
        finally { _loading = false; }

        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Native.ForceForeground(new WindowInteropHelper(this).Handle);
        _rtb.Focus();
        _rtb.CaretPosition = _rtb.Document.ContentEnd;
    }

    private void Persist()
    {
        _save.Stop();
        if (_id == Guid.Empty || _loading) return;

        var range = new TextRange(_rtb.Document.ContentStart, _rtb.Document.ContentEnd);
        string xaml;
        using (var ms = new MemoryStream())
        {
            range.Save(ms, DataFormats.Xaml);
            xaml = Encoding.UTF8.GetString(ms.ToArray());
        }

        var plain = range.Text.TrimEnd('\r', '\n');
        _notes.Update(_id, xaml, plain, _folderName);
        _placeholder.Visibility = plain.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DeleteNote()
    {
        var answer = MessageBox.Show(this, "Удалить эту заметку? Вернуть её будет нельзя.", "DynamicIsland",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _notes.Delete(_id);
        _id = Guid.Empty;
        Hide();
    }

    private void ToggleStrike()
    {
        var sel = _rtb.Selection;
        if (sel.IsEmpty) return;
        var current = sel.GetPropertyValue(Inline.TextDecorationsProperty);
        var has = current is TextDecorationCollection c && c.Count > 0;
        sel.ApplyPropertyValue(Inline.TextDecorationsProperty, has ? null : TextDecorations.Strikethrough);
    }

    private void ToggleMark()
    {
        var sel = _rtb.Selection;
        if (sel.IsEmpty) return;
        var current = sel.GetPropertyValue(TextElement.BackgroundProperty);
        var has = current is SolidColorBrush b && b.Color.A > 0;
        sel.ApplyPropertyValue(TextElement.BackgroundProperty, has ? null : Mark);
    }

    private void InsertTask()
    {
        var para = _rtb.CaretPosition.Paragraph;
        if (para is null) return;
        var text = new TextRange(para.ContentStart, para.ContentEnd).Text;
        if (text.StartsWith("☐ ") || text.StartsWith("☑ ")) return;
        para.Inlines.InsertBefore(para.Inlines.FirstInline ?? new Run(), new Run("☐ "));
    }

    /// <summary>Щелчок по ☐ или ☑ переключает галочку, а строка зачёркивается.</summary>
    private void ToggleTask(object sender, MouseButtonEventArgs e)
    {
        var pos = _rtb.GetPositionFromPoint(e.GetPosition(_rtb), snapToText: false);
        var para = pos?.Paragraph;
        if (pos is null || para is null) return;

        var start = para.ContentStart;
        var first = start.GetTextInRun(LogicalDirection.Forward);
        if (first.Length == 0 || (first[0] != '☐' && first[0] != '☑')) return;

        // Попали ли в первые два знака строки.
        var offset = start.GetOffsetToPosition(pos);
        if (offset > 3) return;

        var done = first[0] == '☐';
        var boxEnd = start.GetPositionAtOffset(1);
        if (boxEnd is null) return;

        _loading = true;
        try
        {
            new TextRange(start, boxEnd).Text = done ? "☑" : "☐";
            var rest = new TextRange(start.GetPositionAtOffset(2) ?? start, para.ContentEnd);
            rest.ApplyPropertyValue(Inline.TextDecorationsProperty, done ? TextDecorations.Strikethrough : null);
        }
        finally { _loading = false; }

        _save.Stop();
        _save.Start();
        e.Handled = true;
    }
}
