using NuBouncer;

namespace NuBouncer.Tests;

public class BouncerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Bouncer MakeBouncer(BouncerConfig? config = null) =>
        new(new NuGetClient(new HttpClient()), config ?? new BouncerConfig(), new FixedClock(Now));

    private static VersionInfo V(string version, int daysAgo = 400, bool listed = true,
        string? deprecation = null, string[]? reasons = null, int vulns = 0, int severity = -1) =>
        new(version, Now.AddDays(-daysAgo), listed, deprecation, null, reasons ?? [], vulns, severity);

    private static PackageInfo Package(string id, long downloads, bool verified, params VersionInfo[] versions) =>
        new(id, versions, downloads, verified);

    [Fact]
    public void Popular_package_is_let_in()
    {
        var findings = MakeBouncer().Evaluate(new("Serilog"), Package("Serilog", 2_000_000_000, true, V("4.0.1")));
        Assert.All(findings, f => Assert.Equal(Level.Ok, f.Level));
    }

    [Fact]
    public void Missing_version_is_blocked_and_suggests_latest_stable()
    {
        var findings = MakeBouncer().Evaluate(new("Serilog", "99.0.0"),
            Package("Serilog", 2_000_000_000, true, V("4.0.1"), V("4.1.0-dev-1")));
        var f = Assert.Single(findings);
        Assert.Equal(Level.Block, f.Level);
        Assert.Contains("latest is 4.0.1", f.Message);
    }

    [Fact]
    public void Version_ranges_are_not_treated_as_missing()
    {
        var findings = MakeBouncer().Evaluate(new("Serilog", "[4.0,5.0)"), Package("Serilog", 2_000_000_000, true, V("4.0.1")));
        Assert.All(findings, f => Assert.Equal(Level.Ok, f.Level));
    }

    [Fact]
    public void Brand_new_low_download_package_gets_warnings()
    {
        var findings = MakeBouncer().Evaluate(new("Shiny.New.Thing"), Package("Shiny.New.Thing", 12, false, V("1.0.0", daysAgo: 3)));
        Assert.Contains(findings, f => f.Level == Level.Warn && f.Message.Contains("3 days ago"));
        Assert.Contains(findings, f => f.Level == Level.Warn && f.Message.Contains("12 downloads"));
    }

    [Fact]
    public void Unverified_package_spoofing_a_trusted_prefix_is_flagged()
    {
        var findings = MakeBouncer().Evaluate(new("Microsoft.Extensions.SuperCache"),
            Package("Microsoft.Extensions.SuperCache", 5_000, false, V("1.0.0")));
        Assert.Contains(findings, f => f.Level == Level.Warn && f.Message.Contains("'Microsoft.' prefix"));
    }

    [Fact]
    public void Popular_unofficial_package_with_trusted_prefix_gets_a_note_naming_the_publisher()
    {
        var package = new PackageInfo("Microsoft.Extensions.Caching.Redis.Core", [V("1.0.0")], 1_000_000, false, ["SomeDev"]);
        var findings = MakeBouncer().Evaluate(new(package.Id), package);
        Assert.DoesNotContain(findings, f => f.Level >= Level.Warn);
        Assert.Contains(findings, f => f.Level == Level.Info && f.Message.Contains("published by SomeDev"));
    }

    [Fact]
    public void Vulnerable_versions_are_flagged()
    {
        var findings = MakeBouncer().Evaluate(new("Old.Lib", "1.0.0"),
            Package("Old.Lib", 5_000_000, true, V("1.0.0", vulns: 2, severity: 3)));
        Assert.Contains(findings, f => f.Level == Level.Warn && f.Message.Contains("2 known vulnerabilities (worst: critical)"));
    }

    [Fact]
    public void Legacy_deprecation_is_only_a_note()
    {
        // e.g. xunit 2.x: deprecated because v3 exists, but still maintained.
        var findings = MakeBouncer().Evaluate(new("xunit", "2.9.3"),
            Package("xunit", 900_000_000, true, V("2.9.3", deprecation: "Moved to v3.\n\nSee guide.", reasons: ["Legacy"])));
        Assert.DoesNotContain(findings, f => f.Level >= Level.Warn);
        Assert.Contains(findings, f => f.Level == Level.Info && f.Message.Contains("(Moved to v3. See guide.)"));
    }

    [Fact]
    public void Critical_bug_deprecation_is_a_warning()
    {
        var findings = MakeBouncer().Evaluate(new("Buggy.Lib", "1.0.0"),
            Package("Buggy.Lib", 5_000_000, true, V("1.0.0", deprecation: "Data loss bug", reasons: ["CriticalBugs"])));
        Assert.Contains(findings, f => f.Level == Level.Warn && f.Message.Contains("critical bugs"));
    }

    [Fact]
    public async Task Allowlisted_private_packages_skip_all_checks()
    {
        var config = new BouncerConfig { Allow = ["MyCompany.*"] };
        var verdict = await MakeBouncer(config).CheckAsync(new("MyCompany.Internal.Billing"));
        Assert.Equal(Level.Ok, verdict.Level);
    }

    [Fact]
    public void Thresholds_come_from_config()
    {
        var config = new BouncerConfig { MinDownloads = 10, MinAgeDays = 1 };
        var findings = MakeBouncer(config).Evaluate(new("Small.Lib"), Package("Small.Lib", 50, false, V("1.0.0", daysAgo: 3)));
        Assert.All(findings, f => Assert.Equal(Level.Ok, f.Level));
    }
}
