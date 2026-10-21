using System.Net;

namespace ProcessSentinel.Core.Networking;

public enum NetworkTransport { TCP, UDP }

public sealed record NetworkEndpoint(NetworkTransport Protocol, int ProcessId, string LocalAddress, int LocalPort,
    string RemoteAddress, int RemotePort, string State, ProcessInfo? Process = null);

public sealed record NetworkFlow(string Id, NetworkEndpoint Endpoint, ProcessInfo Process,
    DateTimeOffset FirstSeenUtc, DateTimeOffset LastActivityUtc, DateTimeOffset? EndedAtUtc,
    long SentBytes, long ReceivedBytes, double SendBytesPerSecond, double ReceiveBytesPerSecond);

public sealed record NetworkSnapshot(DateTimeOffset StartedAtUtc, DateTimeOffset CapturedAtUtc, bool Running,
    IReadOnlyList<NetworkFlow> Flows, long SentBytes, long ReceivedBytes, double SendBytesPerSecond,
    double ReceiveBytesPerSecond, long EventsLost = 0, long OmittedFlows = 0, string Warning = "");

public sealed record NetworkMessage(string Type, NetworkSnapshot? Snapshot = null, string? Error = null);

public static class NetworkAddress
{
    public static string Normalize(string address)
    {
        if (string.IsNullOrEmpty(address)) return "";
        if (!IPAddress.TryParse(address, out var ip)) return address;
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
    }

    public static string Endpoint(string address, int port) => string.IsNullOrEmpty(address) ? "—" :
        address.Contains(':') ? $"[{address}]:{port}" : $"{address}:{port}";

    public static long Add(long value, long increment) => increment <= 0 ? value :
        value > long.MaxValue - increment ? long.MaxValue : value + increment;
}
