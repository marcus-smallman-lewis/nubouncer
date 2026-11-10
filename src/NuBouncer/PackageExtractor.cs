using System.Text;
using System.Text.RegularExpressions;

namespace NuBouncer;

/// <summary>Finds the NuGet packages a shell command or project-file edit is trying to add.</summary>
public static partial class PackageExtractor
{
    private static readonly string[] ProjectFileEndings =
        [".csproj", ".fsproj", ".vbproj", ".props", ".targets", "packages.config"];

    // ---- Shell commands ----

    /// <summary>
    /// Handles: dotnet add [project] package X [-v|--version V],
    /// dotnet package add X[@V] [--version V], nuget install X [-Version V],
    /// Install-Package X [-Version V]. Chained commands (&&, ||, ;, |) are all checked.
    /// </summary>
    public static List<PackageRequest> FromCommand(string command)
    {
        var results = new List<PackageRequest>();
        foreach (var part in SplitCommands(command))
        {
            var args = Tokenize(part);
            if (args.Count == 0) continue;

            if (Is(args, 0, "dotnet") && Is(args, 1, "add"))
            {
                var idx = args.FindIndex(2, a => a.Equals("package", StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) AddFrom(args, idx + 1, results, ["-v", "--version"]);
            }
            else if (Is(args, 0, "dotnet") && Is(args, 1, "package") && Is(args, 2, "add"))
            {
                AddFrom(args, 3, results, ["-v", "--version"]);
            }
            else if ((Is(args, 0, "nuget") || Is(args, 0, "nuget.exe")) && Is(args, 1, "install"))
            {
                AddFrom(args, 2, results, ["-version"]);
            }
            else if (Is(args, 0, "install-package"))
            {
                AddFrom(args, 1, results, ["-version"]);
            }
        }
        return results;
    }

    private static void AddFrom(List<string> args, int start, List<PackageRequest> into, string[] versionFlags)
    {
        string? id = null, version = null;
        for (var i = start; i < args.Count; i++)
        {
            var arg = args[i];
            if (versionFlags.Contains(arg, StringComparer.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                version = args[++i];
            }
            else if (arg.StartsWith('-'))
            {
                // Skip other options and their values (e.g. --source x, -f net8.0, --prerelease).
                if (i + 1 < args.Count && !args[i + 1].StartsWith('-') && TakesValue(arg)) i++;
            }
            else if (id is null)
            {
                id = arg;
                var at = id.IndexOf('@'); // dotnet package add Foo@1.2.3
                if (at > 0)
                {
                    version = id[(at + 1)..];
                    id = id[..at];
                }
            }
        }

        if (id is not null && IsPlausibleId(id)) into.Add(new PackageRequest(id, version));
    }

    private static bool TakesValue(string flag) => flag.ToLowerInvariant() switch
    {
        "--prerelease" or "-prerelease" or "--no-restore" or "-n" or "--interactive" or "-nocache"
            or "-noninteractive" or "-includeprerelease" or "-whatif" => false,
        _ => true,
    };

    private static bool Is(List<string> args, int i, string value) =>
        i < args.Count && args[i].Equals(value, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SplitCommands(string command) =>
        CommandSeparator().Split(command).Where(s => !string.IsNullOrWhiteSpace(s));

    internal static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var c in input)
        {
            if (quote is not null)
            {
                if (c == quote) quote = null;
                else current.Append(c);
            }
            else if (c is '"' or '\'') quote = c;
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    // ---- Project file edits ----

    public static bool IsProjectFile(string path) =>
        ProjectFileEndings.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Package references in a chunk of project-file XML.</summary>
    public static List<PackageRequest> FromProjectXml(string xml)
    {
        var results = new List<PackageRequest>();
        foreach (Match m in PackageElement().Matches(xml))
        {
            var attrs = m.Groups["attrs"].Value;
            var id = Attr(attrs, "Include") ?? Attr(attrs, "Update") ?? Attr(attrs, "id");
            if (id is null || !IsPlausibleId(id)) continue;
            var version = Attr(attrs, "Version") ?? Attr(attrs, "version") ?? ChildVersion(m.Groups["body"].Value);
            results.Add(new PackageRequest(id, version));
        }
        return results;
    }

    /// <summary>Packages present in the new text but not the old text (for edits).</summary>
    public static List<PackageRequest> NewInEdit(string oldText, string newText)
    {
        var before = FromProjectXml(oldText).Select(p => (p.Id.ToLowerInvariant(), p.Version)).ToHashSet();
        return FromProjectXml(newText)
            .Where(p => !before.Contains((p.Id.ToLowerInvariant(), p.Version)))
            .ToList();
    }

    private static string? Attr(string attrs, string name)
    {
        var m = Regex.Match(attrs, $@"\b{name}\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? ChildVersion(string body)
    {
        var m = Regex.Match(body, @"<Version>\s*([^<\s]+)\s*</Version>", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    // MSBuild properties like $(Foo) can't be checked.
    private static bool IsPlausibleId(string id) =>
        !id.Contains("$(") && PackageId().IsMatch(id);

    [GeneratedRegex(@"\s*(?:&&|\|\||;|\||\r?\n)\s*")]
    private static partial Regex CommandSeparator();

    [GeneratedRegex(@"<(?:PackageReference|PackageVersion|package)\b(?<attrs>[^>]*?)(?:/>|>(?<body>.*?)</(?:PackageReference|PackageVersion|package)>)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PackageElement();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]*$")]
    private static partial Regex PackageId();
}
