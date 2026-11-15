using System.Net;
using System.Text.Json;

namespace UnambitiousFx.NugetReleaser.Tests;

public sealed class GitHubReleasesTests
{
    [Fact]
    public async Task CreateAsync_ReleaseExists_ReturnsFalseWithoutCreating()
    {
        // Given
        var handler = new StubHandler { GetStatus = HttpStatusCode.OK };

        // When
        var created = await handler.Client().CreateAsync("Lib/v1.0.0", "abc", "Lib 1.0.0", "- fix");

        // Then
        Assert.False(created);
        Assert.Empty(handler.Posts);
    }

    [Fact]
    public async Task CreateAsync_ReleaseMissing_PostsTagTargetNameAndBody()
    {
        // Given
        var handler = new StubHandler();

        // When
        var created = await handler.Client().CreateAsync("Lib/v1.0.0", "abc123", "Lib 1.0.0", "- fix: a bug");

        // Then
        Assert.True(created);
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

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode GetStatus { get; init; } = HttpStatusCode.NotFound;
        public HttpStatusCode PostStatus { get; init; } = HttpStatusCode.Created;
        public string PostResponse { get; init; } = "{}";
        public List<string> Gets { get; } = [];
        public List<(string Path, JsonElement Body)> Posts { get; } = [];

        public GitHubReleases Client() => new(new HttpClient(this) { BaseAddress = new Uri("https://api.github.test/") }, "o/r");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                Gets.Add(request.RequestUri!.AbsoluteUri);
                return new HttpResponseMessage(GetStatus);
            }

            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            Posts.Add((request.RequestUri!.AbsolutePath, body));
            return new HttpResponseMessage(PostStatus) { Content = new StringContent(PostResponse) };
        }
    }
}
