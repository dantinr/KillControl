using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Kill.Models;

public enum CleanupItemKind
{
    File,
    Directory,
    RegistryKey
}

public enum RiskLevel
{
    Low,
    Medium,
    High
}

public enum CleanupMode
{
    Quarantine,
    PermanentDelete
}

public sealed class CleanupCandidate : INotifyPropertyChanged
{
    private bool _isSelected;

    public required string Id { get; init; }
    public required CleanupItemKind Kind { get; init; }
    public required string Target { get; init; }
    public required string DisplayTarget { get; init; }
    public required string Reason { get; init; }
    public required RiskLevel Risk { get; init; }
    public string RegistryViewName { get; init; } = "";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string KindLabel => Kind switch
    {
        CleanupItemKind.Directory => "文件夹",
        CleanupItemKind.File => "文件",
        _ => "注册表"
    };

    public string RiskLabel => Risk switch
    {
        RiskLevel.Low => "低风险",
        RiskLevel.Medium => "需确认",
        _ => "高风险"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class CleanupRequest
{
    public required string OperationId { get; init; }
    public required string ApplicationName { get; init; }
    public required List<CleanupCandidateDto> Items { get; init; }
    public required string ResponsePath { get; init; }
}

public sealed class CleanupCandidateDto
{
    public required string Id { get; init; }
    public required CleanupItemKind Kind { get; init; }
    public required string Target { get; init; }
    public required string Reason { get; init; }
    public required RiskLevel Risk { get; init; }
    public string RegistryViewName { get; init; } = "";
}

public sealed class OperationManifest
{
    public required string OperationId { get; init; }
    public required string ApplicationName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public List<OperationEntry> Entries { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public bool IsPermanent { get; init; }
    public bool Restored { get; set; }
}

public sealed class OperationEntry
{
    public required CleanupItemKind Kind { get; init; }
    public required string OriginalTarget { get; init; }
    public required string BackupTarget { get; init; }
    public string RegistryViewName { get; init; } = "";
    public bool Restored { get; set; }
}

public sealed class WorkerResponse
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string ManifestPath { get; init; } = "";
    public List<string> Errors { get; init; } = [];
}
