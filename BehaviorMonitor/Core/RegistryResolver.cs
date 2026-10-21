using System.Buffers.Binary;
using System.Text;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace ProcessSentinel.Core;

public sealed record RegistryPayload(ulong Handle, int? Status, string Name);

// TraceEvent 3.2.8's RegistryTraceData.KeyHandle/Status getters omit their return statements.
// Maintain a separate KCB map rather than sharing the parser's file-name lookup table at handle zero.
public sealed class RegistryResolver
{
    private readonly Dictionary<ulong, string> names = new();
    public static RegistryPayload Decode(ReadOnlySpan<byte> bytes, int version, int pointerSize)
    {
        int nameOffset = 16 + pointerSize;
        if (version < 2 || pointerSize is not (4 or 8) || bytes.Length < nameOffset) return new(0, null, "");
        int status = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        ulong handle = pointerSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        var text = bytes[nameOffset..];
        int count = 0;
        while (count + 1 < text.Length && (text[count] != 0 || text[count + 1] != 0)) count += 2;
        return new(handle, status, Encoding.Unicode.GetString(text[..count]));
    }
    public void Observe(RegistryTraceData data)
    {
        var payload = Decode(data.EventData(), data.Version, data.PointerSize);
        Observe((int)data.Opcode, payload);
    }
    public void Observe(int opcode, RegistryPayload payload)
    {
        if (payload.Handle == 0) return;
        if (opcode == 23) { names.Remove(payload.Handle); return; }
        if (opcode is 22 or 24 or 25 || (opcode is 10 or 11 && payload.Status == 0))
        {
            if (payload.Name.StartsWith(@"\REGISTRY\", StringComparison.OrdinalIgnoreCase)
                && (names.ContainsKey(payload.Handle) || names.Count < 100000)) names[payload.Handle] = payload.Name;
        }
    }
    public (string Target, int? Status) Resolve(RegistryTraceData data)
    {
        var payload = Decode(data.EventData(), data.Version, data.PointerSize);
        int opcode = (int)data.Opcode;
        Observe(opcode, payload);
        bool valueName = opcode is 13 or 14 or 15 or 16 or 18 or 19;
        string key = names.GetValueOrDefault(payload.Handle) ?? "";
        if (!valueName && payload.Name.StartsWith(@"\REGISTRY\", StringComparison.OrdinalIgnoreCase)) key = payload.Name;
        if (string.IsNullOrEmpty(key)) key = !valueName && !string.IsNullOrEmpty(payload.Name) ? "[相对键名] " + payload.Name : "[注册表键路径未解析]";
        return (valueName && !string.IsNullOrEmpty(payload.Name) ? key + "\\" + payload.Name : key, payload.Status);
    }
}
