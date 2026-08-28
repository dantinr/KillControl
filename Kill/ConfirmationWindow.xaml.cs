using System.Windows;
using System.Windows.Controls;

namespace Kill;

public partial class ConfirmationWindow : Window
{
    private readonly string? _expectedText;

    public ConfirmationWindow(string title, string heading, string warning, string? expectedText, string confirmText,
        string subjectLabel = "应用名称")
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = heading;
        SubtitleText.Text = expectedText is null ? "请确认操作范围后继续。" : "此操作被标记为高风险。";
        WarningText.Text = warning;
        ConfirmButton.Content = confirmText;
        _expectedText = expectedText;

        if (expectedText is not null)
        {
            TypingPanel.Visibility = Visibility.Visible;
            TypingPrompt.Text = $"输入{subjectLabel}“{expectedText}”以确认：";
            ConfirmButton.IsEnabled = false;
            Loaded += (_, _) => ConfirmationInput.Focus();
        }
    }

    private void ConfirmationInput_TextChanged(object sender, TextChangedEventArgs e) =>
        ConfirmButton.IsEnabled = string.Equals(ConfirmationInput.Text.Trim(), _expectedText, StringComparison.Ordinal);

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
