using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using ProcessSentinel.Core;
using Activity = ProcessSentinel.Core.Activity;

namespace ProcessSentinel.App;

public partial class BehaviorMonitorView
{
    private ICollectionView? reviewView;
    private bool exportingReview;

    private void BindReviewSession()
    {
        ReviewGrid.SelectedItem = null;
        reviewView = selectedSession is { } session ? CollectionViewSource.GetDefaultView(session.ReviewEvents) : null;
        if (reviewView is not null) reviewView.Filter = MatchReview;
        ReviewGrid.ItemsSource = reviewView;
        UpdateReviewSummary();
    }

    private bool MatchReview(object value) => value is Activity item && (string.IsNullOrWhiteSpace(ReviewSearch.Text)
        || $"{item.ProcessText} {item.Target} {item.Operation} {item.RuleId} {item.Reason} {item.Detail}"
            .Contains(ReviewSearch.Text, StringComparison.OrdinalIgnoreCase));

    private void ReviewSearch_Changed(object sender, TextChangedEventArgs e)
    {
        reviewView?.Refresh();
        if (ReviewGrid is not null) UpdateReviewSummary();
    }

    private void UpdateReviewSummary()
    {
        int count = selectedSession?.ReviewEvents.Count ?? 0;
        ReviewTab.Header = $"需要复核（{count:N0}）";
        long receivedCount = Math.Max(count, client?.Alerts ?? 0);
        ReviewCount.Text = $"累计 {receivedCount:N0} 条 · 面板已保留 {count:N0} 条 · 当前显示 {ReviewGrid.Items.Count:N0} 条";
        string path = client?.ReviewJournalPath ?? "";
        ReviewLogPath.Text = string.IsNullOrEmpty(path) ? "开始新监控后，复核记录会自动保存到独立日志。" : "复核日志：" + path;
        ExportReviewButton.IsEnabled = !exportingReview && !string.IsNullOrEmpty(path);
        ReviewEmptyState.Visibility = ReviewGrid.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ReviewEmptyTitle.Text = count > 0 ? "没有符合搜索条件的复核记录" : "尚无需要复核的事件";
    }

    private void ShowReview_Click(object sender, RoutedEventArgs e)
    {
        ReviewSearch.Clear();
        ShowReviewPanel();
    }
    internal void ShowReviewPanel()
    {
        MonitorTabs.SelectedItem = ReviewTab;
        if (ReviewGrid.SelectedItem is null && ReviewGrid.Items.Count > 0) ReviewGrid.SelectedIndex = 0;
    }

    private void ReviewGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReviewTab.IsSelected && ReviewGrid.SelectedItem is Activity item) ShowActivityEvidence(item);
        else if (ReviewTab.IsSelected)
        {
            DetailTitle.Text = "复核事件证据";
            DetailText.Text = "选择一条复核记录，查看完整目标、触发规则及原因。";
        }
    }

    private async void ExportReview_Click(object sender, RoutedEventArgs e)
    {
        var current = client;
        if (exportingReview || current is null || string.IsNullOrEmpty(current.ReviewJournalPath)) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出本会话的全部复核记录",
            Filter = "CSV 表格 (*.csv)|*.csv|复核原始日志 (*.jsonl)|*.jsonl",
            FileName = "需要复核-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            AddExtension = true
        };
        if (dialog.ShowDialog(HostWindow) != true) return;
        exportingReview = true;
        ExportReviewButton.IsEnabled = false;
        string temporary = Path.Combine(Path.GetTempPath(), "ProcessSentinel-review-export-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            current.CopyReviewJournal(temporary);
            await Task.Run(() =>
            {
                if (dialog.FilterIndex == 1) EvidenceJournal.ExportCsv(temporary, dialog.FileName);
                else File.Copy(temporary, dialog.FileName, true);
            });
            StatusText.Text = "复核记录已导出到 " + dialog.FileName;
        }
        catch (Exception ex) { MessageBox.Show(HostWindow, ex.Message, "导出复核记录失败", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally
        {
            exportingReview = false;
            if (File.Exists(temporary)) File.Delete(temporary);
            UpdateReviewSummary();
        }
    }
}
