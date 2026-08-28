using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Kill.Models;
using Kill.Services;

namespace Kill;

public partial class HistoryWindow : Window
{
    private readonly CleanupCoordinator _coordinator;

    public HistoryWindow(CleanupCoordinator coordinator)
    {
        InitializeComponent();
        _coordinator = coordinator;
        RefreshHistory();
    }

    private void RefreshHistory()
    {
        var rows = _coordinator.GetHistory().Select(item => new HistoryRow(item.Path, item.Manifest)).ToList();
        HistoryGrid.ItemsSource = rows;
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryGrid.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private HistoryRow? SelectedRow => HistoryGrid.SelectedItem as HistoryRow;

    private void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RestoreButton.IsEnabled = SelectedRow is { CanRestore: true };

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row is null || !row.CanRestore) return;
        var confirmation = new ConfirmationWindow(
            "恢复隔离项目", $"恢复“{row.ApplicationName}”的隔离内容？",
            "恢复可能重新引入导致程序异常的旧设置。若原位置已有同名项目，Kill 会停止并保留现有数据。",
            row.ApplicationName, "确认恢复")
        { Owner = this };
        if (confirmation.ShowDialog() != true) return;

        RestoreButton.IsEnabled = false;
        var result = await _coordinator.RestoreAsync(row.ManifestPath);
        var details = result.Errors.Count == 0 ? result.Message : result.Message + "\n\n" + string.Join("\n", result.Errors.Take(5));
        MessageBox.Show(this, details, result.Success ? "恢复完成" : "恢复结果",
            MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        RefreshHistory();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(KillPaths.CommonRoot))
        {
            MessageBox.Show(this, "还没有创建隔离目录。", "隔离记录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = KillPaths.CommonRoot, UseShellExecute = true });
    }

    private sealed class HistoryRow(string manifestPath, OperationManifest manifest)
    {
        public string ManifestPath { get; } = manifestPath;
        public string ApplicationName { get; } = manifest.ApplicationName;
        public string CreatedLabel { get; } = manifest.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        public string ItemCountLabel { get; } = $"{manifest.Entries.Count} 项";
        public string StatusLabel { get; } = manifest.Errors.Count > 0
            ? "部分完成"
            : manifest.Entries.Count == 0
                ? "无变更"
                : manifest.IsPermanent ? "已直接清理" : manifest.Restored ? "已恢复" : "已隔离";
        public bool CanRestore { get; } = !manifest.IsPermanent && !manifest.Restored && manifest.Entries.Any(entry => !entry.Restored);
    }
}
