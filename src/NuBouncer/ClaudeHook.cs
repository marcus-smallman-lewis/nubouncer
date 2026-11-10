using System.Text.Json;
using System.Text.Json.Nodes;

namespace NuBouncer;

/// <summary>
/// Claude Code PreToolUse hook. Reads the tool call as JSON on stdin and, if it adds
/// NuGet packages, answers "deny" (blocked) or "ask" (warnings) on stdout.
/// Says nothing when everything's fine, so Claude Code's normal permissions apply.
/// See https://code.claude.com/docs/en/hooks
/// </summary>
public static class ClaudeHook
{
    public static async Task<int> RunAsync(TextReader stdin, TextWriter stdout, Func<Bouncer> makeBouncer)
    {
        var requests = ExtractRequests(await stdin.ReadToEndAsync());
        if (requests.Count == 0) return 0;

        List<Verdict> verdicts;
        try
        {
            var bouncer = makeBouncer();
            verdicts = [];
            foreach (var request in requests.DistinctBy(r => (r.Id.ToLowerInvariant(), r.Version)))
            {
                verdicts.Add(await bouncer.CheckAsync(request));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Can't reach nuget.org: don't block work, just ask so a human looks at it.
            await stdout.WriteLineAsync(Decision("ask",
                $"NuBouncer couldn't reach nuget.org to check {string.Join(", ", requests)} ({ex.Message}). Check the package yourself before allowing."));
            return 0;
        }

        var worst = verdicts.Max(v => v.Level);
        if (worst <= Level.Info) return 0;

        var problems = verdicts.Where(v => v.Level >= Level.Warn)
            .SelectMany(v => v.Findings.Where(f => f.Level >= Level.Warn).Select(f => f.Message));
        var reason = "NuBouncer: " + string.Join(" ", problems);

        await stdout.WriteLineAsync(Decision(worst == Level.Block ? "deny" : "ask", reason));
        return 0;
    }

    internal static List<PackageRequest> ExtractRequests(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return []; }

        var tool = root?["tool_name"]?.GetValue<string>();
        var input = root?["tool_input"];
        if (tool is null || input is null) return [];

        switch (tool)
        {
            case "Bash":
            case "PowerShell":
                return PackageExtractor.FromCommand(Str(input, "command") ?? "");

            case "Write":
                return IsProjectFile(input) ? PackageExtractor.FromProjectXml(Str(input, "content") ?? "") : [];

            case "Edit":
                return IsProjectFile(input)
                    ? PackageExtractor.NewInEdit(Str(input, "old_string") ?? "", Str(input, "new_string") ?? "")
                    : [];

            case "MultiEdit":
                if (!IsProjectFile(input) || input["edits"] is not JsonArray edits) return [];
                return edits.SelectMany(e => PackageExtractor.NewInEdit(
                    e?["old_string"]?.GetValue<string>() ?? "",
                    e?["new_string"]?.GetValue<string>() ?? "")).ToList();

            default:
                return [];
        }
    }

    private static bool IsProjectFile(JsonNode input) =>
        Str(input, "file_path") is { } path && PackageExtractor.IsProjectFile(path);

    private static string? Str(JsonNode node, string key) =>
        node[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Decision(string decision, string reason) =>
        new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = decision,
                ["permissionDecisionReason"] = reason,
            },
        }.ToJsonString();
}
