using System.Text;

namespace ProcessSentinel.Core;

public sealed class EvidenceJournal : IDisposable
{
    private readonly StreamWriter writer;
    private readonly object gate = new();
    private readonly bool reviewOnly;
    public string Path { get; }
    public EvidenceJournal(string path, MonitorRequest request, bool reviewOnly = false)
    {
        Path = path;
        this.reviewOnly = reviewOnly;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        writer = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        writer.WriteLine(Protocol.Serialize(new
        {
            Type = "session",
            Started = DateTimeOffset.Now,
            Request = request,
            Version = Protocol.Version,
            JournalType = reviewOnly ? "review" : "all"
        }));
        writer.Flush();
    }
    public void Append(WireMessage message)
    {
        if (reviewOnly && message.Event is { Risk: RiskLevel.None }) return;
        lock (gate) writer.WriteLine(Protocol.Serialize(message));
    }
    public void Flush() { lock (gate) writer.Flush(); }
    public void CopyTo(string destination)
    {
        lock (gate)
        {
            writer.Flush();
            if (System.IO.Path.GetFullPath(destination).Equals(System.IO.Path.GetFullPath(Path), StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择与原始日志不同的导出路径。");
            File.Copy(Path, destination, true);
        }
    }
    public void Dispose() { lock (gate) writer.Dispose(); }

    public static IEnumerable<Activity> ReadEvents(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            WireMessage? message;
            try { message = Protocol.Deserialize<WireMessage>(line); }
            catch (System.Text.Json.JsonException) { continue; } // Ignore an interrupted final line.
            if (message?.Event is not null) yield return message.Event;
        }
    }
    public static void ExportCsv(string source, string destination)
    {
        using var output = new StreamWriter(destination, false, new UTF8Encoding(true));
        output.WriteLine("时间,序号,PID,进程,类别,操作,目标,字节,级别,规则,原因,详情");
        foreach (var item in ReadEvents(source))
            output.WriteLine(string.Join(',', new[] { item.Time.ToString("O"), item.Sequence.ToString(), item.ProcessId.ToString(), item.ProcessName,
                item.KindText, item.Operation, item.Target, item.Bytes.ToString(), item.RiskText, item.RuleId, item.Reason, item.Detail }.Select(CsvCell)));
    }
    public static string CsvCell(string value)
    {
        // Untrusted paths / command lines must remain text when opened in a spreadsheet.
        if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
