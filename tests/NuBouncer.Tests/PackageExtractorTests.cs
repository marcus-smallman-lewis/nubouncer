using NuBouncer;

namespace NuBouncer.Tests;

public class PackageExtractorTests
{
    [Theory]
    [InlineData("dotnet add package Serilog", "Serilog", null)]
    [InlineData("dotnet add MyApp.csproj package Serilog --version 4.0.1", "Serilog", "4.0.1")]
    [InlineData("dotnet add package Serilog -v 4.0.1", "Serilog", "4.0.1")]
    [InlineData("dotnet add package Serilog --source https://api.nuget.org/v3/index.json -v 4.0.1", "Serilog", "4.0.1")]
    [InlineData("dotnet add package Serilog --prerelease", "Serilog", null)]
    [InlineData("dotnet package add Serilog@4.0.1", "Serilog", "4.0.1")]
    [InlineData("dotnet package add Serilog --version 4.0.1", "Serilog", "4.0.1")]
    [InlineData("nuget install Serilog -Version 4.0.1", "Serilog", "4.0.1")]
    [InlineData("Install-Package Serilog -Version 4.0.1", "Serilog", "4.0.1")]
    [InlineData("cd src && dotnet add package \"Serilog\"", "Serilog", null)]
    public void Finds_package_in_command(string command, string id, string? version)
    {
        var found = Assert.Single(PackageExtractor.FromCommand(command));
        Assert.Equal(id, found.Id);
        Assert.Equal(version, found.Version);
    }

    [Fact]
    public void Finds_every_package_in_chained_commands()
    {
        var found = PackageExtractor.FromCommand("dotnet add package A && dotnet add package B; dotnet build");
        Assert.Equal(["A", "B"], found.Select(f => f.Id));
    }

    [Theory]
    [InlineData("dotnet build")]
    [InlineData("dotnet add reference ../Other/Other.csproj")]
    [InlineData("echo dotnet add package")]
    [InlineData("git commit -m \"add package\"")]
    public void Ignores_commands_that_dont_add_packages(string command) =>
        Assert.Empty(PackageExtractor.FromCommand(command));

    [Fact]
    public void Reads_package_references_from_project_xml()
    {
        const string xml = """
            <ItemGroup>
              <PackageReference Include="Serilog" Version="4.0.1" />
              <PackageReference Include="Polly">
                <Version>8.4.0</Version>
              </PackageReference>
              <PackageVersion Include="Dapper" Version="2.1.35" />
              <PackageReference Include="$(SomeProperty)" Version="1.0" />
            </ItemGroup>
            """;
        var found = PackageExtractor.FromProjectXml(xml);
        Assert.Equal([("Serilog", "4.0.1"), ("Polly", "8.4.0"), ("Dapper", "2.1.35")],
            found.Select(f => (f.Id, f.Version!)));
    }

    [Fact]
    public void Edit_only_reports_new_or_changed_packages()
    {
        const string before = """<PackageReference Include="Serilog" Version="4.0.1" />""";
        const string after = """
            <PackageReference Include="Serilog" Version="4.0.1" />
            <PackageReference Include="Totally.Made.Up" Version="1.0.0" />
            """;
        var found = Assert.Single(PackageExtractor.NewInEdit(before, after));
        Assert.Equal("Totally.Made.Up", found.Id);
    }

    [Theory]
    [InlineData("src/App/App.csproj", true)]
    [InlineData("Directory.Packages.props", true)]
    [InlineData("packages.config", true)]
    [InlineData("src/App/Program.cs", false)]
    public void Recognises_project_files(string path, bool expected) =>
        Assert.Equal(expected, PackageExtractor.IsProjectFile(path));
}
