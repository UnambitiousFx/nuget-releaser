using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace UnambitiousFx.NugetReleaser;

/// <summary>Creates GitHub Releases through the REST API; an existing release for a tag is left alone.</summary>
internal sealed partial class GitHubReleases(HttpClient http, string repository)
{
    [GeneratedRegex(@"github\.com[:/](?<slug>[^/]+/[^/]+?)(\.git)?/?$")]
    private static partial Regex GitHubRemote();

    /// <summary>A client for <c>GITHUB_REPOSITORY</c> (or the origin remote) with <c>GITHUB_TOKEN</c>, or <c>null</c> when either is missing.</summary>
    public static GitHubReleases? FromEnvironment(string? originUrl)
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        if (string.IsNullOrEmpty(repository) && originUrl is not null && GitHubRemote().Match(originUrl) is { Success: true } match)
        {
            repository = match.Groups["slug"].Value;
        }

        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(repository))
        {
            return null;
        }

        var http = new HttpClient { BaseAddress = new Uri((Environment.GetEnvironmentVariable("GITHUB_API_URL") ?? "https://api.github.com").TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("nuget-releaser");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return new GitHubReleases(http, repository);
    }

    /// <summary>Returns <c>false</c> when a release for <paramref name="tag" /> already exists.</summary>
    public async Task<bool> CreateAsync(string tag, string target, string name, string body)
    {
        using var existing = await http.GetAsync($"repos/{repository}/releases/tags/{Uri.EscapeDataString(tag)}");
        if (existing.IsSuccessStatusCode)
        {
            return false;
        }

        if (existing.StatusCode != HttpStatusCode.NotFound)
        {
            throw new ReleaserException($"GitHub: reading release {tag} failed ({(int)existing.StatusCode}).");
        }

        using var created = await http.PostAsJsonAsync($"repos/{repository}/releases",
            new CreateReleaseRequest(tag, target, name, body), GitHubJsonContext.Default.CreateReleaseRequest);
        if (!created.IsSuccessStatusCode)
        {
            throw new ReleaserException($"GitHub: creating release {tag} failed ({(int)created.StatusCode}): {await created.Content.ReadAsStringAsync()}");
        }

        return true;
    }
}

internal sealed record CreateReleaseRequest(string TagName, string TargetCommitish, string Name, string Body);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(CreateReleaseRequest))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
