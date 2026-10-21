using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using ProcessSentinel.Core;
using Activity = ProcessSentinel.Core.Activity;

namespace ProcessSentinel.App;

internal sealed class MonitorSession(MonitorRequest request, string boundary = "", string? logDirectory = null) : INotifyPropertyChanged
{
    public MonitorSession(ProcessInfo root, bool includeChildren) : this(new(root, includeChildren)) { }
    public MonitorRequest Request { get; } = request;
    public ProcessInfo Root => Request.Root;
    public ProcessInfo Selected => Request.SelectedProcess ?? Root;
    public bool IncludeChildren => Request.IncludeChildren;
    public string Boundary { get; } = boundary;
    public string ScopeText => IncludeChildren ? $"所选：{Selected.Label} · 范围：主进程及全部子孙进程" : $"仅监控所选进程：{Selected.Label}";
    public MonitorClient Client { get; } = new(logDirectory);
    public Stopwatch Duration { get; } = new();
    public Queue<Activity> Recent { get; } = new();
    public ObservableCollection<Activity> ReviewEvents { get; } = new();
    public bool Starting { get; set; } = true;
    public string StartupError { get; set; } = "";
    public string Error => string.IsNullOrEmpty(StartupError) ? Client.Error : StartupError;
    private string label = "";
    public string Label => label;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Retain(Activity activity)
    {
        Recent.Enqueue(activity);
        if (Recent.Count > 5000) Recent.Dequeue();
    }

    public void UpdateLabel(string? previewState = null)
    {
        string state = previewState ?? (Starting ? "连接中" : Client.Running ? "监控中" : string.IsNullOrEmpty(Error) ? "已停止" : "异常");
        string next = $"{Root.Label} · {state}" + (previewState is null ? $" · {Client.Total:N0} 条" : "");
        if (label == next) return;
        label = next;
        PropertyChanged?.Invoke(this, new(nameof(Label)));
    }
}
