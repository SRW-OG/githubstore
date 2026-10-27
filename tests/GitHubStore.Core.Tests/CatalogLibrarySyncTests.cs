using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

[Trait("Category", "Integration")]
public sealed class CatalogLibrarySyncTests
{
    private const string Url = "https://raw.githubusercontent.com/fixture/catalog/main/catalog.json";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public async Task PendingDownloadDoesNotBlockReadingTheLocalCatalog()
    {
        using var directory = new TempDirectory();
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(directory, new Handler((_, _) => { requested.SetResult(); return pending.Task; }));
        var sync = service.SyncLibraryAsync(Url);
        try
        {
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var page = await service.BrowseAsync(new CatalogQuery()).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEmpty(page.Items);
        }
        finally
        {
            pending.TrySetResult(Body(Library("Remote")));
            await sync;
        }
    }

    [Fact]
    public async Task Sync_ReplacesBundledMembershipEmitsEventAndRestoresOffline()
    {
        using var directory = new TempDirectory();
        var handler = new Handler((_, _) => Task.FromResult(Body(Library("Remote"))));
        var service = Service(directory, handler);
        var events = 0;
        service.CatalogUpdated += (_, _) => events++;
        var result = await service.SyncLibraryAsync(Url);
        Assert.Equal(1, result.AppCount);
        Assert.InRange(result.SyncedAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
        Assert.Equal(1, events);
        Assert.Equal("Remote", Assert.Single(await Apps(service)).Repo);
        var restarted = Service(directory, new Handler((_, _) => throw new InvalidOperationException("Offline restart")));
        var remote = Assert.Single(await Apps(restarted));
        Assert.Equal("Remote", remote.Repo);
        Assert.True(remote.Stale);
        Assert.NotNull((await restarted.GetAppAsync(remote.Id)).Curation);
    }

    [Fact]
    public async Task Sync_RemovesOldRemotePreservesManualCollisionAcrossRestartAndEmptyLibrary()
    {
        using var directory = new TempDirectory();
        var body = BuildLibrary("dev-tools", "Old", "Manual");
        var service = Service(directory, new Handler((request, _) => Task.FromResult(
            request.RequestUri!.Host == "api.github.com"
                ? request.RequestUri.AbsolutePath.EndsWith("/latest") ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : Body(ApiFixture.RepositoryJson("fixture", "Manual"))
                : Body(body))));
        await service.AddAsync("fixture/Manual", new CategoryId("office"));
        await service.SyncLibraryAsync(Url);
        // 远端库现在也包含 Manual：手动记录让位给远端条目，只剩一条，分类跟随远端目录。
        var manual = Assert.Single(await Apps(service), a => a.Repo == "Manual");
        Assert.Equal(new CategoryId("dev-tools"), manual.PrimaryCategory);
        Assert.NotNull((await service.GetAppAsync(manual.Id)).Curation);
        body = Library("New");
        await service.SyncLibraryAsync(Url);
        Assert.Equal(new[] { "New" }, (await Apps(service)).Select(a => a.Repo).Order().ToArray());
        var restarted = Service(directory, new Handler((_, _) => Task.FromResult(Body(Library()))));
        Assert.Single(await Apps(restarted));
        await restarted.SyncLibraryAsync(Url);
        Assert.Empty(await Apps(restarted));
        Assert.Empty(await Apps(Service(directory)));
    }

    [Fact]
    public async Task Sync_SharedRepositoryReplacesManualRecordWithOneEntry()
    {
        using var directory = new TempDirectory();
        var body = Library("Manual");
        var service = Service(directory, new Handler((request, _) => Task.FromResult(
            request.RequestUri!.Host == "api.github.com"
                ? request.RequestUri.AbsolutePath.EndsWith("/latest") ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : Body(ApiFixture.RepositoryJson("fixture", "Manual"))
                : Body(body))));
        // 目录内添加（无发行资产确认），与用户手动添加走同一条手动条目路径。
        var added = await service.AddCatalogOnlyAsync("fixture/Manual", new CategoryId("office"),
            new PackagePolicy { InstallFormat = PackageInstallFormat.PortableExecutable }, null, null);
        var result = await service.SyncLibraryAsync(Url);
        Assert.Equal(1, result.AppCount);
        Assert.Equal(1, result.MergedManualCount);
        var merged = Assert.Single(await Apps(service), a => a.Repo == "Manual");
        Assert.Equal(added.App.Id, merged.Id);
        // 分类、策展说明与选包策略都跟随远端目录：本地那份记录不再作为手动条目生效。
        var detail = await service.GetAppAsync(merged.Id);
        Assert.Equal(new CategoryId("dev-tools"), detail.App.PrimaryCategory);
        Assert.NotNull(detail.Curation);
        Assert.Null(detail.PackagePolicy);
        Assert.Single(await Apps(service));
        // 合并后不再算手动条目：远端后续更新可以直接覆盖这条记录。
        Assert.Equal(0, (await service.SyncLibraryAsync(Url)).MergedManualCount);
    }

    [Fact]
    public async Task Sync_KeepsManualRecordThatTheRemoteLibraryDoesNotContain()
    {
        using var directory = new TempDirectory();
        var service = Service(directory, new Handler((request, _) => Task.FromResult(
            request.RequestUri!.Host == "api.github.com"
                ? request.RequestUri.AbsolutePath.EndsWith("/latest") ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : Body(ApiFixture.RepositoryJson("fixture", "Solo"))
                : Body(Library()))));
        await service.AddAsync("fixture/Solo", new CategoryId("office"));
        var result = await service.SyncLibraryAsync(Url);
        Assert.Equal(0, result.MergedManualCount);
        Assert.Equal("Solo", Assert.Single(await Apps(service)).Repo);
        Assert.Equal("Solo", Assert.Single(await Apps(Service(directory))).Repo);
    }

    [Fact]
    public async Task Sync_PreservesBundledRemoteAndManualDeletionTombstones()
    {
        using var directory = new TempDirectory();
        var body = Library("Remote");
        var service = Service(directory, new Handler((_, _) => Task.FromResult(Body(body))));
        var bundled = (await Apps(service)).First();
        await service.RemoveAsync(bundled.Id);
        await service.SyncLibraryAsync(Url);
        await service.RemoveAsync(Assert.Single(await Apps(service)).Id);
        var restarted = Service(directory, new Handler((_, _) => Task.FromResult(Body(body))));
        await restarted.SyncLibraryAsync(Url);
        Assert.Empty(await Apps(restarted));
        Assert.Empty(await Apps(Service(directory)));
    }

    [Fact]
    public async Task Sync_HonorsDeletedBundledEntryWhenRemoteContainsIt()
    {
        using var directory = new TempDirectory();
        await using var stream = typeof(GitHubCatalogService).Assembly.GetManifestResourceStream(CuratedCatalogLoader.ResourceName)!;
        var root = await JsonNode.ParseAsync(stream);
        var entry = root!["entries"]![0]!.DeepClone();
        var key = entry["repository"]!["full_name"]!.GetValue<string>().ToLowerInvariant();
        root["entries"] = new JsonArray(entry);
        var service = Service(directory, new Handler((_, _) => Task.FromResult(Body(root.ToJsonString()))));
        var bundled = Assert.Single(await Apps(service), app => app.Id.PackageId.Value == key);
        await service.RemoveAsync(bundled.Id);
        await service.SyncLibraryAsync(Url);
        Assert.Empty(await Apps(service));
        Assert.Empty(await Apps(Service(directory)));
    }

    [Fact]
    public async Task Sync_HonorsDeletedManualEntryWhenRemoteLaterAddsIt()
    {
        using var directory = new TempDirectory();
        var service = Service(directory, new Handler((request, _) => Task.FromResult(
            request.RequestUri!.Host == "api.github.com"
                ? request.RequestUri.AbsolutePath.EndsWith("/latest") ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : Body(ApiFixture.RepositoryJson("fixture", "Manual"))
                : Body(Library("Manual")))));
        var added = await service.AddAsync("fixture/Manual");
        await service.RemoveAsync(added.App.Id);
        await service.SyncLibraryAsync(Url);
        Assert.Empty(await Apps(service));
        Assert.Empty(await Apps(Service(directory)));
    }

    [Theory]
    [InlineData("http://example.com/catalog.json")]
    [InlineData("/catalog.json")]
    [InlineData("https://reader@example.com/catalog.json")]
    [InlineData("https://example.com/catalog.json#part")]
    [InlineData("https://github.com/owner/repo/blob/main/catalog.json")]
    [InlineData("file:///C:/catalog.json")]
    [InlineData("")]
    public async Task Sync_RejectsInvalidUrlWithoutHttp(string url)
    {
        using var directory = new TempDirectory();
        var service = Service(directory, new Handler((_, _) => throw new InvalidOperationException("No request allowed")));
        var error = await Assert.ThrowsAsync<RepositoryCatalogException>(() => service.SyncLibraryAsync(url));
        Assert.Equal("invalid_library_url", error.Code);
        Assert.False(File.Exists(directory.Combine("github-catalog.json")));
    }

    [Theory]
    [InlineData("malformed", "invalid_response")]
    [InlineData("category", "invalid_response")]
    [InlineData("duplicate", "invalid_response")]
    [InlineData("repository", "invalid_response")]
    [InlineData("oversize", "invalid_response")]
    [InlineData("declaredOversize", "invalid_response")]
    [InlineData("http", "network_error")]
    [InlineData("network", "network_error")]
    [InlineData("timeout", "request_timeout")]
    [InlineData("redirect", "invalid_response")]
    [InlineData("redirectUser", "invalid_response")]
    public async Task Sync_FailureKeepsMemoryAndExactCacheBytes(string fault, string code)
    {
        using var directory = new TempDirectory();
        var fail = false;
        var service = Service(directory, new Handler((_, _) =>
        {
            if (!fail) return Task.FromResult(Body(Library("Original")));
            if (fault == "network") throw new HttpRequestException("offline");
            if (fault == "timeout") throw new TaskCanceledException("simulated transport timeout");
            if (fault == "http") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            if (fault == "redirect" || fault == "redirectUser")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Library("Changed")),
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                        fault == "redirect" ? "http://example.com/catalog.json" : "https://user@example.com/catalog.json"),
                });
            if (fault == "oversize") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new UnseekableStream(new byte[8 * 1024 * 1024 + 1])) });
            if (fault == "declaredOversize")
            {
                var response = Body("{}");
                response.Content.Headers.ContentLength = 8 * 1024 * 1024 + 1;
                return Task.FromResult(response);
            }
            var root = JsonNode.Parse(Library("Changed"))!;
            if (fault == "repository") root["entries"]![0]!["repository"]!["owner"]!["login"] = "invalid/owner";
            if (fault == "category") root["entries"]![0]!["categoryId"] = "unknown";
            if (fault == "duplicate") ((JsonArray)root["entries"]!).Add(root["entries"]![0]!.DeepClone());
            return Task.FromResult(Body(fault == "malformed" ? "{broken" : root.ToJsonString()));
        }));
        await service.SyncLibraryAsync(Url);
        var before = await File.ReadAllBytesAsync(directory.Combine("github-catalog.json"));
        var eventCount = 0;
        service.CatalogUpdated += (_, _) => eventCount++;
        fail = true;
        var error = await Assert.ThrowsAsync<RepositoryCatalogException>(() => service.SyncLibraryAsync(Url));
        Assert.Equal(code, error.Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.Combine("github-catalog.json")));
        Assert.Equal("Original", Assert.Single(await Apps(service)).Repo);
        Assert.Equal(0, eventCount);
    }

    [Fact]
    public async Task Sync_CancellationDuringHttpKeepsExistingCatalog()
    {
        using var directory = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        var cancel = false;
        var service = Service(directory, new Handler(async (_, token) =>
        {
            if (cancel)
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, token);
            }
            return Body(Library("Original"));
        }));
        await service.SyncLibraryAsync(Url);
        var before = await File.ReadAllBytesAsync(directory.Combine("github-catalog.json"));
        cancel = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SyncLibraryAsync(Url, cancellation.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.Combine("github-catalog.json")));
        Assert.Equal("Original", Assert.Single(await Apps(service)).Repo);
    }

    [Fact]
    public async Task Sync_WriteFailureDoesNotPublishOrChangeMembership()
    {
        using var directory = new TempDirectory();
        var service = Service(directory, new Handler((_, _) => Task.FromResult(Body(Library("Remote")))));
        var before = (await Apps(service)).Select(a => a.Id).ToArray();
        var cachePath = directory.Combine("github-catalog.json");
        Directory.CreateDirectory(cachePath);
        var error = await Assert.ThrowsAsync<RepositoryCatalogException>(() => service.SyncLibraryAsync(Url));
        Assert.Equal("cache_write_failed", error.Code);
        Assert.Equal(before, (await Apps(service)).Select(a => a.Id).ToArray());
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath, "*.tmp"));
    }

    private static GitHubCatalogService Service(TempDirectory directory, Handler? handler = null)
        => new(new HttpClient(handler ?? new Handler((_, _) => throw new InvalidOperationException("Offline")))
            { Timeout = Timeout.InfiniteTimeSpan }, directory.DirectoryPath, includeCuratedSeed: true);
    private static async Task<IReadOnlyList<GitHubStore.Core.Abstractions.App>> Apps(GitHubCatalogService service)
        => (await service.BrowseAsync(new CatalogQuery { Limit = int.MaxValue, IncludeArchived = true })).Items;
    private static HttpResponseMessage Body(string body) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private static string Library(params string[] repos) => BuildLibrary("dev-tools", repos);

    // 独立命名：与 Library(params string[]) 重载并存时，单参数调用会绑定到分类参数上。
    private static string BuildLibrary(string categoryId, params string[] repos) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        entries = repos.Select(repo => new
        {
            categoryId = categoryId, fetchedAt = SeedFixture.FetchedAt,
            repository = JsonSerializer.Deserialize<JsonElement>(ApiFixture.RepositoryJson("fixture", repo)),
            release = (object?)null,
            curation = new AppCuration
            {
                RecommendationSources = new[] { new RecommendationSource
                {
                    Platform = "Fixture", Title = "Fixture recommendation", Url = new Uri("https://example.com/post"),
                    DiscoveredAt = SeedFixture.FetchedAt, EvidenceLevel = "direct",
                } },
                Verification = new RepositoryVerification
                {
                    VerifiedAt = SeedFixture.FetchedAt, RepositoryUrl = new Uri($"https://github.com/fixture/{repo}"),
                    Scope = "Repository identity", EvidenceUrls = new[] { new Uri("https://example.com/evidence") },
                    Notes = new[] { "Synthetic fixture" },
                },
            },
        }),
    }, Json);
}
