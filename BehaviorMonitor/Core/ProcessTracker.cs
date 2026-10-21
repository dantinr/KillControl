namespace ProcessSentinel.Core;

// Accessed by the ETW consumer; the lock also protects statistics and initial snapshot seeding.
public sealed class ProcessTracker
{
    private readonly Dictionary<int, ProcessInfo> active = new();
    private readonly Dictionary<(int Id, long Started), TrackedProcess> known = new();
    private readonly object gate = new();
    private readonly bool includeChildren;
    private readonly (int Id, long Started) selectedIdentity;
    private readonly HashSet<(int Id, long Started)> ancestors;
    private long revision = 1;
    public ProcessTracker(ProcessInfo root, bool includeChildren, ProcessInfo? selected = null, IReadOnlyList<ProcessInfo>? lineage = null)
    {
        this.includeChildren = includeChildren;
        selected ??= root;
        selectedIdentity = (selected.Id, selected.StartTimeUtcTicks);
        ancestors = (lineage ?? []).Where(x => x.Id != selected.Id).Select(x => (x.Id, x.StartTimeUtcTicks)).ToHashSet();
        active[root.Id] = root;
        known[(root.Id, root.StartTimeUtcTicks)] = Describe(root, true);
        if (lineage is not null) Seed(lineage);
    }
    public int Count { get { lock (gate) return active.Count; } }
    public ProcessInfo? Find(int id) { lock (gate) return active.GetValueOrDefault(id); }
    public void RecordNetworkSend(int id, long bytes, string destination)
    {
        if (bytes <= 0 || RiskEngine.IsLocalAddress(destination)) return;
        lock (gate)
        {
            if (!active.TryGetValue(id, out var process)) return;
            var identity = (id, process.StartTimeUtcTicks);
            var current = known[identity];
            known[identity] = current with { OutboundBytes = current.OutboundBytes + Math.Min(bytes, long.MaxValue - current.OutboundBytes) };
            revision++;
        }
    }
    public ProcessSnapshot? GetSnapshot(long afterRevision = -1)
    {
        lock (gate)
        {
            if (revision <= afterRevision) return null;
            return new(revision, known.Values.OrderByDescending(x => x.IsRoot)
                .ThenBy(x => x.Process.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Process.Id)
                .ThenBy(x => x.Process.StartTimeUtcTicks).ToArray());
        }
    }
    public void Seed(IEnumerable<ProcessInfo> snapshot)
    {
        if (!includeChildren) return;
        var pending = snapshot.ToList();
        bool changed;
        do
        {
            changed = false;
            foreach (var item in pending.ToArray())
                if (Start(item)) { pending.Remove(item); changed = true; }
        } while (changed);
    }
    public bool Start(ProcessInfo process)
    {
        lock (gate)
        {
            // A start event is authoritative: never retain a terminated identity after PID reuse.
            if (active.TryGetValue(process.Id, out var prior))
            {
                if (prior.StartTimeUtcTicks == process.StartTimeUtcTicks) return false;
                active.Remove(process.Id);
                MarkExited(prior);
            }
            if (!includeChildren || !active.TryGetValue(process.ParentId, out var parent)) return false;
            if (process.StartTimeUtcTicks <= 0 || process.StartTimeUtcTicks < parent.StartTimeUtcTicks) return false;
            active[process.Id] = process;
            known[(process.Id, process.StartTimeUtcTicks)] = Describe(process, false);
            revision++;
            return true;
        }
    }
    private TrackedProcess Describe(ProcessInfo process, bool isRoot) => new(process, isRoot, true,
        (process.Id, process.StartTimeUtcTicks) == selectedIdentity, ancestors.Contains((process.Id, process.StartTimeUtcTicks)));
    public ProcessInfo? Stop(int id)
    {
        lock (gate)
        {
            if (!active.Remove(id, out var process)) return null;
            MarkExited(process);
            return process;
        }
    }
    private void MarkExited(ProcessInfo process)
    {
        var identity = (process.Id, process.StartTimeUtcTicks);
        known[identity] = known[identity] with { IsRunning = false };
        revision++;
    }
}
