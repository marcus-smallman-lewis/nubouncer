using System.Net;
using System.Text.Json;

namespace NuBouncer;

/// <summary>What nuget.org knows about one package.</summary>
public sealed record PackageInfo(
    string Id,
    IReadOnlyList<VersionInfo> Versions,
    long TotalDownloads,
    bool Verified,
    IReadOnlyList<string>? Owners = null);

public sealed record VersionInfo(
    string Version,
    DateTimeOffset? Published,
    bool Listed,
    string? DeprecationMessage,
    string? AlternatePackage,
    IReadOnlyList<string> DeprecationReasons,
    int VulnerabilityCount,
    int MaxVulnerabilitySeverity);

/// <summary>Small client for the public nuget.org v3 API. No API key needed.</summary>
public sealed class NuGetClient(HttpClient http)
{
    private const string ServiceIndex = "https://api.nuget.org/v3/index.json";
    private string? _registrationBase;
    private string? _searchBase;

    public static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NuBouncer/0.1 (+https://github.com/marcus-smallman-lewis/nubouncer)");
        return client;
    }

    /// <summary>Returns null when the package doesn't exist on nuget.org.</summary>
    public async Task<PackageInfo?> GetPackageAsync(string id, CancellationToken ct = default)
    {
        await EnsureServiceIndexAsync(ct);

        var versions = await GetVersionsAsync(id, ct);
        if (versions is null) return null;

        var (downloads, verified, owners) = await GetSearchStatsAsync(id, ct);
        return new PackageInfo(id, versions, downloads, verified, owners);
    }

    private async Task EnsureServiceIndexAsync(CancellationToken ct)
    {
        if (_registrationBase is not null && _searchBase is not null) return;

        using var doc = JsonDocument.Parse(await http.GetStringAsync(ServiceIndex, ct));
        foreach (var resource in doc.RootElement.GetProperty("resources").EnumerateArray())
        {
            var type = resource.GetProperty("@type").GetString();
            var url = resource.GetProperty("@id").GetString();
            if (type == "RegistrationsBaseUrl/3.6.0") _registrationBase ??= url;
            if (type == "SearchQueryService/3.5.0") _searchBase ??= url;
        }

        _registrationBase ??= "https://api.nuget.org/v3/registration5-gz-semver2/";
        _searchBase ??= "https://azuresearch-usnc.nuget.org/query";
    }

    private async Task<List<VersionInfo>?> GetVersionsAsync(string id, CancellationToken ct)
    {
        var url = $"{_registrationBase}{id.ToLowerInvariant()}/index.json";
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        using var index = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var versions = new List<VersionInfo>();

        foreach (var page in index.RootElement.GetProperty("items").EnumerateArray())
        {
            if (page.TryGetProperty("items", out var leaves))
            {
                ReadLeaves(leaves, versions);
            }
            else
            {
                // Big packages split versions into pages that must be fetched separately.
                var pageUrl = page.GetProperty("@id").GetString()!;
                using var pageDoc = JsonDocument.Parse(await http.GetStringAsync(pageUrl, ct));
                ReadLeaves(pageDoc.RootElement.GetProperty("items"), versions);
            }
        }

        return versions;
    }

    private static void ReadLeaves(JsonElement leaves, List<VersionInfo> into)
    {
        foreach (var leaf in leaves.EnumerateArray())
        {
            var entry = leaf.GetProperty("catalogEntry");
            var version = entry.GetProperty("version").GetString()!;

            DateTimeOffset? published = null;
            if (entry.TryGetProperty("published", out var p) && DateTimeOffset.TryParse(p.GetString(), out var date)
                && date.Year > 1900) // unlisted versions report 1900-01-01
            {
                published = date;
            }

            var listed = !entry.TryGetProperty("listed", out var l) || l.ValueKind != JsonValueKind.False;

            string? deprecation = null, alternate = null;
            List<string> reasonList = [];
            if (entry.TryGetProperty("deprecation", out var dep))
            {
                if (dep.TryGetProperty("reasons", out var reasons))
                {
                    reasonList = reasons.EnumerateArray().Select(r => r.GetString() ?? "").ToList();
                }
                deprecation = dep.TryGetProperty("message", out var m) ? m.GetString() : null;
                if (string.IsNullOrWhiteSpace(deprecation))
                {
                    deprecation = reasonList.Count > 0 ? string.Join(", ", reasonList) : "deprecated";
                }
                if (dep.TryGetProperty("alternatePackage", out var alt) && alt.TryGetProperty("id", out var altId))
                {
                    alternate = altId.GetString();
                }
            }

            int vulnCount = 0, maxSeverity = -1;
            if (entry.TryGetProperty("vulnerabilities", out var vulns))
            {
                foreach (var v in vulns.EnumerateArray())
                {
                    vulnCount++;
                    if (v.TryGetProperty("severity", out var s) && int.TryParse(s.GetString(), out var sev))
                    {
                        maxSeverity = Math.Max(maxSeverity, sev);
                    }
                }
            }

            into.Add(new VersionInfo(version, published, listed, deprecation, alternate, reasonList, vulnCount, maxSeverity));
        }
    }

    private async Task<(long Downloads, bool Verified, List<string> Owners)> GetSearchStatsAsync(string id, CancellationToken ct)
    {
        var url = $"{_searchBase}?q=packageid:{Uri.EscapeDataString(id)}&prerelease=true&semVerLevel=2.0.0&take=1";
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0) return (0, false, []);

        var hit = data[0];
        var downloads = hit.TryGetProperty("totalDownloads", out var d) ? d.GetInt64() : 0;
        var verified = hit.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True;
        var owners = hit.TryGetProperty("owners", out var o) && o.ValueKind == JsonValueKind.Array
            ? o.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : [];
        return (downloads, verified, owners);
    }
}
