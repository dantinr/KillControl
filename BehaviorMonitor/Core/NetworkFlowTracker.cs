namespace ProcessSentinel.Core.Networking;

public sealed class NetworkFlowTracker
{
    private readonly object sync = new();
    private readonly Dictionary<FlowKey, Entry> current = [];
    private readonly List<Entry> entries = [];
    private readonly int capacity;
    private readonly DateTimeOffset startedAt;
    private DateTimeOffset sampledAt;
    private long sent, received, sampledSent, sampledReceived, omitted;

    public NetworkFlowTracker(DateTimeOffset startedAtUtc, int capacity = 5000)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        startedAt = sampledAt = startedAtUtc;
        this.capacity = capacity;
    }

    public void Record(NetworkEndpoint endpoint, ProcessInfo process, DateTimeOffset time, long bytes = 0,
        bool receive = false, bool close = false, ulong connectionId = 0)
    {
        if (endpoint.ProcessId <= 0 || time < startedAt || process.StartTimeUtcTicks > time.UtcTicks ||
            endpoint.LocalPort is < 0 or > 65535 || endpoint.RemotePort is < 0 or > 65535) return;
        lock (sync)
        {
            if (receive) received = NetworkAddress.Add(received, bytes);
            else sent = NetworkAddress.Add(sent, bytes);
            var key = Key(endpoint, process);
            current.TryGetValue(key, out var entry);
            if (entry is not null && (entry.EndedAt is { } ended && time > ended ||
                connectionId != 0 && entry.ConnectionId != 0 && connectionId != entry.ConnectionId))
            {
                entry.EndedAt ??= time;
                entry.Endpoint = entry.Endpoint with { State = "REPLACED" };
                entry = null;
            }
            entry ??= Create(key, endpoint, process, time);
            if (entry is null) return;
            if (connectionId != 0) entry.ConnectionId = connectionId;
            entry.LastActivity = time > entry.LastActivity ? time : entry.LastActivity;
            if (receive) entry.Received = NetworkAddress.Add(entry.Received, bytes);
            else entry.Sent = NetworkAddress.Add(entry.Sent, bytes);
            if (close) { entry.EndedAt = time; entry.Endpoint = entry.Endpoint with { State = "DISCONNECTED" }; }
        }
    }

    public void Observe(IReadOnlyList<NetworkEndpoint> endpoints, DateTimeOffset time)
    {
        lock (sync)
        {
            var present = new HashSet<FlowKey>();
            foreach (var endpoint in endpoints)
            {
                var process = endpoint.Process ?? new(endpoint.ProcessId, 0, $"PID {endpoint.ProcessId}", "", 0);
                var key = Key(endpoint, process);
                present.Add(key);
                if (!current.TryGetValue(key, out var entry) || entry.EndedAt is not null)
                    entry = Create(key, endpoint, process, time);
                if (entry is null) continue;
                entry.Process = process;
                entry.Endpoint = endpoint;
            }
            foreach (var entry in current.Values)
            {
                if (entry.EndedAt is not null || present.Contains(entry.Key)) continue;
                bool udpBound = entry.Endpoint.Protocol == NetworkTransport.UDP && endpoints.Any(endpoint =>
                    endpoint.Protocol == NetworkTransport.UDP && endpoint.ProcessId == entry.Process.Id &&
                    endpoint.Process?.StartTimeUtcTicks == entry.Process.StartTimeUtcTicks && endpoint.LocalPort == entry.Endpoint.LocalPort &&
                    (NetworkAddress.Normalize(endpoint.LocalAddress) == NetworkAddress.Normalize(entry.Endpoint.LocalAddress) ||
                     endpoint.LocalAddress == (entry.Endpoint.LocalAddress.Contains(':') ? "::" : "0.0.0.0")));
                if (udpBound)
                {
                    entry.Endpoint = entry.Endpoint with { State = time - entry.LastActivity > TimeSpan.FromSeconds(30) ? "UDP_IDLE" : "UDP_ACTIVE" };
                }
                else if (time - entry.LastActivity >= TimeSpan.FromSeconds(2))
                {
                    entry.EndedAt = time;
                    entry.Endpoint = entry.Endpoint with { State = "DISAPPEARED" };
                }
            }
        }
    }

    public void ProcessExited(int id, DateTimeOffset time)
    {
        lock (sync)
            foreach (var entry in current.Values.Where(entry => entry.Process.Id == id && entry.EndedAt is null && entry.Process.StartTimeUtcTicks <= time.UtcTicks))
            { entry.EndedAt = time; entry.Endpoint = entry.Endpoint with { State = "PROCESS_EXITED" }; }
    }

    public NetworkSnapshot Snapshot(DateTimeOffset time, bool running = true, long eventsLost = 0, string warning = "")
    {
        lock (sync)
        {
            double seconds = Math.Max(0.001, (time - sampledAt).TotalSeconds);
            var flows = new List<NetworkFlow>(entries.Count);
            foreach (var entry in entries)
            {
                if (!running && entry.EndedAt is null)
                { entry.EndedAt = time; entry.Endpoint = entry.Endpoint with { State = "MONITOR_STOPPED" }; }
                flows.Add(new(entry.Id, entry.Endpoint, entry.Process, entry.FirstSeen, entry.LastActivity, entry.EndedAt,
                    entry.Sent, entry.Received, running && entry.EndedAt is null ? (entry.Sent - entry.SampledSent) / seconds : 0,
                    running && entry.EndedAt is null ? (entry.Received - entry.SampledReceived) / seconds : 0));
                entry.SampledSent = entry.Sent;
                entry.SampledReceived = entry.Received;
            }
            var result = new NetworkSnapshot(startedAt, time, running, flows, sent, received,
                running ? (sent - sampledSent) / seconds : 0, running ? (received - sampledReceived) / seconds : 0,
                eventsLost, omitted, warning);
            sampledSent = sent;
            sampledReceived = received;
            sampledAt = time;
            return result;
        }
    }

    private Entry? Create(FlowKey key, NetworkEndpoint endpoint, ProcessInfo process, DateTimeOffset time)
    {
        if (entries.Count >= capacity)
        {
            var expired = entries.FirstOrDefault(entry => entry.EndedAt is not null);
            if (expired is null) { omitted++; return null; }
            entries.Remove(expired);
            if (current.GetValueOrDefault(expired.Key) == expired) current.Remove(expired.Key);
            omitted++;
        }
        var entry = new Entry(key, endpoint, process, time);
        entries.Add(entry);
        current[key] = entry;
        return entry;
    }

    private static FlowKey Key(NetworkEndpoint endpoint, ProcessInfo process) => new(endpoint.Protocol, process.Id,
        process.StartTimeUtcTicks, NetworkAddress.Normalize(endpoint.LocalAddress), endpoint.LocalPort,
        NetworkAddress.Normalize(endpoint.RemoteAddress), endpoint.RemotePort);
    private sealed record FlowKey(NetworkTransport Protocol, int ProcessId, long StartTicks, string LocalAddress,
        int LocalPort, string RemoteAddress, int RemotePort);
    private sealed class Entry(FlowKey key, NetworkEndpoint endpoint, ProcessInfo process, DateTimeOffset time)
    {
        public FlowKey Key { get; } = key;
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public NetworkEndpoint Endpoint = endpoint;
        public ProcessInfo Process = process;
        public DateTimeOffset FirstSeen = time, LastActivity = time;
        public DateTimeOffset? EndedAt;
        public ulong ConnectionId;
        public long Sent, Received, SampledSent, SampledReceived;
    }
}
