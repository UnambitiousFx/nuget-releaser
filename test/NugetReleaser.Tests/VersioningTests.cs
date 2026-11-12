namespace UnambitiousFx.NugetReleaser.Tests;

public sealed class VersioningTests
{
    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("0.0.0", true)]
    [InlineData("01.2.3", false)]
    [InlineData("1.2", false)]
    [InlineData("1.2.3-beta.1", false)]
    [InlineData("-1.2.3", false)]
    [InlineData("", false)]
    public void TryParse_Text_AcceptsOnlyStableSemVer(string text, bool expected)
    {
        // Given / When
        var parsed = SemVersion.TryParse(text, out _);

        // Then
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("Major", "2.0.0")]
    [InlineData("Minor", "1.3.0")]
    [InlineData("Patch", "1.2.4")]
    [InlineData("None", "1.2.3")]
    public void Increment_Bump_ResetsLowerParts(string bump, string expected)
    {
        // Given
        var version = new SemVersion(1, 2, 3);

        // When
        var next = version.Increment(Enum.Parse<Bump>(bump));

        // Then
        Assert.Equal(expected, next.ToString());
    }

    [Theory]
    [InlineData("feat: add x", "Minor")]
    [InlineData("feat(http): add x", "Minor")]
    [InlineData("fix: y", "Patch")]
    [InlineData("perf: z", "Patch")]
    [InlineData("chore(deps): update", "None")]
    [InlineData("docs: readme", "None")]
    [InlineData("feat!: drop api", "Major")]
    [InlineData("refactor(core)!: rename", "Major")]
    [InlineData("fix: y\n\nBREAKING CHANGE: removed z", "Major")]
    [InlineData("fix: y\n\nBREAKING-CHANGE: removed z", "Major")]
    [InlineData("Merge pull request #1 from x/y", "None")]
    [InlineData("feat:missing space", "None")]
    public void BumpOf_Message_FollowsConventionalCommits(string message, string expected)
    {
        // Given / When
        var bump = ConventionalCommit.BumpOf(message);

        // Then
        Assert.Equal(Enum.Parse<Bump>(expected), bump);
    }

    [Fact]
    public void Latest_Tags_ReturnsHighestStableTagOfThatPackageOnly()
    {
        // Given
        string[] tags = ["Lib/v1.9.0", "Lib/v1.10.0", "Lib/v2.0.0-beta.1", "Lib.Core/v9.0.0", "Lib/vnext", "v3.0.0"];

        // When
        var latest = TagFormat.Latest("{package}/v{version}", "Lib", tags);

        // Then
        Assert.Equal((new SemVersion(1, 10, 0), "Lib/v1.10.0"), latest);
    }

    [Fact]
    public void Format_Template_ReplacesPlaceholders()
    {
        // Given / When
        var tag = TagFormat.Format("{package}@{version}", "Lib", "1.0.0");

        // Then
        Assert.Equal("Lib@1.0.0", tag);
        Assert.True(TagFormat.TryParse("{package}@{version}", "Lib", tag, out var version));
        Assert.Equal(new SemVersion(1, 0, 0), version);
    }
}
