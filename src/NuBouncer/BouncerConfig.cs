using System.Text.Json;
using System.Text.RegularExpressions;

namespace NuBouncer;

/// <summary>
/// Settings from an optional .nubouncer.json, looked up from the current folder upwards:
/// { "allow": ["MyCompany.*"], "minDownloads": 1000, "minAgeDays": 30 }
/// </summary>
public sealed class BouncerConfig
{
    public const string FileName = ".nubouncer.json";

    public List<string> Allow { get; init; } = [];
    public long MinDownloads { get; init; } = 1_000;
    public int MinAgeDays { get; init; } = 30;

    public bool IsAllowed(string packageId) =>
        Allow.Any(pattern => Matches(pattern, packageId));

    private static bool Matches(string pattern, string id)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(id, regex, RegexOptions.IgnoreCase);
    }

    public static BouncerConfig Load(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, FileName);
            if (!File.Exists(path)) continue;

            try
            {
                return JsonSerializer.Deserialize<BouncerConfig>(File.ReadAllText(path), JsonOptions) ?? new();
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"NuBouncer: ignoring {path}, it isn't valid JSON ({ex.Message}).");
                return new();
            }
        }
        return new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
