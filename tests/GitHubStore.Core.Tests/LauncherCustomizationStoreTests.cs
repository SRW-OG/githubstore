using System.Text.Json.Nodes;
using GitHubStore.App.Services.Launcher;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class LauncherCustomizationStoreTests
{
    [Fact]
    public async Task EmptyStoreDoesNotCreateAnIndex()
    {
        using var fixture = new Fixture();
        Assert.Empty(await fixture.Store().ListAsync());
        Assert.False(File.Exists(fixture.Manifest));
    }

    [Fact]
    public async Task CountsAndPropertiesRoundTripAndPreserveEachOther()
    {
        using var fixture = new Fixture();
        var first = await fixture.Store().RecordLaunchAsync(true, "local");
        Assert.Equal(1L, first.LaunchCount);
        Assert.Null(first.DisplayName);
        Assert.False(first.HasCategoryOverride);
        var category = new CategoryId("dev-tools");
        var updated = await fixture.Store().UpdatePropertiesAsync(true, "local", "My Editor", category);
        Assert.Equal(1L, updated.LaunchCount);
        Assert.Equal("My Editor", updated.DisplayName);
        Assert.True(updated.HasCategoryOverride);
        Assert.Equal(category, updated.Category);
        var launched = await fixture.Store().RecordLaunchAsync(true, "local");
        Assert.Equal(updated with { LaunchCount = 2 }, launched);
        Assert.Equal(launched, Assert.Single(await fixture.Store().ListAsync()));
        var uncategorized = await fixture.Store().UpdatePropertiesAsync(true, "local", "Renamed", null);
        Assert.Equal(2L, uncategorized.LaunchCount);
        Assert.True(uncategorized.HasCategoryOverride);
        Assert.Null(uncategorized.Category);
        Assert.Equal(uncategorized, Assert.Single(await fixture.Store().ListAsync()));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "managed-apps.json")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "manual-launcher.json")));
    }

    [Fact]
    public async Task SourceAndCaseSensitiveIdFormIndependentKeys()
    {
        using var fixture = new Fixture();
        await fixture.Store().RecordLaunchAsync(true, "same");
        await fixture.Store().RecordLaunchAsync(true, "same");
        await fixture.Store().UpdatePropertiesAsync(false, "same", "Installed", null);
        await fixture.Store().RecordLaunchAsync(false, "SAME");
        var entries = await fixture.Store().ListAsync();
        Assert.Equal(3, entries.Count);
        Assert.Equal(2L, Assert.Single(entries, item => item.IsManual).LaunchCount);
        Assert.Equal(0L, Assert.Single(entries, item => !item.IsManual && item.EntryId == "same").LaunchCount);
        Assert.Equal("Installed", Assert.Single(entries, item => !item.IsManual && item.EntryId == "same").DisplayName);
        Assert.Equal(1L, Assert.Single(entries, item => item.EntryId == "SAME").LaunchCount);
    }

    [Fact]
    public async Task ConcurrentInstancesDoNotLoseIncrementsOrProperties()
    {
        using var fixture = new Fixture();
        var operations = Enumerable.Range(0, 40).Select(_ => fixture.Store().RecordLaunchAsync(false, "entry"));
        await Task.WhenAll(operations.Append(fixture.Store().UpdatePropertiesAsync(false, "entry", "Name", new CategoryId("system"))));
        var saved = Assert.Single(await fixture.Store().ListAsync());
        Assert.Equal(40L, saved.LaunchCount);
        Assert.Equal("Name", saved.DisplayName);
        Assert.Equal(new CategoryId("system"), saved.Category);
        Assert.True(saved.HasCategoryOverride);
    }

    [Fact]
    public async Task LongMaximumCountSaturatesWithoutChangingOtherProperties()
    {
        using var fixture = new Fixture();
        await fixture.Store().UpdatePropertiesAsync(true, "entry", "Tool", new CategoryId("system"));
        var json = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Manifest))!;
        json["entries"]![0]!["launchCount"] = long.MaxValue - 1;
        await File.WriteAllTextAsync(fixture.Manifest, json.ToJsonString());
        var maximum = await fixture.Store().RecordLaunchAsync(true, "entry");
        Assert.Equal(long.MaxValue, maximum.LaunchCount);
        var before = await File.ReadAllBytesAsync(fixture.Manifest);
        Assert.Equal(maximum, await fixture.Store().RecordLaunchAsync(true, "entry"));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Manifest));
        var renamed = await fixture.Store().UpdatePropertiesAsync(true, "entry", "Renamed", null);
        Assert.Equal(long.MaxValue, renamed.LaunchCount);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2,\"entries\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"entries\":[null]}")]
    [InlineData("{\"schemaVersion\":1,\"entries\":[],\"unexpected\":true}")]
    [InlineData("{\"schemaVersion\":1,\"entries\":[{\"entryId\":\"id\",\"launchCount\":-1}]}")]
    [InlineData("{\"schemaVersion\":1,\"entries\":[{\"entryId\":\"id\"},{\"entryId\":\"id\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"entries\":[{\"entryId\":\"id\",\"displayName\":\" \"}]}")]
    [InlineData("{\"schemaVersion\":1,\"entries\":[{\"entryId\":\"id\",\"hasCategoryOverride\":true,\"category\":{\"value\":\"\"}}]}")]
    public async Task CorruptIndexIsNeverOverwrittenByEitherMutation(string contents)
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.Manifest, contents);
        var store = fixture.Store();
        Assert.Equal("launcher_customization_invalid_manifest", (await Assert.ThrowsAsync<LauncherException>(() => store.ListAsync())).Code);
        Assert.Equal("launcher_customization_invalid_manifest", (await Assert.ThrowsAsync<LauncherException>(() => store.RecordLaunchAsync(true, "id"))).Code);
        Assert.Equal("launcher_customization_invalid_manifest", (await Assert.ThrowsAsync<LauncherException>(
            () => store.UpdatePropertiesAsync(true, "id", "Name", null))).Code);
        Assert.Equal(contents, await File.ReadAllTextAsync(fixture.Manifest));
    }

    [Theory]
    [InlineData("", "Tool", null, "launcher_customization_invalid_id")]
    [InlineData("id", " ", null, "launcher_customization_invalid_display_name")]
    [InlineData("id", "Tool\nInjected", null, "launcher_customization_invalid_display_name")]
    [InlineData("id", "Tool", "", "launcher_customization_invalid_category")]
    public async Task InvalidInputDoesNotChangeExistingData(string id, string name, string? category, string code)
    {
        using var fixture = new Fixture();
        await fixture.Store().RecordLaunchAsync(false, "original");
        var before = await File.ReadAllBytesAsync(fixture.Manifest);
        Assert.Equal(code, (await Assert.ThrowsAsync<LauncherException>(() => fixture.Store().UpdatePropertiesAsync(
            true, id, name, category is null ? null : new CategoryId(category)))).Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Manifest));
        Assert.Equal("launcher_customization_invalid_id", (await Assert.ThrowsAsync<LauncherException>(
            () => fixture.Store().RecordLaunchAsync(true, " "))).Code);
    }

    [Fact]
    public async Task CancellationPreservesExistingData()
    {
        using var fixture = new Fixture();
        await fixture.Store().RecordLaunchAsync(false, "id");
        var before = await File.ReadAllBytesAsync(fixture.Manifest);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store().RecordLaunchAsync(false, "id", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store().UpdatePropertiesAsync(false, "id", "Name", null, cancellation.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Manifest));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "githubstore-launcher-customization-" + Guid.NewGuid().ToString("N"));
        public string Manifest => Path.Combine(Root, "launcher-customizations.json");
        public Fixture() => Directory.CreateDirectory(Root);
        public LauncherCustomizationStore Store() => new(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
