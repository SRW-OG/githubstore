using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.Core.Tests;

/// <summary>Per-test isolated directory. Cleanup never fails a test.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory() => DirectoryPath = CreateDirectory();

    public string DirectoryPath { get; }

    public string Combine(string name) => Path.Combine(DirectoryPath, name);

    private static string CreateDirectory()
    {
        var candidates = new[]
        {
            Path.GetTempPath(),
            Path.Combine(Directory.GetCurrentDirectory(), "test-temp"),
        };
        foreach (var root in candidates)
        {
            try
            {
                var path = Path.Combine(root, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(path);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Try the next writable root instead of failing the test.
            }
        }

        throw new InvalidOperationException("No writable temporary directory is available for this test run.");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked leftover file must not mask the test result.
        }
    }
}

/// <summary>Offline HTTP stub. No test may reach a real network endpoint.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request.RequestUri!);
        var response = _respond(request);
        response.RequestMessage ??= request;
        return Task.FromResult(response);
    }

    public static HttpResponseMessage Json(string content, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status)
        => new(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

    public static HttpResponseMessage RateLimited()
    {
        var response = Status(HttpStatusCode.Forbidden);
        response.Headers.Add("X-RateLimit-Remaining", "0");
        return response;
    }
}

/// <summary>
/// The real GitHub payloads embedded in GitHubStore.Core. Reusing the shipped seed keeps the
/// network fixture identical to the offline snapshot instead of inventing metadata.
/// </summary>
internal static class SeedFixture
{
    private static readonly Lazy<(string Repository, string Release, DateTimeOffset FetchedAt)> Payload =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string RepositoryJson => Payload.Value.Repository;
    public static string ReleaseJson => Payload.Value.Release;
    public static DateTimeOffset FetchedAt => Payload.Value.FetchedAt;

    private static (string Repository, string Release, DateTimeOffset FetchedAt) Load()
    {
        using var stream = typeof(ICatalogService).Assembly
            .GetManifestResourceStream("GitHubStore.Core.PowerToys.seed.json")
            ?? throw new InvalidOperationException("The embedded PowerToys seed resource is missing.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        return (root.GetProperty("repository").GetRawText(),
            root.GetProperty("release").GetRawText(),
            root.GetProperty("fetchedAt").GetDateTimeOffset());
    }
}

/// <summary>Reports on the calling thread so assertions cannot race a captured context.</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _callback;
    public SyncProgress(Action<T> callback) => _callback = callback;
    public void Report(T value) => _callback(value);
}

/// <summary>Clearly synthetic payloads for repositories other than the shipped seed.</summary>
internal static class ApiFixture
{
    public static string RepositoryJson(string owner, string name) => $$"""
        {
          "id": 1,
          "name": "{{name}}",
          "full_name": "{{owner}}/{{name}}",
          "private": false,
          "owner": { "login": "{{owner}}" },
          "description": "Synthetic fixture repository used by offline tests.",
          "homepage": "",
          "license": { "key": "mit", "spdx_id": "MIT" },
          "language": "C#",
          "topics": [ "fixture" ],
          "stargazers_count": 1200,
          "forks_count": 30,
          "open_issues_count": 4,
          "archived": false,
          "is_template": false,
          "default_branch": "main",
          "pushed_at": "2026-10-18T00:00:00Z",
          "created_at": "2020-01-01T00:00:00Z"
        }
        """;

    public static string ReleaseJson(string repositoryUrl, string tag, params (string Name, long Size)[] assets)
    {
        var builder = new StringBuilder();
        builder.Append("{\"tag_name\":\"").Append(tag)
            .Append("\",\"name\":\"Synthetic release\",\"body\":\"Synthetic release notes.\"")
            .Append(",\"published_at\":\"2026-10-18T00:00:00Z\",\"prerelease\":false,\"draft\":false,\"assets\":[");
        for (var index = 0; index < assets.Length; index++)
        {
            if (index > 0) builder.Append(',');
            var (name, size) = assets[index];
            builder.Append("{\"id\":").Append(1000 + index)
                .Append(",\"name\":\"").Append(name)
                .Append("\",\"size\":").Append(size)
                .Append(",\"content_type\":\"application/octet-stream\",\"browser_download_url\":\"")
                .Append(repositoryUrl).Append("/releases/download/").Append(tag).Append('/').Append(name)
                .Append("\",\"download_count\":3}");
        }
        builder.Append("]}");
        return builder.ToString();
    }
}
