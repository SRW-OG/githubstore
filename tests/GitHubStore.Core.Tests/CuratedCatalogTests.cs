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
public sealed class CuratedCatalogTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const string PowerToysKey = "microsoft/powertoys";

    [Fact]
    public async Task EmbeddedResource_LoadsOfflineWithUniqueKnownCategoriesAndRequiredMetadata()
    {
        var curated = await LoadEmbeddedAsync();
        Assert.NotEmpty(curated);
        Assert.Equal(curated.Count, curated.Select(detail => Key(detail.App)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        using var directory = new TempDirectory();
        var handler = OfflineHandler();
        var service = Service(directory, handler);
        var categories = await service.ListCategoriesAsync();
        foreach (var detail in curated)
        {
            Assert.Contains(categories, category => category.Id == detail.App.PrimaryCategory);
            Assert.True(detail.App.Stale);
            Assert.NotEqual(default, detail.App.FetchedAt);
            Assert.NotEqual(default, detail.App.CreatedAt);
            Assert.NotEqual(default, detail.App.PushedAt);
            Assert.False(string.IsNullOrWhiteSpace(detail.App.Owner));
            Assert.False(string.IsNullOrWhiteSpace(detail.App.Repo));
            var curation = Assert.IsType<AppCuration>(detail.Curation);
            Assert.NotEmpty(curation.RecommendationSources);
            Assert.NotEqual(default, curation.Verification.VerifiedAt);
            Assert.False(string.IsNullOrWhiteSpace(curation.Verification.Scope));
            Assert.Equal(Key(detail.App), RepositoryAddress.Parse(curation.Verification.RepositoryUrl.AbsoluteUri).Key);
            Assert.NotEmpty(curation.Verification.EvidenceUrls);
            Assert.All(curation.Verification.EvidenceUrls, AssertHttps);
            Assert.All(curation.RecommendationSources, source =>
            {
                Assert.False(string.IsNullOrWhiteSpace(source.Platform));
                Assert.False(string.IsNullOrWhiteSpace(source.Title));
                Assert.False(string.IsNullOrWhiteSpace(source.EvidenceLevel));
                Assert.NotEqual(default, source.DiscoveredAt);
                AssertHttps(source.Url);
            });
            var restored = await service.GetAppAsync(detail.App.Id);
            Assert.Equal(JsonSerializer.Serialize(detail.Curation, Json), JsonSerializer.Serialize(restored.Curation, Json));
        }
        // The resource must retain real API identity metadata, rather than a hand-written display-only record.
        await using var stream = OpenEmbedded();
        using var document = await JsonDocument.ParseAsync(stream);
        foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            var repository = entry.GetProperty("repository");
            Assert.True(repository.GetProperty("id").GetInt64() > 0);
            Assert.False(string.IsNullOrWhiteSpace(repository.GetProperty("full_name").GetString()));
            Assert.Equal(RepositoryAddress.Parse(repository.GetProperty("full_name").GetString()!).Key,
                RepositoryAddress.Parse(repository.GetProperty("html_url").GetString()!).Key);
        }
        Assert.Empty(handler.Requests);
        Assert.False(File.Exists(CachePath(directory)));
    }

    [Fact]
    public async Task Loader_ExplicitNullReleaseMeansNoReleasesAndKeepsCamelCaseCuration()
    {
        var detail = Assert.Single(await LoadJsonAsync(SeedJson()));
        Assert.Empty(detail.Releases);
        Assert.False(detail.App.WindowsReady);
        Assert.Null(detail.Summary);
        Assert.Equal("离线测试说明", detail.Curation!.DescriptionZh);
        Assert.Equal("知乎", Assert.Single(detail.Curation.RecommendationSources).Platform);
        Assert.Equal("推荐原文", Assert.Single(detail.Curation.RecommendationSources).Quote);
    }

    [Theory]
    [InlineData("source", "http://example.com/post")]
    [InlineData("source", "/relative")]
    [InlineData("source", "https://reader@example.com/post")]
    [InlineData("source", "file:///C:/post")]
    [InlineData("evidence", "http://example.com/post")]
    [InlineData("evidence", "https://user@example.com/post")]
    [InlineData("repository", "https://reader" + "@github.com/fixture/ToolBox")]
    [InlineData("repository", "https://github.com/another/ToolBox")]
    [InlineData("repository", "https://example.com/fixture/ToolBox")]
    public async Task Loader_RejectsUnsafeOrMismatchedMetadataUrls(string field, string url)
    {
        var root = JsonNode.Parse(SeedJson())!;
        var curation = root["entries"]![0]!["curation"]!;
        if (field == "source") curation["recommendationSources"]![0]!["url"] = url;
        else if (field == "evidence") curation["verification"]!["evidenceUrls"]![0] = url;
        else curation["verification"]!["repositoryUrl"] = url;
        await Assert.ThrowsAsync<RepositoryCatalogException>(() => LoadJsonAsync(root.ToJsonString()));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("entries")]
    [InlineData("category")]
    [InlineData("duplicate")]
    [InlineData("release")]
    [InlineData("curation")]
    [InlineData("sources")]
    [InlineData("evidence")]
    [InlineData("fetchedAt")]
    [InlineData("verifiedAt")]
    [InlineData("discoveredAt")]
    public async Task Loader_RejectsInvalidSchemaAndMissingRequiredMetadata(string fault)
    {
        var root = JsonNode.Parse(SeedJson())!;
        var entry = root["entries"]![0]!;
        switch (fault)
        {
            case "schema": root["schemaVersion"] = 2; break;
            case "entries": root["entries"] = null; break;
            case "category": entry["categoryId"] = "unknown-category"; break;
            case "duplicate":
                var duplicate = entry.DeepClone();
                duplicate["repository"]!["owner"]!["login"] = "FIXTURE";
                duplicate["repository"]!["name"] = "TOOLBOX";
                ((JsonArray)root["entries"]!).Add(duplicate);
                break;
            case "release": entry["release"] = new JsonObject(); break;
            case "curation": entry["curation"] = null; break;
            case "sources": entry["curation"]!["recommendationSources"] = new JsonArray(); break;
            case "evidence": entry["curation"]!["verification"]!["evidenceUrls"] = new JsonArray(); break;
            case "fetchedAt": ((JsonObject)entry).Remove("fetchedAt"); break;
            case "verifiedAt": ((JsonObject)entry["curation"]!["verification"]!).Remove("verifiedAt"); break;
            case "discoveredAt": ((JsonObject)entry["curation"]!["recommendationSources"]![0]!).Remove("discoveredAt"); break;
        }
        await Assert.ThrowsAsync<RepositoryCatalogException>(() => LoadJsonAsync(root.ToJsonString()));
    }

    [Fact]
    public async Task Initialize_UserCacheAndCategoryWinWhileNewSeedsAndCurrentCurationAreAdded()
    {
        using var directory = new TempDirectory();
        var seeds = await SnapshotAsync(Service(directory));
        var target = seeds.First(detail => detail.Curation is not null);
        var category = new CategoryId(target.App.PrimaryCategory.Value == "office" ? "system" : "office");
        var cached = target with
        {
            App = target.App with
            {
                Owner = target.App.Owner.ToUpperInvariant(), Repo = target.App.Repo.ToUpperInvariant(),
                DisplayName = "Custom cached title", Stars = 7, PrimaryCategory = category, Categories = new[] { category },
            },
            Curation = target.Curation! with { DescriptionZh = "Outdated cached annotation" },
        };
        var custom = GitHubResponseParser.Parse(ApiFixture.RepositoryJson("fixture", "CustomUserTool"), null,
            SeedFixture.FetchedAt, new CategoryId("dev-tools"));
        await WriteCacheAsync(directory, 2, new[] { cached, custom }, Array.Empty<string>());
        var before = await File.ReadAllBytesAsync(CachePath(directory));
        var handler = OfflineHandler();
        var reopened = Service(directory, handler);
        var entries = await SnapshotAsync(reopened);
        Assert.Equal(seeds.Count + 1, entries.Count);
        var restored = Assert.Single(entries, detail => Key(detail.App) == Key(target.App));
        Assert.Equal("Custom cached title", restored.App.DisplayName);
        Assert.Equal(7, restored.App.Stars);
        Assert.Equal(category, restored.App.PrimaryCategory);
        Assert.Equal(new[] { category }, restored.App.Categories);
        Assert.Equal(JsonSerializer.Serialize(target.Curation, Json), JsonSerializer.Serialize(restored.Curation, Json));
        Assert.Contains(entries, detail => Key(detail.App) == "fixture/customusertool");
        Assert.Equal(before, await File.ReadAllBytesAsync(CachePath(directory)));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Initialize_RetiredCuratedCacheEntriesAreRemovedButManualRepositoriesRemain()
    {
        using var directory = new TempDirectory();
        var seeds = await LoadEmbeddedAsync();
        var retired = GitHubResponseParser.Parse(ApiFixture.RepositoryJson("fixture", "RetiredCuratedTool"), null,
            SeedFixture.FetchedAt, new CategoryId("system")) with
        {
            Curation = seeds[0].Curation! with
            {
                Verification = seeds[0].Curation!.Verification with
                {
                    RepositoryUrl = new Uri("https://github.com/fixture/RetiredCuratedTool"),
                },
            },
        };
        var manual = GitHubResponseParser.Parse(ApiFixture.RepositoryJson("fixture", "ManualTool"), null,
            SeedFixture.FetchedAt, new CategoryId("dev-tools"));
        await WriteCacheAsync(directory, 2, new[] { retired, manual }, Array.Empty<string>());
        var before = await File.ReadAllBytesAsync(CachePath(directory));
        var handler = OfflineHandler();
        var restored = await SnapshotAsync(Service(directory, handler));
        Assert.DoesNotContain(restored, detail => Key(detail.App) == "fixture/retiredcuratedtool");
        Assert.Contains(restored, detail => Key(detail.App) == "fixture/manualtool");
        Assert.Equal(seeds.Count + 1, restored.Count);
        Assert.Equal(before, await File.ReadAllBytesAsync(CachePath(directory)));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Initialize_VersionOneAddsNewSeedsAndOnlyInfersHistoricalPowerToysDeletion(bool hadPowerToys)
    {
        using var directory = new TempDirectory();
        var seeds = await SnapshotAsync(Service(directory));
        var oldEntries = hadPowerToys ? new[] { seeds.Single(detail => Key(detail.App) == PowerToysKey) } : Array.Empty<AppDetail>();
        await WriteCacheAsync(directory, 1, oldEntries);
        var before = await File.ReadAllBytesAsync(CachePath(directory));
        var service = Service(directory);
        var restored = await SnapshotAsync(service);
        Assert.Equal(seeds.Count - (hadPowerToys ? 0 : 1), restored.Count);
        Assert.Equal(hadPowerToys, restored.Any(detail => Key(detail.App) == PowerToysKey));
        Assert.All(seeds.Where(detail => Key(detail.App) != PowerToysKey), seed =>
            Assert.Contains(restored, detail => Key(detail.App) == Key(seed.App)));
        Assert.Equal(before, await File.ReadAllBytesAsync(CachePath(directory)));
        await service.RefreshAsync(RefreshScope.All);
        using var cache = JsonDocument.Parse(await File.ReadAllTextAsync(CachePath(directory)));
        Assert.Equal(2, cache.RootElement.GetProperty("schemaVersion").GetInt32());
        var suppressed = cache.RootElement.GetProperty("suppressedRepositoryKeys").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(!hadPowerToys, suppressed.Contains(PowerToysKey));
    }

    [Fact]
    public async Task Initialize_DefaultConstructorKeepsLegacyCacheReplacementSemantics()
    {
        using var directory = new TempDirectory();
        await WriteCacheAsync(directory, 1, Array.Empty<AppDetail>());
        var handler = OfflineHandler();
        var service = new GitHubCatalogService(new HttpClient(handler), directory.DirectoryPath);
        Assert.Empty(await SnapshotAsync(service));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LegacyModeMigration_PreservesHistoricalDeletionWhenCuratedModeIsLaterEnabled()
    {
        using var directory = new TempDirectory();
        await WriteCacheAsync(directory, 1, Array.Empty<AppDetail>());
        var legacy = new GitHubCatalogService(new HttpClient(new StubHttpHandler(Respond)), directory.DirectoryPath);
        await legacy.AddAsync("fixture/LegacyUserTool");
        Assert.Contains(PowerToysKey, await SuppressedAsync(directory));
        var restored = await SnapshotAsync(Service(directory));
        Assert.DoesNotContain(restored, detail => Key(detail.App) == PowerToysKey);
        Assert.Contains(restored, detail => Key(detail.App) == "fixture/legacyusertool");
        Assert.All((await LoadEmbeddedAsync()).Where(detail => Key(detail.App) != PowerToysKey), seed =>
            Assert.Contains(restored, detail => Key(detail.App) == Key(seed.App)));
    }

    [Fact]
    public async Task Remove_SuppressesSeedAcrossRestartAndExplicitAddClearsTombstone()
    {
        using var directory = new TempDirectory();
        var service = Service(directory);
        var removed = (await SnapshotAsync(service)).First(detail => detail.Curation is not null);
        await service.RemoveAsync(removed.App.Id);
        Assert.Contains(Key(removed.App), await SuppressedAsync(directory));
        var restarted = Service(directory);
        Assert.DoesNotContain(await SnapshotAsync(restarted), detail => Key(detail.App) == Key(removed.App));
        var added = await restarted.AddAsync($"{removed.App.Owner}/{removed.App.Repo}", new CategoryId("office"));
        Assert.Equal(new CategoryId("office"), added.App.PrimaryCategory);
        Assert.NotNull(added.Curation);
        Assert.DoesNotContain(Key(removed.App), await SuppressedAsync(directory));
        var restored = await Service(directory).GetAppAsync(added.App.Id);
        Assert.Equal(new CategoryId("office"), restored.App.PrimaryCategory);
        Assert.NotNull(restored.Curation);
    }

    [Fact]
    public async Task Metadata_RemainsOnPreviewRefreshAndCacheRestore()
    {
        using var directory = new TempDirectory();
        var service = Service(directory);
        var target = (await SnapshotAsync(service)).First(detail => detail.Curation is not null);
        var expected = JsonSerializer.Serialize(target.Curation, Json);
        var preview = await service.PreviewAsync($"{target.App.Owner}/{target.App.Repo}");
        Assert.Equal(expected, JsonSerializer.Serialize(preview.Curation, Json));
        Assert.False(File.Exists(CachePath(directory)));
        await service.RefreshAsync(RefreshScope.All);
        Assert.Equal(expected, JsonSerializer.Serialize((await service.GetAppAsync(target.App.Id)).Curation, Json));
        var restored = await Service(directory, OfflineHandler()).GetAppAsync(target.App.Id);
        Assert.Equal(expected, JsonSerializer.Serialize(restored.Curation, Json));
        Assert.True(restored.App.Stale);
    }

    [Fact]
    public async Task SeedMerge_NewerCuratedPowerToysSnapshotWinsAndCurationIsRetained()
    {
        using var directory = new TempDirectory();
        var service = Service(directory);
        var curated = (await LoadEmbeddedAsync()).SingleOrDefault(detail => Key(detail.App) == PowerToysKey);
        var powerToys = Assert.Single(await SnapshotAsync(service), detail => Key(detail.App) == PowerToysKey);
        var expectedDate = curated is not null && curated.App.FetchedAt >= SeedFixture.FetchedAt
            ? curated.App.FetchedAt : SeedFixture.FetchedAt;
        Assert.Equal(expectedDate, powerToys.App.FetchedAt);
        Assert.Equal(JsonSerializer.Serialize(curated?.Curation, Json), JsonSerializer.Serialize(powerToys.Curation, Json));
        if (curated is not null && curated.App.FetchedAt >= SeedFixture.FetchedAt)
        {
            Assert.Equal(curated.App.Stars, powerToys.App.Stars);
            Assert.Equal(curated.App.PrimaryCategory, powerToys.App.PrimaryCategory);
        }
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"schemaVersion\":99,\"entries\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"entries\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"entries\":[],\"suppressedRepositoryKeys\":[\"https://github.com/owner/repo\"]}")]
    [InlineData("{\"schemaVersion\":2,\"entries\":[],\"suppressedRepositoryKeys\":[null]}")]
    public async Task InvalidCache_FallsBackWithoutChangingOriginalFile(string json)
    {
        using var directory = new TempDirectory();
        var seedCount = (await SnapshotAsync(Service(directory))).Count;
        await File.WriteAllTextAsync(CachePath(directory), json);
        var before = await File.ReadAllBytesAsync(CachePath(directory));
        var service = Service(directory, OfflineHandler());
        Assert.Equal(seedCount, (await SnapshotAsync(service)).Count);
        Assert.Equal("cache_read_failed", service.LastCacheError);
        Assert.Equal(before, await File.ReadAllBytesAsync(CachePath(directory)));
    }

    [Fact]
    public async Task FailedRefresh_KeepsEntriesTombstonesAndCacheBytes()
    {
        using var directory = new TempDirectory();
        var fail = false;
        var handler = new StubHttpHandler(request => fail ? StubHttpHandler.Status(HttpStatusCode.InternalServerError) : Respond(request));
        var service = Service(directory, handler);
        var seed = (await SnapshotAsync(service))[0];
        await service.AddAsync("fixture/RefreshFailureTool");
        await service.RemoveAsync(seed.App.Id);
        var beforeEntries = JsonSerializer.Serialize(await SnapshotAsync(service), Json);
        var beforeBytes = await File.ReadAllBytesAsync(CachePath(directory));
        fail = true;
        var exception = await Assert.ThrowsAsync<RepositoryCatalogException>(() => service.RefreshAsync(RefreshScope.All));
        Assert.Equal("network_error", exception.Code);
        Assert.Equal(beforeEntries, JsonSerializer.Serialize(await SnapshotAsync(service), Json));
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(CachePath(directory)));
        Assert.Contains(Key(seed.App), await SuppressedAsync(directory));
    }

    [Fact]
    public async Task FailedRemovePersist_DoesNotPublishEntriesOrTombstone()
    {
        using var directory = new TempDirectory();
        var service = Service(directory);
        var before = await SnapshotAsync(service);
        var target = before[0];
        Directory.CreateDirectory(CachePath(directory)); // Force the final atomic rename to fail.
        var exception = await Assert.ThrowsAsync<RepositoryCatalogException>(() => service.RemoveAsync(target.App.Id));
        Assert.Equal("cache_write_failed", exception.Code);
        Assert.Equal(before.Count, (await SnapshotAsync(service)).Count);
        Assert.Equal(target, await service.GetAppAsync(target.App.Id));
        Directory.Delete(CachePath(directory));
        await service.RefreshAsync(RefreshScope.All);
        Assert.DoesNotContain(Key(target.App), await SuppressedAsync(directory));
        Assert.Contains(await SnapshotAsync(Service(directory)), detail => Key(detail.App) == Key(target.App));
    }

    [Fact]
    public async Task FailedRefreshPersist_KeepsPreviousSnapshot()
    {
        using var directory = new TempDirectory();
        var service = Service(directory);
        var before = JsonSerializer.Serialize(await SnapshotAsync(service), Json);
        Directory.CreateDirectory(CachePath(directory));
        var exception = await Assert.ThrowsAsync<RepositoryCatalogException>(() => service.RefreshAsync(RefreshScope.All));
        Assert.Equal("cache_write_failed", exception.Code);
        Assert.Equal(before, JsonSerializer.Serialize(await SnapshotAsync(service), Json));
    }

    [Fact]
    public async Task FailedReAddPersist_DoesNotClearTombstoneOrPublishEntry()
    {
        using var directory = new TempDirectory();
        var service = Service(directory);
        var target = (await SnapshotAsync(service))[0];
        await service.RemoveAsync(target.App.Id);
        File.Delete(CachePath(directory));
        Directory.CreateDirectory(CachePath(directory));
        var exception = await Assert.ThrowsAsync<RepositoryCatalogException>(() => service.AddAsync($"{target.App.Owner}/{target.App.Repo}"));
        Assert.Equal("cache_write_failed", exception.Code);
        Assert.DoesNotContain(await SnapshotAsync(service), detail => Key(detail.App) == Key(target.App));
        Directory.Delete(CachePath(directory));
        await service.RefreshAsync(RefreshScope.All);
        Assert.Contains(Key(target.App), await SuppressedAsync(directory));
        Assert.DoesNotContain(await SnapshotAsync(Service(directory)), detail => Key(detail.App) == Key(target.App));
    }

    private static GitHubCatalogService Service(TempDirectory directory, StubHttpHandler? handler = null)
        => new(new HttpClient(handler ?? new StubHttpHandler(Respond)) { Timeout = Timeout.InfiniteTimeSpan }, directory.DirectoryPath, includeCuratedSeed: true);

    private static StubHttpHandler OfflineHandler() => new(_ => throw new InvalidOperationException("Initialization must remain offline."));

    private static HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 3) return StubHttpHandler.Status(HttpStatusCode.NotFound);
        return StubHttpHandler.Json(ApiFixture.RepositoryJson(segments[1], segments[2]));
    }

    private static async Task<IReadOnlyList<AppDetail>> SnapshotAsync(GitHubCatalogService service)
    {
        var page = await service.BrowseAsync(new CatalogQuery { IncludeArchived = true, Limit = int.MaxValue });
        var entries = new List<AppDetail>();
        foreach (var app in page.Items) entries.Add(await service.GetAppAsync(app.Id));
        return entries;
    }

    private static string Key(Abstractions.App app) => $"{app.Owner}/{app.Repo}".ToLowerInvariant();
    private static string CachePath(TempDirectory directory) => directory.Combine("github-catalog.json");
    private static Stream OpenEmbedded() => typeof(GitHubCatalogService).Assembly.GetManifestResourceStream(CuratedCatalogLoader.ResourceName)
        ?? throw new InvalidOperationException("The curated catalog embedded resource is missing.");
    private static async Task<IReadOnlyList<AppDetail>> LoadEmbeddedAsync()
    {
        await using var stream = OpenEmbedded();
        return await CuratedCatalogLoader.LoadAsync(stream);
    }
    private static async Task<IReadOnlyList<AppDetail>> LoadJsonAsync(string json)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await CuratedCatalogLoader.LoadAsync(stream);
    }
    private static async Task WriteCacheAsync(TempDirectory directory, int version, AppDetail[] entries, string[]? suppressed = null)
    {
        var envelope = new { schemaVersion = version, entries, suppressedRepositoryKeys = suppressed };
        await File.WriteAllTextAsync(CachePath(directory), JsonSerializer.Serialize(envelope, Json));
    }
    private static async Task<string[]> SuppressedAsync(TempDirectory directory)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(CachePath(directory)));
        return document.RootElement.GetProperty("suppressedRepositoryKeys").EnumerateArray().Select(value => value.GetString()!).ToArray();
    }
    private static void AssertHttps(Uri uri)
    {
        Assert.True(uri.IsAbsoluteUri);
        Assert.Equal("https", uri.Scheme);
        Assert.Empty(uri.UserInfo);
    }
    private static string SeedJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        entries = new[]
        {
            new
            {
                categoryId = "dev-tools", fetchedAt = SeedFixture.FetchedAt,
                repository = JsonSerializer.Deserialize<JsonElement>(ApiFixture.RepositoryJson("fixture", "ToolBox")),
                release = (object?)null,
                curation = new AppCuration
                {
                    DescriptionZh = "离线测试说明",
                    RecommendationSources = new[]
                    {
                        new RecommendationSource
                        {
                            Platform = "知乎", Title = "离线测试推荐", Url = new Uri("https://www.zhihu.com/question/123/answer/456"),
                            DiscoveredAt = SeedFixture.FetchedAt, EvidenceLevel = "direct", Quote = "推荐原文",
                        },
                    },
                    Verification = new RepositoryVerification
                    {
                        VerifiedAt = SeedFixture.FetchedAt, RepositoryUrl = new Uri("https://github.com/fixture/ToolBox"),
                        Scope = "repository identity and public API metadata",
                        EvidenceUrls = new[] { new Uri("https://api.github.com/repos/fixture/ToolBox") },
                        Notes = new[] { "Synthetic fixture only; not an install safety statement." },
                    },
                },
            },
        },
    }, Json);
}
