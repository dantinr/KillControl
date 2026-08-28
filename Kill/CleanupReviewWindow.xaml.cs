using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using Kill.Models;

namespace Kill;

public partial class CleanupReviewWindow : Window
{
    private readonly string _applicationName;
    private readonly ObservableCollection<CleanupCandidate> _candidates;

    public IReadOnlyList<CleanupCandidate> SelectedCandidates => _candidates.Where(item => item.IsSelected).ToList();
    public CleanupMode SelectedMode { get; private set; } = CleanupMode.Quarantine;

    public CleanupReviewWindow(string applicationName, IReadOnlyList<CleanupCandidate> candidates)
    {
        InitializeComponent();
        _applicationName = applicationName;
        _candidates = new ObservableCollection<CleanupCandidate>(candidates);
        CandidatesGrid.ItemsSource = _candidates;
        SubtitleText.Text = $"为“{applicationName}”找到 {candidates.Count} 个可核对项目。请只选择你确认属于它的内容。";
        foreach (var candidate in _candidates) candidate.PropertyChanged += Candidate_PropertyChanged;
        UpdateSelectedCount();
    }

    private void Candidate_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupCandidate.IsSelected)) UpdateSelectedCount();
    }

    private void SelectLowButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var candidate in _candidates) candidate.IsSelected = candidate.Risk == RiskLevel.Low;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var candidate in _candidates) candidate.IsSelected = false;
    }

    private void ExecuteButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedCandidates;
        if (selected.Count == 0) return;
        if (selected.Any(item => item.Risk == RiskLevel.High))
        {
            var confirmation = new ConfirmationWindow(
                "高风险清理确认", "选中项中包含高风险目标",
                "高风险目标可能包含共享文件或其他程序仍在使用的设置。Kill 会备份，但恢复不能保证重新注册所有服务。",
                _applicationName, "确认并清理")
            { Owner = this };
            if (confirmation.ShowDialog() != true) return;
        }
        SelectedMode = CleanupMode.Quarantine;
        DialogResult = true;
        Close();
    }

    private void DirectCleanupButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCandidates.Count == 0) return;
        var confirmation = new ConfirmationWindow(
            "直接清理确认", "永久删除选中的残留项目？",
            "直接清理不会创建文件或注册表备份，删除后无法通过 Kill 恢复。请确认所选内容不包含共享数据或其他程序仍在使用的设置。",
            _applicationName, "永久清理")
        { Owner = this };
        if (confirmation.ShowDialog() != true) return;

        SelectedMode = CleanupMode.PermanentDelete;
        DialogResult = true;
        Close();
    }

    private void UpdateSelectedCount()
    {
        var count = _candidates.Count(item => item.IsSelected);
        SelectedCountText.Text = $"已选择 {count} / {_candidates.Count}";
        ExecuteButton.IsEnabled = count > 0;
        DirectCleanupButton.IsEnabled = count > 0;
    }
}
