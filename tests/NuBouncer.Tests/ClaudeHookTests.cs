using NuBouncer;

namespace NuBouncer.Tests;

public class ClaudeHookTests
{
    [Fact]
    public void Reads_bash_tool_calls()
    {
        const string json = """{"tool_name":"Bash","tool_input":{"command":"dotnet add package Serilog --version 4.0.1"}}""";
        var found = Assert.Single(ClaudeHook.ExtractRequests(json));
        Assert.Equal(("Serilog", "4.0.1"), (found.Id, found.Version));
    }

    [Fact]
    public void Reads_new_packages_from_csproj_edits()
    {
        const string json = """
            {"tool_name":"Edit","tool_input":{"file_path":"C:\\src\\App\\App.csproj",
             "old_string":"<ItemGroup>",
             "new_string":"<ItemGroup>\n<PackageReference Include=\"Made.Up.Pkg\" Version=\"1.2.3\" />"}}
            """;
        var found = Assert.Single(ClaudeHook.ExtractRequests(json));
        Assert.Equal("Made.Up.Pkg", found.Id);
    }

    [Fact]
    public void Ignores_edits_to_non_project_files()
    {
        const string json = """{"tool_name":"Write","tool_input":{"file_path":"notes.md","content":"<PackageReference Include=\"X\" />"}}""";
        Assert.Empty(ClaudeHook.ExtractRequests(json));
    }

    [Theory]
    [InlineData("""{"tool_name":"Read","tool_input":{"file_path":"App.csproj"}}""")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void Ignores_everything_else(string json) => Assert.Empty(ClaudeHook.ExtractRequests(json));

    [Fact]
    public async Task Says_nothing_when_no_packages_are_added()
    {
        var output = new StringWriter();
        var code = await ClaudeHook.RunAsync(
            new StringReader("""{"tool_name":"Bash","tool_input":{"command":"dotnet build"}}"""),
            output, () => throw new InvalidOperationException("should not check anything"));
        Assert.Equal(0, code);
        Assert.Equal("", output.ToString());
    }
}
