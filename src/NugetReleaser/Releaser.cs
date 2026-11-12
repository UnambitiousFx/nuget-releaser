using System.Security;
using System.Text;

namespace UnambitiousFx.NugetReleaser;

/// <summary>
///     Executes a <see cref="ReleasePlan" />: pack, push, tags, GitHub Release. Each step skips what already exists. A
///     package whose tag already points at HEAD is planned again as if untagged, so re-running after any failure
///     finishes the release that run started.
/// </summary>
/// <param name="repository">The git work tree to release from.</param>
/// <param name="config">The loaded <c>nuget-releaser.toml</c>.</param>
/// <param name="log">Where progress is written.</param>
/// <param name="gitHub">Where GitHub Releases are created; <c>null</c> skips them.</param>
internal sealed class Releaser(Repository repository, ReleaserConfig config, TextWriter log, GitHubReleases? gitHub)
{
    /// <summary>Builds the plan from the repository's packages, tags and history.</summary>
    public ReleasePlan Plan()
    {
        var tagsAtHead = repository.TagsAtHead().ToHashSet(StringComparer.Ordinal);
        var tags = repository.Tags().Where(t => !tagsAtHead.Contains(t)).ToList();
        var states = repository.DiscoverPackages(config).Select(package =>
        {
            var latest = TagFormat.Latest(config.Defaults.TagFormat, package.Id, tags);
            var tagged = TagFormat.Latest(config.Defaults.TagFormat, package.Id, tagsAtHead);
            return new PackageState(package, latest?.Version, repository.CommitsSince(latest?.Tag, package.Paths), tagged?.Version);
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

        // dotnet pack runs from the repository root: a relative path must not resolve twice, differently.
        outputDirectory = Path.GetFullPath(outputDirectory);
        var head = repository.Head;
        if (!dryRun && repository.UncommittedChanges(outputDirectory) is { Count: > 0 } changes)
        {
            // The tags point at HEAD: what is packed must be exactly HEAD.
            throw new ReleaserException($"Commit or stash these changes before releasing:\n  {string.Join("\n  ", changes)}");
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

        // All tags or none, and before the GitHub Release: the push also uploads HEAD, which GitHub needs to create it.
        var tags = releases.Select(r => TagFormat.Format(config.Defaults.TagFormat, r.Id, r.NextVersion)).ToList();
        log.WriteLine($"Pushing tags {string.Join(", ", tags)}");
        repository.PushTags(tags, head);
        foreach (var tag in tags.Where(t => !repository.TagExists(t)))
        {
            repository.CreateTag(tag, head);
        }

        await CreateGitHubReleasesAsync(releases, tags, head);
    }

    private async Task CreateGitHubReleasesAsync(List<PlannedPackage> releases, List<string> tags, string head)
    {
        if (gitHub is null)
        {
            log.WriteLine("No GITHUB_TOKEN or GitHub repository found: skipping GitHub Releases.");
            return;
        }

        if (config.Github.ReleaseMode == "per-package")
        {
            for (var i = 0; i < releases.Count; i++)
            {
                await CreateAsync(gitHub, tags[i], head, $"{releases[i].Id} {releases[i].NextVersion}", Notes(releases[i]));
            }

            return;
        }

        var name = string.Join(", ", releases.Select(r => $"{r.Id} {r.NextVersion}"));
        var body = string.Join("\n\n", releases.Select(r => $"## {r.Id} {r.NextVersion}\n\n{Notes(r)}"));
        await CreateAsync(gitHub, $"releases/{head[..7]}", head, name, body);
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
