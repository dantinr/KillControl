using System.Windows;
using System.Windows.Controls;
using Kill.Models;

namespace Kill;

public partial class InstallDirectoryMatchWindow : Window
{
    public InstallDirectoryMatchWindow(string directoryPath, IReadOnlyList<InstallDirectoryMatch> matches)
    {
        InitializeComponent();
        DirectoryPathText.Text = directoryPath;
        DirectoryPathText.ToolTip = directoryPath;
        MatchCountText.Text = $"找到 {matches.Count} 个具有明确路径证据的桌面应用";
        MatchesList.ItemsSource = matches;
        if (matches.Count == 1) MatchesList.SelectedIndex = 0;
    }

    public InstallDirectoryMatch? SelectedMatch => MatchesList.SelectedItem as InstallDirectoryMatch;

    private void MatchesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SelectedMatch;
        ContinueButton.IsEnabled = selected?.Application.CanUninstall == true;
        SelectionStatusText.Text = selected switch
        {
            null => "请选择要卸载的应用。",
            { Application.CanUninstall: false } => "该应用没有登记可用的官方卸载入口。",
            { CanUseSelectedDirectoryForCleanup: true } => "卸载后可复核所选目录；整目录候选为高风险且默认不选中。",
            _ => "所选目录只用于识别；卸载后不会把它作为整体清理。"
        };
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMatch?.Application.CanUninstall != true) return;
        DialogResult = true;
        Close();
    }
}
