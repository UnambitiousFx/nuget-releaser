namespace UnambitiousFx.NugetReleaser.Tests;

/// <summary>Runs against a real git repository and real <c>dotnet msbuild</c> / <c>dotnet pack</c>.</summary>
public sealed class ReleaserIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("nuget-releaser-").FullName;

    public ReleaserIntegrationTests()
    {
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "test");
        Git("config", "tag.gpgSign", "false");
        Git("config", "commit.gpgSign", "false");
        Project("src/Lib/Lib.csproj", "");
        // Lib.Core shares the "src/Lib" prefix: its changes must not be attributed to Lib.
        Project("src/Lib.Core/Lib.Core.csproj", "");
        Project("src/Lib.Http/Lib.Http.csproj", """<ItemGroup><ProjectReference Include="../Lib.Core/Lib.Core.csproj" /></ItemGroup>""");
        Project("test/Lib.Tests/Lib.Tests.csproj", "<PropertyGroup><IsPackable>false</IsPackable></PropertyGroup>");
        Commit("feat: initial");
        Git("tag", "Lib/v1.0.0");
        Git("tag", "Lib.Core/v1.0.0");
        Git("tag", "Lib.Http/v1.0.0");
    }

    public void Dispose()
    {
        // git writes its objects read-only, and Windows refuses to delete read-only files.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, true);
    }

    [Fact]
    public void Plan_NothingChangedSinceTags_ReleasesNothing()
    {
        // Given
        File.WriteAllText(Path.Combine(_root, "test/Lib.Tests/x.txt"), "x");
        Commit("fix: test only");

        // When
        var plan = Releaser().Plan();

        // Then
        Assert.Equal(3, plan.Packages.Count);
        Assert.Empty(plan.Releases);
    }

    [Fact]
    public void Plan_CoreFix_ReleasesCoreAndDependentOnly()
    {
        // Given
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/A.cs"), "namespace Lib.Core { public class A { } }");
        Commit("fix: core bug");

        // When
        var plan = Releaser().Plan();

        // Then
        var released = plan.Releases.ToDictionary(p => p.Id, p => p.NextVersion);
        Assert.Equal(new Dictionary<string, string> { ["Lib.Core"] = "1.0.1", ["Lib.Http"] = "1.0.1" }, released);
    }

    [Fact]
    public void Plan_SharedFileChange_ReleasesEveryPackage()
    {
        // Given
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        Commit("fix: shared build setting");

        // When
        var plan = Releaser().Plan();

        // Then
        Assert.Equal(3, plan.Releases.Count());
    }

    [Fact]
    public async Task ReleaseAsync_DryRun_PacksDependentsAgainstNewDependencyVersion()
    {
        // Given
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/A.cs"), "namespace Lib.Core { public class A { } }");
        Commit("feat!: new core");
        var releaser = Releaser();
        var output = Path.Combine(_root, "artifacts");

        // When
        await releaser.ReleaseAsync(releaser.Plan(), output, dryRun: true);

        // Then: each package keeps its own version, and Lib.Http depends on the new Lib.Core
        Assert.True(File.Exists(Path.Combine(output, "Lib.Core.2.0.0.nupkg")));
        using var http = System.IO.Compression.ZipFile.OpenRead(Path.Combine(output, "Lib.Http.1.1.0.nupkg"));
        using var nuspec = new StreamReader(http.GetEntry("Lib.Http.nuspec")!.Open());
        Assert.Contains("""<dependency id="Lib.Core" version="2.0.0" """, await nuspec.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Empty(Git("tag", "--list", "*2.0.0").Trim());
    }

    private Releaser Releaser() => new(Repository.Open(_root), new ReleaserConfig(), TextWriter.Null);

    private void Project(string path, string content)
    {
        var fullPath = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework></PropertyGroup>{content}</Project>""");
    }

    private void Commit(string message)
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
    }

    private string Git(params string[] arguments) => ProcessRunner.Run(_root, "git", arguments);
}
