namespace UnambitiousFx.NugetReleaser.Tests;

/// <summary>Runs against a real git repository and real <c>dotnet msbuild</c> / <c>dotnet pack</c>.</summary>
public sealed class ReleaserIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _cleanup = [];

    public ReleaserIntegrationTests()
    {
        var directory = Directory.CreateTempSubdirectory("nuget-releaser-").FullName;
        ProcessRunner.Run(directory, "git", "init", "-q", "-b", "main");
        // The path git reports (on macOS /var/... is a symlink to /private/var/...), as the tool sees it.
        _root = Repository.Open(directory).Root;
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "test");
        Git("config", "tag.gpgSign", "false");
        Git("config", "commit.gpgSign", "false");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "bin/\nobj/\n");
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
        foreach (var directory in _cleanup.Prepend(_root))
        {
            // git writes its objects read-only, and Windows refuses to delete read-only files.
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(directory, true);
        }
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

    [Fact]
    public void Plan_HigherTagOnAnotherBranch_IsIgnored()
    {
        // Given: Lib/v2.0.0 only exists on a branch that is not merged into main
        Git("switch", "-q", "-c", "next");
        File.WriteAllText(Path.Combine(_root, "src/Lib/Next.cs"), "namespace Lib { public class Next { } }");
        Commit("feat!: next major");
        Git("tag", "Lib/v2.0.0");
        Git("switch", "-q", "main");
        File.WriteAllText(Path.Combine(_root, "src/Lib/Fix.cs"), "namespace Lib { public class Fix { } }");
        Commit("fix: maintenance fix");

        // When
        var lib = Releaser().Plan().Packages.Single(p => p.Id == "Lib");

        // Then
        Assert.Equal("1.0.0", lib.CurrentVersion);
        Assert.Equal("1.0.1", lib.NextVersion);
    }

    [Fact]
    public void Plan_ReferenceForOneTargetFrameworkOnly_CascadesRelease()
    {
        // Given: Lib.Multi references Lib.Core for net8.0 only, invisible to the outer (multi-targeting) evaluation
        var multi = Path.Combine(_root, "src/Lib.Multi/Lib.Multi.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(multi)!);
        File.WriteAllText(multi, """
                                 <Project Sdk="Microsoft.NET.Sdk">
                                   <PropertyGroup><TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks></PropertyGroup>
                                   <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'"><ProjectReference Include="../Lib.Core/Lib.Core.csproj" /></ItemGroup>
                                 </Project>
                                 """);
        Commit("feat: multi");
        Git("tag", "Lib.Multi/v1.0.0");
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/A.cs"), "namespace Lib.Core { public class A { } }");
        Commit("fix: core bug");

        // When
        var plan = Releaser().Plan();

        // Then
        Assert.Contains(plan.Releases, p => p.Id == "Lib.Multi" && p.NextVersion == "1.0.1");
    }

    [Fact]
    public async Task ReleaseAsync_UncommittedChange_RefusesToPublish()
    {
        // Given: a committed fix, plus an edit that the plan does not know about
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/A.cs"), "namespace Lib.Core { public class A { } }");
        Commit("fix: core bug");
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/A.cs"), "namespace Lib.Core { public class B { } }");
        var releaser = Releaser();

        // When
        var error = await Assert.ThrowsAsync<ReleaserException>(() =>
            releaser.ReleaseAsync(releaser.Plan(), Path.Combine(_root, "artifacts"), dryRun: false));

        // Then
        Assert.Contains("src/Lib.Core/A.cs", error.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "artifacts")));
    }

    [Fact]
    public async Task ReleaseAsync_GitHubFailsThenRerun_ResumesAndTags()
    {
        // Given: an origin remote, a local folder feed, and a GitHub API that fails
        var origin = Directory.CreateTempSubdirectory("nuget-releaser-origin-").FullName;
        var feed = Directory.CreateTempSubdirectory("nuget-releaser-feed-").FullName;
        _cleanup.AddRange([origin, feed]);
        ProcessRunner.Run(origin, "git", "init", "-q", "--bare");
        Git("remote", "add", "origin", origin);
        Environment.SetEnvironmentVariable("NUGET_RELEASER_TESTS_KEY", "test");
        var config = new ReleaserConfig { Feed = [new FeedConfig { Name = "local", Source = feed, ApiKeyEnv = "NUGET_RELEASER_TESTS_KEY" }] };
        var failingGitHub = new GitHubReleases(new HttpClient(new FailingHandler()) { BaseAddress = new Uri("https://api.github.test/") }, "o/r");
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/A.cs"), "namespace Lib.Core { public class A { } }");
        Commit("fix: core bug");
        // inside the work tree on purpose: the output directory must not count as an uncommitted change
        var output = Path.Combine(_root, "artifacts");

        // When: the first run fails at the GitHub Release
        var first = new Releaser(Repository.Open(_root), config, TextWriter.Null, failingGitHub);
        await Assert.ThrowsAsync<ReleaserException>(() => first.ReleaseAsync(first.Plan(), output, dryRun: false));

        // Then: packages are on the feed, but no tag exists, so the plan still lists the release
        Assert.Contains("lib.core.1.0.1.nupkg", Directory.EnumerateFiles(feed, "*.nupkg", SearchOption.AllDirectories).Select(f => Path.GetFileName(f).ToLowerInvariant()));
        Assert.Empty(ProcessRunner.Run(_root, "git", "ls-remote", "--tags", "origin").Trim());

        // When: the release is run again
        var second = new Releaser(Repository.Open(_root), config, TextWriter.Null, null);
        var plan = second.Plan();
        await second.ReleaseAsync(plan, output, dryRun: false);

        // Then: the same versions are released and tagged, locally and on the remote
        Assert.Equal(["Lib.Core", "Lib.Http"], plan.Releases.Select(p => p.Id));
        var remoteTags = ProcessRunner.Run(_root, "git", "ls-remote", "--tags", "origin");
        Assert.Contains("refs/tags/Lib.Core/v1.0.1", remoteTags);
        Assert.Contains("refs/tags/Lib.Http/v1.0.1", remoteTags);
        Assert.Empty(second.Plan().Releases);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
    }

    private Releaser Releaser() => new(Repository.Open(_root), new ReleaserConfig(), TextWriter.Null, null);

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
