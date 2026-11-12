namespace UnambitiousFx.NugetReleaser.Tests;

public sealed class ReleaserConfigTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nuget-releaser-{Guid.NewGuid():N}.toml");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        // Given / When
        var config = ReleaserConfig.Load(_path);

        // Then
        Assert.Equal("{package}/v{version}", config.Defaults.TagFormat);
        Assert.Equal("grouped", config.Github.ReleaseMode);
        Assert.Equal("NUGET_API_KEY", Assert.Single(config.Feed).ApiKeyEnv);
    }

    [Fact]
    public void Load_FullFile_MapsSnakeCaseKeys()
    {
        // Given
        File.WriteAllText(_path, """
                                 [defaults]
                                 tag_format = "{package}@{version}"
                                 initial_version = "1.0.0"
                                 shared_paths = ["Directory.Build.props"]

                                 [[package]]
                                 project = "src/A/A.csproj"
                                 extra_paths = ["shared/http"]

                                 [github]
                                 release_mode = "per-package"

                                 [[feed]]
                                 name = "github"
                                 source = "https://nuget.pkg.github.com/x/index.json"
                                 api_key_env = "GITHUB_TOKEN"
                                 """);

        // When
        var config = ReleaserConfig.Load(_path);

        // Then
        Assert.Equal("{package}@{version}", config.Defaults.TagFormat);
        Assert.Equal("1.0.0", config.Defaults.InitialVersion);
        Assert.Equal(["Directory.Build.props"], config.Defaults.SharedPaths);
        var package = Assert.Single(config.Package);
        Assert.Equal("src/A/A.csproj", package.Project);
        Assert.Equal(["shared/http"], package.ExtraPaths);
        Assert.Equal("per-package", config.Github.ReleaseMode);
        var feed = Assert.Single(config.Feed);
        Assert.Equal("github", feed.Name);
        Assert.Equal("GITHUB_TOKEN", feed.ApiKeyEnv);
    }

    [Theory]
    [InlineData("[defaults]\ntag_format = \"v{version}\"", "tag_format")]
    [InlineData("[defaults]\nbump_source = \"changesets\"", "bump_source")]
    [InlineData("[defaults]\nversion_source = \"feed\"", "version_source")]
    [InlineData("[github]\nrelease_mode = \"sometimes\"", "release_mode")]
    [InlineData("[[feed]]\nauth = \"magic\"", "auth")]
    [InlineData("[defaults\n", "")]
    public void Load_InvalidFile_ThrowsReleaserException(string toml, string mentioned)
    {
        // Given
        File.WriteAllText(_path, toml);

        // When
        var error = Assert.Throws<ReleaserException>(() => ReleaserConfig.Load(_path));

        // Then
        Assert.Contains(mentioned, error.Message);
    }
}
