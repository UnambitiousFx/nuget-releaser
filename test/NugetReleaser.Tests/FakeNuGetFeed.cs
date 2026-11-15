using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace UnambitiousFx.NugetReleaser.Tests;

/// <summary>
///     An HTTP NuGet feed on localhost, for what a folder feed cannot show: a folder feed silently overwrites a package
///     that already exists, a real feed answers <c>409 Conflict</c>, which <c>dotnet nuget push</c> fails on unless it
///     is given <c>--skip-duplicate</c>. Packages are told apart by their <c>.nuspec</c> name, so each id once.
/// </summary>
internal sealed partial class FakeNuGetFeed : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _prefix;
    private readonly Task _loop;

    public FakeNuGetFeed()
    {
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            _prefix = $"http://localhost:{((IPEndPoint)probe.LocalEndpoint).Port}/";
        }

        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    /// <summary>The service index, to use as a feed source.</summary>
    public string Source => $"{_prefix}v3/index.json";

    /// <summary>The package ids accepted, in push order.</summary>
    public List<string> Published { get; } = [];

    /// <summary>The package ids refused because they were already published.</summary>
    public List<string> Conflicts { get; } = [];

    /// <summary>The API keys the pushes were sent with.</summary>
    public List<string?> ApiKeys { get; } = [];

    /// <summary>Writes a <c>NuGet.config</c> into <paramref name="directory" /> that lets <c>dotnet nuget push</c> use this HTTP feed.</summary>
    public void AllowIn(string directory) =>
        File.WriteAllText(Path.Combine(directory, "NuGet.config"), $"""
            <configuration>
              <packageSources>
                <add key="fake" value="{Source}" allowInsecureConnections="true" />
              </packageSources>
            </configuration>
            """);

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // the listener was stopped under a pending GetContextAsync
        }
    }

    [GeneratedRegex(@"(?<id>[A-Za-z0-9_.\-]+)\.nuspec")]
    private static partial Regex NuspecEntry();

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            using var response = context.Response;
            if (context.Request.HttpMethod == "GET")
            {
                var index = Encoding.UTF8.GetBytes(
                    $$"""{"version":"3.0.0","resources":[{"@id":"{{_prefix}}api/v2/package","@type":"PackagePublish/2.0.0"}]}""");
                response.ContentType = "application/json";
                await response.OutputStream.WriteAsync(index);
                continue;
            }

            using var body = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(body);
            // Entry names are stored uncompressed in a zip: Latin-1 keeps every byte as one char.
            var id = NuspecEntry().Match(Encoding.Latin1.GetString(body.ToArray())).Groups["id"].Value;
            lock (Published)
            {
                ApiKeys.Add(context.Request.Headers["X-NuGet-ApiKey"]);
                if (Published.Contains(id))
                {
                    Conflicts.Add(id);
                    response.StatusCode = (int)HttpStatusCode.Conflict;
                }
                else
                {
                    Published.Add(id);
                    response.StatusCode = (int)HttpStatusCode.Created;
                }
            }
        }
    }
}
