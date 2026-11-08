using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DynamicIsland.Features;
using DynamicIsland.Interop;
using DynamicIsland.Platform;

namespace DynamicIsland.Ui;

/// <summary>Окно быстрого ответа: цитата сообщения, поле ввода, Enter отправляет.</summary>
internal sealed class ReplyWindow : Window
{
    private static readonly Brush Panel = Frozen(new SolidColorBrush(Color.FromRgb(0x10, 0x0F, 0x0D)));
    private static readonly Brush Edge = Frozen(new SolidColorBrush(Color.FromArgb(0x3A, 0xF0, 0xED, 0xE6)));
    private static readonly Brush Primary = Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0xED, 0xE6)));
    private static readonly Brush Muted = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0x95, 0x8B)));
    private static readonly Brush Blue = Frozen(new SolidColorBrush(Color.FromRgb(0x2A, 0xAB, 0xEE)));

    private static T Frozen<T>(T b) where T : Freezable { b.Freeze(); return b; }

    private static ReplyWindow? _open;

    private readonly Func<string, Task<string?>> _send;
    private readonly TextBox _box = new();
    private readonly TextBlock _status = new();
    private bool _sending;

    /// <param name="send">Отправка ответа: возвращает текст ошибки или null, если ушло.</param>
    public static void Open(string chat, string quote, Func<string, Task<string?>> send) =>
        UiThread.Post(() =>
        {
            _open?.Close();
            _open = new ReplyWindow(chat, quote, send);
            _open.ShowReply();
        });

    private ReplyWindow(string chat, string quote, Func<string, Task<string?>> send)
    {
        _send = send;

        Title = "Ответ — " + chat;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;

        var heading = new TextBlock
        {
            Text = chat, Foreground = Primary, FontSize = 14, FontWeight = FontWeights.SemiBold,
            FontFamily = WpfFonts.Text, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var quoted = new TextBlock
        {
            Text = quote, Foreground = Muted, FontSize = 12.5, FontFamily = WpfFonts.Text,
            TextWrapping = TextWrapping.Wrap, MaxHeight = 56, Margin = new Thickness(0, 4, 0, 10),
        };

        _box.AcceptsReturn = false;
        _box.Foreground = Primary;
        _box.CaretBrush = Primary;
        _box.Background = Brushes.Transparent;
        _box.BorderThickness = new Thickness(0, 0, 0, 1);
        _box.BorderBrush = Blue;
        _box.FontSize = 15;
        _box.FontFamily = WpfFonts.Text;
        _box.Padding = new Thickness(0, 6, 0, 6);
        _box.PreviewKeyDown += OnKey;

        _status.Foreground = Muted;
        _status.FontSize = 11.5;
        _status.FontFamily = WpfFonts.Text;
        _status.Margin = new Thickness(0, 8, 0, 0);
        _status.Text = "Enter — отправить, Esc — закрыть";

        var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        stack.Children.Add(heading);
        stack.Children.Add(quoted);
        stack.Children.Add(_box);
        stack.Children.Add(_status);

        Content = new Border
        {
            Background = Panel, BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Margin = new Thickness(16), Child = stack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.4, Color = Colors.Black },
        };

        Deactivated += (_, _) => { if (!_sending) Close(); };
        Closed += (_, _) => { if (ReferenceEquals(_open, this)) _open = null; };
    }

    public void ShowReply()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width - 32) / 2;
        Top = area.Top + 70;
        Show();
        Activate();
        Native.ForceForeground(new WindowInteropHelper(this).Handle);
        _box.Focus();
        Keyboard.Focus(_box);
    }

    private async void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (e.Key != Key.Enter || _sending) return;
        e.Handled = true;

        var text = _box.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        _sending = true;
        _box.IsEnabled = false;
        _status.Text = "Отправляю…";

        var error = await _send(text);
        if (error is null) { Close(); return; }

        _sending = false;
        _box.IsEnabled = true;
        _box.Focus();
        _status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x69, 0x61));
        _status.Text = error;
    }
}
