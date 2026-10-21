using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using ProcessSentinel.Core;

namespace ProcessSentinel.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Kill Control · 版本 {Protocol.Version}";
    }

    private void Project_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "无法打开项目地址：" + ex.Message, "打开项目", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
