using System.Text.Json;

namespace Unlose.Core.Config;

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static async Task<UnloseConfig> LoadAsync(string path = "unlose.json", CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return new UnloseConfig();
        var json = await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8, ct);
        return JsonSerializer.Deserialize<UnloseConfig>(json, Options) ?? new UnloseConfig();
    }

    public static async Task SaveAsync(UnloseConfig config, string path = "unlose.json", CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(config, Options);
        await File.WriteAllTextAsync(path, json, System.Text.Encoding.UTF8, ct);
    }
}
