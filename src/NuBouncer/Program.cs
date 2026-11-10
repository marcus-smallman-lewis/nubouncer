using NuBouncer;

const string Usage = """
NuBouncer: checks NuGet packages' ID at the door.

Usage:
  nubouncer check <PackageId> [--version <Version>]   Check one package
  nubouncer scan [folder]                             Check every package in the project files under a folder
  nubouncer hook                                      Run as a Claude Code PreToolUse hook (reads JSON on stdin)

Exit codes for 'check' and 'scan': 0 = fine, 1 = warnings, 2 = blocked.
Settings: optional .nubouncer.json, e.g. { "allow": ["MyCompany.*"], "minDownloads": 1000, "minAgeDays": 30 }
""";

Bouncer MakeBouncer() =>
    new(new NuGetClient(NuGetClient.CreateHttpClient()), BouncerConfig.Load(Directory.GetCurrentDirectory()));

switch (args.FirstOrDefault()?.ToLowerInvariant())
{
    case "hook":
        return await ClaudeHook.RunAsync(Console.In, Console.Out, MakeBouncer);

    case "scan":
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var root = Path.GetFullPath(args.Length >= 2 ? args[1] : Directory.GetCurrentDirectory());
        var files = Scanner.FindProjectFiles(root).ToList();
        var packages = Scanner.CollectPackages(files);
        Console.WriteLine($"Scanning {packages.Count} package reference(s) in {files.Count} project file(s) under {root}");

        List<Verdict> verdicts;
        try
        {
            var bouncer = new Bouncer(new NuGetClient(NuGetClient.CreateHttpClient()), BouncerConfig.Load(root));
            verdicts = await Scanner.CheckAllAsync(bouncer, packages.Select(p => p.Request));
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Couldn't reach nuget.org: {ex.Message}");
            return 3;
        }

        var where = packages.ToDictionary(p => p.Request, p => p.Files);
        foreach (var verdict in verdicts.Where(v => v.Level > Level.Ok).OrderByDescending(v => v.Level))
        {
            Console.WriteLine();
            var label = verdict.Level switch { Level.Block => "✗ BLOCKED", Level.Warn => "⚠ CHECK", _ => "ℹ NOTE" };
            Console.WriteLine($"{label}: {verdict.Request}");
            foreach (var finding in verdict.Findings.Where(f => f.Level > Level.Ok))
            {
                Console.WriteLine($"  - {finding.Message}");
            }
            foreach (var file in where[verdict.Request].Take(3))
            {
                Console.WriteLine($"    in {Path.GetRelativePath(root, file)}");
            }
        }

        var blocked = verdicts.Count(v => v.Level == Level.Block);
        var warned = verdicts.Count(v => v.Level == Level.Warn);
        var noted = verdicts.Count(v => v.Level == Level.Info);
        Console.WriteLine();
        Console.WriteLine($"{verdicts.Count - blocked - warned} fine ({noted} with notes), {warned} to check, {blocked} blocked.");
        return verdicts.Count == 0 ? 0 : ExitCode(verdicts.Max(v => v.Level));
    }

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
            Level.Ok or Level.Info => ("✓", "LET IN"),
            Level.Warn => ("⚠", "CHECK FIRST"),
            _ => ("✗", "BLOCKED"),
        };
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"{icon} {label}: {verdict.Request}");
        foreach (var finding in verdict.Findings)
        {
            Console.WriteLine($"  {(finding.Level == Level.Info ? "ℹ" : "-")} {finding.Message}");
        }
        return ExitCode(verdict.Level);
    }

    default:
        Console.WriteLine(Usage);
        return args.Length == 0 || args[0] is "--help" or "-h" or "help" ? 0 : 64;
}

static int ExitCode(Level level) => level switch
{
    Level.Warn => 1,
    Level.Block => 2,
    _ => 0,
};
