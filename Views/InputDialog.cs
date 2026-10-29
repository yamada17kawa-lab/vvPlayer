using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MyMediaPlayer.Views;

/// <summary>
/// 简易文本输入对话框（用于新建目录 / 重命名），纯代码构建，风格与主窗口暗色主题一致
/// </summary>
public class InputDialog : Window
{
    private readonly TextBox _textBox;

    private InputDialog(string title, string prompt, string defaultText)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1C, 0x24));

        var panel = new StackPanel { Margin = new Thickness(18) };

        panel.Children.Add(new TextBlock
        {
            Text = prompt,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD6, 0xE8)),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8)
        });

        _textBox = new TextBox
        {
            Text = defaultText,
            FontSize = 13,
            Padding = new Thickness(6, 4, 6, 4),
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x28, 0x36)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x36, 0x3B, 0x4E)),
            CaretBrush = Brushes.White
        };
        _textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Confirm();
            }
        };
        panel.Children.Add(_textBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };

        var okButton = new Button { Content = "确定", Width = 76, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        okButton.Click += (_, _) => Confirm();
        var cancelButton = new Button { Content = "取消", Width = 76, IsCancel = true };

        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        panel.Children.Add(buttons);

        Content = panel;

        Loaded += (_, _) =>
        {
            _textBox.Focus();
            _textBox.SelectAll();
        };
    }

    private void Confirm()
    {
        if (!string.IsNullOrWhiteSpace(_textBox.Text))
        {
            DialogResult = true;
        }
    }

    /// <summary>弹出输入框，用户确定时返回去除首尾空白的文本，取消返回 null</summary>
    public static string? Prompt(string title, string prompt, string defaultText = "")
    {
        var dialog = new InputDialog(title, prompt, defaultText)
        {
            Owner = Application.Current?.MainWindow
        };

        return dialog.ShowDialog() == true ? dialog._textBox.Text.Trim() : null;
    }
}
