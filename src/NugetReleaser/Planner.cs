using System.Text.Json.Serialization;

namespace UnambitiousFx.NugetReleaser;

/// <summary>A packable project, with the package ids it references through <c>ProjectReference</c>.</summary>
internal sealed record PackageInfo(string Id, string Project, IReadOnlyList<string> Dependencies, IReadOnlyList<string> Paths);

/// <summary>
///     A package with its last released version (<c>null</c> if never released) and the commits since.
///     <see cref="TaggedAtHead" /> is the version a previous run of this commit already tagged, left out of
///     <see cref="Current" /> so the plan finishes that release instead of skipping it.
/// </summary>
internal sealed record PackageState(PackageInfo Package, SemVersion? Current, IReadOnlyList<Commit> Commits, SemVersion? TaggedAtHead = null);

/// <summary>
///     What the run does with one package. <see cref="NextVersion" /> equals the current one when it is not released.
///     <see cref="Resumed" />: already tagged at HEAD by a previous run, which this run finishes.
/// </summary>
internal sealed record PlannedPackage(
    string Id,
    string Project,
    string? CurrentVersion,
    string NextVersion,
    Bump Bump,
    bool Release,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<Commit> Commits,
    bool Resumed = false);

/// <summary>The single source of truth of a run: every package in dependency order.</summary>
internal sealed record ReleasePlan(IReadOnlyList<PlannedPackage> Packages)
{
    [JsonIgnore]
    public IEnumerable<PlannedPackage> Releases => Packages.Where(p => p.Release);
}

internal static class Planner
{
    public static ReleasePlan Plan(IReadOnlyList<PackageState> states, SemVersion initialVersion)
    {
        var planned = new Dictionary<string, PlannedPackage>();
        foreach (var state in TopologicalOrder(states))
        {
            var reasons = new List<string>();
            var bump = Bump.None;
            foreach (var commit in state.Commits)
            {
                var commitBump = ConventionalCommit.BumpOf(commit.Message);
                if (commitBump != Bump.None)
                {
                    reasons.Add($"{commitBump.ToString().ToLowerInvariant()}: {commit.Subject} ({commit.Sha[..Math.Min(7, commit.Sha.Length)]})");
                }

                bump = Max(bump, commitBump);
            }

            foreach (var dependency in state.Package.Dependencies.Select(id => planned[id]).Where(d => d.Release))
            {
                // A breaking dependency changes what a consumer pulls in: at least a minor bump.
                var cascaded = dependency.Bump == Bump.Major ? Bump.Minor : Bump.Patch;
                reasons.Add($"dependency {dependency.Id} → {dependency.NextVersion}");
                bump = Max(bump, cascaded);
            }

            PlannedPackage result;
            if (state.Current is not { } current)
            {
                reasons.Insert(0, "first release");
                result = new PlannedPackage(state.Package.Id, state.Package.Project, null, initialVersion.ToString(), bump, true, reasons, state.Commits);
            }
            else
            {
                var release = bump != Bump.None;
                result = new PlannedPackage(state.Package.Id, state.Package.Project, current.ToString(),
                    current.Increment(bump).ToString(), bump, release, release ? reasons : [], state.Commits);
            }

            if (state.TaggedAtHead is { } tagged)
            {
                if (!result.Release || result.NextVersion != tagged.ToString())
                {
                    throw new ReleaserException($"{result.Id} {tagged} is tagged at HEAD, but the plan now computes {(result.Release ? result.NextVersion : "no release")}: "
                                                + "the configuration or history changed since that release. Fix it or delete the tag.");
                }

                result = result with { Resumed = true, Reasons = ["tagged at HEAD by a previous run: finishing its release", .. result.Reasons] };
            }

            planned[state.Package.Id] = result;
        }

        return new ReleasePlan([.. planned.Values]);
    }

    private static Bump Max(Bump a, Bump b) => a > b ? a : b;

    /// <summary>Dependencies first; ties broken by id so the plan is stable.</summary>
    private static List<PackageState> TopologicalOrder(IReadOnlyList<PackageState> states)
    {
        var byId = states.ToDictionary(s => s.Package.Id);
        var order = new List<PackageState>();
        var done = new HashSet<string>();
        var visiting = new List<string>();

        void Visit(PackageState state)
        {
            var id = state.Package.Id;
            if (done.Contains(id))
            {
                return;
            }

            if (visiting.Contains(id))
            {
                throw new ReleaserException($"Dependency cycle: {string.Join(" → ", visiting.SkipWhile(v => v != id))} → {id}.");
            }

            visiting.Add(id);
            foreach (var dependency in state.Package.Dependencies.Order())
            {
                if (!byId.TryGetValue(dependency, out var dependencyState))
                {
                    throw new ReleaserException($"{id} references {dependency}, which is not a package of this repository.");
                }

                Visit(dependencyState);
            }

            visiting.RemoveAt(visiting.Count - 1);
            done.Add(id);
            order.Add(state);
        }

        foreach (var state in states.OrderBy(s => s.Package.Id, StringComparer.Ordinal))
        {
            Visit(state);
        }

        return order;
    }
}
