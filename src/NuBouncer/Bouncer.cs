namespace NuBouncer;

/// <summary>
/// Ok and Info never interrupt anyone (Info is shown by check/scan only).
/// Warn makes the Claude Code hook ask first; Block denies.
/// </summary>
public enum Level { Ok = 0, Info = 1, Warn = 2, Block = 3 }

public sealed record Finding(Level Level, string Message);

public sealed record PackageRequest(string Id, string? Version = null)
{
    public override string ToString() => Version is null ? Id : $"{Id} {Version}";
}

public sealed record Verdict(PackageRequest Request, Level Level, IReadOnlyList<Finding> Findings);

/// <summary>Decides whether a package is let in.</summary>
public sealed class Bouncer(NuGetClient nuget, BouncerConfig config, TimeProvider? clock = null)
{
    // Prefixes owned by big publishers. A package using one without a verified
    // (reserved) prefix and with few downloads is a classic spoofing trick.
    private static readonly string[] TrustedPrefixes =
    [
        "Microsoft.", "System.", "Azure.", "AWSSDK.", "Amazon.", "Google.", "Newtonsoft.",
        "NuGet.", "Serilog.", "xunit.", "NUnit.", "Polly.", "Dapper.", "AutoMapper.",
        "MediatR.", "FluentValidation.", "Npgsql.", "StackExchange.", "Grpc.", "OpenTelemetry.",
    ];

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<Verdict> CheckAsync(PackageRequest request, CancellationToken ct = default)
    {
        if (config.IsAllowed(request.Id))
        {
            return new Verdict(request, Level.Ok, [new Finding(Level.Ok, "On your allowlist.")]);
        }

        var package = await nuget.GetPackageAsync(request.Id, ct);
        if (package is null)
        {
            return new Verdict(request, Level.Block,
            [
                new Finding(Level.Block,
                    $"'{request.Id}' doesn't exist on nuget.org. AI assistants sometimes invent package names, " +
                    "and attackers register them with malware. Double-check the name. " +
                    "If it's a private package, add it to .nubouncer.json \"allow\"."),
            ]);
        }

        var findings = Evaluate(request, package);
        return new Verdict(request, Worst(findings), findings);
    }

    internal List<Finding> Evaluate(PackageRequest request, PackageInfo package)
    {
        var findings = new List<Finding>();
        var now = _clock.GetUtcNow();

        // Requested version must exist.
        VersionInfo? selected = null;
        if (request.Version is not null && !IsVersionRange(request.Version))
        {
            selected = package.Versions.FirstOrDefault(v =>
                string.Equals(v.Version, request.Version, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                var latest = LatestListed(package)?.Version ?? "none";
                findings.Add(new Finding(Level.Block,
                    $"Version {request.Version} of {package.Id} doesn't exist (latest is {latest}). " +
                    "AI assistants often make up version numbers."));
                return findings;
            }
        }
        selected ??= LatestListed(package) ?? package.Versions.LastOrDefault();

        if (package.Versions.All(v => !v.Listed))
        {
            findings.Add(new Finding(Level.Warn, $"Every version of {package.Id} is unlisted (hidden) on nuget.org."));
        }

        // Brand new packages are the main slopsquatting risk.
        var firstPublished = package.Versions.Where(v => v.Published is not null).Min(v => v.Published);
        if (firstPublished is { } first)
        {
            var ageDays = (now - first).TotalDays;
            if (ageDays < config.MinAgeDays)
            {
                findings.Add(new Finding(Level.Warn,
                    $"{package.Id} was first published {DescribeAge(ageDays)}. " +
                    "Brand new packages are how hallucinated names get hijacked."));
            }
        }

        if (package.TotalDownloads < config.MinDownloads)
        {
            findings.Add(new Finding(Level.Warn,
                $"{package.Id} has only {package.TotalDownloads:N0} downloads."));
        }

        var prefix = TrustedPrefixes.FirstOrDefault(p => package.Id.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (prefix is not null && !package.Verified)
        {
            var by = package.Owners is { Count: > 0 } owners ? $" (published by {string.Join(", ", owners)})" : "";
            // Popular ones are usually community forks: worth knowing, not worth stopping for.
            findings.Add(package.TotalDownloads < 100_000
                ? new Finding(Level.Warn,
                    $"{package.Id} uses the '{prefix}' prefix but isn't from its verified owner{by}. " +
                    "It may be pretending to be an official package.")
                : new Finding(Level.Info,
                    $"{package.Id} uses the '{prefix}' prefix but isn't from its verified owner{by}, so it isn't official."));
        }

        if (selected?.DeprecationMessage is { } deprecation)
        {
            // "Legacy"/"Other" just means there's a newer option (e.g. xunit 2 -> xunit.v3), so it's
            // only a note. "CriticalBugs" is worth stopping for.
            var critical = selected.DeprecationReasons.Any(r => r.Equals("CriticalBugs", StringComparison.OrdinalIgnoreCase));
            var alt = selected.AlternatePackage is null ? "" : $" Suggested alternative: {selected.AlternatePackage}.";
            findings.Add(new Finding(critical ? Level.Warn : Level.Info,
                $"{package.Id} {selected.Version} is deprecated{(critical ? " because of critical bugs" : "")} ({Shorten(deprecation)}).{alt}"));
        }

        if (selected is { VulnerabilityCount: > 0 })
        {
            var severity = selected.MaxVulnerabilitySeverity switch
            {
                3 => "critical",
                2 => "high",
                1 => "moderate",
                0 => "low",
                _ => "unknown",
            };
            findings.Add(new Finding(Level.Warn,
                $"{package.Id} {selected.Version} has {selected.VulnerabilityCount} known " +
                $"vulnerabilit{(selected.VulnerabilityCount == 1 ? "y" : "ies")} (worst: {severity})."));
        }

        if (findings.All(f => f.Level == Level.Info))
        {
            findings.Add(new Finding(Level.Ok,
                $"{package.Id} looks fine: {package.TotalDownloads:N0} downloads" +
                (package.Verified ? ", verified owner." : ".")));
        }

        return findings;
    }

    private static Level Worst(IEnumerable<Finding> findings) =>
        findings.Select(f => f.Level).DefaultIfEmpty(Level.Ok).Max();

    // Prefer the latest stable version; fall back to the latest prerelease.
    private static VersionInfo? LatestListed(PackageInfo package) =>
        package.Versions.LastOrDefault(v => v.Listed && !v.Version.Contains('-'))
        ?? package.Versions.LastOrDefault(v => v.Listed);

    private static string Shorten(string text, int max = 160)
    {
        var oneLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= max ? oneLine : oneLine[..max].TrimEnd() + "…";
    }

    private static bool IsVersionRange(string version) =>
        version.IndexOfAny(['[', '(', ',', '*']) >= 0;

    private static string DescribeAge(double days) => days switch
    {
        < 1 => "today",
        < 2 => "yesterday",
        _ => $"{(int)days} days ago",
    };
}
