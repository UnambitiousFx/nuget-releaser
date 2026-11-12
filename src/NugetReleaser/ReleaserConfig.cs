using System.Text.Json.Serialization;
using Tomlyn;
using Tomlyn.Serialization;

namespace UnambitiousFx.NugetReleaser;

/// <summary>The content of <c>nuget-releaser.toml</c>; every key is optional.</summary>
internal sealed class ReleaserConfig
{
    public const string FileName = "nuget-releaser.toml";

    public DefaultsConfig Defaults { get; set; } = new();
    public List<PackageConfig> Package { get; set; } = [];
    public GitHubConfig Github { get; set; } = new();
    public List<FeedConfig> Feed { get; set; } = [new()];

    /// <summary>Reads and validates the file, or returns the defaults when it does not exist.</summary>
    public static ReleaserConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new ReleaserConfig();
        }

        ReleaserConfig config;
        try
        {
            config = TomlSerializer.Deserialize(File.ReadAllText(path), ReleaserConfigContext.Default.ReleaserConfig)
                     ?? new ReleaserConfig();
        }
        catch (TomlException e)
        {
            throw new ReleaserException($"{path}: {e.Message}");
        }

        config.Validate(path);
        return config;
    }

    private void Validate(string path)
    {
        void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new ReleaserException($"{path}: {message}");
            }
        }

        Require(Defaults.TagFormat.Contains("{package}") && Defaults.TagFormat.Contains("{version}"),
            "tag_format must contain both {package} and {version}.");
        Require(SemVersion.TryParse(Defaults.InitialVersion, out _), "initial_version must be a major.minor.patch version.");
        Require(Defaults.BumpSource == "conventional-commits", "bump_source supports only \"conventional-commits\" for now.");
        Require(Defaults.VersionSource == "tag", "version_source supports only \"tag\" for now.");
        Require(Github.ReleaseMode is "grouped" or "per-package", "github.release_mode must be \"grouped\" or \"per-package\".");
        Require(Package.All(p => !string.IsNullOrWhiteSpace(p.Project)), "every [[package]] needs a project path.");
        foreach (var feed in Feed)
        {
            Require(feed.Auth is "api-key" or "trusted-publishing", $"feed '{feed.Name}': auth must be \"api-key\" or \"trusted-publishing\".");
        }
    }
}

internal sealed class DefaultsConfig
{
    public string TagFormat { get; set; } = "{package}/v{version}";
    public string BumpSource { get; set; } = "conventional-commits";
    public string VersionSource { get; set; } = "tag";
    public string InitialVersion { get; set; } = "0.1.0";
    public List<string> SharedPaths { get; set; } = ["Directory.Build.props", "Directory.Packages.props", "global.json"];
}

internal sealed class PackageConfig
{
    public string Project { get; set; } = "";
    public List<string> ExtraPaths { get; set; } = [];
}

internal sealed class GitHubConfig
{
    public string ReleaseMode { get; set; } = "grouped";
}

internal sealed class FeedConfig
{
    public string Name { get; set; } = "nuget.org";
    public string Source { get; set; } = "https://api.nuget.org/v3/index.json";

    // shortcut: "trusted-publishing" reads the short-lived key NuGet/login exports, it does not do the OIDC exchange itself.
    public string Auth { get; set; } = "api-key";
    public string ApiKeyEnv { get; set; } = "NUGET_API_KEY";
}

[TomlSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[TomlSerializable(typeof(ReleaserConfig))]
internal sealed partial class ReleaserConfigContext : TomlSerializerContext;
