using System.Security;
using System.Text;

namespace UnambitiousFx.NugetReleaser;

/// <summary>Executes a <see cref="ReleasePlan" />: pack, push, tag, GitHub Release. Each step skips what already exists.</summary>
internal sealed class Releaser(Repository repository, ReleaserConfig config, TextWriter log)
{
    /// <summary>Builds the plan from the repository's packages, tags and history.</summary>
    public ReleasePlan Plan()
    {
        var tags = repository.Tags();
        var states = repository.DiscoverPackages(config).Select(package =>
        {
            var latest = TagFormat.Latest(config.Defaults.TagFormat, package.Id, tags);
            return new PackageState(package, latest?.Version, repository.CommitsSince(latest?.Tag, package.Paths));
        }).ToList();

        return Planner.Plan(states, SemVersion.Parse(config.Defaults.InitialVersion));
    }

    /// <summary>Packs every released package into <paramref name="outputDirectory" />; unless <paramref name="dryRun" />, publishes them.</summary>
    public async Task ReleaseAsync(ReleasePlan plan, string outputDirectory, bool dryRun)
    {
        var releases = plan.Releases.ToList();
        if (releases.Count == 0)
        {
            log.WriteLine("Nothing to release.");
            return;
        }

        var apiKeys = dryRun ? [] : config.Feed.ToDictionary(f => f.Name, ApiKey);

        Directory.CreateDirectory(outputDirectory);
        var versionsProps = WriteVersionsProps(plan, outputDirectory);
        var packages = new List<string>();
        foreach (var release in releases)
        {
            log.WriteLine($"Packing {release.Id} {release.NextVersion}");
            ProcessRunner.Run(repository.Root, "dotnet", "pack", Path.Combine(repository.Root, release.Project),
                "--configuration", "Release", "--output", outputDirectory, $"-p:CustomBeforeMicrosoftCommonProps={versionsProps}");
            var nupkg = Path.Combine(outputDirectory, $"{release.Id}.{release.NextVersion}.nupkg");
            if (!File.Exists(nupkg))
            {
                throw new ReleaserException($"dotnet pack did not produce {nupkg}.");
            }

            packages.Add(nupkg);
        }

        if (dryRun)
        {
            log.WriteLine($"Dry run: {packages.Count} package(s) packed into {outputDirectory}; nothing pushed, tagged or released.");
            return;
        }

        foreach (var feed in config.Feed)
        {
            foreach (var nupkg in packages)
            {
                log.WriteLine($"Pushing {Path.GetFileName(nupkg)} to {feed.Name}");
                // --skip-duplicate makes a re-run after a partial failure safe; the .snupkg next to it is pushed too.
                ProcessRunner.Run(repository.Root, "dotnet", "nuget", "push", nupkg,
                    "--source", feed.Source, "--api-key", apiKeys[feed.Name], "--skip-duplicate");
            }
        }

        var tags = new List<string>();
        foreach (var release in releases)
        {
            var tag = TagFormat.Format(config.Defaults.TagFormat, release.Id, release.NextVersion);
            if (!repository.TagExists(tag))
            {
                repository.CreateTag(tag, $"{release.Id} {release.NextVersion}");
            }

            tags.Add(tag);
        }

        log.WriteLine($"Pushing tags {string.Join(", ", tags)}");
        repository.PushTags(tags);

        await CreateGitHubReleasesAsync(releases, tags);
    }

    private async Task CreateGitHubReleasesAsync(List<PlannedPackage> releases, List<string> tags)
    {
        var github = GitHubReleases.FromEnvironment(repository.OriginUrl());
        if (github is null)
        {
            log.WriteLine("No GITHUB_TOKEN or GitHub repository found: skipping GitHub Releases.");
            return;
        }

        var head = repository.Head;
        if (config.Github.ReleaseMode == "per-package")
        {
            for (var i = 0; i < releases.Count; i++)
            {
                await CreateAsync(github, tags[i], head, $"{releases[i].Id} {releases[i].NextVersion}", Notes(releases[i]));
            }

            return;
        }

        var name = string.Join(", ", releases.Select(r => $"{r.Id} {r.NextVersion}"));
        var body = string.Join("\n\n", releases.Select(r => $"## {r.Id} {r.NextVersion}\n\n{Notes(r)}"));
        await CreateAsync(github, $"releases/{head[..7]}", head, name, body);
    }

    private async Task CreateAsync(GitHubReleases github, string tag, string target, string name, string body)
    {
        var created = await github.CreateAsync(tag, target, name, body);
        log.WriteLine(created ? $"Created GitHub Release {tag}" : $"GitHub Release {tag} already exists");
    }

    private static string Notes(PlannedPackage release) =>
        string.Join("\n", release.Reasons.Select(r => $"- {r}"));

    private static string ApiKey(FeedConfig feed) =>
        Environment.GetEnvironmentVariable(feed.ApiKeyEnv) is { Length: > 0 } key
            ? key
            : throw new ReleaserException($"Feed '{feed.Name}': set the {feed.ApiKeyEnv} environment variable"
                                          + (feed.Auth == "trusted-publishing" ? " (from the NuGet/login action's NUGET_API_KEY output)." : "."));

    /// <summary>
    ///     Pins every package's version, the released ones and the unchanged ones, in one props file imported by every project.
    ///     A global <c>-p:Version</c> would also reach referenced projects and give dependencies the wrong version.
    /// </summary>
    private string WriteVersionsProps(ReleasePlan plan, string outputDirectory)
    {
        var builder = new StringBuilder("<Project>\n  <PropertyGroup>\n");
        foreach (var package in plan.Packages)
        {
            var fullPath = SecurityElement.Escape(Path.GetFullPath(Path.Combine(repository.Root, package.Project)));
            builder.Append($"    <Version Condition=\"'$(MSBuildProjectFullPath)' == '{fullPath}'\">{package.NextVersion}</Version>\n");
        }

        builder.Append("  </PropertyGroup>\n</Project>\n");
        var path = Path.Combine(Path.GetFullPath(outputDirectory), "versions.props");
        File.WriteAllText(path, builder.ToString());
        return path;
    }
}
