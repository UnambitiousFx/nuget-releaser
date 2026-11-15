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
        var packages = repository.DiscoverPackages(config);
        if (!config.Defaults.TagFormat.Contains("{package}") && packages.Count > 1)
        {
            throw new ReleaserException($"tag_format \"{config.Defaults.TagFormat}\" has no {{package}}, so it can tag only one package; "
                                        + $"found {packages.Count}: {string.Join(", ", packages.Select(p => p.Id).Order(StringComparer.Ordinal))}.");
        }

        var tagsAtHead = repository.TagsAtHead().ToHashSet(StringComparer.Ordinal);
        var tags = repository.Tags().Where(t => !tagsAtHead.Contains(t)).ToList();
        var states = packages.Select(package =>
        {
            var latest = TagFormat.Latest(config.Defaults.TagFormat, package.Id, tags);
            var tagged = TagFormat.Latest(config.Defaults.TagFormat, package.Id, tagsAtHead);
            return new PackageState(package, latest?.Version, repository.CommitsSince(latest?.Tag, package.Paths, package.LocatedImports),tagged?.Version);
        }).ToList();

        return Planner.Plan(states, SemVersion.Parse(config.Defaults.InitialVersion));
    }

    /// <summary>
    ///     Packs every released package into <paramref name="outputDirectory" />; unless <paramref name="dryRun" />, publishes
    ///     them, and attaches the files matching <paramref name="assets" /> to every GitHub Release the run creates.
    /// </summary>
    public async Task ReleaseAsync(ReleasePlan plan, string outputDirectory, bool dryRun, IReadOnlyList<string>? assets = null)
    {
        var releases = plan.Releases.ToList();
        if (releases.Count == 0)
        {
            log.WriteLine("Nothing to release.");
            return;
        }

        // Before anything is published: a missing asset is a mistake to fix, not a release to finish later.
        var assetFiles = ResolveAssets(assets ?? []);
        if (!dryRun && assetFiles.Count > 0 && gitHub is null)
        {
            throw new ReleaserException("--asset needs GitHub Releases: set GITHUB_TOKEN, and GITHUB_REPOSITORY unless origin is on github.com.");
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
            // GeneratePackageOnBuild (often turned on in CI) makes pack skip its own build to avoid a build/pack cycle,
            // so a clean checkout would have no DLL to pack: the tool packs explicitly and turns it off.
            ProcessRunner.Run(repository.Root, "dotnet", "pack", Path.Combine(repository.Root, release.Project),
                "--configuration", "Release", "--output", outputDirectory, $"-p:CustomBeforeMicrosoftCommonProps={versionsProps}",
                "-p:GeneratePackageOnBuild=false");
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

        if (config.Feed.Count == 0)
        {
            log.WriteLine("No feed configured: nothing pushed.");
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

        await CreateGitHubReleasesAsync(releases, tags, head, assetFiles);
    }

    /// <summary>
    ///     Each pattern is a file, or a directory followed by a file-name pattern with <c>*</c> or <c>?</c>
    ///     (<c>dist/*.zip</c>), relative to the current directory. Every pattern must match at least one file.
    /// </summary>
    private static List<string> ResolveAssets(IReadOnlyList<string> patterns)
    {
        var files = new List<string>();
        foreach (var pattern in patterns)
        {
            var fullPattern = Path.GetFullPath(pattern);
            var fileName = Path.GetFileName(fullPattern);
            var directory = Path.GetDirectoryName(fullPattern)!;
            var matches = fileName.AsSpan().IndexOfAny('*', '?') >= 0
                ? Directory.Exists(directory) ? Directory.GetFiles(directory, fileName).Order(StringComparer.Ordinal).ToList() : []
                : File.Exists(fullPattern) ? [fullPattern] : [];
            if (matches.Count == 0)
            {
                throw new ReleaserException($"--asset {pattern} matches no file.");
            }

            files.AddRange(matches);
        }

        if (files.GroupBy(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            // An asset is named after its file: two files of the same name would be one asset.
            throw new ReleaserException($"--asset matches {duplicate.Key} more than once: {string.Join(", ", duplicate)}.");
        }

        return [.. files.Distinct(StringComparer.Ordinal)];
    }

    private async Task CreateGitHubReleasesAsync(List<PlannedPackage> releases, List<string> tags, string head, List<string> assets)
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
                await CreateAsync(gitHub, tags[i], head, $"{releases[i].Id} {releases[i].NextVersion}", Notes(releases[i]), assets);
            }

            return;
        }

        var name = string.Join(", ", releases.Select(r => $"{r.Id} {r.NextVersion}"));
        var body = string.Join("\n\n", releases.Select(r => $"## {r.Id} {r.NextVersion}\n\n{Notes(r)}"));
        await CreateAsync(gitHub, $"releases/{head[..7]}", head, name, body, assets);
    }

    /// <summary>Assets are uploaded to an existing release too: a re-run after a failed upload sends what is missing.</summary>
    private async Task CreateAsync(GitHubReleases github, string tag, string target, string name, string body, List<string> assets)
    {
        var (created, release) = await github.CreateAsync(tag, target, name, body);
        log.WriteLine(created ? $"Created GitHub Release {tag}" : $"GitHub Release {tag} already exists");
        foreach (var asset in await github.UploadAssetsAsync(release, assets))
        {
            log.WriteLine($"Attached {asset} to {tag}");
        }
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
