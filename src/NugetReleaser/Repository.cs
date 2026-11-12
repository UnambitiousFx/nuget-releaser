using System.Text.Json;

namespace UnambitiousFx.NugetReleaser;

/// <summary>Reads packages, tags and commits from a git work tree.</summary>
internal sealed class Repository
{
    private Repository(string root) => Root = root;

    public string Root { get; }

    public static Repository Open(string directory)
    {
        try
        {
            return new Repository(Path.GetFullPath(Git(directory, "rev-parse", "--show-toplevel").Trim()));
        }
        catch (ReleaserException)
        {
            throw new ReleaserException($"{directory} is not inside a git repository.");
        }
    }

    public string Head => Git(Root, "rev-parse", "HEAD").Trim();

    /// <summary>Every tracked project with <c>IsPackable=true</c>, read through MSBuild evaluation.</summary>
    public IReadOnlyList<PackageInfo> DiscoverPackages(ReleaserConfig config)
    {
        var projects = Git(Root, "ls-files", "-z", "--", "*.csproj")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Path.GetFullPath(Path.Combine(Root, p)))
            .ToList();

        var evaluated = projects
            .AsParallel()
            .AsOrdered()
            .Select(Evaluate)
            .Where(p => p.IsPackable)
            .ToDictionary(p => p.FullPath, StringComparer.Ordinal);

        var overrides = new Dictionary<string, PackageConfig>(StringComparer.Ordinal);
        foreach (var package in config.Package)
        {
            var fullPath = Path.GetFullPath(Path.Combine(Root, package.Project));
            if (!evaluated.ContainsKey(fullPath))
            {
                throw new ReleaserException($"[[package]] {package.Project} is not a tracked project with IsPackable=true.");
            }

            overrides[fullPath] = package;
        }

        return evaluated.Values.Select(project =>
        {
            var dependencies = project.References.Select(reference =>
                evaluated.TryGetValue(reference, out var dependency)
                    ? dependency.PackageId
                    : throw new ReleaserException($"{project.PackageId} references {Relative(reference)}, which is not packable: it could not be restored from the feed."));

            var extraPaths = overrides.TryGetValue(project.FullPath, out var o) ? o.ExtraPaths : [];
            List<string> paths = [Relative(Path.GetDirectoryName(project.FullPath)!), .. extraPaths, .. config.Defaults.SharedPaths];
            return new PackageInfo(project.PackageId, Relative(project.FullPath), [.. dependencies.Distinct()], paths);
        }).ToList();
    }

    /// <summary>Tags reachable from HEAD: a tag cut on another branch says nothing about this history's versions.</summary>
    public IReadOnlyList<string> Tags() =>
        Git(Root, "tag", "--list", "--merged", "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Tracked or untracked (not ignored) changes outside <paramref name="excludedDirectory" />.</summary>
    public IReadOnlyList<string> UncommittedChanges(string excludedDirectory)
    {
        var excluded = Relative(Path.GetFullPath(excludedDirectory)) + "/";
        return Git(Root, "status", "--porcelain", "--untracked-files=all")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line[3..].Trim('"'))
            .Where(path => !path.StartsWith(excluded, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>Commits reachable from HEAD but not from <paramref name="since" /> that touch one of <paramref name="paths" />.</summary>
    public IReadOnlyList<Commit> CommitsSince(string? since, IEnumerable<string> paths) =>
        Git(Root, ["log", "--format=%H%x1f%B%x1e", since is null ? "HEAD" : $"{since}..HEAD", "--", .. paths])
            .Split('\x1e', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Split('\x1f', 2))
            .Select(parts => new Commit(parts[0], parts[1].Trim()))
            .ToList();

    public bool TagExists(string tag) =>
        Git(Root, "tag", "--list", tag).Trim().Length > 0;

    /// <summary>
    ///     Lightweight tags, so pushing a tag GitHub already created for a release at the same commit is a no-op
    ///     and an interrupted release can be re-run.
    /// </summary>
    public void CreateTag(string tag, string commit) =>
        Git(Root, "tag", tag, commit);

    public void PushTags(IEnumerable<string> tags, string commit) =>
        Git(Root, ["push", "origin", .. tags.Select(t => $"{commit}:refs/tags/{t}")]);

    public string? OriginUrl()
    {
        try
        {
            return Git(Root, "remote", "get-url", "origin").Trim();
        }
        catch (ReleaserException)
        {
            return null;
        }
    }

    private string Relative(string fullPath) => Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    private static string Git(string directory, params IEnumerable<string> arguments) =>
        ProcessRunner.Run(directory, "git", arguments);

    /// <summary>
    ///     Evaluated as <c>dotnet pack</c> sees it: Release, and once per target framework, since a
    ///     <c>ProjectReference</c> conditioned on <c>$(TargetFramework)</c> is invisible to the outer build.
    /// </summary>
    private EvaluatedProject Evaluate(string fullPath)
    {
        using var document = MsBuild(fullPath, null, "-getProperty:IsPackable", "-getProperty:PackageId", "-getProperty:TargetFrameworks");
        var properties = document.RootElement.GetProperty("Properties");
        var references = References(document);

        var targetFrameworks = properties.GetProperty("TargetFrameworks").GetString()!
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var targetFramework in targetFrameworks)
        {
            using var inner = MsBuild(fullPath, targetFramework);
            references.UnionWith(References(inner));
        }

        return new EvaluatedProject(
            fullPath,
            properties.GetProperty("PackageId").GetString()!,
            string.Equals(properties.GetProperty("IsPackable").GetString(), "true", StringComparison.OrdinalIgnoreCase),
            [.. references.Order(StringComparer.Ordinal)]);
    }

    private JsonDocument MsBuild(string fullPath, string? targetFramework, params string[] properties) =>
        JsonDocument.Parse(ProcessRunner.Run(Root, "dotnet",
        [
            "msbuild", fullPath, "-p:Configuration=Release",
            .. targetFramework is null ? Array.Empty<string>() : [$"-p:TargetFramework={targetFramework}"],
            .. properties, "-getItem:ProjectReference"
        ]));

    private static HashSet<string> References(JsonDocument document) =>
        document.RootElement.TryGetProperty("Items", out var items) && items.TryGetProperty("ProjectReference", out var refs)
            ? refs.EnumerateArray().Select(r => Path.GetFullPath(r.GetProperty("FullPath").GetString()!)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private sealed record EvaluatedProject(string FullPath, string PackageId, bool IsPackable, List<string> References);
}

/// <summary>Turns <c>tag_format</c> (<c>{package}/v{version}</c>) into tag names and back.</summary>
internal static class TagFormat
{
    public static string Format(string format, string packageId, string version) =>
        format.Replace("{package}", packageId).Replace("{version}", version);

    /// <summary>The highest stable version tagged for <paramref name="packageId" />, with its tag.</summary>
    public static (SemVersion Version, string Tag)? Latest(string format, string packageId, IEnumerable<string> tags)
    {
        (SemVersion Version, string Tag)? latest = null;
        foreach (var tag in tags)
        {
            if (TryParse(format, packageId, tag, out var version) && (latest is null || version.CompareTo(latest.Value.Version) > 0))
            {
                latest = (version, tag);
            }
        }

        return latest;
    }

    public static bool TryParse(string format, string packageId, string tag, out SemVersion version)
    {
        version = default;
        var at = format.IndexOf("{version}", StringComparison.Ordinal);
        var prefix = format[..at].Replace("{package}", packageId);
        var suffix = format[(at + "{version}".Length)..].Replace("{package}", packageId);
        return tag.Length > prefix.Length + suffix.Length
               && tag.StartsWith(prefix, StringComparison.Ordinal)
               && tag.EndsWith(suffix, StringComparison.Ordinal)
               && SemVersion.TryParse(tag[prefix.Length..^suffix.Length], out version);
    }
}
