namespace UnambitiousFx.NugetReleaser.Tests;

public sealed class PlannerTests
{
    private static readonly SemVersion Initial = new(0, 1, 0);

    private static PackageState State(string id, string? current, string[] messages, params string[] dependencies) =>
        new(new PackageInfo(id, $"src/{id}/{id}.csproj", dependencies, [$"src/{id}"]),
            current is null ? null : SemVersion.Parse(current),
            [.. messages.Select((m, i) => new Commit($"{id}{i}000000", m))]);

    [Fact]
    public void Plan_NoReleasableCommit_KeepsVersion()
    {
        // Given
        var states = new[] { State("A", "1.0.0", ["chore: tidy", "docs: x"]) };

        // When
        var plan = Planner.Plan(states, Initial);

        // Then
        var a = Assert.Single(plan.Packages);
        Assert.False(a.Release);
        Assert.Equal("1.0.0", a.NextVersion);
        Assert.Empty(a.Reasons);
    }

    [Fact]
    public void Plan_HighestCommitBumpWins()
    {
        // Given
        var states = new[] { State("A", "1.2.3", ["fix: a", "feat: b", "fix: c"]) };

        // When
        var a = Assert.Single(Planner.Plan(states, Initial).Packages);

        // Then
        Assert.True(a.Release);
        Assert.Equal(Bump.Minor, a.Bump);
        Assert.Equal("1.3.0", a.NextVersion);
    }

    [Fact]
    public void Plan_NeverReleased_StartsAtInitialVersion()
    {
        // Given
        var states = new[] { State("A", null, ["feat!: everything"]) };

        // When
        var a = Assert.Single(Planner.Plan(states, Initial).Packages);

        // Then
        Assert.True(a.Release);
        Assert.Null(a.CurrentVersion);
        Assert.Equal("0.1.0", a.NextVersion);
        Assert.Equal("first release", a.Reasons[0]);
    }

    [Theory]
    [InlineData("fix: a", "1.0.1")]
    [InlineData("feat: a", "1.0.1")]
    [InlineData("feat!: a", "1.1.0")]
    public void Plan_ReleasedDependency_CascadesToDependents(string dependencyCommit, string expectedDependent)
    {
        // Given: C → B → A, only A changed
        var states = new[]
        {
            State("C", "1.0.0", [], "B"),
            State("B", "1.0.0", [], "A"),
            State("A", "1.0.0", [dependencyCommit])
        };

        // When
        var plan = Planner.Plan(states, Initial);

        // Then: dependencies first, and the cascade reaches C through B
        Assert.Equal(["A", "B", "C"], plan.Packages.Select(p => p.Id));
        Assert.Equal(expectedDependent, plan.Packages[1].NextVersion);
        Assert.True(plan.Packages[2].Release);
        Assert.Contains(plan.Packages[1].Reasons, r => r.StartsWith("dependency A → "));
    }

    [Fact]
    public void Plan_UnchangedDependency_DoesNotReleaseDependent()
    {
        // Given
        var states = new[] { State("B", "1.0.0", [], "A"), State("A", "1.0.0", ["chore: x"]) };

        // When
        var plan = Planner.Plan(states, Initial);

        // Then
        Assert.Empty(plan.Releases);
    }

    [Fact]
    public void Plan_Cycle_Throws()
    {
        // Given
        var states = new[] { State("A", "1.0.0", [], "B"), State("B", "1.0.0", [], "A") };

        // When
        var error = Assert.Throws<ReleaserException>(() => Planner.Plan(states, Initial));

        // Then
        Assert.Contains("cycle", error.Message);
    }

    [Fact]
    public void Plan_UnknownDependency_Throws()
    {
        // Given
        var states = new[] { State("A", "1.0.0", [], "Missing") };

        // When / Then
        Assert.Throws<ReleaserException>(() => Planner.Plan(states, Initial));
    }
}
