using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class WizardCatalogTests
{
    private static readonly PackagePolicy Policy = new()
    {
        InstallFormat = PackageInstallFormat.PortableArchive,
        AssetRules = [new PackageAssetRule { Include = ["Product-*.zip"] }],
    };

    [Fact]
    public async Task ConfiguredPreviewFetchesPinnedChannelWithoutSaving()
    {
        using var directory = new TempDirectory();
        var requests = new List<string>();
        using var client = Client(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return Response(request, ["Product-a.zip"]);
        });
        IPackagePolicyCatalogService service = new GitHubCatalogService(client, directory.DirectoryPath);
        var policy = Policy with { ReleaseChannel = PackageReleaseChannel.Pinned, PinnedTag = "v1" };
        var preview = await service.PreviewAsync("fixture/Tool", policy);
        Assert.Equal(policy, preview.PackagePolicy);
        Assert.Equal("v1", Assert.Single(preview.Releases).TagName);
        Assert.Contains("/repos/fixture/Tool/releases/tags/v1", requests);
        Assert.False(File.Exists(directory.Combine("github-catalog.json")));
    }

    [Fact]
    public async Task ConfiguredAddAcceptsEligibleNonWinnerAndPersistsDescriptionAcrossRefreshPolicyUpdateAndRestart()
    {
        using var directory = new TempDirectory();
        using var client = Client(request => Response(request, ["Product-a.zip", "Product-z.zip"]));
        var service = new GitHubCatalogService(client, directory.DirectoryPath);
        var preview = await service.PreviewAsync("fixture/Tool", Policy);
        Assert.Equal("Product-a.zip", DefaultAssetSelectionPolicy.Select(preview, CurrentArchitecture)!.Name);
        var events = 0;
        service.CatalogUpdated += (_, _) => events++;
        var detail = await service.AddAsync("fixture/Tool", new CategoryId("office"), Policy, "  Local description\nwith details  ", "Product-z.zip", "v1");
        Assert.Equal(1, events);
        Assert.Equal("Local description\nwith details", detail.DescriptionOverride);
        Assert.Equal(detail.DescriptionOverride, detail.App.Description);
        Assert.Equal(new CategoryId("office"), detail.App.PrimaryCategory);
        Assert.Null(detail.Curation);
        Assert.Equal(Policy, detail.PackagePolicy);
        Assert.DoesNotContain("confirmedAssetName", await File.ReadAllTextAsync(directory.Combine("github-catalog.json")), StringComparison.OrdinalIgnoreCase);
        var seed = (await service.BrowseAsync(new CatalogQuery())).Items.Single(a => a.Id != detail.App.Id);
        await service.RemoveAsync(seed.Id);
        await service.RefreshAsync(RefreshScope.All);
        Assert.Equal(detail.DescriptionOverride, (await service.GetAppAsync(detail.App.Id)).App.Description);
        await service.UpdatePackagePolicyAsync(detail.App.Id, Policy with { PreservePaths = ["data"] });
        Assert.Equal(detail.DescriptionOverride, (await service.GetAppAsync(detail.App.Id)).DescriptionOverride);
        using var offline = Client(_ => throw new InvalidOperationException("Offline restart"));
        var restarted = new GitHubCatalogService(offline, directory.DirectoryPath);
        var restored = await restarted.GetAppAsync(detail.App.Id);
        Assert.Equal(detail.DescriptionOverride, restored.DescriptionOverride);
        Assert.Equal(detail.DescriptionOverride, restored.App.Description);
        Assert.Equal("data", Assert.Single(restored.PackagePolicy!.PreservePaths));
    }

    [Theory]
    [InlineData("Product-vanished.zip", "Product-other.zip")]
    [InlineData("Product-a.zip", "product-a.zip")]
    [InlineData("Product-linux.zip", "Product-linux.zip")]
    [InlineData("Product-source.zip", "Product-source.zip")]
    [InlineData("Other.zip", "Other.zip")]
    public async Task StaleOrIneligibleSelectionLeavesExactCacheMembershipAndNotificationsUnchanged(string confirmed, string actual)
    {
        using var directory = new TempDirectory();
        using var client = Client(request => Response(request, [actual]));
        var service = new GitHubCatalogService(client, directory.DirectoryPath);
        var seed = Assert.Single((await service.BrowseAsync(new CatalogQuery())).Items);
        await service.RemoveAsync(seed.Id);
        var before = await File.ReadAllBytesAsync(directory.Combine("github-catalog.json"));
        var events = 0;
        service.CatalogUpdated += (_, _) => events++;
        var error = await Assert.ThrowsAsync<RepositoryCatalogException>(() => service.AddAsync("fixture/Tool", null, Policy, "Local", confirmed, "v1"));
        Assert.Equal("selected_asset_unavailable", error.Code);
        Assert.Equal(0, events);
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.Combine("github-catalog.json")));
        Assert.Empty((await service.BrowseAsync(new CatalogQuery())).Items);
    }

    [Fact]
    public async Task ChangedLatestReleaseWithSameFilenameRequiresNewConfirmation()
    {
        using var directory = new TempDirectory();
        var tag = "v1";
        using var client = Client(request => request.RequestUri!.AbsolutePath.Contains("/releases/")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ApiFixture.ReleaseJson("https://github.com/fixture/Tool", tag, ("Product-a.zip", 20))) }
            : Response(request, ["Product-a.zip"]));
        var service = new GitHubCatalogService(client, directory.DirectoryPath);
        var preview = await service.PreviewAsync("fixture/Tool", Policy);
        var confirmedTag = Assert.Single(preview.Releases).TagName;
        tag = "v2";
        var events = 0;
        service.CatalogUpdated += (_, _) => events++;
        Assert.Equal("selected_asset_unavailable", (await Assert.ThrowsAsync<RepositoryCatalogException>(() =>
            service.AddAsync("fixture/Tool", null, Policy, "Local", "Product-a.zip", confirmedTag))).Code);
        Assert.Equal(0, events);
        Assert.False(File.Exists(directory.Combine("github-catalog.json")));
        Assert.DoesNotContain((await service.BrowseAsync(new CatalogQuery())).Items, app => app.Repo == "Tool");
    }

    [Fact]
    public async Task ExplicitRuleCannotConfirmWrongArchitecture()
    {
        using var directory = new TempDirectory();
        var token = CurrentArchitecture == Arch.Arm64 ? "x64" : "arm64";
        var name = $"Product-{token}.zip";
        using var client = Client(request => Response(request, [name]));
        var service = new GitHubCatalogService(client, directory.DirectoryPath);
        var error = await Assert.ThrowsAsync<RepositoryCatalogException>(() => service.AddAsync("fixture/Tool", null, Policy, null, name, "v1"));
        Assert.Equal("selected_asset_unavailable", error.Code);
        Assert.False(File.Exists(directory.Combine("github-catalog.json")));
    }

    [Theory]
    [InlineData("../Product.zip")]
    [InlineData("C:\\Product.zip")]
    [InlineData("Product.zip:stream")]
    [InlineData("Product-*.zip")]
    [InlineData("Product-?.zip")]
    [InlineData("CON.zip")]
    [InlineData("Product.zip.")]
    [InlineData("")]
    public async Task UnsafeConfirmedFilenameIsRejectedBeforeHttp(string name)
    {
        using var directory = new TempDirectory();
        using var client = Client(_ => throw new InvalidOperationException("No HTTP"));
        var service = new GitHubCatalogService(client, directory.DirectoryPath);
        var error = await Assert.ThrowsAsync<RepositoryCatalogException>(() => service.AddAsync("fixture/Tool", null, Policy, null, name, "v1"));
        Assert.Equal("invalid_asset_name", error.Code);
        Assert.False(File.Exists(directory.Combine("github-catalog.json")));
    }

    [Fact]
    public async Task InvalidDescriptionAndPolicyAreRejectedBeforeHttp()
    {
        using var directory = new TempDirectory();
        using var client = Client(_ => throw new InvalidOperationException("No HTTP"));
        var service = new GitHubCatalogService(client, directory.DirectoryPath);
        foreach (var description in new[] { new string('a', 4001), "text\0hidden" })
            Assert.Equal("invalid_description", (await Assert.ThrowsAsync<RepositoryCatalogException>(() =>
                service.AddAsync("fixture/Tool", null, Policy, description, "Product-a.zip", "v1"))).Code);
        Assert.Equal("invalid_package_policy", (await Assert.ThrowsAsync<RepositoryCatalogException>(() => service.PreviewAsync("fixture/Tool",
            new PackagePolicy { ReleaseChannel = PackageReleaseChannel.Pinned }))).Code);
    }

    [Fact]
    public async Task ConfiguredAddWriteFailureDoesNotPublishEntryOrNotification()
    {
        using var directory = new TempDirectory();
        using var client = Client(request => Response(request, ["Product-a.zip"]));
        var service = new GitHubCatalogService(client, directory.DirectoryPath);
        var before = (await service.BrowseAsync(new CatalogQuery())).Items.Select(a => a.Id).ToArray();
        Directory.CreateDirectory(directory.Combine("github-catalog.json"));
        var events = 0;
        service.CatalogUpdated += (_, _) => events++;
        Assert.Equal("cache_write_failed", (await Assert.ThrowsAsync<RepositoryCatalogException>(() =>
            service.AddAsync("fixture/Tool", null, Policy, "Description", "Product-a.zip", "v1"))).Code);
        Assert.Equal(before, (await service.BrowseAsync(new CatalogQuery())).Items.Select(a => a.Id).ToArray());
        Assert.Equal(0, events);
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task DuplicateConfiguredAddIsRejectedWithoutFurtherRequestsOrChanges()
    {
        using var directory = new TempDirectory();
        var calls = 0;
        using var client = Client(request => { calls++; return Response(request, ["Product-a.zip"]); });
        var service = new GitHubCatalogService(client, directory.DirectoryPath);
        await service.AddAsync("fixture/Tool", null, Policy, null, "Product-a.zip", "v1");
        var before = await File.ReadAllBytesAsync(directory.Combine("github-catalog.json"));
        var requestsBefore = calls;
        Assert.Equal("duplicate_repository", (await Assert.ThrowsAsync<RepositoryCatalogException>(() =>
            service.AddAsync("fixture/TOOL", null, Policy, "Other", "Product-a.zip", "v1"))).Code);
        Assert.Equal(requestsBefore, calls);
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.Combine("github-catalog.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitCatalogOnlyAdditionPersistsRepositoriesWithoutUsableReleaseAssets(bool hasRelease)
    {
        using var directory = new TempDirectory();
        using var client = Client(request => !hasRelease && request.RequestUri!.AbsolutePath.Contains("/releases/")
            ? new HttpResponseMessage(HttpStatusCode.NotFound) : Response(request, ["Product-linux.zip", "README.txt", "Product-?.zip"]));
        var service = new GitHubCatalogService(client, directory.DirectoryPath);
        var policy = Policy with { PreservePaths = ["data"] };
        var detail = await service.AddCatalogOnlyAsync("fixture/Tool", new CategoryId("office"), policy, "Catalog description", hasRelease ? "v1" : null);
        Assert.Equal("Catalog description", detail.DescriptionOverride);
        Assert.Equal("Catalog description", detail.App.Description);
        Assert.Equal(new CategoryId("office"), detail.App.PrimaryCategory);
        Assert.Equal(policy, detail.PackagePolicy);
        Assert.Equal(hasRelease ? 1 : 0, detail.Releases.Count);
        using var offline = Client(_ => throw new InvalidOperationException("Offline restart"));
        var restarted = new GitHubCatalogService(offline, directory.DirectoryPath);
        Assert.Equal("Catalog description", (await restarted.GetAppAsync(detail.App.Id)).App.Description);
    }

    [Theory]
    [InlineData(null, "v1")]
    [InlineData("v1", null)]
    [InlineData("v1", "v2")]
    public async Task CatalogOnlyAdditionRejectsChangedReleaseOrReleaseAvailability(string? confirmed, string? current)
    {
        using var directory = new TempDirectory();
        using var client = Client(request => !request.RequestUri!.AbsolutePath.Contains("/releases/") ? Response(request, [])
            : current is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ApiFixture.ReleaseJson("https://github.com/fixture/Tool", current)) });
        var service = new GitHubCatalogService(client, directory.DirectoryPath);
        var events = 0;
        service.CatalogUpdated += (_, _) => events++;
        Assert.Equal("selected_asset_unavailable", (await Assert.ThrowsAsync<RepositoryCatalogException>(() =>
            service.AddCatalogOnlyAsync("fixture/Tool", null, Policy, "Local", confirmed))).Code);
        Assert.Equal(0, events);
        Assert.False(File.Exists(directory.Combine("github-catalog.json")));
    }

    [Theory]
    [InlineData("Product-a.zip")]
    [InlineData("Client.exe")]
    public async Task CatalogOnlyAdditionCannotHideAvailableAssetBehindRestrictivePolicy(string assetName)
    {
        using var directory = new TempDirectory();
        using var client = Client(request => Response(request, [assetName]));
        var service = new GitHubCatalogService(client, directory.DirectoryPath);
        var restrictive = Policy with { AssetRules = [new PackageAssetRule { Include = ["NeverMatches*"] }] };
        Assert.Equal("selected_asset_unavailable", (await Assert.ThrowsAsync<RepositoryCatalogException>(() =>
            service.AddCatalogOnlyAsync("fixture/Tool", null, restrictive, null, "v1"))).Code);
        Assert.False(File.Exists(directory.Combine("github-catalog.json")));
    }

    private static Arch CurrentArchitecture => RuntimeInformation.OSArchitecture switch
    { Architecture.Arm64 => Arch.Arm64, Architecture.X86 => Arch.X86, _ => Arch.Amd64 };
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new Handler(respond));
    private static HttpResponseMessage Response(HttpRequestMessage request, string[] names)
    {
        var segments = request.RequestUri!.AbsolutePath.Split('/');
        var owner = segments[2];
        var repo = segments[3];
        var body = request.RequestUri.AbsolutePath.Contains("/releases/")
            ? ApiFixture.ReleaseJson($"https://github.com/{owner}/{repo}", "v1", names.Select(n => (n, 20L)).ToArray())
            : ApiFixture.RepositoryJson(owner, repo);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
