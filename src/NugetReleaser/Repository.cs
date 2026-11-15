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

    /// <summary>
    ///     Every tracked project with <c>IsPackable=true</c>, read through MSBuild evaluation. A package's paths are its
    ///     directory, its <c>extra_paths</c>, the <c>shared_paths</c>, and every tracked file MSBuild imports for it.
    /// </summary>
    public IReadOnlyList<PackageInfo> DiscoverPackages(ReleaserConfig config)
    {
        var tracked = Git(Root, "ls-files", "-z")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        var projects = tracked
            .Where(p => p.EndsWith(".csproj", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
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
            var directory = Relative(Path.GetDirectoryName(project.FullPath)!);
            // Inside the repository and tracked: SDK files, and what restore writes to obj/, never change in a commit.
            var imports = project.Imports
                .Select(Relative)
                .Where(path => tracked.Contains(path) && !path.StartsWith(directory + "/", StringComparison.Ordinal));
            List<string> paths = [directory, .. extraPaths, .. config.Defaults.SharedPaths, .. imports];
            return new PackageInfo(project.PackageId, Relative(project.FullPath), [.. dependencies.Distinct()], [.. paths.Distinct(StringComparer.Ordinal)]);
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

    /// <summary>Tags on the HEAD commit itself: what a previous run of this commit already released.</summary>
    public IReadOnlyList<string> TagsAtHead() =>
        Git(Root, "tag", "--points-at", "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    ///     Lightweight tags, so pushing a tag GitHub already created for a release at the same commit is a no-op
    ///     and an interrupted release can be re-run.
    /// </summary>
    public void CreateTag(string tag, string commit) =>
        Git(Root, "tag", tag, commit);

    /// <summary>Pushes every tag or none (<c>--atomic</c>), uploading <paramref name="commit" /> with them.</summary>
    public void PushTags(IEnumerable<string> tags, string commit) =>
        Git(Root, ["push", "--atomic", "origin", .. tags.Select(t => $"{commit}:refs/tags/{t}")]);

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
    ///     <c>ProjectReference</c> or an <c>Import</c> conditioned on <c>$(TargetFramework)</c> is invisible to the outer build.
    ///     Imports are read only for a packable project: they are what makes a change release it.
    /// </summary>
    private EvaluatedProject Evaluate(string fullPath)
    {
        using var document = MsBuild(fullPath, null, "-getProperty:IsPackable", "-getProperty:PackageId", "-getProperty:TargetFrameworks");
        var properties = document.RootElement.GetProperty("Properties");
        var isPackable = string.Equals(properties.GetProperty("IsPackable").GetString(), "true", StringComparison.OrdinalIgnoreCase);
        var references = References(document);
        var imports = new HashSet<string>(StringComparer.Ordinal);
        if (isPackable)
        {
            imports.UnionWith(Imports(fullPath, null));
        }

        var targetFrameworks = properties.GetProperty("TargetFrameworks").GetString()!
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var targetFramework in targetFrameworks)
        {
            using var inner = MsBuild(fullPath, targetFramework);
            references.UnionWith(References(inner));
            if (isPackable)
            {
                imports.UnionWith(Imports(fullPath, targetFramework));
            }
        }

        return new EvaluatedProject(
            fullPath,
            properties.GetProperty("PackageId").GetString()!,
            isPackable,
            [.. references.Order(StringComparer.Ordinal)],
            [.. imports.Order(StringComparer.Ordinal)]);
    }

    private JsonDocument MsBuild(string fullPath, string? targetFramework, params string[] properties) =>
        JsonDocument.Parse(ProcessRunner.Run(Root, "dotnet",
        [
            "msbuild", fullPath, "-p:Configuration=Release", .. TargetFramework(targetFramework),
            .. properties, "-getItem:ProjectReference"
        ]));

    /// <summary>
    ///     Every file the evaluation imported, read from <c>-preprocess</c>: it marks each import with a comment holding
    ///     the file's full path on a line of its own. <c>-getProperty</c> turns <c>-preprocess</c> off, hence a call of its own;
    ///     <c>MSBuildAllProjects</c> is no substitute, recent SDKs no longer list <c>Directory.Build.props</c> in it.
    /// </summary>
    private IEnumerable<string> Imports(string fullPath, string? targetFramework) =>
        ProcessRunner.Run(Root, "dotnet", ["msbuild", fullPath, "-p:Configuration=Release", .. TargetFramework(targetFramework), "-preprocess"])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => Path.IsPathFullyQualified(line) && File.Exists(line))
            .Select(Path.GetFullPath);

    private static string[] TargetFramework(string? targetFramework) =>
        targetFramework is null ? [] : [$"-p:TargetFramework={targetFramework}"];

    private static HashSet<string> References(JsonDocument document) =>
        document.RootElement.TryGetProperty("Items", out var items) && items.TryGetProperty("ProjectReference", out var refs)
            ? refs.EnumerateArray().Select(r => Path.GetFullPath(r.GetProperty("FullPath").GetString()!)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private sealed record EvaluatedProject(string FullPath, string PackageId, bool IsPackable, List<string> References, List<string> Imports);
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
