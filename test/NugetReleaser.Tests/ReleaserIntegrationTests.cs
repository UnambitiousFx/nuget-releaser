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
    public void Plan_NestedDirectoryBuildPropsChange_ReleasesThePackagesBelowIt()
    {
        // Given: src/Directory.Build.props, not a default shared path, imports build/common.props
        File.WriteAllText(Path.Combine(_root, "src/Directory.Build.props"), """<Project><Import Project="../build/common.props" /></Project>""");
        BuildFile("build/common.props", "<Project />");
        CommitAndTagAll("build: shared settings", "1.0.1");
        BuildFile("build/common.props", "<Project><PropertyGroup><Authors>me</Authors></PropertyGroup></Project>");
        Commit("fix: package authors");

        // When
        var plan = Releaser().Plan();

        // Then
        Assert.Equal(["Lib", "Lib.Core", "Lib.Http"], plan.Releases.Select(p => p.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Plan_NestedDirectoryBuildPropsDeleted_ReleasesThePackagesBelowIt()
    {
        // Given: src/Directory.Build.props is released with every package, then deleted on its own
        File.WriteAllText(Path.Combine(_root, "src/Directory.Build.props"), "<Project><PropertyGroup><Authors>me</Authors></PropertyGroup></Project>");
        CommitAndTagAll("build: shared settings", "1.0.1");
        File.Delete(Path.Combine(_root, "src/Directory.Build.props"));
        Commit("fix: drop shared settings");

        // When
        var plan = Releaser().Plan();

        // Then: nothing the packages still import changed, but what they build with did
        Assert.Equal(["Lib", "Lib.Core", "Lib.Http"], plan.Releases.Select(p => p.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Plan_ShadowedDirectoryBuildPropsChanged_ReleasesNothing()
    {
        // Given: src/Lib/../Directory.Build.props exists but src/Lib.Core has its own, which does not import it
        File.WriteAllText(Path.Combine(_root, "src/Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/Directory.Build.props"), "<Project />");
        CommitAndTagAll("build: settings", "1.0.1");
        File.WriteAllText(Path.Combine(_root, "src/Directory.Build.props"), "<Project><PropertyGroup><Authors>me</Authors></PropertyGroup></Project>");
        Commit("fix: authors");

        // When
        var plan = Releaser().Plan();

        // Then: Lib.Core's own Directory.Build.props stops MSBuild's search, so only Lib and Lib.Http import the changed file
        Assert.Equal(["Lib", "Lib.Http"], plan.Releases.Select(p => p.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Plan_ShadowedDirectoryBuildPropsDeleted_ReleasesOnlyThePackagesThatImportedIt()
    {
        // Given: src/Lib.Core has its own Directory.Build.props, so it never imported src/Directory.Build.props
        File.WriteAllText(Path.Combine(_root, "src/Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/Directory.Build.props"), "<Project />");
        CommitAndTagAll("build: settings", "1.0.1");
        File.Delete(Path.Combine(_root, "src/Directory.Build.props"));
        Commit("fix: drop shared settings");

        // When
        var plan = Releaser().Plan();

        // Then
        Assert.Equal(["Lib", "Lib.Http"], plan.Releases.Select(p => p.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Plan_DirectoryBuildPropsDeletedThenNearerOneAdded_ReleasesTheFix()
    {
        // Given: Lib imported src/Directory.Build.props when it was deleted; only later did Lib get its own
        File.WriteAllText(Path.Combine(_root, "src/Directory.Build.props"), "<Project />");
        CommitAndTagAll("build: settings", "1.0.1");
        File.Delete(Path.Combine(_root, "src/Directory.Build.props"));
        Commit("fix: drop shared settings");
        File.WriteAllText(Path.Combine(_root, "src/Lib/Directory.Build.props"), "<Project />");
        Commit("chore: lib settings");

        // When
        var lib = Releaser().Plan().Packages.Single(p => p.Id == "Lib");

        // Then: the layout at HEAD does not hide a deletion that happened before it
        Assert.Equal("1.0.2", lib.NextVersion);
    }

    [Fact]
    public void Plan_ShadowedDeletionUnderNonAsciiPath_ReleasesNothing()
    {
        // Given: under src/é, Lib.Accent has its own Directory.Build.props, so it never imported src/é/Directory.Build.props
        Project("src/é/Lib.Accent/Lib.Accent.csproj", "");
        File.WriteAllText(Path.Combine(_root, "src/é/Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "src/é/Lib.Accent/Directory.Build.props"), "<Project />");
        Commit("feat: accent");
        Git("tag", "Lib.Accent/v1.0.0");
        File.Delete(Path.Combine(_root, "src/é/Directory.Build.props"));
        Commit("fix: drop shared settings");

        // When
        var plan = Releaser().Plan();

        // Then: git's quoting of non-ASCII paths must not hide the nearer file
        Assert.Empty(plan.Releases);
    }

    [Fact]
    public void Plan_FileImportedUnderNonAsciiPath_ReleasesThePackage()
    {
        // Given: Lib imports a file under a non-ASCII directory
        Project("src/Lib/Lib.csproj", """<Import Project="../../build/é/lib.targets" />""");
        BuildFile("build/é/lib.targets", "<Project />");
        CommitAndTagAll("build: lib targets", "1.0.1");
        BuildFile("build/é/lib.targets", """<Project><Target Name="Noop" /></Project>""");
        Commit("fix: lib targets");

        // When
        var plan = Releaser().Plan();

        // Then
        Assert.Equal([("Lib", "1.0.2")], plan.Releases.Select(p => (p.Id, p.NextVersion)));
    }

    [Fact]
    public void Plan_FileImportedByOneProject_ReleasesOnlyThatPackage()
    {
        // Given: only Lib imports build/lib.targets
        Project("src/Lib/Lib.csproj", """<Import Project="../../build/lib.targets" />""");
        BuildFile("build/lib.targets", "<Project />");
        CommitAndTagAll("build: lib targets", "1.0.1");
        BuildFile("build/lib.targets", """<Project><Target Name="Noop" /></Project>""");
        Commit("fix: lib targets");

        // When
        var plan = Releaser().Plan();

        // Then
        Assert.Equal([("Lib", "1.0.2")], plan.Releases.Select(p => (p.Id, p.NextVersion)));
    }

    [Fact]
    public void Plan_FileImportedForOneTargetFrameworkOnly_ReleasesThePackage()
    {
        // Given: Lib.Core imports build/net8.targets only when building for net8.0
        BuildFile("src/Lib.Core/Lib.Core.csproj", """
                                                  <Project Sdk="Microsoft.NET.Sdk">
                                                    <PropertyGroup><TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks></PropertyGroup>
                                                    <Import Project="../../build/net8.targets" Condition="'$(TargetFramework)' == 'net8.0'" />
                                                  </Project>
                                                  """);
        BuildFile("build/net8.targets", "<Project />");
        CommitAndTagAll("build: net8 targets", "1.0.1");
        BuildFile("build/net8.targets", """<Project><Target Name="Noop" /></Project>""");
        Commit("fix: net8 targets");

        // When
        var plan = Releaser().Plan();

        // Then: Lib.Http follows its dependency
        Assert.Equal(["Lib.Core", "Lib.Http"], plan.Releases.Select(p => p.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Plan_UntrackedImportedFile_IsIgnored()
    {
        // Given: Lib imports a file that git ignores (as obj/*.nuget.g.props, written by restore)
        File.AppendAllText(Path.Combine(_root, ".gitignore"), "generated/\n");
        Project("src/Lib/Lib.csproj", """<Import Project="../../generated/lib.props" Condition="Exists('../../generated/lib.props')" />""");
        BuildFile("generated/lib.props", "<Project />");
        CommitAndTagAll("build: generated props", "1.0.1");
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/A.cs"), "namespace Lib.Core { public class A { } }");
        Commit("fix: core bug");

        // When
        var lib = Repository.Open(_root).DiscoverPackages(new ReleaserConfig()).Single(p => p.Id == "Lib");

        // Then
        Assert.DoesNotContain("generated/lib.props", lib.Paths);
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
    public async Task ReleaseAsync_SourceFileMovedIntoOutput_RefusesToPublish()
    {
        // Given: a committed fix, then that file staged as moved into the output directory
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/A.cs"), "namespace Lib.Core { public class A { } }");
        Commit("fix: core bug");
        Git("config", "status.renames", "true");
        Directory.CreateDirectory(Path.Combine(_root, "artifacts"));
        Git("mv", "src/Lib.Core/A.cs", "artifacts/A.cs");
        var releaser = Releaser();

        // When
        var error = await Assert.ThrowsAsync<ReleaserException>(() =>
            releaser.ReleaseAsync(releaser.Plan(), Path.Combine(_root, "artifacts"), dryRun: false));

        // Then: HEAD still has src/Lib.Core/A.cs, which the pack would leave out
        Assert.Contains("src/Lib.Core/A.cs", error.Message);
    }

    [Fact]
    public void UncommittedChanges_NonAsciiPaths_AreReportedVerbatimAndOutputIsExcluded()
    {
        // Given: an output directory with a non-ASCII name holding a file, an untracked non-ASCII file,
        // and a staged rename (two NUL-separated paths with -z)
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/A.cs"), "namespace Lib.Core { }");
        Commit("chore: add a");
        // Reported as a rename whatever the user's git config says.
        Git("config", "status.renames", "true");
        Git("mv", "src/Lib.Core/A.cs", "src/Lib.Core/Ä.cs");
        File.WriteAllText(Path.Combine(_root, "src/Lib/Café.cs"), "namespace Lib { }");
        var output = Path.Combine(_root, "artifacts", "é");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "Lib.1.0.0.nupkg"), "x");

        // When
        var changes = Repository.Open(_root).UncommittedChanges(output);

        // Then
        Assert.Equal(["src/Lib.Core/Ä.cs", "src/Lib/Café.cs"], changes);
    }

    [Fact]
    public async Task ReleaseAsync_SecondGitHubReleaseFailsThenRerun_FinishesTheRelease()
    {
        // Given: an origin remote, a local folder feed, per-package GitHub Releases, and a GitHub that fails on the second one
        var (config, feed) = Publishing();
        var gitHub = new FakeGitHub(_root) { FailOn = "Lib.Http/v1.0.1" };
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/A.cs"), "namespace Lib.Core { public class A { } }");
        // Committed but never pushed: the release itself must get HEAD to GitHub before creating a release on it.
        Commit("fix: core bug");
        // inside the work tree on purpose: the output directory must not count as an uncommitted change
        var output = Path.Combine(_root, "artifacts");

        // When: the first run fails at the second GitHub Release
        var first = new Releaser(Repository.Open(_root), config, TextWriter.Null, gitHub.Client());
        await Assert.ThrowsAsync<ReleaserException>(() => first.ReleaseAsync(first.Plan(), output, dryRun: false));

        // Then: packages and tags are published, one GitHub Release is missing
        Assert.Contains("lib.http.1.0.1.nupkg", Directory.EnumerateFiles(feed, "*.nupkg", SearchOption.AllDirectories).Select(f => Path.GetFileName(f).ToLowerInvariant()));
        Assert.Contains("refs/tags/Lib.Http/v1.0.1", ProcessRunner.Run(_root, "git", "ls-remote", "--tags", "origin"));
        Assert.Equal(["Lib.Core/v1.0.1"], gitHub.Releases);

        // When: the release is run again
        gitHub.FailOn = null;
        var second = new Releaser(Repository.Open(_root), config, TextWriter.Null, gitHub.Client());
        var plan = second.Plan();
        await second.ReleaseAsync(plan, output, dryRun: false);

        // Then: the tagged packages are planned again with the same versions, and the missing release is created
        Assert.Equal([("Lib.Core", "1.0.1", true), ("Lib.Http", "1.0.1", true)], plan.Releases.Select(p => (p.Id, p.NextVersion, p.Resumed)));
        Assert.Equal(["Lib.Core/v1.0.1", "Lib.Http/v1.0.1"], gitHub.Releases);
        // Each release was created once its commit and tag were on the remote.
        Assert.All(gitHub.TagOnRemoteWhenCreated, Assert.True);
    }

    [Fact]
    public void Plan_ExistingTagsAtHead_AreExistingReleases()
    {
        // Given: the fixture as is, every package tagged v1.0.0 on HEAD, as when adopting the tool on a tagged repository

        // When
        var plan = Releaser().Plan();

        // Then
        Assert.Empty(plan.Releases);
        Assert.All(plan.Packages, p => Assert.Equal("1.0.0", p.CurrentVersion));
    }

    [Fact]
    public async Task ReleaseAsync_ManualTagOnDependencyThenRerun_FinishesDependentRelease()
    {
        // Given: Lib.Core has a breaking change and a manual 1.5.0 tag at HEAD (the plan would say 2.0.0, so it is an
        // existing release); Lib.Http, which depends on it, has its own fix
        var (config, _) = Publishing();
        var gitHub = new FakeGitHub(_root) { FailOn = "Lib.Http/v1.0.1" };
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/A.cs"), "namespace Lib.Core { public class A { } }");
        Commit("feat!: core rewrite");
        File.WriteAllText(Path.Combine(_root, "src/Lib.Http/B.cs"), "namespace Lib.Http { public class B { } }");
        Commit("fix: http bug");
        Git("tag", "Lib.Core/v1.5.0");
        var output = Path.Combine(_root, "artifacts");

        // When: the first run releases Lib.Http alone, then fails at its GitHub Release
        var first = new Releaser(Repository.Open(_root), config, TextWriter.Null, gitHub.Client());
        var firstPlan = first.Plan();
        await Assert.ThrowsAsync<ReleaserException>(() => first.ReleaseAsync(firstPlan, output, dryRun: false));

        // When: the release is run again
        gitHub.FailOn = null;
        var second = new Releaser(Repository.Open(_root), config, TextWriter.Null, gitHub.Client());
        var plan = second.Plan();
        await second.ReleaseAsync(plan, output, dryRun: false);

        // Then: both runs plan the same release, and the re-run finishes it
        Assert.Equal([("Lib.Http", "1.0.1")], firstPlan.Releases.Select(p => (p.Id, p.NextVersion)));
        Assert.Equal([("Lib.Http", "1.0.1", true)], plan.Releases.Select(p => (p.Id, p.NextVersion, p.Resumed)));
        Assert.Equal(["Lib.Http/v1.0.1"], gitHub.Releases);
    }

    [Fact]
    public void Plan_NewCommitAfterRelease_ReleasesNormally()
    {
        // Given: a release fully tagged at its commit, then a new change on top
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/A.cs"), "namespace Lib.Core { public class A { } }");
        Commit("fix: core bug");
        Git("tag", "Lib.Core/v1.0.1");
        Git("tag", "Lib.Http/v1.0.1");
        File.WriteAllText(Path.Combine(_root, "src/Lib/B.cs"), "namespace Lib { public class B { } }");
        Commit("feat: lib feature");

        // When
        var plan = Releaser().Plan();

        // Then: the earlier tags are ordinary tags again, nothing is resumed
        Assert.Equal([("Lib", "1.1.0", false)], plan.Releases.Select(p => (p.Id, p.NextVersion, p.Resumed)));
    }

    [Fact]
    public async Task ReleaseAsync_RunTwice_SecondRunPublishesNothingNew()
    {
        // Given: a complete release, to an HTTP feed that refuses duplicates as nuget.org does
        var (config, _) = Publishing();
        using var feed = new FakeNuGetFeed();
        config.Feed = [new FeedConfig { Name = "http", Source = feed.Source, ApiKeyEnv = "NUGET_RELEASER_TESTS_KEY" }];
        feed.AllowIn(_root);
        var gitHub = new FakeGitHub(_root);
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/A.cs"), "namespace Lib.Core { public class A { } }");
        Commit("fix: core bug");
        var output = Path.Combine(_root, "artifacts");
        var first = new Releaser(Repository.Open(_root), config, TextWriter.Null, gitHub.Client());
        await first.ReleaseAsync(first.Plan(), output, dryRun: false);
        var tagsAfterFirst = RemoteTags();

        // When: the release is run again on the same commit
        var log = new StringWriter();
        var second = new Releaser(Repository.Open(_root), config, log, gitHub.Client());
        await second.ReleaseAsync(second.Plan(), output, dryRun: false);

        // Then: the duplicate packages are skipped, and the remote tags and the GitHub Releases are unchanged
        Assert.Equal(["Lib.Core", "Lib.Http"], feed.Published);
        Assert.Equal(["Lib.Core", "Lib.Http"], feed.Conflicts);
        Assert.All(feed.ApiKeys, k => Assert.Equal("test", k));
        Assert.Equal(tagsAfterFirst, RemoteTags());
        Assert.Equal(["Lib.Core/v1.0.1", "Lib.Http/v1.0.1"], gitHub.Releases);
        Assert.Equal(2, gitHub.Posts);
        Assert.Contains("GitHub Release Lib.Core/v1.0.1 already exists", log.ToString());
    }

    [Fact]
    public async Task ReleaseAsync_TagPushFailsThenRerun_FinishesTheRelease()
    {
        // Given: an origin that cannot be reached, so the run stops after the packages are pushed and before any tag
        var (config, _) = Publishing();
        using var feed = new FakeNuGetFeed();
        config.Feed = [new FeedConfig { Name = "http", Source = feed.Source, ApiKeyEnv = "NUGET_RELEASER_TESTS_KEY" }];
        feed.AllowIn(_root);
        var origin = Git("remote", "get-url", "origin").Trim();
        Git("remote", "set-url", "origin", Path.Combine(_root, "missing-origin.git"));
        var gitHub = new FakeGitHub(_root);
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/A.cs"), "namespace Lib.Core { public class A { } }");
        Commit("fix: core bug");
        var output = Path.Combine(_root, "artifacts");

        // When: the first run fails at the tag push
        var first = new Releaser(Repository.Open(_root), config, TextWriter.Null, gitHub.Client());
        await Assert.ThrowsAsync<ReleaserException>(() => first.ReleaseAsync(first.Plan(), output, dryRun: false));

        // Then: the packages are on the feed, but no tag and no GitHub Release exist
        Assert.Equal(["Lib.Core", "Lib.Http"], feed.Published);
        Assert.Empty(Git("tag", "--list", "*1.0.1").Trim());
        Assert.Empty(gitHub.Releases);

        // When: origin is back and the release is run again
        Git("remote", "set-url", "origin", origin);
        var second = new Releaser(Repository.Open(_root), config, TextWriter.Null, gitHub.Client());
        var plan = second.Plan();
        await second.ReleaseAsync(plan, output, dryRun: false);

        // Then: the same versions are planned, the duplicate packages are skipped, and the tags and releases are created
        Assert.Equal([("Lib.Core", "1.0.1"), ("Lib.Http", "1.0.1")], plan.Releases.Select(p => (p.Id, p.NextVersion)));
        Assert.Equal(["Lib.Core", "Lib.Http"], feed.Conflicts);
        Assert.Equal(["Lib.Core/v1.0.1", "Lib.Http/v1.0.1"], RemoteTags().Where(t => t.EndsWith("1.0.1", StringComparison.Ordinal)));
        Assert.Equal(["Lib.Core/v1.0.1", "Lib.Http/v1.0.1"], gitHub.Releases);
    }

    [Fact]
    public async Task ReleaseAsync_TagAlreadyAtHead_IsKeptAndPushed()
    {
        // Given: Lib.Core/v1.0.1 was created by hand at HEAD, locally only
        var (config, _) = Publishing();
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/A.cs"), "namespace Lib.Core { public class A { } }");
        Commit("fix: core bug");
        Git("tag", "-a", "Lib.Core/v1.0.1", "-m", "manual");
        var annotated = Git("rev-parse", "Lib.Core/v1.0.1").Trim();

        // When
        var releaser = new Releaser(Repository.Open(_root), config, TextWriter.Null, null);
        await releaser.ReleaseAsync(releaser.Plan(), Path.Combine(_root, "artifacts"), dryRun: false);

        // Then: the existing tag is not replaced, and both tags reach origin at HEAD
        Assert.Equal(annotated, Git("rev-parse", "Lib.Core/v1.0.1").Trim());
        var head = Git("rev-parse", "HEAD").Trim();
        var remote = ProcessRunner.Run(_root, "git", "ls-remote", "--tags", "origin");
        Assert.Contains($"{head}\trefs/tags/Lib.Core/v1.0.1", remote);
        Assert.Contains($"{head}\trefs/tags/Lib.Http/v1.0.1", remote);
    }

    [Fact]
    public async Task ReleaseAsync_GroupedMode_CreatesOneReleaseForEveryPackage()
    {
        // Given
        var (config, _) = Publishing("grouped");
        var gitHub = new FakeGitHub(_root);
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/A.cs"), "namespace Lib.Core { public class A { } }");
        Commit("fix: core bug");

        // When
        var releaser = new Releaser(Repository.Open(_root), config, TextWriter.Null, gitHub.Client());
        await releaser.ReleaseAsync(releaser.Plan(), Path.Combine(_root, "artifacts"), dryRun: false);

        // Then: one release on HEAD, named after every package, with a section for each
        var head = Git("rev-parse", "HEAD").Trim();
        var (tag, target, name, body) = Assert.Single(gitHub.Created);
        Assert.Equal($"releases/{head[..7]}", tag);
        Assert.Equal(head, target);
        Assert.Equal("Lib.Core 1.0.1, Lib.Http 1.0.1", name);
        Assert.Contains("## Lib.Core 1.0.1", body);
        Assert.Contains("## Lib.Http 1.0.1", body);
        Assert.Contains("fix: core bug", body);
    }

    [Fact]
    public async Task ReleaseAsync_PushFails_DoesNotLeakTheApiKey()
    {
        // Given: a feed whose source is a file, so dotnet nuget push fails
        const string key = "oy2-secret-api-key-value";
        var (config, _) = Publishing();
        var source = Path.Combine(_cleanup[^1], "not-a-feed.txt");
        File.WriteAllText(source, "");
        config.Feed = [new FeedConfig { Name = "broken", Source = source, ApiKeyEnv = "NUGET_RELEASER_TESTS_SECRET_KEY" }];
        Environment.SetEnvironmentVariable("NUGET_RELEASER_TESTS_SECRET_KEY", key);
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/A.cs"), "namespace Lib.Core { public class A { } }");
        Commit("fix: core bug");
        var log = new StringWriter();

        // When
        var releaser = new Releaser(Repository.Open(_root), config, log, null);
        var error = await Assert.ThrowsAsync<ReleaserException>(() => releaser.ReleaseAsync(releaser.Plan(), Path.Combine(_root, "artifacts"), dryRun: false));

        // Then
        Assert.Contains("dotnet nuget", error.Message);
        Assert.DoesNotContain(key, error.Message);
        Assert.DoesNotContain(key, log.ToString());
    }

    [Fact]
    public async Task ReleaseAsync_ApiKeyMissing_FailsBeforePacking()
    {
        // Given
        var (config, _) = Publishing();
        config.Feed = [new FeedConfig { Name = "nuget.org", ApiKeyEnv = "NUGET_RELEASER_TESTS_UNSET_KEY" }];
        File.WriteAllText(Path.Combine(_root, "src/Lib.Core/A.cs"), "namespace Lib.Core { public class A { } }");
        Commit("fix: core bug");

        // When
        var releaser = new Releaser(Repository.Open(_root), config, TextWriter.Null, null);
        var error = await Assert.ThrowsAsync<ReleaserException>(() => releaser.ReleaseAsync(releaser.Plan(), Path.Combine(_root, "artifacts"), dryRun: false));

        // Then
        Assert.Contains("NUGET_RELEASER_TESTS_UNSET_KEY", error.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "artifacts")));
    }

    /// <summary>A bare <c>origin</c> remote, and a local folder feed with GitHub Releases in <paramref name="releaseMode" />.</summary>
    private (ReleaserConfig Config, string Feed) Publishing(string releaseMode = "per-package")
    {
        var origin = Directory.CreateTempSubdirectory("nuget-releaser-origin-").FullName;
        var feed = Directory.CreateTempSubdirectory("nuget-releaser-feed-").FullName;
        _cleanup.AddRange([origin, feed]);
        ProcessRunner.Run(origin, "git", "init", "-q", "--bare");
        Git("remote", "add", "origin", origin);
        Environment.SetEnvironmentVariable("NUGET_RELEASER_TESTS_KEY", "test");
        return (new ReleaserConfig
        {
            Github = new GitHubConfig { ReleaseMode = releaseMode },
            Feed = [new FeedConfig { Name = "local", Source = feed, ApiKeyEnv = "NUGET_RELEASER_TESTS_KEY" }]
        }, feed);
    }

    /// <summary>The tag names on <c>origin</c>, sorted.</summary>
    private List<string> RemoteTags() =>
        ProcessRunner.Run(_root, "git", "ls-remote", "--tags", "--refs", "origin")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line[(line.IndexOf("refs/tags/", StringComparison.Ordinal) + "refs/tags/".Length)..].Trim())
            .Order(StringComparer.Ordinal).ToList();

    /// <summary>The GitHub Releases REST endpoints used by <see cref="GitHubReleases" />, in memory.</summary>
    private sealed class FakeGitHub(string repository) : HttpMessageHandler
    {
        public List<string> Releases { get; } = [];
        public List<(string Tag, string Target, string Name, string Body)> Created { get; } = [];
        public List<bool> TagOnRemoteWhenCreated { get; } = [];
        public int Posts { get; private set; }
        public string? FailOn { get; set; }

        public GitHubReleases Client() => new(new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("https://api.github.test/") }, "o/r");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string byTag = "/releases/tags/";
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                var tag = Uri.UnescapeDataString(path[(path.IndexOf(byTag, StringComparison.Ordinal) + byTag.Length)..]);
                return new HttpResponseMessage(Releases.Contains(tag) ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.NotFound);
            }

            Posts++;
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var created = body.RootElement.GetProperty("tag_name").GetString()!;
            if (created == FailOn)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
            }

            TagOnRemoteWhenCreated.Add(ProcessRunner.Run(repository, "git", "ls-remote", "--tags", "origin").Contains($"refs/tags/{created}"));
            Releases.Add(created);
            Created.Add((created, body.RootElement.GetProperty("target_commitish").GetString()!,
                body.RootElement.GetProperty("name").GetString()!, body.RootElement.GetProperty("body").GetString()!));
            return new HttpResponseMessage(System.Net.HttpStatusCode.Created);
        }
    }

    private Releaser Releaser() => new(Repository.Open(_root), new ReleaserConfig(), TextWriter.Null, null);

    private void Project(string path, string content)
    {
        var fullPath = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework></PropertyGroup>{content}</Project>""");
    }

    private void BuildFile(string path, string content)
    {
        var fullPath = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    /// <summary>Commits, then tags every package at <paramref name="version" />, as a release of that commit would.</summary>
    private void CommitAndTagAll(string message, string version)
    {
        Commit(message);
        foreach (var id in new[] { "Lib", "Lib.Core", "Lib.Http" })
        {
            Git("tag", $"{id}/v{version}");
        }
    }

    private void Commit(string message)
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
    }

    private string Git(params string[] arguments) => ProcessRunner.Run(_root, "git", arguments);
}
