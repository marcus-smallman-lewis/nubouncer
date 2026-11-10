using NuBouncer;

const string Usage = """
NuBouncer: checks NuGet packages' ID at the door.

Usage:
  nubouncer check <PackageId> [--version <Version>]   Check one package
  nubouncer hook                                      Run as a Claude Code PreToolUse hook (reads JSON on stdin)

Exit codes for 'check': 0 = fine, 1 = warnings, 2 = blocked.
Settings: optional .nubouncer.json, e.g. { "allow": ["MyCompany.*"], "minDownloads": 1000, "minAgeDays": 30 }
""";

Bouncer MakeBouncer() =>
    new(new NuGetClient(NuGetClient.CreateHttpClient()), BouncerConfig.Load(Directory.GetCurrentDirectory()));

switch (args.FirstOrDefault()?.ToLowerInvariant())
{
    case "hook":
        return await ClaudeHook.RunAsync(Console.In, Console.Out, MakeBouncer);

    case "check" when args.Length >= 2:
    {
        string? version = null;
        for (var i = 2; i < args.Length - 1; i++)
        {
            if (args[i] is "--version" or "-v") version = args[i + 1];
        }

        Verdict verdict;
        try
        {
            verdict = await MakeBouncer().CheckAsync(new PackageRequest(args[1], version));
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Couldn't reach nuget.org: {ex.Message}");
            return 3;
        }

        var (icon, label) = verdict.Level switch
        {
            Level.Ok => ("✓", "LET IN"),
            Level.Warn => ("⚠", "CHECK FIRST"),
            _ => ("✗", "BLOCKED"),
        };
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"{icon} {label}: {verdict.Request}");
        foreach (var finding in verdict.Findings)
        {
            Console.WriteLine($"  - {finding.Message}");
        }
        return (int)verdict.Level;
    }

    default:
        Console.WriteLine(Usage);
        return args.Length == 0 || args[0] is "--help" or "-h" or "help" ? 0 : 64;
}
