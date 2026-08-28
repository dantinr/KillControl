using System.Text.Json;
using Kill.Models;

namespace Kill.Services;

public static class ServiceBlacklistStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string FilePath => Path.Combine(KillPaths.CommonRoot, "ServiceBlacklist.json");

    public static IReadOnlyList<ServiceBlacklistEntry> Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<List<ServiceBlacklistEntry>>(File.ReadAllText(FilePath), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static async Task WriteAsync(IEnumerable<ServiceBlacklistEntry> entries)
    {
        Directory.CreateDirectory(KillPaths.CommonRoot);
        var ordered = entries.OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(ordered, JsonOptions)).ConfigureAwait(false);
    }
}
