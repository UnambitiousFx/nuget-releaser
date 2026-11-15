using System.Net;
using System.Text.Json;

namespace UnambitiousFx.NugetReleaser.Tests;

public sealed class GitHubReleasesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("nuget-releaser-assets-").FullName;

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task CreateAsync_ReleaseExists_ReturnsItWithoutCreating()
    {
        // Given
        var handler = new StubHandler { GetStatus = HttpStatusCode.OK };

        // When
        var (created, release) = await handler.Client().CreateAsync("Lib/v1.0.0", "abc", "Lib 1.0.0", "- fix");

        // Then
        Assert.False(created);
        Assert.Equal(StubHandler.ReleaseId, release.Id);
        Assert.Empty(handler.Posts);
    }

    [Fact]
    public async Task CreateAsync_ReleaseMissing_PostsTagTargetNameAndBody()
    {
        // Given
        var handler = new StubHandler();

        // When
        var (created, release) = await handler.Client().CreateAsync("Lib/v1.0.0", "abc123", "Lib 1.0.0", "- fix: a bug");

        // Then
        Assert.True(created);
        Assert.Equal(StubHandler.UploadUrl, release.UploadUrl);
        var (path, body) = Assert.Single(handler.Posts);
        Assert.Equal("/repos/o/r/releases", path);
        Assert.Equal("Lib/v1.0.0", body.GetProperty("tag_name").GetString());
        Assert.Equal("abc123", body.GetProperty("target_commitish").GetString());
        Assert.Equal("Lib 1.0.0", body.GetProperty("name").GetString());
        Assert.Equal("- fix: a bug", body.GetProperty("body").GetString());
    }

    [Fact]
    public async Task CreateAsync_TagWithSlash_IsEscapedInTheLookup()
    {
        // Given
        var handler = new StubHandler();

        // When
        await handler.Client().CreateAsync("Lib.Core/v1.0.0", "abc", "n", "b");

        // Then: the slash of the tag must not be read as a path separator
        Assert.EndsWith("/releases/tags/Lib.Core%2Fv1.0.0", Assert.Single(handler.Gets));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task CreateAsync_LookupFails_ThrowsWithStatusCode(HttpStatusCode status)
    {
        // Given
        var handler = new StubHandler { GetStatus = status };

        // When
        var error = await Assert.ThrowsAsync<ReleaserException>(() => handler.Client().CreateAsync("Lib/v1.0.0", "abc", "n", "b"));

        // Then
        Assert.Contains($"({(int)status})", error.Message);
        Assert.Empty(handler.Posts);
    }

    [Fact]
    public async Task CreateAsync_CreationRejected_ThrowsWithGitHubMessage()
    {
        // Given
        var handler = new StubHandler { PostStatus = HttpStatusCode.UnprocessableEntity, PostResponse = """{"message":"Validation Failed"}""" };

        // When
        var error = await Assert.ThrowsAsync<ReleaserException>(() => handler.Client().CreateAsync("Lib/v1.0.0", "abc", "n", "b"));

        // Then
        Assert.Contains("(422)", error.Message);
        Assert.Contains("Validation Failed", error.Message);
    }

    [Fact]
    public async Task CreateAsync_Failure_DoesNotLeakTheToken()
    {
        // Given: a client authenticated the way FromEnvironment does it
        const string token = "ghp_secret-token-value";
        var handler = new StubHandler { PostStatus = HttpStatusCode.UnprocessableEntity, PostResponse = "{}" };
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.test/") };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // When
        var error = await Assert.ThrowsAsync<ReleaserException>(() => new GitHubReleases(http, "o/r").CreateAsync("Lib/v1.0.0", "abc", "n", "b"));

        // Then
        Assert.DoesNotContain(token, error.Message);
        Assert.DoesNotContain(token, error.ToString());
    }

    [Fact]
    public async Task UploadAssetsAsync_NewFiles_UploadsEachUnderItsName()
    {
        // Given
        var handler = new StubHandler();
        var zip = Asset("tool-linux-x64.tar.gz", "binary");
        var sums = Asset("checksums.txt", "abc  tool-linux-x64.tar.gz");

        // When
        var uploaded = await handler.Client().UploadAssetsAsync(Release(), [zip, sums]);

        // Then: posted to upload_url without its URI template, as raw bytes
        Assert.Equal(["tool-linux-x64.tar.gz", "checksums.txt"], uploaded);
        Assert.Equal(["tool-linux-x64.tar.gz", "checksums.txt"], handler.Uploads.Select(u => u.Name));
        Assert.All(handler.Uploads, u => Assert.StartsWith("https://uploads.github.test/repos/o/r/releases/42/assets?name=", u.Url));
        Assert.All(handler.Uploads, u => Assert.Equal("application/octet-stream", u.ContentType));
        Assert.Equal("binary", handler.Uploads[0].Content);
    }

    [Fact]
    public async Task UploadAssetsAsync_AssetAlreadyThere_IsSkipped()
    {
        // Given: a previous run uploaded the archive, then failed
        var handler = new StubHandler { ExistingAssets = ["tool-linux-x64.tar.gz"] };

        // When
        var uploaded = await handler.Client().UploadAssetsAsync(Release(), [Asset("tool-linux-x64.tar.gz", "x"), Asset("checksums.txt", "y")]);

        // Then
        Assert.Equal(["checksums.txt"], uploaded);
        Assert.Equal(["checksums.txt"], handler.Uploads.Select(u => u.Name));
    }

    [Fact]
    public async Task UploadAssetsAsync_NameWithSpace_IsEscaped()
    {
        // Given
        var handler = new StubHandler();

        // When
        await handler.Client().UploadAssetsAsync(Release(), [Asset("release notes.txt", "x")]);

        // Then
        Assert.EndsWith("?name=release%20notes.txt", Assert.Single(handler.Uploads).Url);
    }

    [Fact]
    public async Task UploadAssetsAsync_UploadRejected_ThrowsWithStatusCode()
    {
        // Given
        var handler = new StubHandler { UploadStatus = HttpStatusCode.UnprocessableEntity };

        // When
        var error = await Assert.ThrowsAsync<ReleaserException>(() => handler.Client().UploadAssetsAsync(Release(), [Asset("a.zip", "x")]));

        // Then
        Assert.Contains("a.zip", error.Message);
        Assert.Contains("(422)", error.Message);
    }

    [Fact]
    public async Task UploadAssetsAsync_NoFiles_CallsNothing()
    {
        // Given
        var handler = new StubHandler();

        // When
        var uploaded = await handler.Client().UploadAssetsAsync(Release(), []);

        // Then
        Assert.Empty(uploaded);
        Assert.Empty(handler.Gets);
    }

    private static GitHubRelease Release() => new(StubHandler.ReleaseId, "v1.0.0", StubHandler.UploadUrl);

    private string Asset(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public const long ReleaseId = 42;
        public const string UploadUrl = "https://uploads.github.test/repos/o/r/releases/42/assets{?name,label}";

        public HttpStatusCode GetStatus { get; init; } = HttpStatusCode.NotFound;
        public HttpStatusCode PostStatus { get; init; } = HttpStatusCode.Created;
        public HttpStatusCode UploadStatus { get; init; } = HttpStatusCode.Created;
        public string PostResponse { get; init; } = ReleaseJson;
        public List<string> ExistingAssets { get; init; } = [];
        public List<string> Gets { get; } = [];
        public List<(string Path, JsonElement Body)> Posts { get; } = [];
        public List<(string Name, string Url, string? ContentType, string Content)> Uploads { get; } = [];

        private static string ReleaseJson => $$"""{"id":{{ReleaseId}},"tag_name":"v1.0.0","upload_url":"{{UploadUrl}}"}""";

        public GitHubReleases Client() => new(new HttpClient(this) { BaseAddress = new Uri("https://api.github.test/") }, "o/r");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host.StartsWith("uploads.", StringComparison.Ordinal))
            {
                var name = Uri.UnescapeDataString(uri.Query["?name=".Length..]);
                Uploads.Add((name, uri.AbsoluteUri, request.Content!.Headers.ContentType?.MediaType, await request.Content.ReadAsStringAsync(cancellationToken)));
                return new HttpResponseMessage(UploadStatus) { Content = new StringContent("{}") };
            }

            if (request.Method == HttpMethod.Get)
            {
                Gets.Add(uri.AbsoluteUri);
                if (uri.AbsolutePath.EndsWith("/assets", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(ExistingAssets.Select(n => new { name = n })))
                    };
                }

                return new HttpResponseMessage(GetStatus) { Content = new StringContent(GetStatus == HttpStatusCode.OK ? ReleaseJson : "{}") };
            }

            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            Posts.Add((uri.AbsolutePath, body));
            return new HttpResponseMessage(PostStatus) { Content = new StringContent(PostResponse) };
        }
    }
}
