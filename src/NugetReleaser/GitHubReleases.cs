using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace UnambitiousFx.NugetReleaser;

/// <summary>
///     Creates GitHub Releases through the REST API and attaches assets to them; an existing release, or an asset of the
///     same name, is left alone.
/// </summary>
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

    /// <summary>Creates the release for <paramref name="tag" />; <c>Created</c> is <c>false</c> when it already existed.</summary>
    public async Task<(bool Created, GitHubRelease Release)> CreateAsync(string tag, string target, string name, string body)
    {
        using var existing = await http.GetAsync($"repos/{repository}/releases/tags/{Uri.EscapeDataString(tag)}");
        if (existing.IsSuccessStatusCode)
        {
            return (false, await ReadReleaseAsync(existing, tag));
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

        return (true, await ReadReleaseAsync(created, tag));
    }

    /// <summary>
    ///     Uploads each of <paramref name="files" /> under its file name, skipping the names the release already has, so a
    ///     re-run after a failed upload only sends what is missing. Returns the names uploaded.
    /// </summary>
    public async Task<IReadOnlyList<string>> UploadAssetsAsync(GitHubRelease release, IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return [];
        }

        using var listed = await http.GetAsync($"repos/{repository}/releases/{release.Id}/assets?per_page=100");
        if (!listed.IsSuccessStatusCode)
        {
            throw new ReleaserException($"GitHub: listing the assets of release {release.TagName} failed ({(int)listed.StatusCode}).");
        }

        var existing = (await listed.Content.ReadFromJsonAsync(GitHubJsonContext.Default.ListGitHubAsset) ?? [])
            .Select(a => a.Name).ToHashSet(StringComparer.Ordinal);

        // upload_url is a URI template: "https://uploads.github.com/repos/o/r/releases/1/assets{?name,label}".
        var uploadUrl = release.UploadUrl.Split('{')[0];
        var uploaded = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (existing.Contains(name))
            {
                continue;
            }

            await using var stream = File.OpenRead(file);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await http.PostAsync($"{uploadUrl}?name={Uri.EscapeDataString(name)}", content);
            if (!response.IsSuccessStatusCode)
            {
                throw new ReleaserException($"GitHub: uploading {name} to release {release.TagName} failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
            }

            uploaded.Add(name);
        }

        return uploaded;
    }

    private static async Task<GitHubRelease> ReadReleaseAsync(HttpResponseMessage response, string tag) =>
        await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubRelease)
        ?? throw new ReleaserException($"GitHub: release {tag} came back empty.");
}

internal sealed record CreateReleaseRequest(string TagName, string TargetCommitish, string Name, string Body);

/// <summary>The fields of a GitHub Release this tool uses.</summary>
internal sealed record GitHubRelease(long Id, string TagName, string UploadUrl);

internal sealed record GitHubAsset(string Name);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(CreateReleaseRequest))]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(List<GitHubAsset>))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
