using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ProcessSentinel.App;

public partial class SettingsWindow : Window
{
    internal AppSettings Settings { get; private set; }

    internal SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Settings = settings;
        ProgramTreeOption.IsChecked = settings.IncludeProgramTree;
        RiskOnlyOption.IsChecked = settings.RiskOnly;
        LogDirectoryText.Text = settings.EffectiveLogDirectory;
    }

    private void BrowseLogs_Click(object sender, RoutedEventArgs e)
    {
        var pick = new OpenFolderDialog { Title = "选择日志保存目录", Multiselect = false };
        try
        {
            string directory = SessionLogs.ResolveDirectory(LogDirectoryText.Text);
            if (Directory.Exists(directory)) pick.InitialDirectory = directory;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        if (pick.ShowDialog(this) == true) LogDirectoryText.Text = pick.FolderName;
    }

    private void ResetLogs_Click(object sender, RoutedEventArgs e) => LogDirectoryText.Text = SessionLogs.DefaultDirectory;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string directory = SessionLogs.ResolveDirectory(LogDirectoryText.Text);
            SessionLogs.VerifyWritable(directory);
            var next = new AppSettings
            {
                IncludeProgramTree = ProgramTreeOption.IsChecked == true,
                RiskOnly = RiskOnlyOption.IsChecked == true,
                LogDirectory = directory.Equals(SessionLogs.DefaultDirectory, StringComparison.OrdinalIgnoreCase) ? "" : directory
            };
            next.Save();
            Settings = next;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(this, "设置未保存：" + ex.Message, "无法保存设置", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
