using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProcessSentinel.App;

public sealed class LaunchDialog : Window
{
    private readonly TextBox arguments = new();
    public string Arguments => arguments.Text;
    public LaunchDialog(string path)
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ProcessSentinel.UI;component/Themes/MonitorStyles.xaml", UriKind.Relative) });
        Title = "启动并监控";
        Width = 640; Height = 315; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(244, 247, 251));
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "从启动阶段开始记录", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 14) });
        panel.Children.Add(new TextBlock { Text = "启动参数（可留空）", Margin = new Thickness(0, 0, 0, 7) });
        panel.Children.Add(arguments);
        panel.Children.Add(new TextBlock { Text = "目标程序将在采集器就绪后，以当前界面的权限开始运行。", FontSize = 12, Margin = new Thickness(0, 12, 0, 14) });
        var button = new Button { Content = "启动并监控", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        button.Click += (_, _) => { DialogResult = true; };
        panel.Children.Add(button);
        Content = panel;
    }
}
