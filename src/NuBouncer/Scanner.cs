namespace NuBouncer;

/// <summary>Checks every package referenced by the project files under a folder.</summary>
public static class Scanner
{
    private static readonly HashSet<string> SkipFolders =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".git", ".vs", "packages" };

    public static IEnumerable<string> FindProjectFiles(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(dir);
                dirs = Directory.EnumerateDirectories(dir);
            }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var file in files.Where(PackageExtractor.IsProjectFile))
            {
                yield return file;
            }
            foreach (var sub in dirs.Where(d => !SkipFolders.Contains(Path.GetFileName(d))))
            {
                pending.Push(sub);
            }
        }
    }

    public static List<(PackageRequest Request, List<string> Files)> CollectPackages(IEnumerable<string> projectFiles)
    {
        var found = new Dictionary<(string, string?), (PackageRequest, List<string>)>();
        foreach (var file in projectFiles)
        {
            string xml;
            try { xml = File.ReadAllText(file); }
            catch (IOException) { continue; }

            foreach (var request in PackageExtractor.FromProjectXml(xml))
            {
                var key = (request.Id.ToLowerInvariant(), request.Version);
                if (!found.TryGetValue(key, out var entry))
                {
                    entry = (request, []);
                    found[key] = entry;
                }
                entry.Item2.Add(file);
            }
        }
        return found.Values.Select(v => (v.Item1, v.Item2)).OrderBy(v => v.Item1.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<List<Verdict>> CheckAllAsync(Bouncer bouncer, IEnumerable<PackageRequest> requests, int parallel = 6)
    {
        using var gate = new SemaphoreSlim(parallel);
        var tasks = requests.Select(async request =>
        {
            await gate.WaitAsync();
            try { return await bouncer.CheckAsync(request); }
            finally { gate.Release(); }
        });
        return [.. await Task.WhenAll(tasks)];
    }
}
